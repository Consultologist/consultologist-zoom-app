namespace Consultologist.ZoomApp.Zoom;

/// <summary>The Zoom General app's OAuth + API configuration (the <c>Zoom</c>
/// config section). Secrets live in the git-ignored dev settings / App Service
/// settings, never in the checked-in <c>appsettings.json</c>.</summary>
public sealed class ZoomOptions
{
    public const string Section = "Zoom";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The OAuth redirect back to this app (e.g. <c>https://host/zoom/callback</c>).</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>User-OAuth scopes — the transcript needs <c>cloud_recording:read</c>.</summary>
    public string[] Scopes { get; set; } = { "cloud_recording:read" };

    public string AuthorizeEndpoint { get; set; } = "https://zoom.us/oauth/authorize";

    public string TokenEndpoint { get; set; } = "https://zoom.us/oauth/token";

    public string ApiBaseUrl { get; set; } = "https://api.zoom.us/v2/";
}
