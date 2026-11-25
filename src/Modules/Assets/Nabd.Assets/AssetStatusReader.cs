using Npgsql;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Assets;

/// <summary>
/// P5 spec 03: the status read model over `assets.asset_status_by_tenant` (0028).
/// The SQL function is the one place the derivation lives (B1/B2); this class only
/// carries the tenant scope and the two lanes' rules.
/// </summary>
public sealed class AssetStatusReader(AssetsDb db) : IAssetStatusReader
{
    /// <summary>Door lane: permission asserted before any data access (layer 2, 07 §5).</summary>
    public async Task<Result<IReadOnlyList<AssetStatusSnapshot>>> ListAsync(
        CallerContext acting, bool includeArchived = false, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AssetRead, acting.TenantId);
        if (forbidden is not null)
            return Result<IReadOnlyList<AssetStatusSnapshot>>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var rows = new List<AssetStatusSnapshot>();

            await using (var cmd = new NpgsqlCommand("""
                SELECT asset_id, status, since, alert_id, severity, last_seen_at, is_active
                FROM assets.asset_status_by_tenant(@tenant)
                WHERE (@includeArchived OR is_active)
                ORDER BY asset_id
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("tenant", acting.TenantId);
                cmd.Parameters.AddWithValue("includeArchived", includeArchived);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add(Read(reader));
            }

            await tx.CommitAsync(ct);
            return Result<IReadOnlyList<AssetStatusSnapshot>>.Success(rows);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// System lane (the hub's re-read, B5): the tenant is the committed alert's own
    /// tenant, so the function can only return that tenant's asset. A null answer
    /// means "nothing to broadcast" — never an error into the listen loop.
    /// </summary>
    public async Task<AssetStatusSnapshot?> GetForTenantAsync(
        Guid tenantId, Guid assetId, CancellationToken ct = default)
    {
        var (conn, tx) = await db.OpenTenantScopeAsync(tenantId, ct);
        try
        {
            AssetStatusSnapshot? row = null;

            await using (var cmd = new NpgsqlCommand("""
                SELECT asset_id, status, since, alert_id, severity, last_seen_at, is_active
                FROM assets.asset_status_by_tenant(@tenant)
                WHERE asset_id = @asset
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("tenant", tenantId);
                cmd.Parameters.AddWithValue("asset", assetId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    row = Read(reader);
            }

            await tx.CommitAsync(ct);
            return row;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private static AssetStatusSnapshot Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
        r.IsDBNull(3) ? null : r.GetGuid(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.GetBoolean(6));
}
