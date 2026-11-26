namespace Nabd.IsolationSuite;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

// P5 spec 06 S2 — the submit DOOR over real HTTP (WebApplicationFactory: full
// middleware chain, JWT wall, policies, the controller, the service, the 0030
// function under the app role). S1 proved the lane; this proves the door speaks
// it honestly.
//
// Facts owned here: 1 (auth: 401 / every role 201), 2 + 9 (the stamp cannot be
// forged from the body, and the answer echoes only the receipt), 3 (the four
// 422 codes AND their inclusive edges in the table's own unit), 4 (the 429
// carries the wait), and the no-forged-stamp branch of
// F5 (a token whose directory row is gone answers 401, never a row).
//
// NOT here, deliberately: the admin read (S3) and the button (S4).

public sealed class FeedbackDoorTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture _pg;

    // the REAL host boots in-process (same shape as HttpWallTests), so the door
    // is proven with the middleware chain, not a hand-built controller call.
    private readonly WebApplicationFactory<Program> _factory =
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));

    private readonly HttpClient _client;

    public FeedbackDoorTests(PostgresFixture pg)
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

    private sealed record Tenant(Guid Id, string AdminEmail, string AdminPassword, Guid AdminId);

    private sealed record Seat(string Email, string Password, string Token, Guid UserId);

    // ---- seeds: everything through REAL doors, tokens from real logins --------

    private async Task<Tenant> RegisterAsync()
    {
        var email = $"{Uid()}@fb-door.test";
        var posted = await SendAsync(HttpMethod.Post, "/api/v1/tenants", new
        {
            name = $"FB {Uid()}",
            slug = $"fb-door-{Uid()}",
            adminEmail = email,
            adminDisplayName = "Feedback Admin",
            adminPassword = "CorrectHorse1!",
            adminLocale = "en",
        });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var root = JsonDocument.Parse(await posted.Content.ReadAsStringAsync()).RootElement;
        return new Tenant(
            root.GetProperty("tenant").GetProperty("tenantId").GetGuid(),
            email, "CorrectHorse1!",
            root.GetProperty("adminUser").GetProperty("userId").GetGuid());
    }

    private async Task<Seat> LoginAsync(string email, string password)
    {
        var login = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password });
        Assert.True(login.IsSuccessStatusCode, $"login {email} → {(int)login.StatusCode}");
        var root = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        return new Seat(email, password,
            root.GetProperty("accessToken").GetString()!,
            root.GetProperty("user").GetProperty("userId").GetGuid());
    }

    private async Task<Seat> CreateViewerAsync(Tenant tenant, string adminToken)
    {
        var email = $"{Uid()}-viewer@fb-door.test";
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

    private async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();

    private static object Body(
        string message = "التبريد لا يعمل منذ الصباح",
        string page = "/alerts",
        string locale = "ar",
        string? contactEmail = null) =>
        new { message, page, locale, contactEmail };

    // ---- owner-lane probes (bookkeeping, never the door itself) --------------

    private async Task<NpgsqlConnection> OwnerAsync() => await _pg.Owner.OpenConnectionAsync();

    private async Task<long> CountAsync(Guid tenant)
    {
        await using var conn = await OwnerAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM feedback.messages WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// Moves the 30 s window without a sleep (S1's own trick, reused).
    private async Task BackdateAsync(Guid user)
    {
        await using var conn = await OwnerAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE feedback.messages SET created_at = now() - interval '31 seconds' WHERE user_id = @u", conn);
        cmd.Parameters.AddWithValue("u", user);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- fact 1: anonymous 401, every authenticated role 201 ------------------

    [Fact]
    public async Task anonymous_is_401_and_every_authenticated_role_may_submit()
    {
        var tenant = await RegisterAsync();
        var admin = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);
        var viewer = await CreateViewerAsync(tenant, admin.Token);

        var anonymous = await SendAsync(HttpMethod.Post, "/api/v1/feedback", Body());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var asViewer = await SendAsync(HttpMethod.Post, "/api/v1/feedback", Body("viewer writes"), viewer.Token);
        Assert.Equal(HttpStatusCode.Created, asViewer.StatusCode);

        var asAdmin = await SendAsync(HttpMethod.Post, "/api/v1/feedback",
            Body("tenant admin writes"), admin.Token);
        Assert.Equal(HttpStatusCode.Created, asAdmin.StatusCode);

        // platform_admin: the matrix has no door that ASSIGNS the role (FR-03),
        // so the suite makes the directory row itself — then logs in for real,
        // which is how a token gets its single `roles` claim.
        await using (var conn = await OwnerAsync())
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE identity.user_roles SET role_code = 'platform_admin' WHERE user_id = @u AND tenant_id = @t",
                conn);
            cmd.Parameters.AddWithValue("u", admin.UserId);
            cmd.Parameters.AddWithValue("t", tenant.Id);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        await BackdateAsync(admin.UserId);    // the same user must pass the window again
        var platform = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);

        var asPlatform = await SendAsync(HttpMethod.Post, "/api/v1/feedback",
            Body("platform writes"), platform.Token);
        Assert.Equal(HttpStatusCode.Created, asPlatform.StatusCode);

        Assert.Equal(3L, await CountAsync(tenant.Id));
    }

    // ---- facts 2 + 9: the stamp is not in the body; the answer is a receipt --

    [Fact]
    public async Task a_forged_body_cannot_set_identity_and_the_response_carries_only_the_receipt()
    {
        var tenant = await RegisterAsync();
        var admin = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);
        var other = await RegisterAsync();     // a second tenant: the forgery's target

        // every identity field the table has, forged in the body
        var posted = await SendAsync(HttpMethod.Post, "/api/v1/feedback", new
        {
            message = "  جهاز التبريد صامت  ",
            page = "/alerts",
            locale = "ar",
            contactEmail = "  reply@fb-door.test  ",
            tenantId = other.Id,
            userId = other.AdminId,
            authorEmail = "forged@fb-door.test",
            authorDisplayName = "Forged Name",
        }, admin.Token);

        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var raw = await BodyAsync(posted);

        // fact 9: exactly the receipt, nothing else — no email, no tenant, no id of anyone else
        var receipt = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(2, receipt.EnumerateObject().Count());
        Assert.Equal(["createdAt", "id"], receipt.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain(other.Id.ToString(), raw);
        Assert.DoesNotContain("forged@fb-door.test", raw);
        Assert.DoesNotContain("forged", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tenant.AdminEmail, raw);

        // fact 2: the stored row carries the TOKEN's tenant/user and the DIRECTORY's email
        await using var conn = await OwnerAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, user_id, author_email::text, author_display_name,
                   page, locale, message, contact_email
              FROM feedback.messages WHERE id = @id
            """, conn);
        cmd.Parameters.AddWithValue("id", receipt.GetProperty("id").GetGuid());
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(tenant.Id, reader.GetGuid(0));                 // not `other.Id`
        Assert.Equal(admin.UserId, reader.GetGuid(1));              // not `other.AdminId`
        Assert.Equal(tenant.AdminEmail, reader.GetString(2));       // the directory's, not "forged@"
        Assert.Equal("Feedback Admin", reader.GetString(3));
        Assert.Equal("/alerts", reader.GetString(4));
        Assert.Equal("ar", reader.GetString(5));
        Assert.Equal("جهاز التبريد صامت", reader.GetString(6));       // trimmed before the CHECK
        Assert.Equal("reply@fb-door.test", reader.GetString(7));
    }

    // ---- fact 3: the bounds answer 422 and store nothing ---------------------

    [Fact]
    public async Task the_bounds_refuse_with_422_codes_and_nothing_is_stored()
    {
        var tenant = await RegisterAsync();
        var admin = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);

        async Task ExpectAsync(object body, string code)
        {
            var res = await SendAsync(HttpMethod.Post, "/api/v1/feedback", body, admin.Token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            var root = JsonDocument.Parse(await BodyAsync(res)).RootElement;
            Assert.Equal(code, root.GetProperty("code").GetString());
        }

        await ExpectAsync(Body(message: "   "), "feedback:invalid_message");
        await ExpectAsync(Body(message: new string('x', 2001)), "feedback:invalid_message");
        await ExpectAsync(Body(page: "/" + new string('p', 200)), "feedback:invalid_page");
        await ExpectAsync(Body(page: "  "), "feedback:invalid_page");
        await ExpectAsync(Body(locale: "fr"), "feedback:invalid_locale");
        await ExpectAsync(Body(contactEmail: "not-an-email"), "feedback:invalid_email");
        await ExpectAsync(Body(contactEmail: "two@at@fb-door.test"), "feedback:invalid_email");
        await ExpectAsync(Body(contactEmail: new string('a', 300) + "@x.test"), "feedback:invalid_email");
        await ExpectAsync(Body(message: null!), "feedback:invalid_message");

        Assert.Equal(0L, await CountAsync(tenant.Id));
    }

    // ---- fact 3's edges: the ceilings are inclusive, in CHARACTERS ----------
    //
    // Two laws in one test: (a) 2,000/200 are ACCEPTED, not "one under"; (b) the
    // unit is the table's, not .NET's — 2,000 astral characters are 4,000 UTF-16
    // units, and `length()` in Postgres says 2,000, so the door must agree.

    [Fact]
    public async Task the_bounds_accept_their_edges_and_count_characters_not_utf16_units()
    {
        var tenant = await RegisterAsync();
        var admin = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);

        var atEdge = await SendAsync(HttpMethod.Post, "/api/v1/feedback",
            Body(message: new string('x', 2000), page: "/" + new string('p', 199), contactEmail: ""),
            admin.Token);
        Assert.Equal(HttpStatusCode.Created, atEdge.StatusCode);

        await using (var conn = await OwnerAsync())
        await using (var cmd = new NpgsqlCommand("""
            SELECT length(message), length(page), contact_email
              FROM feedback.messages WHERE user_id = @u ORDER BY created_at DESC LIMIT 1
            """, conn))
        {
            cmd.Parameters.AddWithValue("u", admin.UserId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2000, reader.GetInt32(0));      // the CHECK's ceiling, inclusive
            Assert.Equal(200, reader.GetInt32(1));
            Assert.True(reader.IsDBNull(2));             // "" became NULL in the function
        }

        await BackdateAsync(admin.UserId);
        var astral = await SendAsync(HttpMethod.Post, "/api/v1/feedback",
            Body(message: string.Concat(Enumerable.Repeat("\U0001F600", 2000))), admin.Token);
        Assert.Equal(HttpStatusCode.Created, astral.StatusCode);

        await using (var conn = await OwnerAsync())
        await using (var cmd = new NpgsqlCommand(
            "SELECT length(message) FROM feedback.messages WHERE user_id = @u ORDER BY created_at DESC LIMIT 1",
            conn))
        {
            cmd.Parameters.AddWithValue("u", admin.UserId);
            Assert.Equal(2000, (int)(await cmd.ExecuteScalarAsync())!);
        }

        await BackdateAsync(admin.UserId);
        var over = await SendAsync(HttpMethod.Post, "/api/v1/feedback",
            Body(message: string.Concat(Enumerable.Repeat("\U0001F600", 2001))), admin.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
    }

    // ---- fact 4: the window is the door's 429, with a positive wait ----------

    [Fact]
    public async Task a_second_message_inside_the_window_is_429_with_retry_after_seconds()
    {
        var tenant = await RegisterAsync();
        var admin = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);

        var first = await SendAsync(HttpMethod.Post, "/api/v1/feedback", Body("first"), admin.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await SendAsync(HttpMethod.Post, "/api/v1/feedback", Body("second"), admin.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var root = JsonDocument.Parse(await BodyAsync(second)).RootElement;
        Assert.Equal("feedback:rate_limited", root.GetProperty("code").GetString());
        Assert.InRange(root.GetProperty("retryAfterSeconds").GetInt32(), 1, 30);
        // the wait also rides the standard header (REV 4 C4)
        Assert.Equal(
            root.GetProperty("retryAfterSeconds").GetInt32().ToString(),
            second.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0"));

        Assert.Equal(1L, await CountAsync(tenant.Id));   // refused means not stored

        await BackdateAsync(admin.UserId);
        var third = await SendAsync(HttpMethod.Post, "/api/v1/feedback", Body("third"), admin.Token);
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        Assert.Equal(2L, await CountAsync(tenant.Id));
    }

    // ---- F5's honesty branch: no directory row → no stamp, no row, 401 -------

    [Fact]
    public async Task a_token_whose_directory_row_is_gone_gets_401_and_no_row()
    {
        var tenant = await RegisterAsync();
        var admin = await LoginAsync(tenant.AdminEmail, tenant.AdminPassword);
        var viewer = await CreateViewerAsync(tenant, admin.Token);

        // the real delete door removes the user; the already-issued token is
        // still cryptographically valid — exactly the window this branch owns.
        var deleted = await SendAsync(HttpMethod.Delete,
            $"/api/v1/tenants/{tenant.Id}/users/{viewer.UserId}", null, admin.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var res = await SendAsync(HttpMethod.Post, "/api/v1/feedback", Body("ghost"), viewer.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var root = JsonDocument.Parse(await BodyAsync(res)).RootElement;
        Assert.Equal("feedback:user_not_found", root.GetProperty("code").GetString());

        Assert.Equal(0L, await CountAsync(tenant.Id));
    }
}
