namespace Nabd.Notifications;

using System.Data;
using Npgsql;

/// <summary>
/// The Notifications room's only door to Postgres (same shape as IdentityDb/AlertingDb:
/// app identity, env-driven, fail-shut). Two lanes and no third:
///   · OpenTenantScopeAsync   — writes the send log (RLS on alerting.notification_log)
///   · OpenPlatformReadAsync  — reads channel_config/message_templates (platform rows)
/// </summary>
public sealed class NotificationsDb
{
    private readonly NpgsqlDataSource _dataSource;

    public NotificationsDb()
    {
        var host = Environment.GetEnvironmentVariable("NABD_DB_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("NABD_DB_PORT") ?? "5432";
        var db   = Environment.GetEnvironmentVariable("NABD_DB_NAME") ?? "nabd";

        var user = Environment.GetEnvironmentVariable("NABD_APP_DB_USER")
                   ?? throw new InvalidOperationException("NABD_APP_DB_USER must be set (least-privilege app identity).");
        var pass = Environment.GetEnvironmentVariable("NABD_APP_DB_PASSWORD")
                   ?? throw new InvalidOperationException("NABD_APP_DB_PASSWORD must be set (NFR-08).");

        _dataSource = new NpgsqlDataSourceBuilder(
            $"Host={host};Port={port};Database={db};Username={user};Password={pass}").Build();
    }

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

    public ValueTask<NpgsqlConnection> OpenPlatformReadAsync(CancellationToken ct = default) =>
        _dataSource.OpenConnectionAsync(ct);
}
