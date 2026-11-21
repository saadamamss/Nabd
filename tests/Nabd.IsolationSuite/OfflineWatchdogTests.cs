namespace Nabd.IsolationSuite;

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nabd.Alerting;
using Nabd.Alerting.Contracts;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Rules;
using Nabd.Rules.Contracts;
using Nabd.SharedKernel;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;
using Xunit;

// P4 spec 07 — the offline watchdog (FR-31, acceptance #3; plan/06 §4, plan/05 §9).
//
// RED-FIRST: `OfflineWatchdog`, the reshaped `OfflineDetected`, `OnlineRestored`,
// `RuleSnapshot.OfflineAfterMinutes`, `RuleUpdateRequest.OfflineAfterMinutes`,
// `IDeviceStatusTracker.LastSeen` and `AlertDetail.OfflineGapMinutes` do not exist
// yet — the build fails on purpose until the owner implants the code from
// study/implementation-code.md (RULES §9.1). The agent owns this net.
//
// The laws this net pins:
//   1. A dead device fires ONCE when the silence passes its `offline_after_minutes`
//      (strictly greater, §4's `>`), with the last-seen time and the true gap.
//   2. The return emits `OnlineRestored` and auto-resolves the open alert with the
//      TRUE gap (`last seen → first seen again`), reason `device_online_restored`.
//   3. Revoked / inactive devices never fire (the watchdog rides `is_active`).
//   4. A device that never reported is not "offline" — it was never online.
//   5. The episode's state lives in `rule_eval_state`: a restart neither re-fires
//      nor forgets the return (`last_state` + `violating_since` + `fired`).
//   6. In-memory last-seen is authoritative between flushes (plan/04 §6): a mark
//      that has not reached `device_status` still counts, and marking never writes.
//   7. `offline_after_minutes` is editable through the rule door (§8 default 30),
//      validated at the door (422, never a 500), and a disabled rule is silent.
//   8. Mute windows can NEVER hide a dead device (plan/06 §5.4's honesty sentence,
//      promised as an integration fact by spec 06).
//   9. The threshold engine NEVER touches the offline rule's state: both machines
//      share `rule_eval_state`, so the engine must load channel rules only — its
//      60 s checkpoint writes every loaded row and would otherwise erase the
//      watchdog's `violating`/`fired` memory (the 0024 lesson, cross-feature).
//  10. Mint seeds exactly one offline rule per device — 30 min, per-device, no
//      channel — and re-issue never duplicates it (FR-15's idempotency).
//  11. Postgres microseconds vs .NET 100 ns ticks: the SAME instant must not read
//      as "the device returned" (found by the 2026-11-20 live drill — the alert
//      resolved 90 s BEFORE it opened, then re-detected into a cooldown suppression).
//
// Every assertion is scoped to the fact's OWN rule/asset: the watchdog window is
// cross-tenant by design (one SECURITY DEFINER read), and the class shares one
// container across facts.

