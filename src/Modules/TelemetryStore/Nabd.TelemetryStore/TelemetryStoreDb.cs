namespace Nabd.TelemetryStore;

using Npgsql;

/// <summary>
/// The TelemetryStore room's only door to Postgres — opens as the APP (nabd_app),
/// never the owner. Mirror of AssetsDb by law (03 §4 rule 1: rooms share NOTHING
/// but Contracts, each room owns its own DB door). No tenant scope here BY DESIGN:
/// telemetry.readings carries NO RLS policy (0003) — its wall is the IDENTITY KEY
/// and the pipeline in front of the store; every row arrives pre-validated with
/// its tenant stamped. The 0018 grants are this role's whole footprint on telemetry.
/// </summary>
public sealed class TelemetryStoreDb
{
    private readonly NpgsqlDataSource _dataSource;

    public TelemetryStoreDb()
    {
        var host = Environment.GetEnvironmentVariable("NABD_DB_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("NABD_DB_PORT") ?? "5432";
        var db = Environment.GetEnvironmentVariable("NABD_DB_NAME") ?? "nabd";

        var user = Environment.GetEnvironmentVariable("NABD_APP_DB_USER")
                   ?? throw new InvalidOperationException("NABD_APP_DB_USER must be set (least-privilege app identity, 0018).");
        var pass = Environment.GetEnvironmentVariable("NABD_APP_DB_PASSWORD")
                   ?? throw new InvalidOperationException("NABD_APP_DB_PASSWORD must be set (NFR-08: 12-factor).");

        var builder = new NpgsqlDataSourceBuilder($"Host={host};Port={port};Database={db};Username={user};Password={pass}");
        _dataSource = builder.Build();
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        return conn;
    }
}
