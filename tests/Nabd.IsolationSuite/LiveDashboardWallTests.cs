namespace Nabd.IsolationSuite;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Realtime.Contracts;
using Xunit;

// P5 spec 03 — the live dashboard over REAL HTTP and a REAL SignalR connection.
//
// Facts: the snapshot door never crosses tenants and never echoes a tenant id;
// the hub refuses a handshake without a token and accepts the documented
// `?access_token=` on /hubs; one tenant's alert hint reaches that tenant's
// connection and no other (fact 7); the broadcast is a FRESH server-side re-read
// (fact 9), never the payload echoed back.
//
// The host boots in-process (WebApplicationFactory) with its real middleware,
// policies, controller, hub, and the LISTEN worker — the same composition the
// owner runs, not a copy.

public sealed class LiveDashboardWallTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _pg;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Guid _templateId;

    public LiveDashboardWallTests(PostgresFixture pg)
    {
        _pg = pg;
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));
        _client = _factory.CreateClient();
        _templateId = SeedTemplateAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---- seeding -------------------------------------------------------------

    private async Task<Guid> SeedTemplateAsync()
    {
        await using var conn = await _pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
            VALUES (@k, 'قالب لوحة', 'Dashboard Template')
            RETURNING id
            """, conn);
        cmd.Parameters.AddWithValue("k", $"dash-{Uid()}");
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> SeedAssetAsync(Guid tenantId, string name, bool active = true)
    {
        await using var conn = await _pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO assets.assets (tenant_id, template_id, name, is_active)
            VALUES (@tenant, @template, @name, @active)
            RETURNING id
            """, conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("template", _templateId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("active", active);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> SeedAlertAsync(Guid tenantId, Guid assetId, string severity = "critical")
    {
        await using var conn = await _pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO alerting.alerts
                (tenant_id, asset_id, rule_id, type, severity, status, opened_at, title, detail)
            VALUES (@tenant, @asset, @rule, 'threshold', @severity, 'open', @at,
                    '{"en":"Cold room","ar":"غرفة"}'::jsonb, '{}'::jsonb)
            RETURNING id
            """, conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("asset", assetId);
        cmd.Parameters.AddWithValue("rule", Guid.NewGuid());
        cmd.Parameters.AddWithValue("severity", severity);
        cmd.Parameters.AddWithValue("at", T0.UtcDateTime);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(Guid TenantId, string Token)> SeedTenantAsync(string label)
    {
        var registration = new TenantRegistration(_pg.Db);
        var email = $"{Uid()}-{label}@dash.test";
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Dashboard {label}",
            Slug: $"dash-{label}-{Uid()}",
            AdminEmail: email,
            AdminDisplayName: "Dash Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");

        var login = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = "CorrectHorse1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var root = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        return (result.Value.Tenant.TenantId, root.GetProperty("accessToken").GetString()!);
    }

    private async Task<HttpResponseMessage> GetStatusAsync(string? token, bool includeArchived = false)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/assets/status?includeArchived={includeArchived}");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private HubConnection Connect(string token, Action<string, JsonElement> onChannel)
    {
        var url = new Uri(
            _factory.Server.BaseAddress,
            $"hubs/dashboard?access_token={Uri.EscapeDataString(token)}");

        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        foreach (var channel in RealtimeChannels.All)
            connection.On<JsonElement>(channel, payload => onChannel(channel, payload));

        return connection;
    }

    private static async Task WaitForAsync(Task task, int seconds = 8)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        Assert.True(finished == task, $"timed out after {seconds}s waiting for the hub");
        await task;
    }

    /// <summary>
    /// A token signed with the suite's REAL key (the host validates the same
    /// secret). Defaults are a live 10-minute token; callers can expire it or
    /// omit the `tid` claim to drive the wall's fail-shut lanes.
    /// </summary>
    private static string MintToken(
        Guid? tenantId, DateTime? expires = null, DateTime? notBefore = null)
    {
        var hex = Environment.GetEnvironmentVariable("NABD_JWT_SECRET")
            ?? throw new InvalidOperationException("NABD_JWT_SECRET must be set by the fixture.");

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("roles", "tenant_admin"),
        };
        if (tenantId is not null) claims.Add(new Claim("tid", tenantId.Value.ToString()));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = "nabd-identity",
            Audience = "nabd-api",
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            NotBefore = notBefore,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromHexString(hex)),
                SecurityAlgorithms.HmacSha256),
        };

        return new JwtSecurityTokenHandler().CreateToken(descriptor)
            is JwtSecurityToken token
            ? token.RawData
            : throw new InvalidOperationException("mint failed");
    }

    // ---- fact 4: the door is tenant-scoped by the token and echoes no tenant ----

    [Fact]
    public async Task the_status_door_serves_only_the_callers_tenant_and_never_echoes_it()
    {
        var (aTenant, aToken) = await SeedTenantAsync("a");
        var (bTenant, bToken) = await SeedTenantAsync("b");
        var aAsset = await SeedAssetAsync(aTenant, "A cold room");
        var bAsset = await SeedAssetAsync(bTenant, "B cold room");
        await SeedAlertAsync(aTenant, aAsset);

        var aResponse = await GetStatusAsync(aToken);
        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        var aBody = await aResponse.Content.ReadAsStringAsync();
        Assert.Contains(aAsset.ToString(), aBody);
        Assert.DoesNotContain(bAsset.ToString(), aBody);
        Assert.DoesNotContain("tenantId", aBody);          // nothing to learn, nothing to spoof
        Assert.Contains("\"status\":\"alert\"", aBody);

        var bResponse = await GetStatusAsync(bToken);
        Assert.Equal(HttpStatusCode.OK, bResponse.StatusCode);
        var bBody = await bResponse.Content.ReadAsStringAsync();
        Assert.Contains(bAsset.ToString(), bBody);
        Assert.DoesNotContain(aAsset.ToString(), bBody);

        // the door has no tenant parameter at all: a guessed id cannot be expressed
        var anonymous = await GetStatusAsync(token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task archived_assets_appear_only_when_asked()
    {
        var (tenant, token) = await SeedTenantAsync("arch");
        var archived = await SeedAssetAsync(tenant, "Old room", active: false);

        var byDefault = await GetStatusAsync(token);
        var hidden = await byDefault.Content.ReadAsStringAsync();
        Assert.DoesNotContain(archived.ToString(), hidden);

        var flagged = await GetStatusAsync(token, includeArchived: true);
        Assert.Contains(archived.ToString(), await flagged.Content.ReadAsStringAsync());
    }

    // ---- fact 8: no token, no hub -------------------------------------------

    [Fact]
    public async Task the_hub_refuses_a_handshake_without_a_valid_token()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/dashboard"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task the_hub_refuses_an_expired_token()
    {
        var (tenant, _) = await SeedTenantAsync("expired");
        await using var connection = Connect(
            MintToken(tenant, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-2)),
            (_, _) => { });

        // The signature is valid and the tenant claim exists — only `exp` is past.
        // A handshake that completes here would mean the wall accepts dead sessions.
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        Assert.NotEqual(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task the_hub_refuses_a_valid_token_without_a_tenant()
    {
        // A `tid`-less token used to pass [Authorize], connect, and be aborted by
        // OnConnectedAsync — a close the client could only retry forever. The
        // policy now refuses it at the handshake, where the client sees 401/403.
        await using var connection = Connect(MintToken(tenantId: null), (_, _) => { });

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        Assert.NotEqual(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task the_hub_accepts_the_documented_query_token_on_hubs_paths()
    {
        var (_, token) = await SeedTenantAsync("hub-ok");
        await using var connection = Connect(token, (_, _) => { });

        // StartAsync throwing IS the failure: a 401 negotiate never completes.
        await WaitForAsync(connection.StartAsync());
        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    // ---- fact 7 + 9: one tenant's hint, a fresh re-read, both channels ---------

    [Fact]
    public async Task a_hint_reaches_only_its_tenant_and_carries_the_fresh_status()
    {
        var (aTenant, aToken) = await SeedTenantAsync("live-a");
        var (_, bToken) = await SeedTenantAsync("live-b");
        var aAsset = await SeedAssetAsync(aTenant, "A's fridge");

        var aStatus = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bAnything = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aAlerts = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var a = Connect(aToken, (channel, payload) =>
        {
            if (channel == RealtimeChannels.AssetStatus) aStatus.TrySetResult(payload);
            else aAlerts.TrySetResult(payload);
        });
        await using var b = Connect(bToken, (channel, _) => bAnything.TrySetResult(channel));

        await WaitForAsync(a.StartAsync());
        await WaitForAsync(b.StartAsync());

        // A real alert row commits in another connection: the AFTER trigger is the
        // only producer, and the listener re-reads through the status function.
        var alertId = await SeedAlertAsync(aTenant, aAsset);

        await WaitForAsync(aStatus.Task);
        var status = await aStatus.Task;
        Assert.Equal(aAsset, status.GetProperty("assetId").GetGuid());
        Assert.Equal("alert", status.GetProperty("status").GetString());
        Assert.Equal("critical", status.GetProperty("severity").GetString());
        Assert.Equal(T0, status.GetProperty("since").GetDateTimeOffset());

        await WaitForAsync(aAlerts.Task);
        var alerts = await aAlerts.Task;
        Assert.Equal("raised", alerts.GetProperty("kind").GetString());
        Assert.Equal(alertId, alerts.GetProperty("alert").GetProperty("alertId").GetGuid());
        Assert.Equal(aAsset, alerts.GetProperty("alert").GetProperty("assetId").GetGuid());

        // B shares nothing with A: no channel is ever delivered to its connection
        var leaked = await Task.WhenAny(bAnything.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(bAnything.Task, leaked);
    }

    // ---- fact: an ack is still a live problem on the wire ----------------------

    [Fact]
    public async Task acknowledging_through_the_door_pushes_a_changed_hint_and_keeps_the_pill()
    {
        var (tenant, token) = await SeedTenantAsync("ack-live");
        var asset = await SeedAssetAsync(tenant, "Ack fridge");
        var alertId = await SeedAlertAsync(tenant, asset, severity: "warn");

        var changed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusPushes = new List<JsonElement>();

        await using var connection = Connect(token, (channel, payload) =>
        {
            if (channel == RealtimeChannels.Alerts) changed.TrySetResult(payload);
            else lock (statusPushes) statusPushes.Add(payload);
        });
        await WaitForAsync(connection.StartAsync());

        using var ack = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/alerts/{alertId}/ack");
        ack.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(ack);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WaitForAsync(changed.Task);
        var changedHint = await changed.Task;
        Assert.Equal("changed", changedHint.GetProperty("kind").GetString());
        Assert.Equal("acknowledged", changedHint.GetProperty("alert").GetProperty("status").GetString());

        // acknowledged still counts (B7): the re-read says warning, not healthy
        JsonElement last;
        lock (statusPushes) last = statusPushes[^1];
        Assert.Equal(AssetStatuses.Warning, last.GetProperty("status").GetString());
        Assert.Equal(alertId, last.GetProperty("alertId").GetGuid());
    }
}
