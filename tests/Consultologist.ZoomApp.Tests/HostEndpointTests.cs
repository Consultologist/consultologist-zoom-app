using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

using Consultologist.ZoomApp.Zoom;

using Microsoft.AspNetCore.Mvc.Testing;

using Xunit;

namespace Consultologist.ZoomApp.Tests;

/// <summary>
/// Host-level tests over the panel-facing endpoints, with a test Entra principal,
/// a stubbed ITokenAcquisition, and stub engine/Zoom HttpClients (see ZoomAppFactory).
/// Each test gets a fresh factory so the in-memory stores are isolated.
/// </summary>
public class HostEndpointTests
{
    private const string ClientSecret = "test-zoom-client-secret";

    [Fact]
    public async Task Home_CarriesTheCspHeader()
    {
        await using var factory = new ZoomAppFactory();
        var response = await factory.CreateClient().GetAsync("/");

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        Assert.Contains("frame-ancestors https://zoom.us", string.Join(' ', values!));
    }

    [Fact]
    public async Task Home_CapturesTheZoomContext_AndTheContextEndpointReturnsIt()
    {
        await using var factory = new ZoomAppFactory();
        var client = HttpsClient(factory); // the zoom_ctx cookie is Secure

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Zoom-App-Context", MakeContext("MEETING-1"));
        await client.SendAsync(request); // sets the zoom_ctx cookie (the client persists cookies)

        var context = await client.GetFromJsonAsync<ContextResult>("/api/meeting/context");
        Assert.Equal("MEETING-1", context!.MeetingId);
        Assert.True(context.InMeeting);
    }

    [Fact]
    public async Task Consults_FiltersHistoryToTheMeetingsRecordedJobs()
    {
        await using var factory = new ZoomAppFactory();
        await factory.Map.RecordAsync("clinician-1", "MEETING-2", "job-A");

        var jobs = await factory.CreateClient().GetFromJsonAsync<JobSummary[]>("/api/meeting/MEETING-2/consults");

        Assert.Single(jobs!); // engine history also has job-B, but it is not this meeting's
        Assert.Equal("job-A", jobs![0].JobId);
    }

    [Fact]
    public async Task Generate_FetchesTheTranscript_SubmitsIt_AndRecordsTheJob()
    {
        await using var factory = new ZoomAppFactory();
        await factory.Tokens.StoreAsync("clinician-1", new ZoomTokenSet("z", "r", DateTimeOffset.UtcNow.AddHours(1)));

        var response = await factory.CreateClient().PostAsync("/api/meeting/MEETING-3/generate", content: null);
        response.EnsureSuccessStatusCode();

        Assert.Contains("job-STARTED", await factory.Map.JobsForAsync("clinician-1", "MEETING-3"));
    }

    [Fact]
    public async Task Generate_Is409_WhenZoomNotConnected()
    {
        await using var factory = new ZoomAppFactory();
        var response = await factory.CreateClient().PostAsync("/api/meeting/MEETING-4/generate", content: null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Meeting_Mismatch_Is403()
    {
        await using var factory = new ZoomAppFactory();
        var client = HttpsClient(factory); // the zoom_ctx cookie is Secure

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Zoom-App-Context", MakeContext("MEETING-REAL"));
        await client.SendAsync(request);

        var response = await client.GetAsync("/api/meeting/MEETING-SPOOFED/consults");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static HttpClient HttpsClient(ZoomAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/") });

    private sealed record ContextResult(string? MeetingId, bool InMeeting);
    private sealed record JobSummary(string JobId, string Status);

    private static string MakeContext(string meetingId)
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
        var json = $$"""{"uid":"clinician-1","typ":"meeting","mid":"{{meetingId}}","exp":{{exp}}}""";
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(ClientSecret));
        var plaintext = Encoding.UTF8.GetBytes(json);
        var iv = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16))
        {
            gcm.Encrypt(iv, plaintext, ct, tag, Array.Empty<byte>());
        }

        using var raw = new MemoryStream();
        raw.WriteByte((byte)iv.Length);
        raw.Write(iv);
        var aadLen = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(aadLen, 0);
        raw.Write(aadLen);
        var ctLen = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(ctLen, (uint)ct.Length);
        raw.Write(ctLen);
        raw.Write(ct);
        raw.Write(tag);
        return Convert.ToBase64String(raw.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
