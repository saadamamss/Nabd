namespace Nabd.IsolationSuite;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;
using Xunit.Abstractions;

// P2 spec 07 — the token lifecycle as PERMANENT facts. The revocation SLA
// (≤ 60 s, plan/09 §6) is MEASURED here, not hoped; the revoked token joins the
// uniform-deny set (no existence hints even for a credential that used to
// work); and the shown-once surface is pinned on the wire shape.
//
// Scope decision (recorded in NOTES): door-level facts — the app-side SLA chain
// (revoke door → DB → auth door) is fully scriptable here; the broker-side is
// config (EMQX OSS 5.8 has no auth cache) and was witnessed live twice
// (spec 05 mosquitto ladder + spec 06 simulator ladder).
// Facts the manual lists that are ALREADY permanent: ACL allow-set shape
// (EmqxDoorWallTests.acl_allows_exactly_own_topic_publish, spec 05) and the
// mint→verify round-trip (DeviceWallTests facts 1+3, spec 03) — cited, not
// duplicated.

public sealed class TokenLifecycleTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string SuiteSecret = "suite-local-internal-secret";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public TokenLifecycleTests(PostgresFixture pg, ITestOutputHelper output)
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));
        _client = _factory.CreateClient();
        Pg = pg;
        _output = output;
    }

    private PostgresFixture Pg { get; }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---- seed: real tenant → template → asset → device (services, proven paths) -

    private sealed record Seed(Guid DeviceId, string Token, Guid TenantId, Guid AssetId, string AdminEmail);

    private async Task<Seed> SeedAsync(string label)
    {
        var registration = new TenantRegistration(Pg.Db);
        var registered = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Lifecycle {label}",
            Slug: $"tl-{label}-{Guid.NewGuid().ToString("N")[..12]}",
            AdminEmail: $"{Guid.NewGuid().ToString("N")[..12]}-{label}@tl.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(registered.IsSuccess, $"SETUP (registration): {registered.Error}");

        var admin = new CallerContext(
            registered.Value.AdminUser.UserId, registered.Value.Tenant.TenantId, ["tenant_admin"]);

        var key = $"tl-{Guid.NewGuid().ToString("N")[..12]}";
        Guid templateId;
        await using (var conn = await Pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new Npgsql.NpgsqlCommand(
                "INSERT INTO assets.asset_type_templates (key, name_ar, name_en) VALUES (@k, 'قالب', 'Tl') RETURNING id;",
                conn);
            ins.Parameters.AddWithValue("k", key);
            templateId = (Guid)(await ins.ExecuteScalarAsync())!;
        }

        var assets = new AssetCatalog(Pg.Assets);
        var asset = await assets.CreateAssetAsync(admin, new CreateAssetRequest(
            registered.Value.Tenant.TenantId, templateId, $"Tl anchor {label}", null, null));
        Assert.True(asset.IsSuccess, $"SETUP (asset): {asset.Error}");

        var devices = new DeviceLifecycle(Pg.Assets, new DeviceTokenHasher());
        var minted = await devices.CreateDeviceAsync(admin, new DeviceCreateRequest(
            registered.Value.Tenant.TenantId, asset.Value.AssetId, $"Sensor {label}"));
        Assert.True(minted.IsSuccess, $"SETUP (mint): {minted.Error}");

        return new Seed(minted.Value.Device.DeviceId, minted.Value.Token,
            registered.Value.Tenant.TenantId, asset.Value.AssetId,
            registered.Value.AdminUser.Email);
    }

    private async Task<string> PostAsync(string pathAndQuery, string json, string? bearer = null)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, pathAndQuery) { Content = body };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        using var response = await _client.SendAsync(request);
        return $"{(int)response.StatusCode}|{await response.Content.ReadAsStringAsync()}";
    }

    private async Task<string> DeleteAsync(string path, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new("Bearer", bearer);
        using var response = await _client.SendAsync(request);
        return $"{(int)response.StatusCode}";
    }

    private async Task<string> AuthDoorAsync(Guid deviceId, string password) =>
        await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            $$"""{"username":"{{deviceId}}","password":"{{password}}"}""");

    // ---- fact 1: revocation kills the token within the 60 s SLA — MEASURED ------

    [Fact]
    public async Task revocation_kills_the_token_within_the_60s_sla_measured()
    {
        var s = await SeedAsync("sla");

        var before = await AuthDoorAsync(s.DeviceId, s.Token);
        Assert.Equal("""200|{"result":"allow"}""", before);

        // the REAL chain: login door → bearer → revoke door (204)
        var login = await PostAsync("/api/v1/auth/login",
            $$"""{"email":"{{s.AdminEmail}}","password":"CorrectHorse1"}""");
        var bearer = login.Split('|')[1].Split("\"accessToken\":\"")[1].Split('"')[0];

        var clock = Stopwatch.StartNew();
        Assert.Equal("204", await DeleteAsync($"/api/v1/devices/{s.DeviceId}", bearer));

        // poll the broker's informant — the FIRST deny is the propagation time
        string answer;
        do { answer = await AuthDoorAsync(s.DeviceId, s.Token); }
        while (answer == """200|{"result":"allow"}""" && clock.ElapsedMilliseconds < 60_000);
        clock.Stop();

        _output.WriteLine($"MEASURED revocation propagation: {clock.ElapsedMilliseconds} ms (SLA bound: 60_000 ms)");

        Assert.Equal("""200|{"result":"deny"}""", answer);           // it died
        Assert.True(clock.ElapsedMilliseconds < 60_000,
            $"revocation took {clock.ElapsedMilliseconds} ms — the 60 s SLA is violated");
    }

    // ---- fact 2: the revoked token joins the uniform-deny set --------------------

    [Fact]
    public async Task a_revoked_token_denies_identically_to_wrong_and_unknown()
    {
        var s = await SeedAsync("uniform");
        var admin = new CallerContext(Guid.NewGuid(), s.TenantId, ["tenant_admin"]);
        var devices = new DeviceLifecycle(Pg.Assets, new DeviceTokenHasher());
        Assert.True((await devices.RevokeDeviceAsync(admin, s.DeviceId)).IsSuccess);

        var revoked = await AuthDoorAsync(s.DeviceId, s.Token);
        var wrongToken = await AuthDoorAsync(s.DeviceId, "not-the-token");
        var unknownDevice = await AuthDoorAsync(Guid.NewGuid(), s.Token);

        Assert.Equal("""200|{"result":"deny"}""", revoked);
        Assert.Equal(revoked, wrongToken);    // byte-identical: a USED-to-work credential
        Assert.Equal(revoked, unknownDevice); // leaves no more of a trace than a fake one
    }

    // ---- fact 3: shown once — the mint door carries the token, nothing else does -

    [Fact]
    public async Task the_token_appears_on_the_wire_exactly_once()
    {
        var s = await SeedAsync("wire");

        var login = await PostAsync("/api/v1/auth/login",
            $$"""{"email":"{{s.AdminEmail}}","password":"CorrectHorse1"}""");
        var bearer = login.Split('|')[1].Split("\"accessToken\":\"")[1].Split('"')[0];

        // the mint door: the ONE moment the plaintext token exists on a wire
        var mint = await PostAsync($"/api/v1/assets/{s.AssetId}/devices",
            """{"name":"Shown-once probe"}""", bearer);
        using var minted = JsonDocument.Parse(mint.Split('|')[1]);
        Assert.True(minted.RootElement.TryGetProperty("token", out var token));
        Assert.False(string.IsNullOrWhiteSpace(token.GetString()));

        // the list door: snapshots only — no token property, ever
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/assets/{s.AssetId}/devices");
        listRequest.Headers.Authorization = new("Bearer", bearer);
        using var list = await _client.SendAsync(listRequest);
        list.EnsureSuccessStatusCode();
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.True(listed.RootElement.ValueKind == JsonValueKind.Array);
        foreach (var device in listed.RootElement.EnumerateArray())
            Assert.False(device.TryGetProperty("token", out _),
                "the list door leaked a token field — the shown-once contract is broken");
        Assert.True(listed.RootElement.EnumerateArray().Count() >= 2); // seed device + probe
    }
}
