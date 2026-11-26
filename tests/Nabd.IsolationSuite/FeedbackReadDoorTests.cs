namespace Nabd.IsolationSuite;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Nabd.Admin;
using Nabd.Admin.Contracts;
using Nabd.Identity.Contracts;
using Npgsql;
using Xunit;

// P5 spec 06 S3 — the OWNER's read: GET /api/v1/platform/feedback over the real
// host (full middleware chain, JWT wall, the audited admin lane) on real Postgres.
//
// Facts owned here: 6 (tenant_admin/viewer 403; platform_admin newest-first with
// tenant name + author identity; a deleted author's row keeps its stamped email
// (F10); `total` is the table's true count, not `items.Count` (C6); an invalid
// `limit` is 422 `feedback:invalid_limit`; every crossing writes its audit line).
//
// NOT here, deliberately: the submit door (S2) and the button (S4).

public sealed class FeedbackReadDoorTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture _pg;

    private readonly WebApplicationFactory<Program> _factory =
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    private readonly HttpClient _client;

    public FeedbackReadDoorTests(PostgresFixture pg)
    {
        _pg = pg;
        _client = _factory.CreateClient();
    }

    private HttpClient Client => _client;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private sealed record Tenant(Guid Id, string Name, string AdminEmail, string AdminPassword, Guid AdminId);

    private sealed record Seat(string Email, string Token, Guid UserId);

    // ---- seeds: everything through REAL doors, tokens from real logins --------

    private async Task<Tenant> RegisterAsync()
    {
        var name = $"Read {Uid()}";
        var email = $"{Uid()}@fb-read.test";
        var posted = await SendAsync(HttpMethod.Post, "/api/v1/tenants", new
        {
            name,
            slug = $"fb-read-{Uid()}",
            adminEmail = email,
            adminDisplayName = "Feedback Admin",
            adminPassword = "CorrectHorse1!",
            adminLocale = "en",
        });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var root = JsonDocument.Parse(await posted.Content.ReadAsStringAsync()).RootElement;
        return new Tenant(
            root.GetProperty("tenant").GetProperty("tenantId").GetGuid(),
            name, email, "CorrectHorse1!",
            root.GetProperty("adminUser").GetProperty("userId").GetGuid());
    }

    private async Task<Seat> LoginAsync(string email, string password)
    {
        var login = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password });
        Assert.True(login.IsSuccessStatusCode, $"login {email} → {(int)login.StatusCode}");
        var root = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        return new Seat(email,
            root.GetProperty("accessToken").GetString()!,
            root.GetProperty("user").GetProperty("userId").GetGuid());
    }

    /// <summary>
    /// The matrix has no door that ASSIGNS platform_admin (FR-03), so the suite
    /// makes the directory row itself, then logs in for real — the token's single
    /// `roles` claim is what the wall reads (S2's trick, reused).
    /// </summary>
    private async Task<Seat> PromoteToPlatformAsync(Tenant tenant)
    {
        await using (var conn = await OwnerAsync())
        await using (var cmd = new NpgsqlCommand(
            "UPDATE identity.user_roles SET role_code = 'platform_admin' WHERE user_id = @u AND tenant_id = @t",
            conn))
        {
            cmd.Parameters.AddWithValue("u", tenant.AdminId);
            cmd.Parameters.AddWithValue("t", tenant.Id);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        return await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);
    }

    private async Task<Seat> CreateViewerAsync(Tenant tenant, string adminToken)
    {
        var email = $"{Uid()}-viewer@fb-read.test";
        var created = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{tenant.Id}/users", new
        {
            targetTenantId = tenant.Id,
            email,
            displayName = "Feedback Viewer",
            locale = "ar",
            password = "ViewerPass11!",
            roleCode = "viewer",
        }, adminToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return await LoginAsync(email, "ViewerPass11!");
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonWeb), Encoding.UTF8, "application/json");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await Client.SendAsync(request);
    }

    private static object Body(
        string message,
        string page = "/alerts",
        string locale = "ar",
        string? contactEmail = null) =>
        new { message, page, locale, contactEmail };

    private async Task<JsonElement> SubmitAsync(string token, object body)
    {
        var posted = await SendAsync(HttpMethod.Post, "/api/v1/feedback", body, token);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        return JsonDocument.Parse(await posted.Content.ReadAsStringAsync()).RootElement;
    }

    // ---- owner-lane probes (bookkeeping, never the door itself) --------------

    private async Task<NpgsqlConnection> OwnerAsync() => await _pg.Owner.OpenConnectionAsync();

    private async Task<long> TotalAsync()
    {
        await using var conn = await OwnerAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM feedback.messages", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Pins a row's `created_at` so "newest first" is a fact, not a race.</summary>
    private async Task AgeAsync(string message, int minutes)
    {
        await using var conn = await OwnerAsync();
        await using var cmd = new NpgsqlCommand("""
            UPDATE feedback.messages
               SET created_at = now() - make_interval(mins => @m)
             WHERE message = @msg
            """, conn);
        cmd.Parameters.AddWithValue("m", minutes);
        cmd.Parameters.AddWithValue("msg", message);
        await cmd.ExecuteNonQueryAsync();
    }

    private static List<JsonElement> Items(JsonElement root) =>
        root.GetProperty("items").EnumerateArray().ToList();

    private static string MessageOf(JsonElement item) => item.GetProperty("message").GetString()!;

    // ---- fact 6a: the wall — two roles refused, the platform seat reads ------

    [Fact]
    public async Task tenant_admin_and_viewer_are_403_and_platform_admin_reads_newest_first()
    {
        var a = await RegisterAsync();
        var b = await RegisterAsync();
        var aAdmin = await LoginAsync(a.AdminEmail, a.AdminPassword);
        var bAdmin = await LoginAsync(b.AdminEmail, b.AdminPassword);
        var viewer = await CreateViewerAsync(a, aAdmin.Token);

        // three rows across TWO tenants, aged so the order is unambiguous
        await SubmitAsync(aAdmin.Token, Body("A oldest", page: "/overview"));
        await AgeAsync("A oldest", 5);
        await SubmitAsync(aAdmin.Token, Body("A newer", contactEmail: "a@fb-read.test"));
        await AgeAsync("A newer", 2);
        await SubmitAsync(bAdmin.Token, Body("B newest", locale: "en"));

        var platform = await PromoteToPlatformAsync(a);

        var expectedTotal = await TotalAsync();
        Assert.True(expectedTotal >= 3, $"setup wrote {expectedTotal} rows");

        // the door: platform_admin only
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(HttpMethod.Get, "/api/v1/platform/feedback", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Get, "/api/v1/platform/feedback", null, bAdmin.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Get, "/api/v1/platform/feedback", null, viewer.Token)).StatusCode);

        var read = await SendAsync(HttpMethod.Get, "/api/v1/platform/feedback", null, platform.Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var root = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;

        // C6: total is the TABLE's count, not the page's size
        Assert.Equal(expectedTotal, root.GetProperty("total").GetInt64());

        var items = Items(root);
        Assert.Equal(expectedTotal, items.Count);   // default limit 50 > this class's rows

        // newest first, globally
        var stamps = items.Select(i => i.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        for (var i = 1; i < stamps.Count; i++)
            Assert.True(stamps[i - 1] >= stamps[i], $"items[{i - 1}] is older than items[{i}]");

        // our three rows in the order the clock says
        var order = items.Select(MessageOf).ToList();
        Assert.True(order.IndexOf("B newest") < order.IndexOf("A newer"));
        Assert.True(order.IndexOf("A newer") < order.IndexOf("A oldest"));

        // the row carries the tenant name and the STAMPED author (F9), not a join to users
        var newer = items.First(i => MessageOf(i) == "A newer");
        Assert.Equal(a.Name, newer.GetProperty("tenantName").GetString());
        Assert.Equal(a.AdminEmail, newer.GetProperty("authorEmail").GetString());
        Assert.Equal("Feedback Admin", newer.GetProperty("authorDisplayName").GetString());
        Assert.Equal("/alerts", newer.GetProperty("page").GetString());
        Assert.Equal("ar", newer.GetProperty("locale").GetString());
        Assert.Equal("a@fb-read.test", newer.GetProperty("contactEmail").GetString());

        var newest = items.First(i => MessageOf(i) == "B newest");
        Assert.Equal(b.Name, newest.GetProperty("tenantName").GetString());
        Assert.Equal("en", newest.GetProperty("locale").GetString());
        // the API writes nulls away (`WhenWritingNull`), so "no contact email" is ABSENT
        Assert.False(newest.TryGetProperty("contactEmail", out _));
    }

    // ---- fact 6b: `limit` is a page size, never a `total` -------------------

    [Fact]
    public async Task a_small_limit_pages_the_items_but_total_stays_the_true_count()
    {
        var tenant = await RegisterAsync();
        var admin = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);
        await SubmitAsync(admin.Token, Body("paging row"));

        var platform = await PromoteToPlatformAsync(tenant);
        var expectedTotal = await TotalAsync();
        Assert.True(expectedTotal >= 1);

        var read = await SendAsync(HttpMethod.Get, "/api/v1/platform/feedback?limit=1", null, platform.Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var root = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;

        Assert.Single(Items(root));
        Assert.Equal(expectedTotal, root.GetProperty("total").GetInt64());   // C6: not 1
    }

    // ---- fact 6c: the limit's own bounds ------------------------------------

    [Fact]
    public async Task an_invalid_limit_is_422_feedback_invalid_limit()
    {
        var tenant = await RegisterAsync();
        var platform = await PromoteToPlatformAsync(tenant);

        foreach (var limit in new[] { 0, -1, 201, 999 })
        {
            var res = await SendAsync(HttpMethod.Get, $"/api/v1/platform/feedback?limit={limit}",
                null, platform.Token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("feedback:invalid_limit", root.GetProperty("code").GetString());
        }

        // 200 is legal (the ceiling is inclusive) — no 422
        var edge = await SendAsync(HttpMethod.Get, "/api/v1/platform/feedback?limit=200",
            null, platform.Token);
        Assert.Equal(HttpStatusCode.OK, edge.StatusCode);
    }

    // ---- fact 6d / F10: the row outlives its author ------------------------

    [Fact]
    public async Task a_deleted_authors_row_keeps_its_stamped_email_in_the_read()
    {
        var tenant = await RegisterAsync();
        var admin = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);
        var viewer = await CreateViewerAsync(tenant, admin.Token);

        await SubmitAsync(viewer.Token, Body("the author will be deleted"));

        // the real delete door removes the user; the row must not follow (F10)
        var deleted = await SendAsync(HttpMethod.Delete,
            $"/api/v1/tenants/{tenant.Id}/users/{viewer.UserId}", null, admin.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var platform = await PromoteToPlatformAsync(tenant);
        var read = await SendAsync(HttpMethod.Get, "/api/v1/platform/feedback?limit=200",
            null, platform.Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var row = Items(JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement)
            .First(i => MessageOf(i) == "the author will be deleted");

        Assert.Equal(viewer.Email, row.GetProperty("authorEmail").GetString());   // stamped, still there
        Assert.Equal("Feedback Viewer", row.GetProperty("authorDisplayName").GetString());
        Assert.Equal(tenant.Name, row.GetProperty("tenantName").GetString());
    }

    // ---- fact 6e: EVERY read crossing is audited (service level, the seam) --

    [Fact]
    public async Task every_read_writes_its_audit_line_and_refusals_are_audited_too()
    {
        var log = new CapturingLog();
        var admin = new PlatformTenantAdmin(_pg.Admin, log);

        var actor = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var platform = new CallerContext(actor, tenant, ["platform_admin"]);

        var ok = await admin.ListFeedbackAsync(platform, FeedbackReadLimits.Default);
        Assert.True(ok.IsSuccess, ok.Error);

        var line = log.Lines[^1];
        Assert.Contains("admin.audit", line);
        Assert.Contains("action=feedback.list", line);
        Assert.Contains($"actor={actor}", line);
        Assert.Contains("roles=platform_admin", line);
        Assert.Matches(@"listed=\d+", line);

        // a refused crossing is recorded too (plan/04 §10: the shield records the attempt)
        var denied = await admin.ListFeedbackAsync(new CallerContext(actor, tenant, ["viewer"]), 50);
        Assert.False(denied.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, denied.Error);
        Assert.Contains("outcome=denied", log.Lines[^1]);

        // and so is a bad limit — the door refuses before any SQL runs
        var bad = await admin.ListFeedbackAsync(platform, 0);
        Assert.False(bad.IsSuccess);
        Assert.Equal(AdminErrors.InvalidFeedbackLimit, bad.Error);
        Assert.Contains("outcome=invalid_limit", log.Lines[^1]);
    }

    private sealed class CapturingLog : ILogger<PlatformTenantAdmin>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
