using Consultologist.ZoomApp.Core.Engine;
using Consultologist.ZoomApp.Core.Meetings;
using Consultologist.ZoomApp.Core.Security;
using Consultologist.ZoomApp.Core.Transcript;
using Consultologist.ZoomApp.Core.Zoom;
using Consultologist.ZoomApp.Storage;
using Consultologist.ZoomApp.Zoom;

using Azure.Data.Tables;
using Azure.Identity;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// --- Microsoft Entra: sign the clinician in and call the engine AS them. ---
// A confidential web app (auth-code flow) that can acquire a delegated token for
// the engine's access_as_user scope directly — no On-Behalf-Of needed. #610
// refuses app-only tokens, so this is always the clinician's delegated token.
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddInMemoryTokenCaches();

// The app runs inside Zoom's cross-site iframe, so the session cookie must be
// SameSite=None + Secure or the browser drops it and the clinician never stays
// signed in. (Full in-iframe sign-in — correlation/nonce cookies, any pop-out —
// is confirmed against a live Zoom launch during provisioning.)
builder.Services.Configure<CookieAuthenticationOptions>(
    CookieAuthenticationDefaults.AuthenticationScheme,
    options =>
    {
        options.Cookie.SameSite = SameSiteMode.None;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization();
builder.Services.AddDataProtection();
builder.Services.AddControllersWithViews(); // brings in the Identity.Web UI sign-in/out endpoints

// --- The engine client (.Core), base URL from config, delegated bearer per call. ---
builder.Services.AddHttpClient<EngineApiClient>(client =>
{
    var apiHost = builder.Configuration["Engine:ApiHost"]
        ?? throw new InvalidOperationException("Engine:ApiHost is not configured (see appsettings.json).");
    client.BaseAddress = new Uri(apiHost.TrimEnd('/') + "/");
});

// --- Zoom: user-OAuth + transcript fetch; a per-clinician token store. ---
builder.Services.Configure<ZoomOptions>(builder.Configuration.GetSection(ZoomOptions.Section));
builder.Services.AddHttpClient<ZoomClient>();
builder.Services.AddScoped<ZoomTokenProvider>();

// --- Stores: durable (Azure Table, identity-only) when configured, else in-memory
//     for local/dev/test. The Zoom token store is encrypted at rest; the meeting ->
//     job map holds ids only (no PHI). The engine models no meeting, so the map is ours. ---
var tableServiceUri = builder.Configuration["Storage:TableServiceUri"];
if (!string.IsNullOrWhiteSpace(tableServiceUri))
{
    builder.Services.AddSingleton(new TableServiceClient(new Uri(tableServiceUri), new DefaultAzureCredential()));
    builder.Services.AddSingleton<IClinicianZoomTokens, TableClinicianZoomTokens>();
    builder.Services.AddSingleton<IMeetingJobMap, TableMeetingJobMap>();
}
else
{
    builder.Services.AddSingleton<IClinicianZoomTokens, InMemoryClinicianZoomTokens>();
    builder.Services.AddSingleton<IMeetingJobMap, InMemoryMeetingJobMap>();
}

// --- CSP: the webview is framed by the Zoom client (frame-ancestors); configurable. ---
builder.Services.Configure<CspOptions>(builder.Configuration.GetSection(CspOptions.Section));

// The verified meeting context (from the signed X-Zoom-App-Context), in an encrypted cookie.
builder.Services.AddSingleton<ZoomContextCookie>();

var app = builder.Build();

// The Home URL (and every response) carries the CSP so the panel loads framed in
// Zoom; set before static files so index.html carries it. Domain Allow List (the
// Marketplace-side twin) is in the README runbook.
var csp = CspPolicy.Build(app.Services.GetRequiredService<IOptions<CspOptions>>().Value);
app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] = csp;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

// On the Home-URL load Zoom sends the encrypted X-Zoom-App-Context; decrypt it
// (client secret = the key), check exp, and stash the server-verified meeting in
// an encrypted cookie the meeting endpoints trust over the browser's value.
var zoomSecret = app.Services.GetRequiredService<IOptions<ZoomOptions>>().Value.ClientSecret;
app.Use(async (context, next) =>
{
    if ((context.Request.Path == "/" || context.Request.Path == "/index.html")
        && context.Request.Headers.TryGetValue("X-Zoom-App-Context", out var header)
        && ZoomAppContext.TryDecrypt(header.ToString(), zoomSecret, out var zc)
        && zc is not null
        && !zc.IsExpired(DateTimeOffset.UtcNow))
    {
        context.RequestServices.GetRequiredService<ZoomContextCookie>().Write(context, zc);
    }

    await next();
});

app.UseDefaultFiles();   // serve wwwroot/index.html at "/" (the Zoom Home URL)
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var engineScope = app.Configuration["Engine:AccessAsUserScope"]
    ?? throw new InvalidOperationException("Engine:AccessAsUserScope is not configured.");
var transcriptSlot = app.Configuration["Engine:TranscriptInputSlot"]
    ?? throw new InvalidOperationException("Engine:TranscriptInputSlot is not configured.");

// ----- Zoom OAuth (Leg 2 connect) -----

// Send the clinician to Zoom to grant cloud_recording:read.
app.MapGet("/zoom/connect", (System.Security.Claims.ClaimsPrincipal user, ZoomClient zoom) =>
    Results.Redirect(zoom.BuildAuthorizeUrl(state: Clinician(user)))).RequireAuthorization();

// Zoom returns here with a code; store the clinician's token and return to the panel.
app.MapGet("/zoom/callback", async (
    string code, string? state, System.Security.Claims.ClaimsPrincipal user,
    ZoomClient zoom, IClinicianZoomTokens tokens, CancellationToken ct) =>
{
    var clinician = Clinician(user);
    var set = await zoom.ExchangeCodeAsync(code, ct);
    await tokens.StoreAsync(clinician, set, ct);
    return Results.Redirect("/");
}).RequireAuthorization();

// ----- The panel-facing API (the only surface the browser calls) -----

// The server-verified meeting (from the signed X-Zoom-App-Context) — the panel
// prefers this over the browser's getMeetingUUID.
app.MapGet("/api/meeting/context", (HttpContext http, ZoomContextCookie contextCookie) =>
{
    var verified = contextCookie.Read(http)?.Mid;
    return Results.Ok(new { meetingId = verified, inMeeting = !string.IsNullOrEmpty(verified) });
}).RequireAuthorization();

// Leg 1: the clinician's consults for this meeting.
app.MapGet("/api/meeting/{uuid}/consults", async (
    string uuid, HttpContext http, System.Security.Claims.ClaimsPrincipal user,
    EngineApiClient engine, IMeetingJobMap map, ZoomContextCookie contextCookie,
    ITokenAcquisition tokenAcquisition, CancellationToken ct) =>
{
    if (MeetingMismatch(contextCookie, http, uuid) is { } mismatch)
    {
        return mismatch;
    }

    var clinician = Clinician(user);
    var jobIds = await map.JobsForAsync(clinician, uuid, ct);
    if (jobIds.Count == 0)
    {
        return Results.Ok(Array.Empty<AccountJobSummaryResponse>());
    }

    var bearer = await tokenAcquisition.GetAccessTokenForUserAsync(new[] { engineScope }, user: user);
    var page = await engine.ListJobsAsync(bearer, limit: 50, ct: ct);
    var set = jobIds.ToHashSet(StringComparer.Ordinal);
    return Results.Ok(page.Jobs.Where(j => set.Contains(j.JobId)).ToArray());
}).RequireAuthorization();

// Leg 2: fetch the meeting's transcript from Zoom and start a consult as the clinician.
app.MapPost("/api/meeting/{uuid}/generate", async (
    string uuid, HttpContext http, System.Security.Claims.ClaimsPrincipal user,
    ZoomClient zoom, ZoomTokenProvider zoomTokens, EngineApiClient engine,
    IMeetingJobMap map, ZoomContextCookie contextCookie,
    ITokenAcquisition tokenAcquisition, CancellationToken ct) =>
{
    if (MeetingMismatch(contextCookie, http, uuid) is { } mismatch)
    {
        return mismatch;
    }

    var clinician = Clinician(user);

    var zoomToken = await zoomTokens.GetValidTokenAsync(clinician, ct);
    if (zoomToken is null)
    {
        return Results.Json(new { error = "zoom_not_connected" }, statusCode: StatusCodes.Status409Conflict);
    }

    var vtt = await zoom.GetTranscriptVttAsync(zoomToken.AccessToken, uuid, ct);
    if (string.IsNullOrWhiteSpace(vtt))
    {
        return Results.Json(new { error = "no_transcript" }, statusCode: StatusCodes.Status404NotFound);
    }

    var text = VttToText.ToText(vtt);
    var body = TranscriptSubmission.ForTranscript(text, transcriptSlot);
    var bearer = await tokenAcquisition.GetAccessTokenForUserAsync(new[] { engineScope }, user: user);
    var started = await engine.StartJobAsync(bearer, body, ct);
    await map.RecordAsync(clinician, uuid, started.JobId, ct);
    return Results.Ok(new { jobId = started.JobId, statusUrl = started.StatusUrl });
}).RequireAuthorization();

// Poll a job (status + deliverable) for the panel.
app.MapGet("/api/jobs/{jobId}", async (
    string jobId, System.Security.Claims.ClaimsPrincipal user,
    EngineApiClient engine, ITokenAcquisition tokenAcquisition, CancellationToken ct) =>
{
    var bearer = await tokenAcquisition.GetAccessTokenForUserAsync(new[] { engineScope }, user: user);
    var job = await engine.GetJobAsync(bearer, jobId, ct);
    return Results.Ok(job);
}).RequireAuthorization();

app.Run();

// The clinician's stable id — the Entra object id — keys both the Zoom token
// store and the meeting->job map.
static string Clinician(System.Security.Claims.ClaimsPrincipal user) =>
    user.GetObjectId() ?? throw new InvalidOperationException("No Entra object id on the signed-in user.");

// When Zoom signed a meeting context, the requested meeting must be that one —
// the browser cannot substitute another. Absent a verified context (local dev,
// or the header not sent) the request proceeds on the browser-supplied uuid.
static IResult? MeetingMismatch(ZoomContextCookie contextCookie, HttpContext http, string uuid)
{
    var verified = contextCookie.Read(http)?.Mid;
    return !string.IsNullOrEmpty(verified) && !string.Equals(verified, uuid, StringComparison.Ordinal)
        ? Results.Json(new { error = "meeting_context_mismatch" }, statusCode: StatusCodes.Status403Forbidden)
        : null;
}

// Exposed so the integration tests can host the app with WebApplicationFactory.
public partial class Program;
