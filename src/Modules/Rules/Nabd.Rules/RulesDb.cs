using System.Data;
using Npgsql;

namespace Nabd.Rules;

/// <summary>
/// The Rules room's only door to Postgres (P4 spec 03) — mirror of AssetsDb by
/// law, not by accident (03 §4 rule 1: rooms share NOTHING but Contracts, so
/// each room owns its DB door). Same env vars, same fail-shut constructor,
/// same tenant discipline: BEGIN → SET LOCAL app.current_tenant, always.
/// </summary>
public sealed class RulesDb
{
    private readonly NpgsqlDataSource _dataSource;

    public RulesDb()
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
    /// Opens a tenant-scoped transaction: BEGIN → SET LOCAL app.current_tenant —
    /// the ONLY shape any tenant-scoped query may use (the 0021 policy reads
    /// exactly this setting; forgetting it = zero rows, fail-shut).
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
}