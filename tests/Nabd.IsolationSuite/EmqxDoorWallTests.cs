namespace Nabd.IsolationSuite;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// P2 spec 05 — the broker's two doors over REAL HTTP (the in-process host, the
// HttpWallTests shape). The owner's Postman ladder proved the happy path LIVE;
// this net pins the PERMANENT facts: uniform deny (no existence hints), the
// secret guard, the exact ACL allow-set (one topic shape), and revocation
// dying at the door. What EMQX itself does with these answers is witnessed by
// the live mosquitto ladder (2026-11-12) — this suite keeps the contract.

public sealed class EmqxDoorWallTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string SuiteSecret = "suite-local-internal-secret";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public EmqxDoorWallTests(PostgresFixture pg)
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));
        _client = _factory.CreateClient();
        Pg = pg;
    }

    private PostgresFixture Pg { get; }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---- seed: real tenant → real asset → real device (services, proven paths) -

    private sealed record Seed(Guid DeviceId, string Token, Guid TenantId);

    private async Task<Seed> SeedAsync(string label)
    {
        var registration = new TenantRegistration(Pg.Db);
        var registered = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"EmqxDoor {label}",
            Slug: $"ed-{label}-{Guid.NewGuid().ToString("N")[..12]}",
            AdminEmail: $"{Guid.NewGuid().ToString("N")[..12]}-{label}@ed.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(registered.IsSuccess, $"SETUP (registration): {registered.Error}");

        var admin = new CallerContext(
            registered.Value.AdminUser.UserId, registered.Value.Tenant.TenantId, ["tenant_admin"]);

        var key = $"ed-{Guid.NewGuid().ToString("N")[..12]}";
        Guid templateId;
        await using (var conn = await Pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new Npgsql.NpgsqlCommand(
                "INSERT INTO assets.asset_type_templates (key, name_ar, name_en) VALUES (@k, 'قالب', 'Ed') RETURNING id;",
                conn);
            ins.Parameters.AddWithValue("k", key);
            templateId = (Guid)(await ins.ExecuteScalarAsync())!;
        }

        var assets = new AssetCatalog(Pg.Assets);
        var asset = await assets.CreateAssetAsync(admin, new CreateAssetRequest(
            registered.Value.Tenant.TenantId, templateId, $"Ed anchor {label}", null, null));
        Assert.True(asset.IsSuccess, $"SETUP (asset): {asset.Error}");

        var devices = new DeviceLifecycle(Pg.Assets, new DeviceTokenHasher());
        var minted = await devices.CreateDeviceAsync(admin, new DeviceCreateRequest(
            registered.Value.Tenant.TenantId, asset.Value.AssetId, $"Sensor {label}"));
        Assert.True(minted.IsSuccess, $"SETUP (mint): {minted.Error}");

        return new Seed(minted.Value.Device.DeviceId, minted.Value.Token, registered.Value.Tenant.TenantId);
    }

    private async Task<string> PostAsync(string pathAndQuery, string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(pathAndQuery, body);
        return $"{(int)response.StatusCode}|{await response.Content.ReadAsStringAsync()}";
    }

    // ---- fact 1: the happy path — a valid token admits its OWN device ----------

    [Fact]
    public async Task auth_allows_a_valid_token_for_its_own_device()
    {
        var s = await SeedAsync("a");
        var raw = await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            $$"""{"username":"{{s.DeviceId}}","password":"{{s.Token}}"}""");
        Assert.Equal("200|{\"result\":\"allow\"}", raw);
    }

    // ---- fact 2: uniform deny — wrong token, unknown device, malformed: --------
    //      same status, same body shape, zero existence hints

    [Fact]
    public async Task auth_denies_uniformly_with_no_existence_hints()
    {
        var s = await SeedAsync("b");

        var wrongToken = await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            $$"""{"username":"{{s.DeviceId}}","password":"not-the-token"}""");
        var unknownDevice = await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            $$"""{"username":"{{Guid.NewGuid()}}","password":"whatever"}""");
        var malformedUser = await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            """{"username":"not-a-guid","password":"x"}""");

        Assert.Equal("200|{\"result\":\"deny\"}", wrongToken);
        Assert.Equal(wrongToken, unknownDevice);   // byte-identical: anti-enumeration
        Assert.Equal(wrongToken, malformedUser);
    }

    // ---- fact 3: the shared secret is the ONLY guard on these doors ------------

    [Fact]
    public async Task the_secret_is_the_guard_missing_or_wrong_is_denied()
    {
        var s = await SeedAsync("c");

        var noSecret = await PostAsync("/internal/emqx/auth",
            $$"""{"username":"{{s.DeviceId}}","password":"{{s.Token}}"}""");
        var wrongSecret = await PostAsync($"/internal/emqx/auth?k=not-the-secret",
            $$"""{"username":"{{s.DeviceId}}","password":"{{s.Token}}"}""");

        Assert.Equal("200|{\"result\":\"deny\"}", noSecret);    // 200-deny: EMQX-correct,
        Assert.Equal("200|{\"result\":\"deny\"}", wrongSecret); // never 401/403 (5.x "ignore" trap)
    }

    // ---- fact 4: the ACL allow-set is EXACTLY one topic shape -------------------

    [Fact]
    public async Task acl_allows_exactly_own_topic_publish()
    {
        var s = await SeedAsync("d");
        var ownTopic = $"t/{s.TenantId}/dev/{s.DeviceId}/v1";

        var own = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{s.DeviceId}}","topic":"{{ownTopic}}","action":"publish"}""");
        Assert.Equal("200|{\"result\":\"allow\"}", own);

        var subscribe = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{s.DeviceId}}","topic":"{{ownTopic}}","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", subscribe);

        var foreignTopic = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{s.DeviceId}}","topic":"t/{{s.TenantId}}/dev/{{Guid.NewGuid()}}/v1","action":"publish"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", foreignTopic);

        var foreignTenant = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{s.DeviceId}}","topic":"t/{{Guid.NewGuid()}}/dev/{{s.DeviceId}}/v1","action":"publish"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", foreignTenant);

        var malformed = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{s.DeviceId}}","topic":"t/+/dev/{{s.DeviceId}}/#","action":"publish"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", malformed);   // wildcards die in the split
    }

    // ---- fact 5: revocation dies at the auth door (FR-12 end to end) ------------

    [Fact]
    public async Task a_revoked_token_dies_at_the_auth_door()
    {
        var s = await SeedAsync("e");
        var admin = new CallerContext(Guid.NewGuid(), s.TenantId, ["tenant_admin"]);
        var devices = new DeviceLifecycle(Pg.Assets, new DeviceTokenHasher());

        var revoked = await devices.RevokeDeviceAsync(admin, s.DeviceId);
        Assert.True(revoked.IsSuccess, $"revoke failed: {revoked.Error}");

        var raw = await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            $$"""{"username":"{{s.DeviceId}}","password":"{{s.Token}}"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", raw);
    }

    // ---- fact 6: the secret is fail-shut — unconfigured = doors refuse to exist -

    [Fact]
    public async Task internal_secrets_fail_shut_without_the_env_var()
    {
        var original = Environment.GetEnvironmentVariable("NABD_INTERNAL_SHARED_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("NABD_INTERNAL_SHARED_SECRET", null);
            var ex = Record.Exception(() => new Nabd.ApiHost.Auth.InternalSecrets());
            Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("NABD_INTERNAL_SHARED_SECRET", ex!.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NABD_INTERNAL_SHARED_SECRET", original);
        }
    }
}
