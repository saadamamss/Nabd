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
public sealed class IngestionEdges(AssetsDb db) : IDeviceChannels, IIngestionIdentity
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
}
