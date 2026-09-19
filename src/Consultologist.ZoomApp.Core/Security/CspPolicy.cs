namespace Consultologist.ZoomApp.Core.Security;

/// <summary>
/// The Content-Security-Policy the Home URL serves. A Zoom General app runs as a
/// webview framed by the Zoom client, so <c>frame-ancestors</c> must permit Zoom
/// (and nothing else — the app is never framed elsewhere). The panel loads the
/// Zoom Apps SDK (script) and calls only the satellite's own origin (the app is
/// backend-mediated — the browser never calls the engine, so no engine origin is
/// needed here); Entra sign-in is a top-level navigation to login.microsoftonline.com.
/// The framing/script origins are configurable so a deployment can tighten them.
/// </summary>
public sealed class CspOptions
{
    public const string Section = "Csp";

    /// <summary>Who may frame the app — the Zoom client only.</summary>
    public IReadOnlyList<string> FrameAncestors { get; set; } =
        new[] { "https://zoom.us", "https://*.zoom.us", "https://*.zoomgov.com" };

    /// <summary>Script sources — self plus the Zoom Apps SDK CDN (until the SDK is bundled).</summary>
    public IReadOnlyList<string> ScriptSrc { get; set; } =
        new[] { "'self'", "https://cdn.jsdelivr.net" };
}

/// <summary>Builds the CSP header value from <see cref="CspOptions"/> — pure and testable.</summary>
public static class CspPolicy
{
    public static string Build(CspOptions options)
    {
        var scriptSrc = string.Join(' ', options.ScriptSrc);
        var frameAncestors = string.Join(' ', options.FrameAncestors);
        return string.Join("; ", new[]
        {
            "default-src 'self'",
            $"script-src {scriptSrc}",
            "connect-src 'self'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "base-uri 'self'",
            "form-action 'self' https://login.microsoftonline.com https://zoom.us",
            $"frame-ancestors {frameAncestors}",
        });
    }
}
