namespace Nabd.IsolationSuite;

using Npgsql;
using NpgsqlTypes;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// P5 spec 06 S1 — the feedback LANE, before any door exists (RULES §11: prove the
// lane that stores truth before the lane that draws it).
//
// Every fact below speaks to `0030` exactly as the suite applies it (the real file,
// one implicit transaction, mirroring _apply.sh). Two lanes are used on purpose:
// the OWNER lane is bookkeeping (reading the stamped row, backdating a message to
// move the 30 s window, probing has_*_privilege), and the APP lane is the door the
// API will use — `nabd_app`, least privilege, RLS on. A fact that could pass on the
// owner lane proves nothing about the wall.
//
// What is NOT here, deliberately: HTTP codes, the Nadb.Feedback room, the admin
// read, the button. Those are S2/S3/S4.

public sealed class FeedbackTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private sealed record Author(Guid TenantId, Guid UserId, string Email, string DisplayName);

    private sealed record Submitted(Guid? Id, DateTime? CreatedAt, bool Accepted, int? RetryAfterSeconds);

    private async Task<Author> RegisterAsync(string label)
    {
        var email = $"{Uid()}-{label}@fb.test";
        var display = $"{label} Author";
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Feedback {label}",
            Slug: $"fb-{label}-{Uid()}",
            AdminEmail: email,
            AdminDisplayName: display,
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return new Author(result.Value.Tenant.TenantId, result.Value.AdminUser.UserId, email, display);
    }

    /// The one call the S2 door will make. Through the APP lane: if `nabd_app`
    /// cannot execute it, the door cannot exist.
    private async Task<Submitted> SubmitAsync(
        Guid tenant, Guid user,
        string page = "/alerts", string locale = "ar", string message = "رسالة تجربة", string? email = null)
    {
        await using var conn = await pg.Db.OpenPreAuthConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, created_at, accepted, retry_after_seconds FROM feedback.submit_message(@t, @u, @p, @l, @m, @e)",
            conn);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("u", user);
        cmd.Parameters.AddWithValue("p", page);
        cmd.Parameters.AddWithValue("l", locale);
        cmd.Parameters.AddWithValue("m", message);
        cmd.Parameters.AddWithValue("e", NpgsqlDbType.Text, (object?)email ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "submit_message returned no row (it must always answer)");
        return new Submitted(
            Id: reader.IsDBNull(0) ? null : reader.GetGuid(0),
            CreatedAt: reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTime>(1),
            Accepted: reader.GetBoolean(2),
            RetryAfterSeconds: reader.IsDBNull(3) ? null : reader.GetInt32(3));
    }

    private async Task<long> CountAsync(Guid tenant)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM feedback.messages WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// Owner-lane bookkeeping: moving a message's clock is how the window is tested
    /// without a 30 s sleep.
    private async Task BackdateAsync(Guid tenant)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE feedback.messages SET created_at = now() - interval '31 seconds' WHERE tenant_id = @t", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }

    private static PostgresException Check(PostgresException ex, string sqlState)
    {
        Assert.Equal(sqlState, ex.SqlState);
        return ex;
    }

    // ---- fact 1: the stamp comes from the directory, never from the caller ------

    [Fact]
    public async Task the_author_is_stamped_from_the_directory_and_the_message_is_stored_trimmed()
    {
        var a = await RegisterAsync("stamp");

        var res = await SubmitAsync(a.TenantId, a.UserId,
            page: "/alerts", locale: "en",
            message: "  جهاز التبريد يصرخ منذ ساعة  ",
            email: "  reply@fb.test  ");

        Assert.True(res.Accepted);
        Assert.NotNull(res.Id);
        Assert.Null(res.RetryAfterSeconds);

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, user_id, author_email::text, author_display_name,
                   page, locale, message, contact_email, created_at
              FROM feedback.messages
             WHERE id = @id
            """, conn);
        cmd.Parameters.AddWithValue("id", res.Id!.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(a.TenantId, reader.GetGuid(0));
        Assert.Equal(a.UserId, reader.GetGuid(1));                 // the token's user, not a body value
        Assert.Equal(a.Email, reader.GetString(2));                // the directory's email, stamped
        Assert.Equal(a.DisplayName, reader.GetString(3));
        Assert.Equal("/alerts", reader.GetString(4));
        Assert.Equal("en", reader.GetString(5));
        Assert.Equal("جهاز التبريد يصرخ منذ ساعة", reader.GetString(6)); // trimmed before the CHECK sees it
        Assert.Equal("reply@fb.test", reader.GetString(7));
        Assert.Equal(res.CreatedAt!.Value, reader.GetFieldValue<DateTime>(8));
    }

    // ---- fact 2: one message per user per 30 s, on the DB clock ---------------

    [Fact]
    public async Task a_second_message_inside_the_window_is_refused_with_a_wait_then_allowed()
    {
        var a = await RegisterAsync("window");

        Assert.True((await SubmitAsync(a.TenantId, a.UserId)).Accepted);

        var second = await SubmitAsync(a.TenantId, a.UserId);
        Assert.False(second.Accepted);
        Assert.Null(second.Id);
        Assert.InRange(second.RetryAfterSeconds!.Value, 1, 30);
        Assert.Equal(1L, await CountAsync(a.TenantId));            // refused means NOT stored

        await BackdateAsync(a.TenantId);

        var third = await SubmitAsync(a.TenantId, a.UserId);
        Assert.True(third.Accepted);
        Assert.Equal(2L, await CountAsync(a.TenantId));
    }

    [Fact]
    public async Task the_window_is_per_author_not_per_tenant()
    {
        var a = await RegisterAsync("perauthor");

        // a second author in the same tenant (owner lane: the directory row is bookkeeping,
        // exactly as the suite seeds templates)
        var other = Guid.NewGuid();
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            // role lives in identity.user_roles since 0004, not as a column on users
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO identity.users (id, tenant_id, email, display_name, locale)
                VALUES (@id, @t, @email, 'Second Author', 'ar');
                INSERT INTO identity.user_roles (user_id, role_code, tenant_id)
                VALUES (@id, 'viewer', @t);
                """, conn);
            cmd.Parameters.AddWithValue("id", other);
            cmd.Parameters.AddWithValue("t", a.TenantId);
            cmd.Parameters.AddWithValue("email", $"{Uid()}-second@fb.test");
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.True((await SubmitAsync(a.TenantId, a.UserId)).Accepted);
        // the first author is now inside the window; the second is not behind it
        Assert.True((await SubmitAsync(a.TenantId, other)).Accepted);
        Assert.Equal(2L, await CountAsync(a.TenantId));
    }

    // ---- fact 3: a (user, tenant) pair that does not exist stores nothing -----

    [Fact]
    public async Task a_user_from_another_tenant_gets_no_stamp_and_no_row()
    {
        var a = await RegisterAsync("crossA");
        var b = await RegisterAsync("crossB");

        // A's tenant claimed, B's user: the caller's token cannot be trusted into a stamp
        var mismatched = await SubmitAsync(a.TenantId, b.UserId);
        Assert.False(mismatched.Accepted);
        Assert.Null(mismatched.Id);

        var ghost = await SubmitAsync(a.TenantId, Guid.NewGuid());
        Assert.False(ghost.Accepted);

        Assert.Equal(0L, await CountAsync(a.TenantId));
        Assert.Equal(0L, await CountAsync(b.TenantId));
    }

    // ---- fact 4: the app role cannot read the table it writes ----------------

    [Fact]
    public async Task the_app_role_cannot_read_feedback_even_its_own_tenant()
    {
        var a = await RegisterAsync("noselect");
        Assert.True((await SubmitAsync(a.TenantId, a.UserId)).Accepted);

        await using var conn = await pg.Db.OpenPreAuthConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM feedback.messages", conn);
        Check(await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync()), "42501");
    }

    // ---- fact 5: the wall, three ways ---------------------------------------

    [Fact]
    public async Task a_direct_insert_for_another_tenant_is_refused_by_the_policy()
    {
        var a = await RegisterAsync("wallA");
        var b = await RegisterAsync("wallB");

        // control: the SAME statement with the session's own tenant is allowed —
        // so the refusal below is the wall, not a missing grant
        var (connOk, txOk) = await pg.Db.OpenTenantScopeAsync(a.TenantId);
        try
        {
            await using var ok = new NpgsqlCommand("""
                INSERT INTO feedback.messages (tenant_id, user_id, author_email, page, locale, message)
                VALUES (@t, @u, 'forged@fb.test', '/x', 'ar', 'own tenant')
                """, connOk, txOk);
            ok.Parameters.AddWithValue("t", a.TenantId);
            ok.Parameters.AddWithValue("u", a.UserId);
            await ok.ExecuteNonQueryAsync();
            await txOk.CommitAsync();
        }
        finally
        {
            await connOk.DisposeAsync();
        }

        var (connForge, txForge) = await pg.Db.OpenTenantScopeAsync(a.TenantId);
        try
        {
            await using var forged = new NpgsqlCommand("""
                INSERT INTO feedback.messages (tenant_id, user_id, author_email, page, locale, message)
                VALUES (@t, @u, 'forged@fb.test', '/x', 'ar', 'another tenant')
                """, connForge, txForge);
            forged.Parameters.AddWithValue("t", b.TenantId);
            forged.Parameters.AddWithValue("u", b.UserId);
            var ex = Check(await Assert.ThrowsAsync<PostgresException>(
                () => forged.ExecuteNonQueryAsync()), "42501");
            Assert.Contains("row-level security", ex.MessageText);
        }
        finally
        {
            await txForge.RollbackAsync();
            await connForge.DisposeAsync();
        }

        Assert.Equal(1L, await CountAsync(a.TenantId));
        Assert.Equal(0L, await CountAsync(b.TenantId));
    }

    // ---- fact 6: the bounds reject at the table, not just in the door --------

    [Fact]
    public async Task the_table_itself_refuses_an_empty_or_oversized_shape()
    {
        var a = await RegisterAsync("bounds");

        async Task ExpectViolation(string message, string locale, string page, string? email)
        {
            await using var conn = await pg.Owner.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO feedback.messages (tenant_id, user_id, author_email, page, locale, message, contact_email)
                VALUES (@t, @u, 'bounds@fb.test', @p, @l, @m, @e)
                """, conn);
            cmd.Parameters.AddWithValue("t", a.TenantId);
            cmd.Parameters.AddWithValue("u", a.UserId);
            cmd.Parameters.AddWithValue("p", page);
            cmd.Parameters.AddWithValue("l", locale);
            cmd.Parameters.AddWithValue("m", message);
            cmd.Parameters.AddWithValue("e", NpgsqlDbType.Text, (object?)email ?? DBNull.Value);
            Check(await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync()), "23514");
        }

        await ExpectViolation("", "ar", "/alerts", null);                  // blank
        await ExpectViolation(new string('x', 2001), "ar", "/alerts", null); // over 2000
        await ExpectViolation("hi", "fr", "/alerts", null);                // locale outside ar|en
        await ExpectViolation("hi", "ar", "/" + new string('x', 200), null); // page over 200
        await ExpectViolation("hi", "ar", "/alerts", new string('x', 255)); // contact over 254

        Assert.Equal(0L, await CountAsync(a.TenantId));
    }

    // ---- fact 7: the grants are exactly what the spec promised --------------

    [Fact]
    public async Task the_grants_are_least_privilege_and_the_hash_table_stays_closed()
    {
        // the app role: INSERT only, and the function is the one thing it may execute
        Assert.Equal(false, await ScalarAsync(
            "SELECT has_table_privilege('nabd_app', 'feedback.messages', 'SELECT')"));
        Assert.Equal(true, await ScalarAsync(
            "SELECT has_table_privilege('nabd_app', 'feedback.messages', 'INSERT')"));
        Assert.Equal(true, await ScalarAsync(
            "SELECT has_function_privilege('nabd_app', 'feedback.submit_message(uuid,uuid,text,text,text,text)', 'EXECUTE')"));

        // the admin lane: reads messages, still no grant on the hash-bearing table
        Assert.Equal(true, await ScalarAsync(
            "SELECT has_schema_privilege('nabd_admin', 'feedback', 'USAGE')"));
        Assert.Equal(true, await ScalarAsync(
            "SELECT has_table_privilege('nabd_admin', 'feedback.messages', 'SELECT')"));
        Assert.Equal(false, await ScalarAsync(
            "SELECT has_table_privilege('nabd_admin', 'identity.users', 'SELECT')"));

        // the function is not public: the admin lane cannot submit on someone's behalf
        Assert.Equal(false, await ScalarAsync(
            "SELECT has_function_privilege('nabd_admin', 'feedback.submit_message(uuid,uuid,text,text,text,text)', 'EXECUTE')"));
    }
}
