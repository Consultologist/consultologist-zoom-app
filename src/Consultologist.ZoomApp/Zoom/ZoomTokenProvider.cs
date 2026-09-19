using Consultologist.ZoomApp.Core.Zoom;

namespace Consultologist.ZoomApp.Zoom;

/// <summary>
/// A clinician's currently-valid Zoom token: reads the stored set and, when it is
/// at or near expiry, refreshes it and persists the new set. Returns null when the
/// clinician has not connected Zoom (Leg 2 then asks them to).
/// </summary>
public sealed class ZoomTokenProvider(IClinicianZoomTokens store, ZoomClient zoom, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ZoomTokenSet?> GetValidTokenAsync(string clinicianId, CancellationToken ct = default)
    {
        var set = await store.GetAsync(clinicianId, ct).ConfigureAwait(false);
        if (set is null)
        {
            return null;
        }

        if (set.RefreshToken is null || !ZoomTokenExpiry.NeedsRefresh(set.ExpiresAtUtc, _clock.GetUtcNow()))
        {
            return set;
        }

        var refreshed = await zoom.RefreshAsync(set.RefreshToken, ct).ConfigureAwait(false);
        await store.StoreAsync(clinicianId, refreshed, ct).ConfigureAwait(false);
        return refreshed;
    }
}
