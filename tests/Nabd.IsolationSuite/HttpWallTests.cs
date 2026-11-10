namespace Nabd.IsolationSuite;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Nabd.Identity.Contracts;
using Xunit;

// Isolation-suite piece 9 (P1 spec 07): the deny matrix over REAL HTTP doors.
//
// COVERAGE HONESTY (DoD line 4, spec 07): this ladder covers the P1 surface —
// tenants/users CRUD + auth (specs 02/03/05) — and NOTHING else. It does NOT
// cover: assets/telemetry isolation (their doors do not exist until P2/P3 —
// they JOIN this same suite when they do), devices deep probes, fuzz/latency
// classes, or direct-in-SQL RLS drills (those live in the P0 spike notebook,
// referenced not rewritten). New doors landing in P2+ must add their rows here.
//
// The honest mid-path: the REAL host boots in-process (WebApplicationFactory)
// — full middleware chain, JWT wall, policies with deny-by-default, controllers,
// service asserts, RLS under the app role. Tokens are minted ONLY by a real
// /auth/login; seeds go through the real registration door. No lane is faked.

public sealed class HttpWallTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string SuiteSecret =
        "000102030405060708090a0b0c0d0e0f" + "101112131415161718191a1b1c1d1e1f";

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    // xunit guarantees the ORDER this class depends on: the fixture's
    // InitializeAsync (container + env vars) completes BEFORE the test class
    // is constructed — so the host reads the container's coordinates, never
    // the dev defaults.
    public HttpWallTests(PostgresFixture pg)
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));  // same shape as the owner's Postman runs
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---- seed: everything through real doors, tokens from real logins ----------

    private sealed record Seed(
        Guid T1, Guid T2, string U1Email, string U1Pass, string U1Token, Guid U1Id,
        string U1vEmail, string U1vPass, string U1vToken, Guid U1vId, string U2Token);

    private async Task<Seed> SeedAsync()
    {
        // T1: admin U1 + viewer U1v (viewer born through U1's REAL create door)
        var t1 = await RegisterAsync();
        var u1 = await LoginAsync(t1.AdminEmail, t1.AdminPassword);
        var u1vEmail = $"{Uid()}-v1@wall.test";
        var created = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{t1.TenantId}/users", new
        {
            targetTenantId = t1.TenantId,
            email = u1vEmail,
            displayName = "T1 Viewer",
            locale = "en",
            password = "ViewerPass11!",
            roleCode = "viewer",
        }, bearer: u1.AccessToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var viewer = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var viewerId = viewer.GetProperty("userId").GetGuid();

        var u1v = await LoginAsync(u1vEmail, "ViewerPass11!");

        // T2: admin U2
        var t2 = await RegisterAsync();
        var u2 = await LoginAsync(t2.AdminEmail, t2.AdminPassword);

        return new Seed(t1.TenantId, t2.TenantId,
            t1.AdminEmail, t1.AdminPassword, u1.AccessToken, u1.UserId,
            u1vEmail, "ViewerPass11!", u1v.AccessToken, viewerId, u2.AccessToken);
    }

    private async Task<(Guid TenantId, string AdminEmail, string AdminPassword)> RegisterAsync()
    {
        var email = $"{Uid()}@wall.test";
        var posted = await SendAsync(HttpMethod.Post, "/api/v1/tenants", new
        {
            name = $"Wall {Uid()}",
            slug = $"wall-{Uid()}",
            adminEmail = email,
            adminDisplayName = "Wall Admin",
            adminPassword = "CorrectHorse1!",
            adminLocale = "en",
        });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var root = JsonDocument.Parse(await posted.Content.ReadAsStringAsync()).RootElement;
        return (root.GetProperty("tenant").GetProperty("tenantId").GetGuid(), email, "CorrectHorse1!");
    }

    private async Task<(string AccessToken, Guid UserId)> LoginAsync(string email, string password)
    {
        var login = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password });
        Assert.True(login.IsSuccessStatusCode, $"login {email} → {(int)login.StatusCode}");
        var root = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        return (root.GetProperty("accessToken").GetString()!, root.GetProperty("user").GetProperty("userId").GetGuid());
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            // Web serializer options = camelCase, the same convention Postman sees (law 17 family:
            // MVC's own [FromBody] is case-insensitive, the honest sender just mirrors what ships).
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonWeb), Encoding.UTF8, "application/json");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _client.SendAsync(request);
    }

    private async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();

    // ---- matrix row 1: U2 (T2) probing T1 — 404-family, ZERO T1 data in body ----

    [Fact]
    public async Task cross_tenant_read_answers_404_and_never_carries_the_other_tenant()
    {
        var seed = await SeedAsync();

        var tenant = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{seed.T1}", null, seed.U2Token);
        Assert.Equal(HttpStatusCode.NotFound, tenant.StatusCode);
        Assert.DoesNotContain(seed.T1.ToString(), await BodyAsync(tenant));

        var users = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{seed.T1}/users", null, seed.U2Token);
        Assert.Equal(HttpStatusCode.NotFound, users.StatusCode);
        Assert.DoesNotContain(seed.U1Email, await BodyAsync(users));
    }

    // ---- matrix row 2: viewer write attempts on OWN surfaces → 403 (verb wall) --
    // These doors are [TenantAdmin]-walled, so the refusal happens at the API
    // ATTRIBUTE layer: middleware Forbid → 403 with an EMPTY body (spec says
    // assert status, don't over-assert). The service-layer refusal for the
    // same verbs is already proven in-process (AdminWallTests fact 5) —
    // here we prove layer 1 catches it alone.

    [Fact]
    public async Task viewer_write_verbs_refuse_with_403_on_every_surface()
    {
        var seed = await SeedAsync();

        var create = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{seed.T1}/users", new
        {
            targetTenantId = seed.T1,
            email = $"{Uid()}@wall.test",
            displayName = "Should Not Land",
            locale = "en",
            password = "ViewerPass11!",
            roleCode = "viewer",
        }, seed.U1vToken);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var delete = await SendAsync(HttpMethod.Delete, $"/api/v1/tenants/{seed.T1}/users/{seed.U1vId}", null, seed.U1vToken);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        var roles = await SendAsync(HttpMethod.Patch, $"/api/v1/tenants/{seed.T1}/users/{seed.U1vId}/roles",
            new { role = "tenant_admin" }, seed.U1vToken);
        Assert.Equal(HttpStatusCode.Forbidden, roles.StatusCode);

        // nothing landed: the fleet is unchanged (the probe proves the wall,
        // the DB proves the wall was never crossed)
        var list = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{seed.T1}/users", null, seed.U1Token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain("Should Not Land", await BodyAsync(list));
    }

    // ---- matrix row 3: no token / garbage token / expired token → 401 ----------

    [Fact]
    public async Task no_token_garbage_and_expired_answer_401()
    {
        var seed = await SeedAsync();

        var anonymous = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{seed.T1}", null, bearer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var garbage = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{seed.T1}", null, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);

        var expired = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{seed.T1}", null, MintExpired());
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    // The expired row must be FRESHLY minted (never a recorded token): same
    // secret, same issuer/audience the wall demands, clock in the past — the
    // wall's own parameters are what fails it (30 s skew can't rescue −5 min).
    private static string MintExpired()
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("tid", Guid.NewGuid().ToString()),
            new("roles", "tenant_admin"),
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Audience = "nabd-api",
            Issuer = "nabd-identity",
            Expires = DateTime.UtcNow.AddMinutes(-5),   // Kind=Utc law ( JwtWallTests #31)
            NotBefore = DateTime.UtcNow.AddMinutes(-60),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromHexString(SuiteSecret)),
                SecurityAlgorithms.HmacSha256),
        };
        return new JwtSecurityTokenHandler().CreateToken(descriptor) is JwtSecurityToken token
            ? token.RawData
            : throw new InvalidOperationException("expired mint failed");
    }

    // ---- matrix row 4: the canary — U1's OWN happy path through 02/03/05 --------

    [Fact]
    public async Task the_own_tenant_canary_passes_the_whole_p1_happy_path()
    {
        var seed = await SeedAsync();

        // reads (spec 02)
        var own = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{seed.T1}", null, seed.U1Token);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Contains(seed.T1.ToString(), await BodyAsync(own));

        var list = await SendAsync(HttpMethod.Get, $"/api/v1/tenants/{seed.T1}/users", null, seed.U1Token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(seed.U1Email, await BodyAsync(list));

        // writes (spec 02): create → then set the new user's locale to ar (FR-06)
        var canaryEmail = $"{Uid()}@wall.test";
        var created = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{seed.T1}/users", new
        {
            targetTenantId = seed.T1,
            email = canaryEmail,
            displayName = "Canary User",
            locale = "en",
            password = "CanaryPass11!",
            roleCode = "viewer",
        }, seed.U1Token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var newUserId = JsonDocument.Parse(await BodyAsync(created)).RootElement.GetProperty("userId").GetGuid();

        var locale = await SendAsync(HttpMethod.Patch,
            $"/api/v1/tenants/{seed.T1}/users/{newUserId}/locale", new { locale = "ar" }, seed.U1Token);
        Assert.Equal(HttpStatusCode.OK, locale.StatusCode);
        Assert.Contains("\"locale\":\"ar\"", await BodyAsync(locale));

        // auth half (spec 03): U1 rotates HIS OWN password (AssertSelf law —
        // no user may rotate another's, the door takes the CALLER's id), 204 →
        // new password opens, old dies
        var change = await SendAsync(HttpMethod.Patch,
            $"/api/v1/auth/{seed.T1}/users/{seed.U1Id}/password",
            new { currentPassword = "CorrectHorse1!", newPassword = "RotatedHorse22!" }, seed.U1Token);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // honest pair: NEW password 200, OLD password 401 (credential rotation survived)
        var newLogin = await SendAsync(HttpMethod.Post, "/api/v1/auth/login",
            new { email = seed.U1Email, password = "RotatedHorse22!" });
        Assert.True(newLogin.IsSuccessStatusCode, "rotated password must open");

        var oldLogin = await SendAsync(HttpMethod.Post, "/api/v1/auth/login",
            new { email = seed.U1Email, password = "CorrectHorse1!" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        // spec 05 surface: the anonymous reset REQUEST answers the same as always (anti-enum 204)
        var reset = await SendAsync(HttpMethod.Post, "/api/v1/auth/reset-password",
            new { email = seed.U1Email });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    }
}
