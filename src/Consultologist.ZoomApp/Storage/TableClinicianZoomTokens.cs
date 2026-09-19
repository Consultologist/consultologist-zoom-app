using Azure;
using Azure.Data.Tables;

using Consultologist.ZoomApp.Core.Storage;
using Consultologist.ZoomApp.Zoom;

using Microsoft.AspNetCore.DataProtection;

namespace Consultologist.ZoomApp.Storage;

/// <summary>
/// A durable per-clinician Zoom-token store on Azure Table storage, encrypted at
/// rest (Data Protection). One entity per clinician; the token set is never stored
/// in the clear. Identity-only auth (the injected <see cref="TableServiceClient"/>
/// carries a managed identity — no shared keys).
/// </summary>
public sealed class TableClinicianZoomTokens : IClinicianZoomTokens
{
    private const string TableName = "ZoomTokens";
    private const string Partition = "tokens";

    private readonly TableClient _table;
    private readonly IDataProtector _protector;

    public TableClinicianZoomTokens(TableServiceClient service, IDataProtectionProvider provider)
    {
        _table = service.GetTableClient(TableName);
        _table.CreateIfNotExists();
        _protector = provider.CreateProtector("Consultologist.ZoomApp.ZoomToken.v1");
    }

    public async Task StoreAsync(string clinicianId, ZoomTokenSet tokens, CancellationToken ct = default)
    {
        var entity = new TableEntity(Partition, TableKeys.Encode(clinicianId))
        {
            ["Protected"] = ProtectedTokenCodec.Encode(_protector, tokens),
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct).ConfigureAwait(false);
    }

    public async Task<ZoomTokenSet?> GetAsync(string clinicianId, CancellationToken ct = default)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(Partition, TableKeys.Encode(clinicianId), cancellationToken: ct).ConfigureAwait(false);
        return response.HasValue && response.Value!.TryGetValue("Protected", out var value) && value is string s
            ? ProtectedTokenCodec.Decode(_protector, s)
            : null;
    }
}
