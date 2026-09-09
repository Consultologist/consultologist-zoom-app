using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

namespace Consultologist.ZoomApp.Zoom;

/// <summary>A clinician's Zoom user-OAuth tokens.</summary>
public sealed record ZoomTokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAtUtc);

/// <summary>Where a clinician's Zoom tokens live, keyed by their Entra id. The
/// scaffold keeps them in memory (lost on restart); a deployed app swaps in a
/// durable, encrypted per-user store. Zoom tokens never reach the browser.</summary>
public interface IClinicianZoomTokens
{
    void Store(string clinicianId, ZoomTokenSet tokens);

    ZoomTokenSet? Get(string clinicianId);
}

/// <inheritdoc/>
public sealed class InMemoryClinicianZoomTokens : IClinicianZoomTokens
{
    private readonly ConcurrentDictionary<string, ZoomTokenSet> _tokens = new(StringComparer.Ordinal);

    public void Store(string clinicianId, ZoomTokenSet tokens) => _tokens[clinicianId] = tokens;

    public ZoomTokenSet? Get(string clinicianId) => _tokens.TryGetValue(clinicianId, out var t) ? t : null;
}

/// <summary>
/// Talks to Zoom as the clinician: the user-OAuth authorization-code exchange and
/// the cloud-recording read that yields the meeting's speaker-labeled VTT. This is
/// the only place the transcript bytes exist before they are handed to the engine;
/// they are never persisted and never sent to the browser.
/// </summary>
public sealed class ZoomClient(HttpClient http, IOptions<ZoomOptions> options)
{
    private readonly ZoomOptions _options = options.Value;

    /// <summary>The Zoom consent URL to send the clinician to (Leg 2 connect).</summary>
    public string BuildAuthorizeUrl(string state)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = string.Join(' ', _options.Scopes),
            ["state"] = state,
        };
        var q = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? string.Empty)}"));
        return $"{_options.AuthorizeEndpoint}?{q}";
    }

    /// <summary>Exchange the authorization code for the clinician's Zoom tokens.</summary>
    public async Task<ZoomTokenSet> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
        });

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty Zoom token response.");

        return new ZoomTokenSet(
            token.AccessToken,
            token.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600));
    }

    /// <summary>
    /// The meeting's speaker-labeled VTT transcript, or null when the meeting has
    /// no transcript recording (transcription is the clinic's Zoom feature; we run
    /// no STT). Zoom double-encodes meeting UUIDs that start with '/' or contain
    /// '//'.
    /// </summary>
    public async Task<string?> GetTranscriptVttAsync(string accessToken, string meetingUuid, CancellationToken ct = default)
    {
        var recordings = await SendAsync<RecordingsResponse>(
            HttpMethod.Get, $"meetings/{EncodeMeetingId(meetingUuid)}/recordings", accessToken, ct).ConfigureAwait(false);

        var transcript = recordings?.RecordingFiles?.FirstOrDefault(f =>
            string.Equals(f.FileType, "TRANSCRIPT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.FileExtension, "VTT", StringComparison.OrdinalIgnoreCase));

        if (transcript?.DownloadUrl is not { Length: > 0 } url)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string relativeUrl, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(_options.ApiBaseUrl), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
    }

    // A UUID with a leading '/' or an embedded '//' must be double URL-encoded.
    private static string EncodeMeetingId(string uuid) =>
        uuid.StartsWith('/') || uuid.Contains("//", StringComparison.Ordinal)
            ? Uri.EscapeDataString(Uri.EscapeDataString(uuid))
            : Uri.EscapeDataString(uuid);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record RecordingsResponse(
        [property: JsonPropertyName("recording_files")] IReadOnlyList<RecordingFile>? RecordingFiles);

    private sealed record RecordingFile(
        [property: JsonPropertyName("file_type")] string? FileType,
        [property: JsonPropertyName("file_extension")] string? FileExtension,
        [property: JsonPropertyName("download_url")] string? DownloadUrl);
}
