using System.Collections.Concurrent;

namespace Consultologist.ZoomApp.Core.Meetings;

/// <summary>
/// The satellite's own <c>meetingUUID → job ids</c> association, per clinician.
/// The engine models no meeting/visit (docs/ZOOM_SATELLITE_SPIKE.md § 5), so
/// "the documents for <em>this</em> meeting" is a mapping the app keeps: it
/// records the job it started from a meeting and, for Leg 1, filters the
/// clinician's engine job History to that meeting. It holds ids only — never
/// transcript text or PHI.
/// </summary>
public interface IMeetingJobMap
{
    /// <summary>Remember that <paramref name="jobId"/> was started from
    /// <paramref name="meetingUuid"/> by <paramref name="clinicianId"/>. Idempotent.</summary>
    Task RecordAsync(string clinicianId, string meetingUuid, string jobId, CancellationToken ct = default);

    /// <summary>The clinician's job ids for a meeting, most-recently recorded first.</summary>
    Task<IReadOnlyList<string>> JobsForAsync(string clinicianId, string meetingUuid, CancellationToken ct = default);
}

/// <summary>
/// In-memory <see cref="IMeetingJobMap"/> — the scaffold default, matching the
/// copilot-agent's <c>MemoryStorage</c> note: correct for one process, lost on
/// restart. A deployed app swaps in a durable store (Azure Table/Blob) keyed the
/// same way. Keyed by clinician then meeting so one clinician's map is isolated.
/// </summary>
public sealed class InMemoryMeetingJobMap : IMeetingJobMap
{
    private readonly ConcurrentDictionary<(string Clinician, string Meeting), List<string>> _byMeeting = new();

    public Task RecordAsync(string clinicianId, string meetingUuid, string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clinicianId);
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var list = _byMeeting.GetOrAdd((clinicianId, meetingUuid), static _ => new List<string>());
        lock (list)
        {
            list.Remove(jobId);       // keep it, but move it to the front
            list.Insert(0, jobId);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> JobsForAsync(string clinicianId, string meetingUuid, CancellationToken ct = default)
    {
        if (_byMeeting.TryGetValue((clinicianId, meetingUuid), out var list))
        {
            lock (list)
            {
                return Task.FromResult<IReadOnlyList<string>>(list.ToArray());
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
