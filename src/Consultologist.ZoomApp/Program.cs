using Consultologist.ZoomApp.Core.Engine;
using Consultologist.ZoomApp.Core.Meetings;
using Consultologist.ZoomApp.Core.Transcript;
using Consultologist.ZoomApp.Zoom;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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

builder.Services.AddAuthorization();
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
builder.Services.AddSingleton<IClinicianZoomTokens, InMemoryClinicianZoomTokens>();

// --- The satellite's own meeting -> job map (the engine models no meeting). ---
builder.Services.AddSingleton<IMeetingJobMap, InMemoryMeetingJobMap>();

var app = builder.Build();

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
    tokens.Store(clinician, set);
    return Results.Redirect("/");
}).RequireAuthorization();

// ----- The panel-facing API (the only surface the browser calls) -----

// Leg 1: the clinician's consults for this meeting.
app.MapGet("/api/meeting/{uuid}/consults", async (
    string uuid, System.Security.Claims.ClaimsPrincipal user,
    EngineApiClient engine, IMeetingJobMap map, ITokenAcquisition tokenAcquisition, CancellationToken ct) =>
{
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
    string uuid, System.Security.Claims.ClaimsPrincipal user,
    ZoomClient zoom, IClinicianZoomTokens tokens, EngineApiClient engine,
    IMeetingJobMap map, ITokenAcquisition tokenAcquisition, CancellationToken ct) =>
{
    var clinician = Clinician(user);

    var zoomToken = tokens.Get(clinician);
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
