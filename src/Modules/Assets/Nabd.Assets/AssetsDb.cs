using System.Data;
using Npgsql;

namespace Nabd.Assets;

/// <summary>
/// The Assets room's only door to Postgres — opens as the APP (nabd_app), never the owner.
/// Mirror of IdentityDb by law, not by accident: rooms share NOTHING but Contracts
/// (03 §4 rule 1), so each room owns its own DB door. Same env vars, same fail-shut
/// constructor, same tenant discipline: BEGIN → SET LOCAL app.current_tenant, always.
/// (If a P6 consolidation ever merges these doors, it changes the WALL LAW first — noted.)
/// </summary>
public sealed class AssetsDb
{
    private readonly NpgsqlDataSource _dataSource;

    public AssetsDb()
    {
        var host = Environment.GetEnvironmentVariable("NABD_DB_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("NABD_DB_PORT") ?? "5432";
        var db = Environment.GetEnvironmentVariable("NABD_DB_NAME") ?? "nabd";

        var user = Environment.GetEnvironmentVariable("NABD_APP_DB_USER")
                   ?? throw new InvalidOperationException("NABD_APP_DB_USER must be set (least-privilege app identity, 0012).");
        var pass = Environment.GetEnvironmentVariable("NABD_APP_DB_PASSWORD")
                   ?? throw new InvalidOperationException("NABD_APP_DB_PASSWORD must be set (NFR-08: 12-factor).");

        var builder = new NpgsqlDataSourceBuilder($"Host={host};Port={port};Database={db};Username={user};Password={pass}");
        _dataSource = builder.Build();
    }

    /// <summary>
    /// Opens a tenant-scoped transaction: BEGIN → SET LOCAL app.current_tenant — the ONLY
    /// shape any tenant-scoped query may use (the 0012 policies read exactly this setting;
    /// forgetting it = zero rows, fail-shut — the wall we built in spec 01).
    /// </summary>
    public async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenTenantScopeAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('app.current_tenant', @tid::text, true)", conn);
        cmd.Parameters.AddWithValue("tid", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        return (conn, tx);
    }

    /// <summary>
    // the lookup rides the 0014 SECURITY DEFINER function — the narrow audited exception.
    /// </summary>
    public async Task<NpgsqlConnection> OpenPreAuthConnectionAsync(CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        return conn;
    }
}