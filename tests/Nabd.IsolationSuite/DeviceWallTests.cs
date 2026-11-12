namespace Nabd.IsolationSuite;

using Npgsql;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// P2 spec 03 — the service-layer wall for the device lifecycle. Same promise as the
// P1/P2 asset walls: these call DeviceLifecycle DIRECTLY (no HTTP) — layer 2 (07 §5)
// survives with layer 1 completely absent. Everything runs behind the real 0012 RLS
// policies; the token verify path rides the 0014 SECURITY DEFINER function.
// What Postman CANNOT prove (old token dying at VerifyTokenAsync) is proven HERE.

public sealed class DeviceWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"DeviceWall {label}",
            Slug: $"dw-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@dw.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    private async Task<Guid> EnsureTemplateAsync()
    {
        var key = $"dw-{Uid()}";
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

    /// <summary>Asset inside tenant A — the anchor every device hangs off.</summary>
    private async Task<Guid> EnsureAssetAsync(TenantRegistrationResult tenant, Guid templateId)
    {
        var assets = new AssetCatalog(pg.Assets);
        var created = await assets.CreateAssetAsync(
            new CallerContext(tenant.AdminUser.UserId, tenant.Tenant.TenantId, ["tenant_admin"]),
            new CreateAssetRequest(tenant.Tenant.TenantId, templateId, $"Anchor {Uid()}", null, null));
        Assert.True(created.IsSuccess, $"SETUP failed (asset): {created.Error}");
        return created.Value.AssetId;
    }

    private static CallerContext AdminOf(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private static CallerContext ViewerOf(TenantRegistrationResult t) =>
        new(Guid.NewGuid(), t.Tenant.TenantId, ["viewer"]);

    // ---- fact 1: mint → verify round-trip (the token WORKS) --------------------

    [Fact]
    public async Task mint_shown_once_and_verify_round_trip()
    {
        var a = await RegisterAsync("a");
        var templateId = await EnsureTemplateAsync();
        var assetId = await EnsureAssetAsync(a, templateId);
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());

        var minted = await devices.CreateDeviceAsync(AdminOf(a), new DeviceCreateRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: assetId, Name: "Sensor 01"));
        Assert.True(minted.IsSuccess, $"mint failed: {minted.Error}");
        Assert.False(string.IsNullOrWhiteSpace(minted.Value.Token));   // the ONE showing

        var verified = await devices.VerifyTokenAsync(minted.Value.Token);
        Assert.Equal(minted.Value.Device.DeviceId, verified);

        // the snapshot surface carries NO token/hash anywhere (shown-once law)
        Assert.DoesNotContain(minted.Value.Device.ToString(), minted.Value.Token);

        // a WRONG token verifies to nothing — uniform deny, no hints
        Assert.Null(await devices.VerifyTokenAsync(DeviceTokenHasher.Mint()));
    }

    // ---- fact 2: reissue kills the OLD token, wakes the NEW one, logs TWO lines --

    [Fact]
    public async Task reissue_kills_old_wakes_new_and_logs_two_lines()
    {
        var a = await RegisterAsync("b");
        var templateId = await EnsureTemplateAsync();
        var assetId = await EnsureAssetAsync(a, templateId);
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var admin = AdminOf(a);

        var first = await devices.CreateDeviceAsync(admin, new DeviceCreateRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: assetId, Name: "Sensor 02"));
        Assert.True(first.IsSuccess, $"mint failed: {first.Error}");

        var reissued = await devices.ReissueTokenAsync(admin, first.Value.Device.DeviceId);
        Assert.True(reissued.IsSuccess, $"reissue failed: {reissued.Error}");
        Assert.NotEqual(first.Value.Token, reissued.Value.Token);      // new credential, really

        Assert.Null(await devices.VerifyTokenAsync(first.Value.Token));   // old is DEAD
        Assert.Equal(first.Value.Device.DeviceId,
            await devices.VerifyTokenAsync(reissued.Value.Token));        // new is ALIVE

        // the audit story: revoked(old) + issued(new) — read via the owner lane
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT action, count(*) FROM assets.device_credentials_log
            WHERE device_id = @d GROUP BY action ORDER BY action
            """, conn);
        cmd.Parameters.AddWithValue("d", first.Value.Device.DeviceId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var actions = new Dictionary<string, int>();
        while (await reader.ReadAsync())
            actions[reader.GetString(0)] = (int)reader.GetInt64(1);
        Assert.Equal(1, actions.GetValueOrDefault("revoked"));
        Assert.Equal(2, actions.GetValueOrDefault("issued"));   // first mint + reissue
    }

    // ---- fact 3: revoke stops verification and deactivates the row --------------

    [Fact]
    public async Task revoke_stops_verification_and_deactivates()
    {
        var a = await RegisterAsync("c");
        var templateId = await EnsureTemplateAsync();
        var assetId = await EnsureAssetAsync(a, templateId);
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var admin = AdminOf(a);

        var minted = await devices.CreateDeviceAsync(admin, new DeviceCreateRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: assetId, Name: "Sensor 03"));
        Assert.True(minted.IsSuccess, $"mint failed: {minted.Error}");

        var revoked = await devices.RevokeDeviceAsync(admin, minted.Value.Device.DeviceId);
        Assert.True(revoked.IsSuccess, $"revoke failed: {revoked.Error}");

        Assert.Null(await devices.VerifyTokenAsync(minted.Value.Token));   // FR-12: dead at once

        var listed = await devices.ListByAssetAsync(admin, assetId);
        var row = Assert.Single(listed.Value, d => d.DeviceId == minted.Value.Device.DeviceId);
        Assert.False(row.IsActive);
        Assert.NotNull(row.TokenRevokedAtUtc);
    }

    // ---- fact 4: cross-tenant device ops are INVISIBLE (404 law) -----------------

    [Fact]
    public async Task cross_tenant_device_ops_are_invisible()
    {
        var a = await RegisterAsync("d");
        var b = await RegisterAsync("e");
        var templateId = await EnsureTemplateAsync();
        var assetId = await EnsureAssetAsync(a, templateId);
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());

        var minted = await devices.CreateDeviceAsync(AdminOf(a), new DeviceCreateRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: assetId, Name: "A's sensor"));
        Assert.True(minted.IsSuccess, $"SETUP mint failed: {minted.Error}");

        var foreignReissue = await devices.ReissueTokenAsync(AdminOf(b), minted.Value.Device.DeviceId);
        Assert.False(foreignReissue.IsSuccess);
        Assert.Equal(DeviceErrors.DeviceNotFound, foreignReissue.Error);

        var foreignRevoke = await devices.RevokeDeviceAsync(AdminOf(b), minted.Value.Device.DeviceId);
        Assert.False(foreignRevoke.IsSuccess);
        Assert.Equal(DeviceErrors.DeviceNotFound, foreignRevoke.Error);

        // B's token still verifies to NOTHING (it never existed) — no hints leak
        Assert.Null(await devices.VerifyTokenAsync(DeviceTokenHasher.Mint()));
    }

    // ---- fact 5: viewer verbs refused at layer 2 ---------------------------------

    [Fact]
    public async Task viewer_writes_are_refused_even_for_own_tenant()
    {
        var a = await RegisterAsync("f");
        var templateId = await EnsureTemplateAsync();
        var assetId = await EnsureAssetAsync(a, templateId);
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var viewer = ViewerOf(a);

        var create = await devices.CreateDeviceAsync(viewer, new DeviceCreateRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: assetId, Name: "Nope"));
        Assert.False(create.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, create.Error);

        var reissue = await devices.ReissueTokenAsync(viewer, Guid.NewGuid());
        Assert.False(reissue.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, reissue.Error);

        var revoke = await devices.RevokeDeviceAsync(viewer, Guid.NewGuid());
        Assert.False(revoke.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, revoke.Error);

        var list = await devices.ListByAssetAsync(viewer, assetId);   // read-only is the contract
        Assert.True(list.IsSuccess);
    }

    // ---- fact 6: a foreign asset id is INVISIBLE at create (RLS hides the parent) -

    [Fact]
    public async Task create_device_on_foreign_asset_is_refused_as_not_found()
    {
        var a = await RegisterAsync("g");
        var b = await RegisterAsync("h");
        var templateId = await EnsureTemplateAsync();
        var foreignAssetId = await EnsureAssetAsync(b, templateId);
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());

        var created = await devices.CreateDeviceAsync(AdminOf(a), new DeviceCreateRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: foreignAssetId, Name: "Hostile mount"));
        Assert.False(created.IsSuccess);
        Assert.Equal(DeviceErrors.AssetNotFound, created.Error);   // not Forbidden — the wall hid it
    }

    // ---- fact 7: the empty-name guard (review finding 2's fix) -------------------

    [Fact]
    public async Task empty_name_is_rejected_as_invalid_name()
    {
        var a = await RegisterAsync("i");
        var templateId = await EnsureTemplateAsync();
        var assetId = await EnsureAssetAsync(a, templateId);
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());

        var created = await devices.CreateDeviceAsync(AdminOf(a), new DeviceCreateRequest(
            TargetTenantId: a.Tenant.TenantId, AssetId: assetId, Name: "   "));
        Assert.False(created.IsSuccess);
        Assert.Equal(DeviceErrors.InvalidName, created.Error);
    }
}
