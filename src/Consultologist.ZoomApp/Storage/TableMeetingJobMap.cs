using System.Text.Json;

using Azure.Data.Tables;

using Consultologist.ZoomApp.Core.Meetings;
using Consultologist.ZoomApp.Core.Storage;

namespace Consultologist.ZoomApp.Storage;

/// <summary>
/// A durable <see cref="IMeetingJobMap"/> on Azure Table storage: partition per
/// clinician, row per meeting, holding the job-id list (most-recent-first) as
/// JSON. Ids only — never transcript text or PHI — so it is not encrypted.
/// Identity-only auth (the injected <see cref="TableServiceClient"/>).
/// </summary>
public sealed class TableMeetingJobMap : IMeetingJobMap
{
    private const string TableName = "MeetingJobs";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TableClient _table;

    public TableMeetingJobMap(TableServiceClient service)
    {
        _table = service.GetTableClient(TableName);
        _table.CreateIfNotExists();
    }

    public async Task RecordAsync(string clinicianId, string meetingUuid, string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clinicianId);
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var (pk, rk) = (TableKeys.Encode(clinicianId), TableKeys.Encode(meetingUuid));
        var list = (await ReadAsync(pk, rk, ct).ConfigureAwait(false)).ToList();
        list.Remove(jobId);   // keep it, move to the front
        list.Insert(0, jobId);

        var entity = new TableEntity(pk, rk) { ["JobIds"] = JsonSerializer.Serialize(list, Json) };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> JobsForAsync(string clinicianId, string meetingUuid, CancellationToken ct = default) =>
        await ReadAsync(TableKeys.Encode(clinicianId), TableKeys.Encode(meetingUuid), ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<string>> ReadAsync(string pk, string rk, CancellationToken ct)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(pk, rk, cancellationToken: ct).ConfigureAwait(false);
        if (response.HasValue && response.Value!.TryGetValue("JobIds", out var value) && value is string json)
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? new List<string>();
        }

        return Array.Empty<string>();
    }
}
