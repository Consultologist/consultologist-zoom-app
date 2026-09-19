namespace Consultologist.ZoomApp.Core.Zoom;

/// <summary>
/// When a stored Zoom access token should be refreshed. Zoom access tokens live
/// ~1 hour; refresh a little early (skew) so a token does not expire mid-request.
/// </summary>
public static class ZoomTokenExpiry
{
    public static readonly TimeSpan DefaultSkew = TimeSpan.FromMinutes(2);

    public static bool NeedsRefresh(DateTimeOffset expiresAtUtc, DateTimeOffset now, TimeSpan? skew = null) =>
        expiresAtUtc - (skew ?? DefaultSkew) <= now;
}
