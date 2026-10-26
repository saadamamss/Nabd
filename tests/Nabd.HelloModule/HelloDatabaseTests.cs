using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Nabd.HelloModule;

/// <summary>
/// The "real database" half of P0's DoD. Uses Testcontainers (09 §2) with the SAME image
/// docker-compose.dev.yml pins (timescale/timescaledb — 04 §6), so "hello row" locally
/// proves the exact database the platform will run on. Asserted later in P3 with the
/// true readings hypertable; today it proves: toolchain → container → SQL → row, round trip.
/// Skips automatically until Docker is installed (docs/dev/SETUP.md Step 3).
/// </summary>
public sealed class HelloDatabaseTests
{
    [SkippableFact]
    public async Task hello_writes_and_reads_one_database_row()
    {
        // Deterministic skip: no Docker runtime ⇒ no container (SETUP.md Step 3 installs it).
        var dockerPresent = File.Exists("/var/run/docker.sock") || Environment.GetEnvironmentVariable("DOCKER_HOST") is not null;
        Skip.IfNot(dockerPresent,
            "Docker unavailable yet — run SETUP.md Step 3 first, then re-run this test.");


        // Wait for single-node Postgres+Timescale (dev spec, 04 §6; kill-happy variant of 09 §2 tooling).
        await using var container = new PostgreSqlBuilder(image: "timescale/timescaledb:2.17.2-pg16")
            .Build();

        try
        {
            await container.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Skip.If(ex.Message.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
                    ex.Source == "Docker.DotNet",
                "Docker unavailable yet — run SETUP.md Step 3 first, then re-run this test.");
        }

        await using var conn = new NpgsqlConnection(container.GetConnectionString());
        await conn.OpenAsync(CancellationToken.None);

        // One row: the test's whole point (10 §3 DoD wording).
        await using (var cmd = new NpgsqlCommand(
            "CREATE TABLE hello_row (id serial PRIMARY KEY, tenant_id uuid NOT NULL, created_at timestamptz NOT NULL DEFAULT now());",
            conn))
        {
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var tenantId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO hello_row (tenant_id) VALUES (@tenantId);", conn))
        {
            insert.Parameters.AddWithValue("tenantId", tenantId);
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var select = new NpgsqlCommand("SELECT tenant_id FROM hello_row;", conn);
        var stored = await select.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal(tenantId, (Guid)(stored ?? Guid.Empty));
        // TODO(P3): hello_row becomes the readings hypertable; this test grows the
        // idempotency key (FR-23) and 'ON CONFLICT DO NOTHING' double-insert assertion.
    }
}
