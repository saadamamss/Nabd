using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Nabd.Identity;

namespace Nabd.IsolationSuite;

/// <summary>
/// Shared "real database" for the isolation suite: fresh Timescale container per test class,
/// REAL migration files applied (never a paraphrase — the suite must see the true schema).
/// TODO(spec 07 honesty): the suite applies files directly today; one day CI should call the
/// real runner (_apply.sh) instead. Recorded in NOTES.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(
            image: "timescale/timescaledb:2.17.2-pg16")      // same image docker-compose.dev.yml pins
        .Build();

    /// Owner lane — owner SKIPs RLS, so only tests and humans use it (house law).
    public NpgsqlDataSource Owner { get; private set; } = null!;

    /// App lane — the exact door the API uses (env-configured, least-privilege nabd_app).
    public IdentityDb Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // 1) boot the container (throws loudly if Docker is off — that's our reminder too)
        await _container.StartAsync();

        // 2) create the "nabd" database — a fresh container only HAS its default db,
        //    and IdentityDb expects NABD_DB_NAME=nabd (same as dev .env)
        await using (var bootstrap = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await bootstrap.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE nabd;", bootstrap);
            await create.ExecuteNonQueryAsync();
        }

        // 3) owner door, pointed at the fresh nabd database
        var ownerCs = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "nabd",
        };
        Owner = new NpgsqlDataSourceBuilder(ownerCs.ConnectionString).Build();

        // 4) apply the real migration files, mirroring _apply.sh exactly:
        //    bootstrap the ledger → per file: skip-if-applied → apply file + ledger
        //    insert as ONE implicit transaction (the runner's psql -1 equivalent),
        //    so a broken file leaves nothing half-applied — not even a fake ledger row.
        var migrations = FindMigrationsDirectory();
        await using var ownerConn = await Owner.OpenConnectionAsync();

        await using (var ledger = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS platform;
            CREATE TABLE IF NOT EXISTS platform.migrations_applied (
                name       text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now());
            """, ownerConn))
        {
            await ledger.ExecuteNonQueryAsync();
        }

        // mirror of _apply.sh's '[0-9]*.sql' — done in C# because the glob class [0-9]
        // proved unreliable through EnumerateFiles on this platform (matched NOTHING).
        var enumerated = Directory.EnumerateFiles(migrations, "*.sql")
            .Where(f => char.IsDigit(Path.GetFileName(f)[0]))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        var applied = new List<string>();

        foreach (var file in enumerated)
        {
            var fileName = Path.GetFileName(file); // the ledger knows files by NAME only

            await using (var check = new NpgsqlCommand(
                "SELECT count(*) FROM platform.migrations_applied WHERE name = @name", ownerConn))
            {
                check.Parameters.AddWithValue("name", fileName);
                if ((long)(await check.ExecuteScalarAsync())! == 1L)
                    continue; // skip — exactly as the runner does
            }

            var batch = "BEGIN;\n" + await File.ReadAllTextAsync(file)
                + $"\nINSERT INTO platform.migrations_applied(name) VALUES ('{fileName}');"
                + "\nCOMMIT;";
            await using var cmd = new NpgsqlCommand(batch, ownerConn);
            await cmd.ExecuteNonQueryAsync();
            applied.Add(fileName);
        }

        // self-diagnosis: the ledger now applied 0005, so nabd_app MUST exist as a role
        await using (var probe = new NpgsqlCommand(
            "SELECT string_agg(rolname, ', ') FROM pg_roles WHERE rolname IN ('nabd_app','nabd')", ownerConn))
        {
            var roles = await probe.ExecuteScalarAsync() as string;
            if (roles is null || !roles.Contains("nabd_app"))
                throw new InvalidOperationException(
                    $"DB diagnosis: dir={migrations} enumerated=[{string.Join(", ", enumerated.Select(Path.GetFileName))}] "
                    + $"applied=[{string.Join(", ", applied)}] roles=[{roles}]");
        }

        // 5) the app role's password — migrations NEVER carry secrets (0005's law, NFR-08);
        //    here the password is a throwaway that exists only inside this container.
        await using (var pw = new NpgsqlCommand(
            "ALTER ROLE nabd_app PASSWORD 'suite-local-password';", ownerConn))
        {
            await pw.ExecuteNonQueryAsync();
        }

        // 6) the env vars IdentityDb reads — set BEFORE building it (it fails fast otherwise)
        Environment.SetEnvironmentVariable("NABD_DB_HOST", _container.Hostname);
        Environment.SetEnvironmentVariable("NABD_DB_PORT", _container.GetMappedPublicPort(5432).ToString());
        Environment.SetEnvironmentVariable("NABD_DB_NAME", "nabd");
        Environment.SetEnvironmentVariable("NABD_APP_DB_USER", "nabd_app");
        Environment.SetEnvironmentVariable("NABD_APP_DB_PASSWORD", "suite-local-password");

        // 7) the app lane — same constructor the API uses, now pointed at the container
        Db = new IdentityDb();
    }

    public async Task DisposeAsync()
    {
        await Owner.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// The real repo root (walk parents until db/migrations exists — never a copied schema).
    private static string FindMigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            dir = dir.Parent!;
        if (dir is null)
            throw new InvalidOperationException("db/migrations not found above the test output directory.");
        return Path.Combine(dir.FullName, "db", "migrations");
    }
}