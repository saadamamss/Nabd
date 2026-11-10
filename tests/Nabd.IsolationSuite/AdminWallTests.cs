namespace Nabd.IsolationSuite;

using System.Data;
using Npgsql;
using Nabd.Admin;
using Nabd.Admin.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// Isolation-suite piece 8 (P1 spec 06): the audited-BYPASSRLS facts, service-level
// (NO HTTP). Same law as ResetWallTests — real roles, real rows, no rehearsal.
// The audited exception lives ONLY here: these facts prove (a) the bypass role
// exists on exactly one login, (b) that lane actually sees the whole fleet
// while the app lane stays walled, (c) the login gate folds on disable and
// reopens on enable with BOTH lanes' answers preserved, (d) the service layer
// refuses non-platform callers before any SQL runs, and (e) EVERY crossing
// writes its audit line — "the audit IS the only shield" (plan/04 §10).
// Agent-authored under the owner's standing delegation; owner lives in the
// production code, the tests in this folder are mine.

public sealed class AdminWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private sealed record Reg(TenantRegistrationResult Value, string Email, string Password)
    {
        public Guid TenantId => Value.Tenant.TenantId;
        public Guid AdminUserId => Value.AdminUser.UserId;
    }

    private async Task<Reg> RegisterAsync(string label)
    {
        var email = $"{Uid()}-{label}@wall.test";
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Admin {label}",
            Slug: $"admin-{label}-{Uid()}",
            AdminEmail: email,
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1!",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return new Reg(result.Value, email, "CorrectHorse1!");
    }

    // The platform caller is a CLAIM truth here, like the JWT would produce it:
    // CallerContext(userId, tenantId, roles) — the same shape Caller.FromUser yields.
    private static CallerContext PlatformCaller(Reg r) =>
        new(r.AdminUserId, r.TenantId, ["platform_admin"]);

    private static CallerContext RoleCaller(Reg r, string role) =>
        new(r.AdminUserId, r.TenantId, [role]);

    private PlatformTenantAdmin NewAdmin(ILogger<PlatformTenantAdmin>? log = null) =>
        new(pg.Admin, log ?? NullLogger<PlatformTenantAdmin>.Instance);

    // The audit seam mirror of ResetWallTests' CapturingSender: the same point
    // the interface (ILogger) promises, captured in-process. The message is read
    // POST-formatted (the provider's rendering), because the DoD greps THAT text.
    private sealed class CapturingLog : ILogger<PlatformTenantAdmin>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    // ---- fact 1: the escape hatch exists on EXACTLY one login -----------------

    [Fact]
    public async Task bypassrls_lives_on_exactly_one_login_and_nabd_app_never_carries_it()
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();

        await using (var cmd = new NpgsqlCommand(
            """
            SELECT rolname FROM pg_roles
            WHERE rolbypassrls AND rolcanlogin AND NOT rolsuper
            ORDER BY rolname
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));

            // the superuser bypasses by NATURE (its own row even carries the
            // attribute bit — that's initdb's, not design) — Logins that are NOT
            // superuser are the seats an app can hold, so the probe counts only
            // those: exactly one seat, the audited exception (DoD probe).
            Assert.Equal(["nabd_admin"], names);
        }

        // the app role — the one that carries every tenant lane — is explicitly
        // attribute-free: the wall's honest projection holds at the role level.
        await using (var app = new NpgsqlCommand(
            "SELECT rolbypassrls FROM pg_roles WHERE rolname = 'nabd_app'", conn))
        {
            Assert.False((bool)(await app.ExecuteScalarAsync())!);
        }
    }

    // ---- fact 2: the audited exception is a DIFFERENT power, provably ----------

    [Fact]
    public async Task the_admin_lane_sees_the_whole_fleet_the_app_lane_stays_walled()
    {
        var a = await RegisterAsync("fleet-a");
        var b = await RegisterAsync("fleet-b");
        var admin = NewAdmin();

        var fleet = await admin.ListTenantsAsync(PlatformCaller(a));
        Assert.True(fleet.IsSuccess, fleet.Error);
        Assert.True(fleet.Value.Count >= 2, $"admin lane saw {fleet.Value.Count} tenants");
        Assert.Contains(fleet.Value, t => t.TenantId == b.TenantId);   // a cross-fleet row, by ID

        // the CONTRAST: through the app lane, scoped to tenant A, the same table
        // answers exactly ONE row — the wall the admin lane just stepped over.
        var (conn, tx) = await pg.Db.OpenTenantScopeAsync(a.TenantId);
        try
        {
            await using var scoped = new NpgsqlCommand(
                "SELECT count(*) FROM identity.tenants", conn, tx);
            Assert.Equal(1L, await scoped.ExecuteScalarAsync());
        }
        catch { await tx.RollbackAsync(); throw; }
        finally { await conn.DisposeAsync(); }
    }

    // ---- fact 3: disable folds the login gate; observer-confusion holds --------

    [Fact]
    public async Task disabling_a_tenant_answers_tenant_disabled_while_wrong_password_stays_unclean()
    {
        var t = await RegisterAsync("gate");
        var admin = NewAdmin();

        var disabled = await admin.DisableTenantAsync(PlatformCaller(t), t.TenantId);
        Assert.True(disabled.IsSuccess, disabled.Error);

        var auth = new UserAuthentication(pg.Db, new LoginThrottle(), new CapturingAuthSender());

        // correct password + disabled tenant → the exact state code (Done when's ask)
        var honest = await auth.LoginAsync(new LoginRequest(t.Email, t.Password, "10.0.1.1"));
        Assert.False(honest.IsSuccess);
        Assert.Equal(IdentityErrors.TenantDisabled, honest.Error);

        // wrong password + disabled tenant → the SAME answer the wall always gives:
        // the observer probing tenant state through password feedback learns NOTHING
        // (spec 06's "confusable to observers" discipline).
        var sneak = await auth.LoginAsync(new LoginRequest(t.Email, "WrongHorse9", "10.0.1.2"));
        Assert.False(sneak.IsSuccess);
        Assert.Equal(IdentityErrors.InvalidCredentials, sneak.Error);
    }

    // ---- fact 4: enable reopens the gate; absent targets answer 404-shape ------

    [Fact]
    public async Task enabling_reopens_the_login_gate_and_an_absent_target_answers_tenant_not_found()
    {
        var t = await RegisterAsync("reopen");
        var admin = NewAdmin();

        _ = await admin.DisableTenantAsync(PlatformCaller(t), t.TenantId);

        var enabled = await admin.EnableTenantAsync(PlatformCaller(t), t.TenantId);
        Assert.True(enabled.IsSuccess, enabled.Error);

        var auth = new UserAuthentication(pg.Db, new LoginThrottle(), new CapturingAuthSender());
        var open = await auth.LoginAsync(new LoginRequest(t.Email, t.Password, "10.0.2.1"));
        Assert.True(open.IsSuccess, open.Error);   // the gate is OPEN again after enable

        var ghost = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var absent = await admin.DisableTenantAsync(PlatformCaller(t), ghost);
        Assert.False(absent.IsSuccess);
        Assert.Equal(IdentityErrors.TenantNotFound, absent.Error);
    }

    // ---- fact 5: the service layer refuses non-platform callers FIRST ----------

    [Fact]
    public async Task viewer_and_tenant_admin_refuse_at_the_service_layer_before_any_sql()
    {
        var t = await RegisterAsync("refuse");
        var admin = NewAdmin();

        // a viewer walks up to the fleet door...
        var viewerList = await admin.ListTenantsAsync(RoleCaller(t, "viewer"));
        Assert.False(viewerList.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, viewerList.Error);

        // ...a tenant_admin tries to disable ANOTHER tenant (house: matrix row check
        // fires before the cross-tenant target check — the role is absent either way).
        var taDisable = await admin.SetTenantActiveAsync(
            RoleCaller(t, "tenant_admin"), t.TenantId, isActive: false, "tenant.disable", default);
        Assert.False(taDisable.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, taDisable.Error);
    }

    // ---- fact 6: EVERY crossing writes its audit line --------------------------
    // plan/07 §9 Admin-actions row + plan/04 §10's "the audit IS the only shield".

    [Fact]
    public async Task every_admin_crossing_leaves_an_audit_line_naming_who_what_target_and_outcome()
    {
        var t = await RegisterAsync("audit");
        var log = new CapturingLog();
        var admin = NewAdmin(log);

        var platform = PlatformCaller(t);
        _ = await admin.ListTenantsAsync(platform);
        _ = await admin.DisableTenantAsync(platform, t.TenantId);

        Assert.Equal(2, log.Lines.Count);   // listed + disabled — one line per crossing, both

        var list = log.Lines[0];
        Assert.Contains("admin.audit", list);
        Assert.Contains("action=tenant.list", list);
        Assert.Contains($"actor={t.AdminUserId}", list);          // who
        Assert.Contains($"roles=platform_admin", list);           // crossing-from
        Assert.Matches(@$"listed=\d+", list);

        var disable = log.Lines[1];
        Assert.Contains("action=tenant.disable", disable);        // what
        Assert.Contains($"target_tenant={t.TenantId}", disable);  // target-tenant
        Assert.Contains("outcome=disabled", disable);             // what happened

        // refused crossings are audited TOO — the shield records the attempt,
        // not only the success (plan/04 §10 sentence, kept literal).
        _ = await admin.ListTenantsAsync(RoleCaller(t, "viewer"));
        Assert.Contains("denied", log.Lines[^1]);
        Assert.Equal(3, log.Lines.Count);
    }

    private sealed class CapturingAuthSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }
}
