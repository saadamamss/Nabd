namespace Nabd.Assets;

using Npgsql;
using Nabd.Ingestion.Contracts;

/// <summary>
/// The Assets room implements the Ingestion pipeline's two edges (P3 spec 03).
/// LEGAL wall-wise: a room's implementation may reference OTHER rooms'
/// CONTRACTS (03 §4 rule 1) — and the data being read is THIS room's schema
/// (assets.devices, assets.channel_definitions), so the edge belongs here, not
/// in Ingestion. Both reads are PRE-AUTH by design (the broker's doors already
/// proved identity; this is a lookup, not a caller-scoped operation — the same
/// shape as BrokerAuthenticateAsync/VerifyTokenAsync).
///
/// BOTH lookups ride SECURITY DEFINER functions (0016 + 0017) — 0012's RLS
/// policy needs app.current_tenant, which a pre-auth connection never sets, so
/// a direct SELECT would always answer ZERO rows. Same narrow-exception law as
/// 0014: the RLS wall stays absolute for direct access; the functions are the
/// only windows, each one fixed SQL, one parameter, no table access beyond it.
/// </summary>
public sealed class IngestionEdges(AssetsDb db) : IDeviceChannels, IIngestionIdentity, IIngestionSetResolver
{
    public async Task<IReadOnlySet<string>?> GetChannelKeysAsync(Guid deviceId, CancellationToken ct = default)
    {
        var conn = await db.OpenPreAuthConnectionAsync(ct);
        try
        {
            // 0017: one SECURITY DEFINER function — the template catalog behind
            // RLS, filtered to active + un-revoked. Empty array = unknown or
            // unusable device; null keeps the pipeline's UNIFORM rejection (a
            // device that can lawfully send nothing has no lawful readings).
            await using var cmd = new NpgsqlCommand(
                "SELECT assets.device_channel_keys_by_id(@d)", conn);
            cmd.Parameters.AddWithValue("d", deviceId);

            var raw = await cmd.ExecuteScalarAsync(ct);
            var keys = raw as string[] ?? [];
            return keys.Length == 0 ? null : new HashSet<string>(keys);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    public async Task<Guid?> ResolveTenantIdAsync(Guid deviceId, CancellationToken ct = default)
    {
        var conn = await db.OpenPreAuthConnectionAsync(ct);
        try
        {
            // 0016 (pre-existing): the EMQX ACL door's tenant lookup. Reusing it
            // costs zero new SQL — the same function serves both doors' needs.
            await using var cmd = new NpgsqlCommand(
                "SELECT assets.device_tenant_by_id(@d)", conn);
            cmd.Parameters.AddWithValue("d", deviceId);

            var raw = await cmd.ExecuteScalarAsync(ct);
            return raw is Guid tenant ? tenant : null;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    public async Task<IReadOnlyDictionary<Guid, DeviceCatalogRow>> ResolveDevicesAsync(
        IEnumerable<Guid> deviceIds, CancellationToken ct = default)
    {
        var ids = deviceIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, DeviceCatalogRow>();

        var conn = await db.OpenPreAuthConnectionAsync(ct);
        try
        {
            // 0020: the set-based twin — ONE call resolves the WHOLE flush
            // (fact 6's resolver half). Absent from the answer = unknown /
            // revoked / deactivated; present with EMPTY keys = no lawful
            // channels. The flush reads BOTH uniformly (anti-enumeration).
            await using var cmd = new NpgsqlCommand(
                "SELECT device_id, tenant_id, channel_keys FROM assets.device_resolve_by_ids(@ids)", conn);
            cmd.Parameters.AddWithValue("ids", ids);

            var resolved = new Dictionary<Guid, DeviceCatalogRow>(ids.Length);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var deviceId = reader.GetGuid(0);
                var keys = reader.GetFieldValue<string[]>(2);
                resolved[deviceId] = new DeviceCatalogRow(
                    deviceId, reader.GetGuid(1),
                    keys.Length == 0 ? new HashSet<string>() : new HashSet<string>(keys));
            }
            return resolved;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
}
