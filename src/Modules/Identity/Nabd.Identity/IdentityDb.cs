using System.Data;
using Npgsql;

namespace Nabd.Identity;

/// <summary>
/// The Identity room's only door to Postgres — and it opens as the APP, never the owner.
/// Law chain: NFR-08 (12-factor, secrets from env) · plan/04 §10 (RLS bites apps, not owners)
/// · P0 spike (owner roles SKIP row security silently — decorative walls) · NOTES fail-shut decision.
/// The tenant discipline rides HERE: every scoped operation opens its transaction and sets
/// app.current_tenant right after BEGIN (pool-reuse discipline from the P0 lessons).
/// </summary>
public sealed class IdentityDb
{
    private readonly NpgsqlDataSource _dataSource;

    public IdentityDb()
    {
        var host = Environment.GetEnvironmentVariable("NABD_DB_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("NABD_DB_PORT") ?? "5432";
        var db   = Environment.GetEnvironmentVariable("NABD_DB_NAME") ?? "nabd";

        var user = Environment.GetEnvironmentVariable("NABD_APP_DB_USER")
                   ?? throw new InvalidOperationException("NABD_APP_DB_USER must be set (least-privilege app identity, 0005).");
        var pass = Environment.GetEnvironmentVariable("NABD_APP_DB_PASSWORD")
                   ?? throw new InvalidOperationException("NABD_APP_DB_PASSWORD must be set (NFR-08: 12-factor).");

        var builder = new NpgsqlDataSourceBuilder($"Host={host};Port={port};Database={db};Username={user};Password={pass}");
        _dataSource = builder.Build();
    }

    /// <summary>
    /// Opens a tenant-scoped transaction: BEGIN → SET LOCAL app.current_tenant — the ONLY
    /// shape any tenant-scoped query may use (springing leakage via pool-reused connections).
    /// </summary>
    public async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenTenantScopeAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await SetTenantAsync(conn, tenantId, ct);
        return (conn, tx);
    }

    /// <summary>
    /// Registration shape: the tenant row does not exist yet, but the wall still guards
    /// everything inside the transaction — so we SET the would-be tenant id FIRST,
    /// then insert (the tenants policy checks id = current_setting on the NEW row).
    /// </summary>
    public async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenRegistrationScopeAsync(
        Guid newTenantId, CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await SetTenantAsync(conn, newTenantId, ct);
        return (conn, tx);
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('app.current_tenant', @tid::text, true)", conn);
        cmd.Parameters.AddWithValue("tid", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
