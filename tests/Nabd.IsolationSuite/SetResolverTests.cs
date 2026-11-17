namespace Nabd.IsolationSuite;

using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Npgsql;
using Xunit;

// P4 spec 01 — STEP 2: the set-based resolver's facts against the REAL schema
// (real migrations applied by PostgresFixture — 0020 included the moment the
// file exists) over the REAL app lane (nabd_app — proving the GRANT, not just
// the SQL):
// (a) fact 6 (resolver half): ONE call resolves MANY devices — every lawful
//     device's tenant + template keys come back from a single query;
// (b) the uniform law: revoked and unknown devices are ABSENT — indistinguishable
//     (anti-enumeration, 0016/0017's own WHERE clause set-shaped);
// (c) an empty-catalog device is PRESENT with an empty key set (0017's
//     coalesce-'{}' law) — the flush's E422 reads it uniformly (step 5's world);
// (d) cross-tenant in one call: each device rides its OWN tenant_id (R4's base).

public sealed class SetResolverTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture Pg;

    public SetResolverTests(PostgresFixture pg) => Pg = pg;

    private sealed record TenantSeed(Guid TenantId, CallerContext Admin);

    private async Task<TenantSeed> SeedTenantAsync(string label)
    {
        var registration = new TenantRegistration(Pg.Db);
        var registered = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"SetResolver {label}",
            Slug: $"sr-{label}-{Guid.NewGuid().ToString("N")[..12]}",
            AdminEmail: $"{Guid.NewGuid().ToString("N")[..12]}-{label}@sr.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(registered.IsSuccess, $"SETUP (registration): {registered.Error}");
        return new TenantSeed(
            registered.Value.Tenant.TenantId,
            new CallerContext(registered.Value.AdminUser.UserId, registered.Value.Tenant.TenantId, ["tenant_admin"]));
    }

    /// <summary>0015's global fridge seed — real channels (temperature + door), no hard-coded uuids.</summary>
    private async Task<Guid> FridgeTemplateIdAsync()
    {
        await using var conn = await Pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM assets.asset_type_templates WHERE key = 'fridge';", conn);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>A live template with NO channel rows — the empty-catalog device's world.</summary>
    private async Task<Guid> InsertEmptyTemplateAsync()
    {
        await using var conn = await Pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO assets.asset_type_templates (key, name_ar, name_en) VALUES (@k, 'قالب فارغ', 'Empty') RETURNING id;",
            conn);
        cmd.Parameters.AddWithValue("k", $"empty-{Guid.NewGuid():N}");
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> CreateAssetAsync(CallerContext admin, Guid tenantId, Guid templateId)
    {
        var assets = new AssetCatalog(Pg.Assets);
        var asset = await assets.CreateAssetAsync(admin, new CreateAssetRequest(
            tenantId, templateId, $"SR anchor {Guid.NewGuid():N}".AsSpan(0, 24).ToString(), null, null));
        Assert.True(asset.IsSuccess, $"SETUP (asset): {asset.Error}");
        return asset.Value.AssetId;
    }

    private async Task<Guid> MintAsync(CallerContext admin, Guid tenantId, Guid assetId)
    {
        var devices = new DeviceLifecycle(Pg.Assets, new DeviceTokenHasher());
        var minted = await devices.CreateDeviceAsync(admin, new DeviceCreateRequest(
            tenantId, assetId, $"SR sensor {Guid.NewGuid():N}".AsSpan(0, 24).ToString()));
        Assert.True(minted.IsSuccess, $"SETUP (device): {minted.Error}");
        return minted.Value.Device.DeviceId;
    }

    private async Task RevokeAsync(CallerContext admin, Guid deviceId)
    {
        var devices = new DeviceLifecycle(Pg.Assets, new DeviceTokenHasher());
        var revoked = await devices.RevokeDeviceAsync(admin, deviceId);
        Assert.True(revoked.IsSuccess, $"SETUP (revoke): {revoked.Error}");
    }

    // ---- fact 6 (resolver half): ONE call resolves MANY devices ---------------

    [Fact]
    public async Task one_call_resolves_many_devices_with_tenant_and_keys()
    {
        var t1 = await SeedTenantAsync("t1");
        var t2 = await SeedTenantAsync("t2");
        var fridge = await FridgeTemplateIdAsync();
        var emptyTemplate = await InsertEmptyTemplateAsync();

        var a1 = await CreateAssetAsync(t1.Admin, t1.TenantId, fridge);
        var a2 = await CreateAssetAsync(t2.Admin, t2.TenantId, fridge);
        var a3 = await CreateAssetAsync(t1.Admin, t1.TenantId, emptyTemplate);

        var d1 = await MintAsync(t1.Admin, t1.TenantId, a1);       // fridge, tenant 1
        var d2 = await MintAsync(t1.Admin, t1.TenantId, a1);       // fridge, tenant 1
        var dT2 = await MintAsync(t2.Admin, t2.TenantId, a2);      // fridge, tenant 2
        var dEmpty = await MintAsync(t1.Admin, t1.TenantId, a3);   // live, NO lawful channels
        var dRevoked = await MintAsync(t1.Admin, t1.TenantId, a1);
        await RevokeAsync(t1.Admin, dRevoked);

        var edges = new IngestionEdges(Pg.Assets);
        var resolved = await edges.ResolveDevicesAsync(
            new[] { d1, d2, dT2, dEmpty, dRevoked, Guid.NewGuid() });   // + one unknown id

        Assert.Equal(4, resolved.Count);   // d1, d2, dT2, dEmpty — revoked + unknown ABSENT
        Assert.Equal(t1.TenantId, resolved[d1].TenantId);
        Assert.Equal(t1.TenantId, resolved[d2].TenantId);
        Assert.Equal(t2.TenantId, resolved[dT2].TenantId);   // cross-tenant in ONE call (R4's base)
        Assert.True(resolved[d1].ChannelKeys.SetEquals(["temperature", "door"]), "0015's fridge channels");
        Assert.True(resolved[dT2].ChannelKeys.SetEquals(["temperature", "door"]));
        Assert.Empty(resolved[dEmpty].ChannelKeys);          // live device, empty catalog — present, not absent
        Assert.False(resolved.ContainsKey(dRevoked));        // uniform: revoked == unknown (no existence hints)
    }

    // ---- edge law: an empty request is an empty map, not a DB round-trip ------

    [Fact]
    public async Task an_empty_request_answers_an_empty_map()
    {
        var edges = new IngestionEdges(Pg.Assets);
        var resolved = await edges.ResolveDevicesAsync([]);
        Assert.Empty(resolved);
    }
}