using Npgsql;

namespace Nabd.Admin;

/// <summary>
/// The Admin room's one door to Postgres — and it is THE bypass login
/// (plan/04 §10 line 143: separate role, logged separately, used by the
/// Admin module ONLY). Fail-shut like IdentityDb: missing env = constructor
/// throws, nothing ever runs through a half-bound admin lane.
/// No tenant scoping lives here BY DESIGN: this connection IS the audited
/// exception; the audit line is written per-call in PlatformTenantAdmin.
/// </summary>
public sealed class AdminDb
{
    private readonly NpgsqlDataSource _dataSource;

    public AdminDb()
    {
        var host = Environment.GetEnvironmentVariable("NABD_DB_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("NABD_DB_PORT") ?? "5432";
        var db   = Environment.GetEnvironmentVariable("NABD_DB_NAME") ?? "nabd";

        var user = Environment.GetEnvironmentVariable("NABD_ADMIN_DB_USER")
                   ?? throw new InvalidOperationException("NABD_ADMIN_DB_USER must be set (the ONE BYPASSRLS login, migration 0011).");
        var pass = Environment.GetEnvironmentVariable("NABD_ADMIN_DB_PASSWORD")
                   ?? throw new InvalidOperationException("NABD_ADMIN_DB_PASSWORD must be set (NFR-08: 12-factor).");

        var builder = new NpgsqlDataSourceBuilder($"Host={host};Port={port};Database={db};Username={user};Password={pass}");
        _dataSource = builder.Build();
    }

    public async Task<NpgsqlConnection> OpenAdminConnectionAsync(CancellationToken ct = default) =>
        await _dataSource.OpenConnectionAsync(ct);
}