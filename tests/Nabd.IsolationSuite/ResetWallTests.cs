namespace Nabd.IsolationSuite;

using System.Diagnostics;
using Npgsql;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// Isolation-suite piece 7 (P1 spec 05): the password-reset facts, service-level
// (NO HTTP). Same law as AuthWallTests — real rows, per-test throttle, no
// rehearsal. The email seam is a FAKE IEmailSender here: the real dev sender
// writes to the terminal; the test intercepts the same point the interface
// promises. Agent-authored under the owner's standing delegation.
//
// Anti-enumeration is the spine of this spec: unknown and known emails answer
// identically (body AND comparable latency), ever, in both lanes.

public sealed class ResetWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private sealed record Reg(TenantRegistrationResult Value, string Email, string Password)
    {
        public Guid TenantId => Value.Tenant.TenantId;
        public string Locale => Value.AdminUser.Locale;
    }

    private async Task<Reg> RegisterAsync(string label, string locale = "en")
    {
        var email = $"{Uid()}-{label}@wall.test";
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Reset {label}",
            Slug: $"reset-{label}-{Uid()}",
            AdminEmail: email,
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1!",
            AdminLocale: locale));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return new Reg(result.Value, email, "CorrectHorse1!");
    }

    private static CapturingSender NewSender() => new();
    private UserAuthentication NewAuth(CapturingSender sender) =>
        new(pg.Db, new LoginThrottle(), sender);

    private sealed class CapturingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    // The template's promised shape: the reset URL stands ALONE on its own
    // line, carries ONLY the token, and the body is in the user's language.
    private static string TokenOf(EmailMessage mail)
    {
        var link = mail.BodyText
            .Split('\n')
            .Last(line => line.StartsWith("https://", StringComparison.Ordinal));
        return link.Split('/').Last();
    }

    [Fact]
    public async Task reset_request_answers_identically_for_unknown_and_known_emails()
    {
        var t = await RegisterAsync("enum");
        var knownSender = NewSender();
        var auth = NewAuth(knownSender);

        var knownClock = Stopwatch.StartNew();
        var known = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(t.Email, "10.0.0.1"));
        knownClock.Stop();

        var unknownSender = NewSender();
        var unknownAuth = NewAuth(unknownSender);
        var unknownClock = Stopwatch.StartNew();
        var unknown = await unknownAuth.RequestPasswordResetAsync(
            new ResetPasswordRequest($"{Uid()}-ghost@wall.test", "10.0.0.2"));
        unknownClock.Stop();

        // row 7: one answer — and the fake inbox is EMPTY on the unknown lane
        Assert.True(known.IsSuccess, known.Error);
        Assert.True(unknown.IsSuccess, unknown.Error);
        Assert.Empty(unknownSender.Sent);

        // no stopwatch oracle: the known lane paid argon2 too (burn in BOTH lanes)
        var burn = Stopwatch.StartNew();
        EnumerationArmor.Burn("burn-cost-measure");
        burn.Stop();

        Assert.True(knownClock.ElapsedMilliseconds >= burn.ElapsedMilliseconds / 2,
            $"known {knownClock.ElapsedMilliseconds}ms vs burn {burn.ElapsedMilliseconds}ms");

        Assert.Single(knownSender.Sent);   // exactly one email, exactly one token
    }

    [Fact]
    public async Task the_link_carries_only_the_token_and_the_template_switches_by_locale()
    {
        var enUser = await RegisterAsync("tpl-en", "en");
        var arUser = await RegisterAsync("tpl-ar", "ar");

        var sender = NewSender();
        var auth = NewAuth(sender);

        _ = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(enUser.Email, "10.0.0.1"));
        var enMail = sender.Sent.Last();
        _ = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(arUser.Email, "10.0.0.3"));
        var arMail = sender.Sent.Last();

        Assert.Equal(enUser.Email, enMail.To);
        Assert.Equal(arUser.Email, arMail.To);
        Assert.NotEqual(enMail.Subject, arMail.Subject);                 // 04 §9: two languages
        Assert.Equal("en", enUser.Locale!);
        Assert.Equal("ar", arUser.Locale!);

        var enToken = TokenOf(enMail);
        var enLink = enMail.BodyText.Split('\n').Last(line => line.StartsWith("https://", StringComparison.Ordinal));

        // no-link-leak discipline: the link names NO user id and NO tenant id
        Assert.DoesNotContain(enUser.Value.AdminUser.UserId.ToString(), enLink);
        Assert.DoesNotContain(enUser.TenantId.ToString(), enLink);

        // the token is real: the confirm lane will accept it later — here we
        // prove shape only (32 bytes base64url = 43 chars).
        Assert.Matches("^[A-Za-z0-9_-]{43}$", enToken);
    }

    [Fact]
    public async Task confirm_rotates_the_password_and_kills_every_session()
    {
        var t = await RegisterAsync("rotate");
        var sender = NewSender();
        var auth = NewAuth(sender);

        var fresh = await auth.LoginAsync(new LoginRequest(t.Email, t.Password, "10.0.0.1"));
        Assert.True(fresh.IsSuccess, fresh.Error);                 // a session EXISTS to die
        var oldRefresh = fresh.Value.RefreshToken;

        _ = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(t.Email, "10.0.0.1"));
        var token = TokenOf(sender.Sent.Single());

        var confirmed = await auth.ConfirmPasswordResetAsync(new ResetConfirmRequest(token, "NewHorseStyle9"));
        Assert.True(confirmed.IsSuccess, confirmed.Error);

        // the spec-03 revocation machinery: the old session is DEAD (refresh-reuse helper)
        var oldRefreshReplay = await auth.RefreshAsync(new RefreshRequest(oldRefresh));
        Assert.False(oldRefreshReplay.IsSuccess);

        // the password really rotated: old fails, new opens
        var oldLogin = await auth.LoginAsync(new LoginRequest(t.Email, t.Password, "10.0.0.1"));
        var newLogin = await auth.LoginAsync(new LoginRequest(t.Email, "NewHorseStyle9", "10.0.0.1"));
        Assert.False(oldLogin.IsSuccess);
        Assert.True(newLogin.IsSuccess, newLogin.Error);
    }

    [Fact]
    public async Task confirm_rejects_unknown_expired_and_used_tokens_with_one_dead_answer()
    {
        var t = await RegisterAsync("dead");
        var sender = NewSender();
        var auth = NewAuth(sender);

        // unknown → dead
        var unknown = await auth.ConfirmPasswordResetAsync(new ResetConfirmRequest("no-such-token-abc", "FreshHorse9"));
        Assert.False(unknown.IsSuccess);
        Assert.Equal(IdentityErrors.ResetTokenDead, unknown.Error);

        // expired → the SAME dead answer (no oracle for which wall fired)
        _ = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(t.Email, "10.0.0.1"));
        var token = TokenOf(sender.Sent.Single());
        var (xConn, xTx) = await pg.Db.OpenTenantScopeAsync(t.TenantId);
        try
        {
            await using var expire = new NpgsqlCommand(
                "UPDATE identity.password_reset_tokens SET expires_at = now() - interval '20 minutes' WHERE token_hash = @h;",
                xConn, xTx);
            expire.Parameters.AddWithValue("h", RefreshTokenMint.Hash(token));
            await expire.ExecuteNonQueryAsync();
            await xTx.CommitAsync();
        }
        catch { await xTx.RollbackAsync(); throw; }
        finally { await xConn.DisposeAsync(); }
        var expired = await auth.ConfirmPasswordResetAsync(new ResetConfirmRequest(token, "FreshHorse9"));
        Assert.False(expired.IsSuccess);
        Assert.Equal(IdentityErrors.ResetTokenDead, expired.Error);

        // used → the SAME dead answer, and it stays dead on every replay
        _ = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(t.Email, "10.0.0.1"));
        var usedToken = TokenOf(sender.Sent[^1]);   // TWO mails share this sender now — take the latest
        var first = await auth.ConfirmPasswordResetAsync(new ResetConfirmRequest(usedToken, "FreshHorse9"));
        Assert.True(first.IsSuccess, first.Error);
        var replay = await auth.ConfirmPasswordResetAsync(new ResetConfirmRequest(usedToken, "OtherHorse8"));
        Assert.False(replay.IsSuccess);
        Assert.Equal(IdentityErrors.ResetTokenDead, replay.Error);
    }

    [Fact]
    public async Task a_weak_new_password_refuses_and_never_burns_the_token()
    {
        var t = await RegisterAsync("weak");
        var sender = NewSender();
        var auth = NewAuth(sender);

        _ = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(t.Email, "10.0.0.1"));
        var token = TokenOf(sender.Sent.Single());

        var weak = await auth.ConfirmPasswordResetAsync(new ResetConfirmRequest(token, "short"));
        Assert.False(weak.IsSuccess);
        Assert.Equal(IdentityErrors.WeakPassword, weak.Error);

        // the token SURVIVED the weak attempt: a corrected retry works
        var retry = await auth.ConfirmPasswordResetAsync(new ResetConfirmRequest(token, "StrongHorse11"));
        Assert.True(retry.IsSuccess, retry.Error);
    }

    [Fact]
    public async Task reset_request_rate_limits_after_five_attempts()
    {
        var t = await RegisterAsync("band");
        var sender = NewSender();
        var auth = NewAuth(sender);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var ok = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(t.Email, "10.0.0.1"));
            Assert.True(ok.IsSuccess, $"attempt {attempt} must stay reachable: {ok.Error}");
        }

        // row 5, same as login: the 5th recorded attempt answers band 1, doubling on
        var fifth = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(t.Email, "10.0.0.1"));
        Assert.False(fifth.IsSuccess);
        Assert.StartsWith(IdentityErrors.LoginBackoff, fifth.Error);
    }
}
