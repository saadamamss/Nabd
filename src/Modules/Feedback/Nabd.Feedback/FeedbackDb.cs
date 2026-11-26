using Npgsql;

namespace Nabd.Feedback;

/// <summary>
/// The Feedback room's only door to Postgres — opens as the APP (nabd_app), never
/// the owner. Mirror of AssetsDb by law, not by accident: rooms share NOTHING but
/// Contracts (03 §4 rule 1), so each room owns its door.
///
/// One deliberate difference from the tenant-scoped doors: there is NO
/// `SET LOCAL app.current_tenant` here. The room's only write path is
/// `feedback.submit_message`, a SECURITY DEFINER function that runs as its owner —
/// the app role has no SELECT/UPDATE/DELETE on the table at all (0030). The
/// tenant wall is therefore the function's own `(p_user, p_tenant)` check from
/// the token plus the table's INSERT policy, not a session GUC a definer would
/// bypass anyway.
/// </summary>
public sealed class FeedbackDb
{
    private readonly NpgsqlDataSource _dataSource;

    public FeedbackDb()
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

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default) =>
        await _dataSource.OpenConnectionAsync(ct);
}
