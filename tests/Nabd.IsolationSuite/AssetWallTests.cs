namespace Nabd.IsolationSuite;

using Npgsql;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// P2 spec 02 — the service-layer wall for the assets doors, same promise as the
// P1 walls: these call AssetCatalog DIRECTLY (no HTTP) — layer 2 (07 §5) survives
// with layer 1 completely absent. Registration is REAL (TenantRegistration, app
// lane) so every assert stands against real rows behind the real 0012 RLS policies.
// The template is seeded through the OWNER lane (superuser): templates are global
// config seeded by migrations (spec 04); a test seeding one is test bookkeeping,
// the same class of act as the fixture's ALTER ROLE passwords.

public sealed class AssetWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"AssetWall {label}",
            Slug: $"aw-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@aw.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    private async Task<Guid> EnsureTemplateAsync()
    {
        var key = $"aw-{Uid()}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO assets.asset_type_templates (key, name_ar, name_en) VALUES (@k, 'قالب', 'Wall Template');",
                conn);
            ins.Parameters.AddWithValue("k", key);
            await ins.ExecuteNonQueryAsync();
        }
        await using (var conn2 = await pg.Owner.OpenConnectionAsync())
        {
            await using var sel = new NpgsqlCommand(
                "SELECT id FROM assets.asset_type_templates WHERE key = @k;", conn2);
            sel.Parameters.AddWithValue("k", key);
            return (Guid)(await sel.ExecuteScalarAsync())!;
        }
    }

    private static CallerContext AdminOf(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private static CallerContext ViewerOf(TenantRegistrationResult t) =>
        new(Guid.NewGuid(), t.Tenant.TenantId, ["viewer"]);

    // ---- fact 1: the happy path, own tenant -----------------------------------

    [Fact]
    public async Task create_get_list_round_trip_inside_the_tenant()
    {
        var a = await RegisterAsync("a");
        var templateId = await EnsureTemplateAsync();
        var assets = new AssetCatalog(pg.Assets);

        var created = await assets.CreateAssetAsync(AdminOf(a), new CreateAssetRequest(
            TargetTenantId: a.Tenant.TenantId, TemplateId: templateId,
            Name: "Fridge 1", Location: "Store A", Notes: null));
        Assert.True(created.IsSuccess, $"create failed: {created.Error}");

        var got = await assets.GetAssetAsync(AdminOf(a), created.Value.AssetId);
        Assert.True(got.IsSuccess);
        Assert.Equal("Fridge 1", got.Value.Name);
        Assert.Equal(a.Tenant.TenantId, got.Value.TenantId);
        Assert.True(got.Value.IsActive);

        var list = await assets.ListAssetsAsync(AdminOf(a), a.Tenant.TenantId);
        Assert.True(list.IsSuccess);
        Assert.Contains(list.Value, s => s.AssetId == created.Value.AssetId);
    }

    // ---- fact 2: cross-tenant WRITES refused before any data touch -------------

    [Fact]
    public async Task cross_tenant_writes_are_refused_before_any_data_touch()
    {
        var a = await RegisterAsync("b");
        var b = await RegisterAsync("c");
        var templateId = await EnsureTemplateAsync();
        var assets = new AssetCatalog(pg.Assets);
        var admin = AdminOf(a);

        var create = await assets.CreateAssetAsync(admin, new CreateAssetRequest(
            TargetTenantId: b.Tenant.TenantId, TemplateId: templateId, Name: "X", Location: null, Notes: null));
        Assert.False(create.IsSuccess);
        Assert.Equal(IdentityErrors.CrossTenantTarget, create.Error);

        var update = await assets.UpdateAssetAsync(admin, new UpdateAssetRequest(
            TargetTenantId: b.Tenant.TenantId, AssetId: Guid.NewGuid(), Name: "X", Location: null, Notes: null));
        Assert.False(update.IsSuccess);
        Assert.Equal(IdentityErrors.CrossTenantTarget, update.Error);

        var archive = await assets.ArchiveAssetAsync(admin, b.Tenant.TenantId, Guid.NewGuid());
        Assert.False(archive.IsSuccess);
        Assert.Equal(IdentityErrors.CrossTenantTarget, archive.Error);

        // the refusal came BEFORE any data touch: B still sees zero assets
        var bList = await assets.ListAssetsAsync(AdminOf(b), b.Tenant.TenantId);
        Assert.True(bList.IsSuccess);
        Assert.Empty(bList.Value);
    }

    // ---- fact 3: cross-tenant ids are INVISIBLE, not forbidden (the 404 law) ----

    [Fact]
    public async Task cross_tenant_asset_ids_are_invisible_not_forbidden()
    {
        var a = await RegisterAsync("d");
        var b = await RegisterAsync("e");
        var templateId = await EnsureTemplateAsync();
        var assets = new AssetCatalog(pg.Assets);

        var created = await assets.CreateAssetAsync(AdminOf(a), new CreateAssetRequest(
            TargetTenantId: a.Tenant.TenantId, TemplateId: templateId,
            Name: "A's asset", Location: null, Notes: null));
        Assert.True(created.IsSuccess, $"SETUP create failed: {created.Error}");

        var foreignGet = await assets.GetAssetAsync(AdminOf(b), created.Value.AssetId);
        Assert.False(foreignGet.IsSuccess);
        Assert.Equal(AssetErrors.AssetNotFound, foreignGet.Error);   // not Forbidden — RLS hid the row

        var foreignList = await assets.ListAssetsAsync(AdminOf(b), b.Tenant.TenantId);
        Assert.True(foreignList.IsSuccess);
        Assert.DoesNotContain(foreignList.Value, s => s.AssetId == created.Value.AssetId);
    }

    // ---- fact 4: viewer verbs refused at layer 2 (own tenant, still refused) ----

    [Fact]
    public async Task viewer_writes_are_refused_even_for_own_tenant()
    {
        var a = await RegisterAsync("f");
        var templateId = await EnsureTemplateAsync();
        var assets = new AssetCatalog(pg.Assets);
        var viewer = ViewerOf(a);

        var create = await assets.CreateAssetAsync(viewer, new CreateAssetRequest(
            TargetTenantId: a.Tenant.TenantId, TemplateId: templateId, Name: "X", Location: null, Notes: null));
        Assert.False(create.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, create.Error);

        var update = await assets.UpdateAssetAsync(viewer, new UpdateAssetRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: Guid.NewGuid(), Name: "X", Location: null, Notes: null));
        Assert.False(update.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, update.Error);

        var archive = await assets.ArchiveAssetAsync(viewer, a.Tenant.TenantId, Guid.NewGuid());
        Assert.False(archive.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, archive.Error);

        // but the viewer READS fine — read-only is exactly the contract
        var list = await assets.ListAssetsAsync(viewer, a.Tenant.TenantId);
        Assert.True(list.IsSuccess);
    }

    // ---- fact 5: unknown template rejected (channels are config, FR-13) ---------

    [Fact]
    public async Task unknown_template_is_rejected()
    {
        var a = await RegisterAsync("g");
        var assets = new AssetCatalog(pg.Assets);

        var created = await assets.CreateAssetAsync(AdminOf(a), new CreateAssetRequest(
            TargetTenantId: a.Tenant.TenantId, TemplateId: Guid.NewGuid(),
            Name: "Ghost asset", Location: null, Notes: null));
        Assert.False(created.IsSuccess);
        Assert.Equal(AssetErrors.UnknownTemplate, created.Error);
    }

    // ---- fact 6: archive is SOFT — the row survives ------------------------------

    [Fact]
    public async Task archive_is_soft_the_row_survives()
    {
        var a = await RegisterAsync("h");
        var templateId = await EnsureTemplateAsync();
        var assets = new AssetCatalog(pg.Assets);
        var admin = AdminOf(a);

        var created = await assets.CreateAssetAsync(admin, new CreateAssetRequest(
            TargetTenantId: a.Tenant.TenantId, TemplateId: templateId,
            Name: "Doomed fridge", Location: null, Notes: null));
        Assert.True(created.IsSuccess, $"SETUP create failed: {created.Error}");

        var archived = await assets.ArchiveAssetAsync(admin, a.Tenant.TenantId, created.Value.AssetId);
        Assert.True(archived.IsSuccess, $"archive failed: {archived.Error}");

        var defaultList = await assets.ListAssetsAsync(admin, a.Tenant.TenantId);
        Assert.DoesNotContain(defaultList.Value, s => s.AssetId == created.Value.AssetId);

        var withArchived = await assets.ListAssetsAsync(admin, a.Tenant.TenantId, includeArchived: true);
        var row = Assert.Single(withArchived.Value, s => s.AssetId == created.Value.AssetId);
        Assert.False(row.IsActive);

        var got = await assets.GetAssetAsync(admin, created.Value.AssetId);
        Assert.True(got.IsSuccess);
        Assert.False(got.Value.IsActive);
    }

    // ---- fact 7: PATCH semantics — null means "leave as is" ----------------------

    [Fact]
    public async Task update_patch_semantics_null_leaves_fields_untouched()
    {
        var a = await RegisterAsync("i");
        var templateId = await EnsureTemplateAsync();
        var assets = new AssetCatalog(pg.Assets);

        var created = await assets.CreateAssetAsync(AdminOf(a), new CreateAssetRequest(
            TargetTenantId: a.Tenant.TenantId, TemplateId: templateId,
            Name: "Old name", Location: "Freezer wing", Notes: "keep me"));
        Assert.True(created.IsSuccess, $"SETUP create failed: {created.Error}");

        var updated = await assets.UpdateAssetAsync(AdminOf(a), new UpdateAssetRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: created.Value.AssetId,
            Name: "New name", Location: null, Notes: null));
        Assert.True(updated.IsSuccess, $"update failed: {updated.Error}");
        Assert.Equal("New name", updated.Value.Name);
        Assert.Equal("Freezer wing", updated.Value.Location);   // null did NOT wipe it
        Assert.Equal("keep me", updated.Value.Notes);
    }
}
