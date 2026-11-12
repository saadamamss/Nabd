namespace Nabd.IsolationSuite;

using Npgsql;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// P2 spec 04 — the template seeds' wall net. The catalog is GLOBAL by design
// (0012 grants SELECT with no RLS policy — 0002 has no tenant_id column), so
// these facts prove the OTHER wall: the matrix (layer 2) at the service, the
// write-denial grant (spec 01's D-probe made permanent), and the seeds being
// real DATA after a from-zero rebuild — acceptance #9's physical form.

public sealed class TemplateWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private ITemplateCatalog Catalog() => new TemplateCatalog(pg.Assets);

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"TemplateWall {label}",
            Slug: $"tw-{label}-{Guid.NewGuid().ToString("N")[..12]}",
            AdminEmail: $"{Guid.NewGuid().ToString("N")[..12]}-{label}@tw.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    private static CallerContext AdminOf(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private static CallerContext ViewerOf(TenantRegistrationResult t) =>
        new(Guid.NewGuid(), t.Tenant.TenantId, ["viewer"]);

    private static CallerContext PlatformOf(TenantRegistrationResult t) =>
        new(Guid.NewGuid(), t.Tenant.TenantId, ["platform_admin"]);

    // ---- fact 1: the seeds ARE data — present after a from-zero rebuild ---------
    // (the fixture runs every real migration on a fresh container per class)

    [Fact]
    public async Task seeds_present_after_from_zero_rebuild()
    {
        var list = await Catalog().ListTemplatesAsync(
            new CallerContext(Guid.NewGuid(), Guid.NewGuid(), ["viewer"]));

        Assert.True(list.IsSuccess, $"read failed: {list.Error}");

        var fridge = Assert.Single(list.Value, t => t.Key == "fridge");
        Assert.Equal("تخزين بارد", fridge.NameAr);       // bilingual names ride the seed
        Assert.Equal("Cold Storage", fridge.NameEn);

        var temp = Assert.Single(fridge.Channels, c => c.Key == "temperature");
        Assert.Equal("°C", temp.Unit);
        Assert.Equal("number", temp.DataType);
        Assert.Equal(2.0, temp.LowerLimit);               // FR-15's SAMPLE defaults
        Assert.Equal(8.0, temp.UpperLimit);               // — data on the channel, nothing more
        var door = Assert.Single(fridge.Channels, c => c.Key == "door");
        Assert.Equal("bool", door.DataType);
        Assert.Null(door.LowerLimit);
        Assert.Null(door.UpperLimit);

        var tank = Assert.Single(list.Value, t => t.Key == "tank");
        var level = Assert.Single(tank.Channels, c => c.Key == "level");
        Assert.Equal("%", level.Unit);
        Assert.Equal(0.0, level.LowerLimit);
        Assert.Equal(100.0, level.UpperLimit);
        Assert.Single(tank.Channels, c => c.Key == "flow"); // no limits — raw L/min
    }

    // ---- fact 2: the catalog is GLOBAL — two tenants see the SAME rows ----------

    [Fact]
    public async Task catalog_is_global_both_tenants_see_the_same_rows()
    {
        var a = await RegisterAsync("a");
        var b = await RegisterAsync("b");

        var forA = await Catalog().ListTemplatesAsync(AdminOf(a));
        var forB = await Catalog().ListTemplatesAsync(ViewerOf(b));

        Assert.True(forA.IsSuccess && forB.IsSuccess);
        var keysA = forA.Value.Select(t => t.Key).ToArray();
        var keysB = forB.Value.Select(t => t.Key).ToArray();
        Assert.Equal(keysA, keysB);
        Assert.Equal(
            forA.Value.Select(t => (t.TemplateId, t.Channels.Count)),
            forB.Value.Select(t => (t.TemplateId, t.Channels.Count)));
    }

    // ---- fact 3: the read serves EVERY admitted role (matrix covers all rows) ---

    [Fact]
    public async Task read_serves_admin_viewer_and_platform()
    {
        var a = await RegisterAsync("c");
        var catalog = Catalog();

        Assert.True((await catalog.ListTemplatesAsync(AdminOf(a))).IsSuccess);
        Assert.True((await catalog.ListTemplatesAsync(ViewerOf(a))).IsSuccess);
        Assert.True((await catalog.ListTemplatesAsync(PlatformOf(a))).IsSuccess);
    }

    // ---- fact 4: app-role INSERT on templates is DENIED (spec 01's D-probe, ------
    //      now a permanent suite fact — template writes are migrations only)

    [Fact]
    public async Task app_role_template_insert_is_denied()
    {
        var conn = await pg.Assets.OpenPreAuthConnectionAsync();   // plain nabd_app
        try
        {
            var ex = await Assert.ThrowsAnyAsync<PostgresException>(() =>
                new NpgsqlCommand(
                    "INSERT INTO assets.asset_type_templates (key, name_ar, name_en) VALUES ('hostile', 'x', 'y');",
                    conn).ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);                    // permission denied
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---- fact 5: dead templates are HIDDEN (alive is a read filter, not a delete) -

    [Fact]
    public async Task dead_templates_are_hidden_from_the_catalog()
    {
        var scratch = $"tw-dead-{Guid.NewGuid().ToString("N")[..8]}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO assets.asset_type_templates (key, name_ar, name_en) VALUES (@k, 'ميت', 'Dead');",
                conn);
            ins.Parameters.AddWithValue("k", scratch);
            await ins.ExecuteNonQueryAsync();
            await using var kill = new NpgsqlCommand(
                "UPDATE assets.asset_type_templates SET alive = false WHERE key = @k;", conn);
            kill.Parameters.AddWithValue("k", scratch);
            await kill.ExecuteNonQueryAsync();
        }

        var list = await Catalog().ListTemplatesAsync(
            new CallerContext(Guid.NewGuid(), Guid.NewGuid(), ["viewer"]));

        Assert.True(list.IsSuccess);
        Assert.DoesNotContain(list.Value, t => t.Key == scratch);  // the wall hides the dead
    }

    // ---- fact 6: a template with ZERO channels comes back with empty channels ----
    //      (the LEFT JOIN's null-row branch — a new vertical starts as one row)

    [Fact]
    public async Task zero_channel_template_comes_back_with_empty_channels()
    {
        var bare = $"tw-bare-{Guid.NewGuid().ToString("N")[..8]}";
        Guid bareId;
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO assets.asset_type_templates (key, name_ar, name_en) VALUES (@k, 'عارية', 'Bare') RETURNING id;",
                conn);
            ins.Parameters.AddWithValue("k", bare);
            bareId = (Guid)(await ins.ExecuteScalarAsync())!;
        }

        var list = await Catalog().ListTemplatesAsync(
            new CallerContext(Guid.NewGuid(), Guid.NewGuid(), ["viewer"]));

        Assert.True(list.IsSuccess);
        var found = Assert.Single(list.Value, t => t.Key == bare);
        Assert.Equal(bareId, found.TemplateId);
        Assert.Empty(found.Channels);   // the LEFT JOIN's NULL channel row was skipped, not crashed on
    }
}
