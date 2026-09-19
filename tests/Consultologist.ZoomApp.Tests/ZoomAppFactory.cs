using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

using Consultologist.ZoomApp.Core.Engine;
using Consultologist.ZoomApp.Core.Meetings;
using Consultologist.ZoomApp.Zoom;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

using NSubstitute;
using NSubstitute.Extensions;

namespace Consultologist.ZoomApp.Tests;

/// <summary>
/// Hosts the app for endpoint tests: a test Entra principal (clinician-1), a
/// stubbed ITokenAcquisition, and stub engine/Zoom HttpClients. Uses the in-memory
/// stores (no Storage:TableServiceUri), exposed for seeding.
/// </summary>
public sealed class ZoomAppFactory : WebApplicationFactory<Program>
{
    public IClinicianZoomTokens Tokens => Services.GetRequiredService<IClinicianZoomTokens>();
    public IMeetingJobMap Map => Services.GetRequiredService<IMeetingJobMap>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Engine:ApiHost"] = "https://engine.test/api",
            ["Engine:AccessAsUserScope"] = "api://engine/access_as_user",
            ["Engine:TranscriptInputSlot"] = "consult_draft",
            ["Zoom:ClientId"] = "zoom-client",
            ["Zoom:ClientSecret"] = "test-zoom-client-secret",
            ["Zoom:RedirectUri"] = "https://localhost/zoom/callback",
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:ClientId"] = "11111111-1111-1111-1111-111111111111",
            ["AzureAd:CallbackPath"] = "/signin-oidc",
        }));

        builder.ConfigureTestServices(services =>
        {
            // Authenticate every request as a fixed clinician.
            services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });

            // The engine bearer is stubbed — no real Entra token acquisition.
            var tokenAcquisition = Substitute.For<ITokenAcquisition>();
            tokenAcquisition.ReturnsForAll(Task.FromResult("test-bearer"));
            services.RemoveAll<ITokenAcquisition>();
            services.AddSingleton(tokenAcquisition);

            // Stub the engine and Zoom HTTP calls.
            services.AddHttpClient<EngineApiClient>().ConfigurePrimaryHttpMessageHandler(() => new RoutingHandler(EngineStub));
            services.AddHttpClient<ZoomClient>().ConfigurePrimaryHttpMessageHandler(() => new RoutingHandler(ZoomStub));

            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        });
    }

    private static HttpResponseMessage EngineStub(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path.EndsWith("/Account/Jobs", StringComparison.Ordinal))
        {
            return Json(new
            {
                jobs = new[]
                {
                    new { jobId = "job-A", status = "Completed", createdAtUtc = DateTimeOffset.UtcNow },
                    new { jobId = "job-B", status = "Completed", createdAtUtc = DateTimeOffset.UtcNow },
                },
                continuationToken = (string?)null,
            });
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/ConsultGenerationJobs", StringComparison.Ordinal))
        {
            return Json(new { jobId = "job-STARTED", statusUrl = "https://engine.test/api/ConsultGenerationJobs/job-STARTED" }, HttpStatusCode.Accepted);
        }

        if (request.Method == HttpMethod.Get && path.Contains("/ConsultGenerationJobs/", StringComparison.Ordinal))
        {
            return Json(new { jobId = path[(path.LastIndexOf('/') + 1)..], status = "Completed" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage ZoomStub(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath.EndsWith("/recordings", StringComparison.Ordinal))
        {
            return Json(new
            {
                recording_files = new[]
                {
                    new { file_type = "TRANSCRIPT", file_extension = "VTT", download_url = "https://zoom.us/rec/dl/transcript" },
                },
            });
        }

        if (uri.AbsolutePath.Contains("/rec/dl/", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("WEBVTT\n\n00:00:00.000 --> 00:00:02.000\n<v Dr Smith>Hello.</v>\n", Encoding.UTF8, "text/vtt"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "clinician-1"),
                new Claim(ClaimTypes.NameIdentifier, "clinician-1"),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }
}