public sealed class OfflineWatchdogTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static CallerContext Admin(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Watchdog {label}",
            Slug: $"wd-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@wd.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    /// <summary>Fridge shape: temperature limited 2–8, door unlimited — the same pair.</summary>
    private async Task<Guid> EnsureTemplateAsync(string label)
    {
        var tplKey = $"wd-{label}-{Uid()}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
                VALUES (@k, 'قالب', 'Watchdog Template');
                INSERT INTO assets.channel_definitions
                    (template_id, key, name_ar, name_en, unit, data_type, lower_limit, upper_limit)
                SELECT t.id, c.key, 'قناة', 'Channel', c.unit, 'number', c.lo, c.hi
                FROM (VALUES
                    ('temperature', '°C',    2.0::double precision, 8.0::double precision),
                    ('door',        'state', NULL::double precision,   NULL::double precision)
                ) AS c(key, unit, lo, hi)
                JOIN assets.asset_type_templates t ON t.key = @k;
                """, conn);
            ins.Parameters.AddWithValue("k", tplKey);
            await ins.ExecuteNonQueryAsync();
        }
        await using var sel = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM assets.asset_type_templates WHERE key = @k;", sel);
        cmd.Parameters.AddWithValue("k", tplKey);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(Guid AssetId, Guid DeviceId)> MintAsync(
        TenantRegistrationResult tenant, string label)
    {
        var template = await EnsureTemplateAsync(label);
        var assets = new AssetCatalog(pg.Assets);
        var created = await assets.CreateAssetAsync(
            Admin(tenant),
            new CreateAssetRequest(tenant.Tenant.TenantId, template, $"Anchor {Uid()}", null, null));
        Assert.True(created.IsSuccess, $"SETUP failed (asset): {created.Error}");

        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var minted = await devices.CreateDeviceAsync(
            Admin(tenant),
            new DeviceCreateRequest(tenant.Tenant.TenantId, created.Value.AssetId, $"Dev {label}"));
        Assert.True(minted.IsSuccess, $"SETUP failed (mint): {minted.Error}");
        return (created.Value.AssetId, minted.Value.Device.DeviceId);
    }

    /// <summary>The device + its minted offline rule (the seed under test).</summary>
    private async Task<(TenantRegistrationResult Tenant, Guid AssetId, Guid DeviceId, RuleSnapshot Offline)> FreshAsync(
        string label)
    {
        var tenant = await RegisterAsync(label);
        var (assetId, deviceId) = await MintAsync(tenant, label);

        var rules = new DeviceRules(pg.Rules);
        var seeded = await rules.ListByDeviceAsync(Admin(tenant), deviceId);
        Assert.True(seeded.IsSuccess, seeded.Error);
        var offline = Assert.Single(seeded.Value, r => r.RuleType == "offline");
        return (tenant, assetId, deviceId, offline);
    }

    /// <summary>A mark that reaches the DB (the fresh-process baseline).</summary>
    private static async Task MarkAndFlushAsync(
        DeviceStatusTracker tracker, TenantRegistrationResult tenant, Guid device, DateTimeOffset at)
    {
        await tracker.MarkSeenAsync(device, tenant.Tenant.TenantId, at, at);
        await tracker.FlushNowAsync();
    }

    private sealed class Recorder
    {
        public List<OfflineDetected> Offline { get; } = [];
        public List<OnlineRestored> Online { get; } = [];
        public List<AlertRaised> Raised { get; } = [];
        public List<AlertResolved> Resolved { get; } = [];
        public IEventBus Bus { get; } = new InProcessEventBus();

        public Recorder()
        {
            Bus.Subscribe<OfflineDetected>((e, _) => { Offline.Add(e); return Task.CompletedTask; });
            Bus.Subscribe<OnlineRestored>((e, _) => { Online.Add(e); return Task.CompletedTask; });
            Bus.Subscribe<AlertRaised>((e, _) => { Raised.Add(e); return Task.CompletedTask; });
            Bus.Subscribe<AlertResolved>((e, _) => { Resolved.Add(e); return Task.CompletedTask; });
        }

        public List<OfflineDetected> ForRule(Guid ruleId) =>
            Offline.Where(e => e.Rule.RuleId == ruleId).ToList();

        public List<OnlineRestored> ForRuleOnline(Guid ruleId) =>
            Online.Where(e => e.Rule.RuleId == ruleId).ToList();

        public List<AlertRaised> RaisedForRule(Guid ruleId) =>
            Raised.Where(e => e.Alert.RuleId == ruleId).ToList();
    }

    /// <summary>Watchdog + the real lifecycle consumer on the real D4 bus.</summary>
    private (OfflineWatchdog Watchdog, AlertService Alerts, AlertLifecycle Lifecycle, Recorder Rec, DeviceStatusTracker Tracker) Rig()
    {
        var rec = new Recorder();
        var alerts = new AlertService(pg.Alerts);
        var lifecycle = new AlertLifecycle(alerts, rec.Bus, NullLogger<AlertLifecycle>.Instance);
        var tracker = new DeviceStatusTracker(pg.Telemetry);
        var watchdog = new OfflineWatchdog(pg.Rules, rec.Bus, tracker, NullLogger<OfflineWatchdog>.Instance);
        return (watchdog, alerts, lifecycle, rec, tracker);
    }

    private async Task<List<(string Event, string? Reason, string? Detail)>> EventsAsync(Guid alertId)
    {
        var events = new List<(string, string?, string?)>();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT event, reason, detail::text FROM alerting.alert_events WHERE alert_id = @a ORDER BY at, event;", conn);
        cmd.Parameters.AddWithValue("a", alertId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            events.Add((reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        return events;
    }

    private async Task<AlertSnapshot?> AlertAsync(AlertService alerts, TenantRegistrationResult tenant, Guid assetId)
    {
        var list = await alerts.ListAsync(Admin(tenant), new AlertFilter(AssetId: assetId));
        Assert.True(list.IsSuccess, list.Error);
        return list.Value.SingleOrDefault();
    }

    private async Task<DateTimeOffset?> DbLastSeenAsync(Guid deviceId)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT last_seen_at FROM telemetry.device_status WHERE device_id = @d;", conn);
        cmd.Parameters.AddWithValue("d", deviceId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    /// <summary>The offline row's durable stamp — a quiet tick must not move it.</summary>
    private async Task<(string State, DateTimeOffset At, bool Fired)> ReadEvalStampAsync(Guid ruleId)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT last_state, last_evaluated_at, fired FROM rules.rule_eval_state WHERE rule_id = @r;", conn);
        cmd.Parameters.AddWithValue("r", ruleId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "the offline state row must exist");
        return (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetBoolean(2));
    }

    // ---- fact 1 — the dropout fires once, at the window, with the true gap -------

    [Fact]
    public async Task a_dead_device_fires_exactly_once_after_its_window()
    {
        var (tenant, asset, device, offline) = await FreshAsync("dead");
        var (watchdog, alerts, _, rec, tracker) = Rig();

        // the device's last words: T0 (durably in device_status, the restart baseline)
        await MarkAndFlushAsync(tracker, tenant, device, T0);

        // §4's comparison is STRICTLY greater: exactly 30 minutes is not yet offline
        await watchdog.TickAsync(T0.AddMinutes(30));
        Assert.Empty(rec.ForRule(offline.RuleId));

        await watchdog.TickAsync(T0.AddMinutes(31));
        var fired = Assert.Single(rec.ForRule(offline.RuleId));
        Assert.Equal(T0, fired.LastSeenAtUtc);
        Assert.Equal(T0.AddMinutes(31), fired.DetectedAtUtc);
        Assert.Equal("offline", fired.Rule.RuleType);
        Assert.Equal(30, fired.Rule.OfflineAfterMinutes);

        var alert = await AlertAsync(alerts, tenant, asset);
        Assert.NotNull(alert);
        Assert.Equal(AlertTypes.Offline, alert!.Type);
        Assert.Equal(AlertStatuses.Open, alert.Status);
        Assert.Equal(31.0, alert.Detail.OfflineGapMinutes!.Value, 3);
        Assert.Equal(T0, alert.Detail.LastSeenAtUtc);
        Assert.Equal("offline", alert.Detail.RuleType);
        // the EVENT must not lie about its type: spec 08 chooses the email template by it
        Assert.Equal(AlertTypes.Offline, Assert.Single(rec.RaisedForRule(offline.RuleId)).Alert.Type);

        // a dead device does not re-notify on every tick (the episode is the unit)
        await watchdog.TickAsync(T0.AddMinutes(40));
        Assert.Single(rec.ForRule(offline.RuleId));

        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened"], events.Select(e => e.Event).ToArray());
    }

    // ---- fact 2 — the return resolves with the TRUE gap (§5.3) -------------------

    [Fact]
    public async Task the_return_auto_resolves_with_the_true_gap()
    {
        var (tenant, asset, device, offline) = await FreshAsync("return");
        var (watchdog, alerts, _, rec, tracker) = Rig();

        await MarkAndFlushAsync(tracker, tenant, device, T0);
        await watchdog.TickAsync(T0.AddMinutes(31));
        var alert = await AlertAsync(alerts, tenant, asset);
        Assert.NotNull(alert);

        // the device speaks again at T0+45 — a mark in memory is enough (fact 6)
        var seenAgain = T0.AddMinutes(45);
        await tracker.MarkSeenAsync(device, tenant.Tenant.TenantId, seenAgain, seenAgain);
        await watchdog.TickAsync(seenAgain.AddSeconds(1));

        var restored = Assert.Single(rec.ForRuleOnline(offline.RuleId));
        Assert.Equal(T0, restored.LastSeenAtUtc);          // last seen BEFORE the dropout
        Assert.Equal(seenAgain, restored.SeenAgainAtUtc);  // first seen after it

        var resolved = await AlertAsync(alerts, tenant, asset);
        Assert.Equal(AlertStatuses.Resolved, resolved!.Status);
        Assert.Equal(seenAgain, resolved.ResolvedAt!.Value);
        Assert.Equal(45.0, resolved.Detail.OfflineGapMinutes!.Value, 3);
        Assert.Equal(T0, resolved.Detail.LastSeenAtUtc);

        var resolvedEvent = Assert.Single(rec.Resolved, e => e.AlertId == alert!.AlertId);
        Assert.Equal("device_online_restored", resolvedEvent.Reason);

        var events = await EventsAsync(alert!.AlertId);
        Assert.Equal(["opened", "resolved"], events.Select(e => e.Event).ToArray());
        Assert.Equal("device_online_restored", events[1].Reason);
        Assert.Contains("45", events[1].Detail!);           // the gap is in the audit record
    }

    // ---- fact 3 — revoked / inactive devices never fire ---------------------------

    [Fact]
    public async Task a_revoked_device_is_never_reported_offline()
    {
        var (tenant, asset, device, offline) = await FreshAsync("revoked");
        var (watchdog, alerts, _, rec, tracker) = Rig();

        await MarkAndFlushAsync(tracker, tenant, device, T0);

        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var revoked = await devices.RevokeDeviceAsync(Admin(tenant), device);
        Assert.True(revoked.IsSuccess, revoked.Error);

        await watchdog.TickAsync(T0.AddHours(2));

        Assert.Empty(rec.ForRule(offline.RuleId));
        Assert.Empty(rec.RaisedForRule(offline.RuleId));
        Assert.Null(await AlertAsync(alerts, tenant, asset));
    }

    // ---- fact 4 — never seen is not offline ---------------------------------------

    [Fact]
    public async Task a_device_that_never_reported_does_not_fire()
    {
        var (tenant, asset, _, offline) = await FreshAsync("never");
        var (watchdog, alerts, _, rec, _) = Rig();

        await watchdog.TickAsync(T0.AddHours(2));   // no device_status row exists at all

        Assert.Empty(rec.ForRule(offline.RuleId));
        Assert.Empty(rec.RaisedForRule(offline.RuleId));
        Assert.Null(await AlertAsync(alerts, tenant, asset));
    }

    // ---- fact 5 — the episode's state is durable across a restart -----------------

    [Fact]
    public async Task a_restart_does_not_refire_and_still_resolves_on_return()
    {
        var (tenant, asset, device, offline) = await FreshAsync("restart");
        var (first, alerts, _, rec, tracker) = Rig();

        await MarkAndFlushAsync(tracker, tenant, device, T0);
        await first.TickAsync(T0.AddMinutes(31));
        Assert.Single(rec.ForRule(offline.RuleId));

        // a NEW process: empty in-memory last-seen, empty counters — the state row is
        // the only memory it has (`last_state` + `violating_since` + `fired`, 0021/0024)
        var restartedTracker = new DeviceStatusTracker(pg.Telemetry);
        var second = new OfflineWatchdog(
            pg.Rules, rec.Bus, restartedTracker, NullLogger<OfflineWatchdog>.Instance);

        await second.TickAsync(T0.AddMinutes(35));                  // still dead
        Assert.Single(rec.ForRule(offline.RuleId));                 // no ghost re-fire

        var seenAgain = T0.AddMinutes(50);
        await restartedTracker.MarkSeenAsync(device, tenant.Tenant.TenantId, seenAgain, seenAgain);
        await second.TickAsync(seenAgain.AddSeconds(1));

        var restored = Assert.Single(rec.ForRuleOnline(offline.RuleId));
        Assert.Equal(T0, restored.LastSeenAtUtc);
        Assert.Equal(seenAgain, restored.SeenAgainAtUtc);

        var alert = await AlertAsync(alerts, tenant, asset);
        Assert.Equal(AlertStatuses.Resolved, alert!.Status);
        Assert.Equal(50.0, alert.Detail.OfflineGapMinutes!.Value, 3);
    }

    // ---- fact 6 — in-memory last-seen is authoritative between flushes ------------

    [Fact]
    public async Task in_memory_last_seen_wins_until_the_flush_and_marking_never_writes()
    {
        var (tenant, _, device, offline) = await FreshAsync("memory");
        var (watchdog, _, _, rec, tracker) = Rig();

        await MarkAndFlushAsync(tracker, tenant, device, T0);
        Assert.Equal(1, tracker.FlushCount);

        // 100 readings between window edges: no DB write, no flush, no lost mark
        for (var i = 0; i < 100; i++)
            await tracker.MarkSeenAsync(device, tenant.Tenant.TenantId,
                T0.AddMinutes(20).AddSeconds(i), T0.AddMinutes(20).AddSeconds(i));
        Assert.Equal(1, tracker.FlushCount);
        Assert.Equal(T0, await DbLastSeenAsync(device));            // the table still says T0

        // the DB alone would call this device dead (31 min); memory says 11 min
        var fresh = T0.AddMinutes(29);
        await tracker.MarkSeenAsync(device, tenant.Tenant.TenantId, fresh, fresh);
        await watchdog.TickAsync(T0.AddMinutes(31));
        Assert.Empty(rec.ForRule(offline.RuleId));

        // the window edge pays the DB once, and only then does the table move
        await tracker.FlushNowAsync();
        Assert.Equal(2, tracker.FlushCount);
        Assert.Equal(fresh, await DbLastSeenAsync(device));

        await watchdog.TickAsync(T0.AddMinutes(60));
        var fired = Assert.Single(rec.ForRule(offline.RuleId));
        Assert.Equal(fresh, fired.LastSeenAtUtc);
        Assert.Equal(31.0, (fired.DetectedAtUtc - fired.LastSeenAtUtc).TotalMinutes, 3);
    }

    // ---- fact 7 — the window is data: editable, validated, respected --------------

    [Fact]
    public async Task offline_after_minutes_is_editable_validated_and_respected()
    {
        var (tenant, _, device, offline) = await FreshAsync("editable");
        var (watchdog, _, _, rec, tracker) = Rig();
        var rules = new DeviceRules(pg.Rules);
        var acting = Admin(tenant);

        // 30 → 15 through the SAME rule door (spec 03's PATCH; the offline row is listed)
        var edited = await rules.UpdateRuleAsync(acting, device, offline.RuleId,
            new RuleUpdateRequest(OfflineAfterMinutes: 15));
        Assert.True(edited.IsSuccess, edited.Error);
        Assert.Equal(15, edited.Value.OfflineAfterMinutes);

        await MarkAndFlushAsync(tracker, tenant, device, T0);
        await watchdog.TickAsync(T0.AddMinutes(14));
        Assert.Empty(rec.ForRule(offline.RuleId));

        // a disabled rule is silent even past its window (the enabled filter)
        var disabled = await rules.UpdateRuleAsync(acting, device, offline.RuleId,
            new RuleUpdateRequest(Enabled: false));
        Assert.True(disabled.IsSuccess, disabled.Error);
        await watchdog.TickAsync(T0.AddMinutes(20));
        Assert.Empty(rec.ForRule(offline.RuleId));

        var enabled = await rules.UpdateRuleAsync(acting, device, offline.RuleId,
            new RuleUpdateRequest(Enabled: true));
        Assert.True(enabled.IsSuccess, enabled.Error);
        await watchdog.TickAsync(T0.AddMinutes(16));
        var fired = Assert.Single(rec.ForRule(offline.RuleId));
        Assert.Equal(15, fired.Rule.OfflineAfterMinutes);

        // the door's ladder: 0 minutes and non-offline targets are 422-by-code, not 500
        var zero = await rules.UpdateRuleAsync(acting, device, offline.RuleId,
            new RuleUpdateRequest(OfflineAfterMinutes: 0));
        Assert.False(zero.IsSuccess);
        Assert.Equal(RuleErrors.InvalidRule, zero.Error);

        var thresholdRule = (await rules.ListByDeviceAsync(acting, device)).Value
            .Single(r => r.RuleType != "offline");
        var wrongType = await rules.UpdateRuleAsync(acting, device, thresholdRule.RuleId,
            new RuleUpdateRequest(OfflineAfterMinutes: 15));
        Assert.False(wrongType.IsSuccess);
        Assert.Equal(RuleErrors.InvalidRule, wrongType.Error);
    }

    // ---- fact 8 — §5.4: a mute window can never hide a dead device ----------------

    [Fact]
    public async Task a_mute_window_never_hides_a_dead_device()
    {
        var (tenant, asset, device, offline) = await FreshAsync("muted");
        var (watchdog, alerts, lifecycle, rec, tracker) = Rig();
        var rules = new DeviceRules(pg.Rules);

        var at = T0.AddMinutes(31);
        var allDay = new MuteWindow((int)at.UtcDateTime.DayOfWeek, 0, 1440);
        var patched = await rules.UpdateRuleAsync(Admin(tenant), device, offline.RuleId,
            new RuleUpdateRequest(MuteWindows: [allDay]));
        Assert.True(patched.IsSuccess, patched.Error);

        await MarkAndFlushAsync(tracker, tenant, device, T0);
        await watchdog.TickAsync(at);

        Assert.Single(rec.ForRule(offline.RuleId));           // detection always runs
        Assert.Single(rec.RaisedForRule(offline.RuleId));     // and it notifies
        Assert.Equal(0, lifecycle.Muted);                     // offline is never muted

        var alert = await AlertAsync(alerts, tenant, asset);
        var events = await EventsAsync(alert!.AlertId);
        Assert.Equal(["opened"], events.Select(e => e.Event).ToArray());   // no suppression
    }

    // ---- fact 9 — the engine's checkpoint must not erase the offline state --------

    [Fact]
    public async Task the_threshold_engine_never_touches_the_offline_state()
    {
        var (tenant, _, device, offline) = await FreshAsync("enginesafe");
        var (watchdog, _, _, rec, tracker) = Rig();

        await MarkAndFlushAsync(tracker, tenant, device, T0);
        await watchdog.TickAsync(T0.AddMinutes(31));
        Assert.Single(rec.ForRule(offline.RuleId));

        // the engine loads this device's rule set (temperature + offline) and its
        // 60 s checkpoint persists EVERY loaded state — offline must not be loaded
        var recEngine = new Recorder();
        var engine = new RulesEngine(pg.Rules, recEngine.Bus, NullLogger<RulesEngine>.Instance);
        await engine.EvaluateBatchAsync(new CommittedBatch(
            tenant.Tenant.TenantId, device,
            [new CommittedReading(device, "temperature", 5.0, T0.AddMinutes(32))]));
        Assert.Equal(1, engine.Evaluated);          // the channel rule only
        await engine.CheckpointAsync();

        // the watchdog's memory stands: still violating, still fired
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT last_state, fired FROM rules.rule_eval_state WHERE rule_id = @r;", conn);
        cmd.Parameters.AddWithValue("r", offline.RuleId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "the offline state row must exist after the fire");
        Assert.Equal("violating", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
    }

    // ---- fact 10 — FR-15: mint seeds exactly one offline rule, idempotently -------

    [Fact]
    public async Task minting_seeds_one_offline_rule_and_reissue_never_duplicates_it()
    {
        var (tenant, _, device, offline) = await FreshAsync("seed");

        Assert.Null(offline.ChannelKey);                    // per device, never per channel
        Assert.Equal(30, offline.OfflineAfterMinutes);      // plan/06 §8 default
        Assert.Equal(60, offline.CooldownMinutes);
        Assert.Equal("warn", offline.Severity);
        Assert.True(offline.Enabled);
        Assert.Null(offline.ThresholdLow);
        Assert.Null(offline.ThresholdHigh);

        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var reissued = await devices.ReissueTokenAsync(Admin(tenant), device);
        Assert.True(reissued.IsSuccess, reissued.Error);

        var rules = new DeviceRules(pg.Rules);
        var after = (await rules.ListByDeviceAsync(Admin(tenant), device)).Value;
        Assert.Single(after, r => r.RuleType == "offline");   // ON CONFLICT law
    }

    // ---- fact 11 — the row's microsecond precision is not a "return" ------------

    [Fact]
    public async Task a_sub_microsecond_mark_is_never_mistaken_for_a_return()
    {
        var (tenant, asset, device, offline) = await FreshAsync("precision");
        var (watchdog, alerts, _, rec, tracker) = Rig();

        // A real mark carries 100 ns ticks; Postgres keeps microseconds. The SAME
        // instant therefore round-trips as "older", and comparing them naively made
        // the device's own last words look like a fresh return. The live drill of
        // 2026-11-20 hit exactly this: an alert opened at 03:16:25 was resolved at
        // 03:14:55 (90 s BEFORE it opened), then re-detected into a cooldown
        // suppression — a device that never came back was reported as returned.
        var mark = T0.AddTicks(3);                    // 300 ns after T0 → truncates to T0
        await MarkAndFlushAsync(tracker, tenant, device, mark);

        await watchdog.TickAsync(T0.AddMinutes(31));
        var alert = await AlertAsync(alerts, tenant, asset);
        Assert.NotNull(alert);
        Assert.Equal(AlertStatuses.Open, alert!.Status);

        var before = await ReadEvalStampAsync(offline.RuleId);

        // one more quiet tick while the device is still dead: no return, no write
        await watchdog.TickAsync(T0.AddMinutes(32));
        Assert.Empty(rec.ForRuleOnline(offline.RuleId));
        Assert.Equal(AlertStatuses.Open, (await AlertAsync(alerts, tenant, asset))!.Status);

        var after = await ReadEvalStampAsync(offline.RuleId);
        Assert.Equal(before, after);                  // still violating, same stamp, still fired
        Assert.Equal(["opened"],
            (await EventsAsync(alert.AlertId)).Select(e => e.Event).ToArray());
    }
}
