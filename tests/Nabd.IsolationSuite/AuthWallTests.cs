namespace Nabd.IsolationSuite;

using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using Npgsql;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// Isolation-suite piece 4 (P1 spec 03): the scripted auth facts, service-level
// (NO HTTP) — the removal-drill law again. Registration is REAL so every wall
// stands against real rows; throttles are per-test instances so facts never
// share a streak. Agent-authored under the owner's standing delegation
// ("you responsible for tests"), 2026-11-08; reviewed in the study file.
//
// Honest mappings on record (spec 03 review):
// - denial "byte-equality" asserts at the service boundary = error-code equality
//   (body shape equality is the API host's Translate — one switch, one shape);
// - the "expired JWT" and "missing-tid claim" scripts DEFER to spec 04: no JWT
//   validator or claim wall exists yet. The suite owns what exists today.

public sealed class AuthWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Auth {label}",
            Slug: $"auth-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@wall.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    private UserAuthentication NewAuth(IEmailSender? sender = null) =>
        new(pg.Db, new LoginThrottle(), sender ?? new NullSender());

    // spec 05 grew the constructor: auth facts that never touch email pass a silent box
    private sealed class NullSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- fact 1: the denial wall (plan/07 §3 row 7) -------------------------

    [Fact]
    public async Task login_denials_are_code_equal_and_burn_comparable_time()
    {
        var t = await RegisterAsync("h");
        var auth = NewAuth();

        var clock = Stopwatch.StartNew();
        var wrongPass = await auth.LoginAsync(new LoginRequest(t.AdminUser.Email, "WrongHorse9", "10.0.0.1"));
        clock.Stop();

        var clock2 = Stopwatch.StartNew();
        var wrongMail = await auth.LoginAsync(new LoginRequest($"{Uid()}-ghost@wall.test", "CorrectHorse1", "10.0.0.1"));
        clock2.Stop();

        Assert.False(wrongPass.IsSuccess);
        Assert.False(wrongMail.IsSuccess);
        // one answer, equal at the service boundary — no oracle for which half was wrong
        Assert.Equal(IdentityErrors.InvalidCredentials, wrongPass.Error);
        Assert.Equal(IdentityErrors.InvalidCredentials, wrongMail.Error);

        // the honest bound: the wrong-EMAIL lane paid at least HALF of one argon2
        // burn (the dummy verify), so a stopwatch can't pick the email lane cheaply.
        // Ratio, not milliseconds — CI-hardware-proof.
        var burn = Stopwatch.StartNew();
        EnumerationArmor.Burn("burn-cost-measure");
        burn.Stop();

        Assert.True(clock.ElapsedMilliseconds >= burn.ElapsedMilliseconds / 2,
            $"wrong-password {clock.ElapsedMilliseconds}ms vs burn {burn.ElapsedMilliseconds}ms");
        Assert.True(clock2.ElapsedMilliseconds >= burn.ElapsedMilliseconds / 2,
            $"wrong-email {clock2.ElapsedMilliseconds}ms vs burn {burn.ElapsedMilliseconds}ms");
    }

    // ---- facts 2+3: rotation, replay, revoke-all (row 3) ---------------------

    [Fact]
    public async Task refresh_rotates_and_a_replay_kills_every_session()
    {
        var t = await RegisterAsync("i");
        var auth = NewAuth();

        var login = await auth.LoginAsync(new LoginRequest(t.AdminUser.Email, "CorrectHorse1", "10.0.0.2"));
        Assert.True(login.IsSuccess, login.Error ?? "login refused");

        var rt1 = login.Value.RefreshToken;

        // rotation: a NEW pair is born on use
        var first = await auth.RefreshAsync(new RefreshRequest(rt1));
        Assert.True(first.IsSuccess, first.Error ?? "refresh refused");
        Assert.NotEqual(rt1, first.Value.RefreshToken);

        // the spent token is dead — presenting it is a REPLAY...
        var spent = await auth.RefreshAsync(new RefreshRequest(rt1));
        Assert.False(spent.IsSuccess);
        Assert.Equal(IdentityErrors.InvalidCredentials, spent.Error);

        // ...and the replay is a security event: the HONEST token from rotation
        // died too. Whole-user revoke proven client-side — no DB peeking,
        // exactly what a thief outside the wall could do.
        var honest = await auth.RefreshAsync(new RefreshRequest(first.Value.RefreshToken));
        Assert.False(honest.IsSuccess);
        Assert.Equal(IdentityErrors.InvalidCredentials, honest.Error);
    }

    // ---- fact: logout is idempotent and its lease dies -----------------------

    [Fact]
    public async Task logout_revokes_the_lease_and_stays_idempotent()
    {
        var t = await RegisterAsync("j");
        var auth = NewAuth();

        var login = await auth.LoginAsync(new LoginRequest(t.AdminUser.Email, "CorrectHorse1", "10.0.0.3"));
        Assert.True(login.IsSuccess, login.Error ?? "login refused");
        var rt = login.Value.RefreshToken;

        var gone = await auth.LogoutAsync(new RefreshRequest(rt));
        Assert.True(gone.IsSuccess, gone.Error ?? "logout failed");

        var after = await auth.RefreshAsync(new RefreshRequest(rt));
        Assert.False(after.IsSuccess);                       // the lease is dead, not expired

        var repeat = await auth.LogoutAsync(new RefreshRequest(rt));
        Assert.True(repeat.IsSuccess, repeat.Error ?? "second logout failed");   // idempotent: dead = done
    }

    // ---- fact 4: expired refresh refuses — it never rotates ------------------

    [Fact]
    public async Task expired_refresh_is_refused_and_never_rotated()
    {
        var t = await RegisterAsync("k");
        var auth = NewAuth();
        var tenantId = t.Tenant.TenantId;
        var userId = t.AdminUser.UserId;

        // seed a row that was born dead (expiry in the past) — inside a real
        // tenant scope, exactly like the service's own INSERT would run.
        var deadToken = RefreshTokenMint.New();
        var (conn, tx) = await pg.Db.OpenTenantScopeAsync(tenantId);
        try
        {
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO identity.refresh_tokens (user_id, token_hash, expires_at) VALUES (@u, @h, now() - interval '1 hour');",
                conn, tx))
            {
                cmd.Parameters.AddWithValue("u", userId);
                cmd.Parameters.AddWithValue("h", RefreshTokenMint.Hash(deadToken));
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        finally
        {
            await conn.DisposeAsync();   // dispose rolls back an open tx implicitly — but we committed above
        }

        var expired = await auth.RefreshAsync(new RefreshRequest(deadToken));
        Assert.False(expired.IsSuccess);
        Assert.Equal(IdentityErrors.InvalidCredentials, expired.Error);
    }

    // ---- fact 5: the backoff ladder (row 5) — direct, no HTTP ----------------

    [Fact]
    public void backoff_bands_escalate_and_success_resets()
    {
        var throttle = new LoginThrottle();
        var key = "ip:9.9.9.9|account:bot@wall.test";

        Assert.Equal(0, throttle.DelaySeconds(key));                       // no history, no band
        for (var i = 0; i < 4; i++)
        {
            throttle.RecordFailure(key);
            Assert.Equal(0, throttle.DelaySeconds(key));                   // fails 1-4 still answer plainly
        }

        throttle.RecordFailure(key);
        Assert.Equal(1, throttle.DelaySeconds(key));                       // 5th failure: first band 1s
        throttle.RecordFailure(key);
        Assert.Equal(2, throttle.DelaySeconds(key));                       // doubling
        throttle.RecordFailure(key);
        Assert.Equal(4, throttle.DelaySeconds(key));                       // doubling

        throttle.RecordSuccess(key);
        Assert.Equal(0, throttle.DelaySeconds(key));                       // reset-on-success: no permanent lockout
    }

    // ---- fact 6: password change kills every lease + retires the old proof ---

    [Fact]
    public async Task password_change_revokes_all_sessions_and_retires_the_old_password()
    {
        var t = await RegisterAsync("l");
        var auth = NewAuth();
        var tenantId = t.Tenant.TenantId;
        var adminId = t.AdminUser.UserId;
        var acting = new CallerContext(adminId, tenantId, ["tenant_admin"]);

        var login = await auth.LoginAsync(new LoginRequest(t.AdminUser.Email, "CorrectHorse1", "10.0.0.4"));
        Assert.True(login.IsSuccess, login.Error ?? "login refused");
        var oldLease = login.Value.RefreshToken;

        // theft-style attempt: WRONG current password must change nothing
        var theft = await auth.ChangePasswordAsync(
            acting, tenantId, adminId, new ChangePasswordRequest("TotallyWrong99", "CorrectHorse2"));
        Assert.False(theft.IsSuccess);
        Assert.Equal(IdentityErrors.InvalidCredentials, theft.Error);

        var stillHimself = await auth.LoginAsync(new LoginRequest(t.AdminUser.Email, "CorrectHorse1", "10.0.0.4"));
        Assert.True(stillHimself.IsSuccess, "old password died from a refused change");   // no silent rotation

        // the real change
        var change = await auth.ChangePasswordAsync(
            acting, tenantId, adminId, new ChangePasswordRequest("CorrectHorse1", "CorrectHorse2"));
        Assert.True(change.IsSuccess, change.Error ?? "change refused");

        // the pre-change lease is dead (revoked-ALL, not decayed)
        var stolenLease = await auth.RefreshAsync(new RefreshRequest(oldLease));
        Assert.False(stolenLease.IsSuccess);
        Assert.Equal(IdentityErrors.InvalidCredentials, stolenLease.Error);

        // the OLD proof no longer opens the door; the NEW proof does
        var oldProof = await auth.LoginAsync(new LoginRequest(t.AdminUser.Email, "CorrectHorse1", "10.0.0.5"));
        Assert.False(oldProof.IsSuccess);
        var newProof = await auth.LoginAsync(new LoginRequest(t.AdminUser.Email, "CorrectHorse2", "10.0.0.5"));
        Assert.True(newProof.IsSuccess, newProof.Error ?? "new password refused");
    }

    // ---- fact 7: the access token's anatomy (row 2) ---------------------------

    [Fact]
    public async Task access_tokens_carry_the_operational_claims()
    {
        var t = await RegisterAsync("m");
        var auth = NewAuth();

        var login = await auth.LoginAsync(new LoginRequest(t.AdminUser.Email, "CorrectHorse1", "10.0.0.6"));
        Assert.True(login.IsSuccess, login.Error ?? "login refused");

        // parse-only: key verification is the ApiHost wall's job (spec 04). Here we
        // assert what the token CARRIES — payload keys, not mapped claim types,
        // so the inbound claim-type map cannot rename our facts under us.
        var payload = new JwtSecurityTokenHandler().ReadJwtToken(login.Value.AccessToken).Payload;

        Assert.Equal(t.AdminUser.UserId.ToString(), Convert.ToString(payload["sub"]));
        Assert.Equal(t.Tenant.TenantId.ToString(), Convert.ToString(payload["tid"]));
        Assert.Equal("tenant_admin", Convert.ToString(payload["roles"]));

        var exp = Convert.ToDouble(payload["exp"]);
        var remaining = exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(remaining, 700, 900);   // the 15-min lease, with honest suite-latency margins
    }
}
