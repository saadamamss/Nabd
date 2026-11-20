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
using Nabd.TelemetryStore.Contracts;
using Xunit;

// P4 spec 06 — severity table + mute windows (FR-35, plan/06 §6 / §5.4 / §7).
//
// RED-FIRST: `MuteWindow`, `RuleSnapshot.MuteWindows`, `RuleUpdateRequest.MuteWindows`,
// `RuleErrors.InvalidMuteWindow|MuteWindowOverlap`, `AlertChannels`, `ChannelRouting`,
// `MuteDecision` and `AlertRaiseOutcome.Muted` do not exist yet — the build fails on
// purpose until the owner implants them from study/implementation-code.md (RULES §9.1).
//
// The laws this net pins:
//   1. §6 — the routing table is data: info → dashboard only · warn → all three ·
//      critical → email + Telegram. Totality: no severity may fall through silently.
//   2. §5.4 — a threshold crossing inside a mute window still OPENS the row (the
//      story stays visible in history) but nobody is notified; the event log says
//      suppressed/mute_window.
//   3. The control: the same crossing outside the window notifies normally.
//   4. §5.3 — auto-resolve is NOT muted: a restore inside the window still closes.
//   5. A muted re-raise reopens the SAME row and still tells nobody.
//   6. §5.4 + §7 — window CRUD is validated at the door (day/range/overlap/cap) and
//      a cleared set is an empty list, never "null means unknown".
//   7. The honesty sentence, verbatim: "offline detection always runs, so a faked
//      maintenance window cannot hide a dead device" — `ShouldMute(offline, …)` is
//      false by law (spec 07 re-proves it end to end).
//   8. Boundaries are exact: start inclusive, end exclusive, in UTC.
//   9. The engine's crossing CARRIES the rule's windows (D6d) — a window added
//      through the door rides the snapshot; no windows is a real [] and never null.

public sealed class SeverityMuteTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero); // Thursday
    private const int Thursday = 4;

    /// <summary>11:00–13:00 UTC on Thursdays — covers T0, ends before T0+2h.</summary>
    private static readonly MuteWindow Lunch = new(Thursday, 11 * 60, 13 * 60);

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static CallerContext Admin(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Mute {label}",
            Slug: $"mu-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@mu.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    /// <summary>Fridge shape: temperature limited 2–8 (the seeded pair), door unlimited.</summary>
    private async Task<Guid> EnsureTemplateAsync(string label)
    {
        var tplKey = $"mu-{label}-{Uid()}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
                VALUES (@k, 'قالب', 'Mute Template');
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

    /// <summary>One known rule per device: the mint's seeded rule is removed first.</summary>
    private async Task<(Guid AssetId, Guid DeviceId, RuleSnapshot Rule)> FreshAsync(
        TenantRegistrationResult tenant, string label, int cooldownMinutes = 60)
    {
        var (asset, device) = await MintAsync(tenant, label);
        var rules = new DeviceRules(pg.Rules);

        foreach (var seeded in (await rules.ListByDeviceAsync(Admin(tenant), device)).Value)
            Assert.True((await rules.DeleteRuleAsync(Admin(tenant), device, seeded.RuleId)).IsSuccess);

        var created = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, device, "temperature", "above",
            ThresholdHigh: 9.0, DurationSeconds: 60,
            CooldownMinutes: cooldownMinutes, Severity: "warn"));
        Assert.True(created.IsSuccess, created.Error);
        return (asset, device, created.Value);
    }

    private static CrossingDetected Crossing(RuleSnapshot rule, Guid deviceId, DateTimeOffset at, double value)
        => new(rule, new CommittedReading(deviceId, "temperature", value, at));

    private static ConditionRestored Restored(RuleSnapshot rule, Guid deviceId, DateTimeOffset at, double value)
        => new(rule, new CommittedReading(deviceId, "temperature", value, at));

    private sealed class Recorder
    {
        public List<CrossingDetected> Crossings { get; } = [];
        public List<AlertRaised> Raised { get; } = [];
        public List<AlertResolved> Resolved { get; } = [];
        public IEventBus Bus { get; } = new InProcessEventBus();

        public Recorder()
        {
            Bus.Subscribe<CrossingDetected>((e, _) => { Crossings.Add(e); return Task.CompletedTask; });
            Bus.Subscribe<AlertRaised>((e, _) => { Raised.Add(e); return Task.CompletedTask; });
            Bus.Subscribe<AlertResolved>((e, _) => { Resolved.Add(e); return Task.CompletedTask; });
        }
    }

    /// <summary>One hot reading — enough for a 60 s rule whose episode starts here.</summary>
    private static CommittedBatch Batch(
        TenantRegistrationResult tenant, Guid deviceId, DateTimeOffset time, double value)
        => new(tenant.Tenant.TenantId, deviceId,
            [new CommittedReading(deviceId, "temperature", value, time)]);

    private (AlertService Service, AlertLifecycle Lifecycle, Recorder Rec) Rig()
    {
        var rec = new Recorder();
        var service = new AlertService(pg.Alerts);
        var lifecycle = new AlertLifecycle(service, rec.Bus, NullLogger<AlertLifecycle>.Instance);
        return (service, lifecycle, rec);
    }

    private async Task<List<(string Event, string? Reason)>> EventsAsync(Guid alertId)
    {
        var events = new List<(string, string?)>();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT event, reason FROM alerting.alert_events WHERE alert_id = @a ORDER BY at, event;", conn);
        cmd.Parameters.AddWithValue("a", alertId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            events.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return events;
    }

    // ---- fact 1 — the §6 table, as data, for every severity ---------------------

    [Fact]
    public void severity_routing_is_the_plan_06_table_for_every_severity()
    {
        Assert.Equal([AlertChannels.Dashboard],
            ChannelRouting.ChannelsFor(AlertSeverities.Info));

        Assert.Equal([AlertChannels.Dashboard, AlertChannels.Email, AlertChannels.Telegram],
            ChannelRouting.ChannelsFor(AlertSeverities.Warn));

        Assert.Equal([AlertChannels.Email, AlertChannels.Telegram],
            ChannelRouting.ChannelsFor(AlertSeverities.Critical));

        // totality: a severity the table does not know is a bug, never a silent fall-through
        foreach (var severity in AlertSeverities.All)
            Assert.False(ChannelRouting.ChannelsFor(severity).Count == 0,
                $"severity '{severity}' has no routing row (§6)");
    }

    // ---- fact 2 — inside the window: the row opens, nobody hears -----------------

    [Fact]
    public async Task a_crossing_inside_a_mute_window_opens_the_row_but_publishes_nothing()
    {
        var tenant = await RegisterAsync("mutedopen");
        var (_, device, rule) = await FreshAsync(tenant, "mutedopen");
        var (service, lifecycle, rec) = Rig();
        var muted = rule with { MuteWindows = [Lunch] };

        await rec.Bus.PublishAsync(Crossing(muted, device, T0, 10.0));

        var alert = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);
        Assert.Equal(AlertStatuses.Open, alert.Status);      // the story exists (§5.4)

        Assert.Empty(rec.Raised);                            // ...but nobody was told
        Assert.Equal(1, lifecycle.Muted);

        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened", "suppressed"], events.Select(e => e.Event).ToArray());
        Assert.Equal("mute_window", events[1].Reason);       // and the record says why (§7)
    }

    // ---- fact 3 — outside the window: business as usual -------------------------

    [Fact]
    public async Task a_crossing_outside_the_window_notifies_normally()
    {
        var tenant = await RegisterAsync("outside");
        var (_, device, rule) = await FreshAsync(tenant, "outside");
        var (service, lifecycle, rec) = Rig();

        await rec.Bus.PublishAsync(Crossing(rule with { MuteWindows = [Lunch] },
            device, T0.AddHours(2), 10.0));                  // 14:00 — past the 13:00 end

        Assert.Single(rec.Raised);
        Assert.Equal(0, lifecycle.Muted);

        var alert = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);
        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened"], events.Select(e => e.Event).ToArray());
    }

    // ---- fact 4 — auto-resolve is never muted (§5.3 vs §5.4) ---------------------

    [Fact]
    public async Task a_restore_inside_a_mute_window_still_auto_resolves()
    {
        var tenant = await RegisterAsync("mutesolve");
        var (_, device, rule) = await FreshAsync(tenant, "mutesolve");
        var (service, _, rec) = Rig();
        var muted = rule with { MuteWindows = [Lunch] };

        await rec.Bus.PublishAsync(Crossing(muted, device, T0, 10.0));
        await rec.Bus.PublishAsync(Restored(muted, device, T0.AddMinutes(10), 5.0));   // 12:10, inside

        var alert = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);
        Assert.Equal(AlertStatuses.Resolved, alert.Status);
        Assert.Equal(T0.AddMinutes(10), alert.ResolvedAt!.Value);
        Assert.Single(rec.Resolved);

        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened", "suppressed", "resolved"], events.Select(e => e.Event).ToArray());
        Assert.Equal("condition_restored", events[2].Reason);
    }

    // ---- fact 5 — a muted re-raise reopens the same row, silently ---------------

    [Fact]
    public async Task a_muted_re_raise_reopens_the_same_row_without_publishing()
    {
        var tenant = await RegisterAsync("mutereraise");
        var (_, device, rule) = await FreshAsync(tenant, "mutereraise", cooldownMinutes: 0);
        var (service, lifecycle, rec) = Rig();
        var muted = rule with { MuteWindows = [Lunch] };

        await rec.Bus.PublishAsync(Crossing(muted, device, T0, 10.0));
        await rec.Bus.PublishAsync(Restored(muted, device, T0.AddMinutes(10), 5.0));
        await rec.Bus.PublishAsync(Crossing(muted, device, T0.AddMinutes(20), 10.0));  // 12:20, inside

        var alert = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);
        Assert.Equal(AlertStatuses.Open, alert.Status);
        Assert.Equal(T0.AddMinutes(20), alert.OpenedAt);      // the new episode's clock

        Assert.Empty(rec.Raised);                             // both notify-worthy transitions muted
        Assert.Equal(2, lifecycle.Muted);

        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened", "suppressed", "resolved", "reopened", "suppressed"],
            events.Select(e => e.Event).ToArray());
        Assert.Equal("mute_window", events[4].Reason);
    }

    // ---- fact 6 — the door's validation (§5.4 CRUD) ----------------------------

    [Fact]
    public async Task mute_windows_are_validated_and_replaced_through_the_rule_door()
    {
        var tenant = await RegisterAsync("windows");
        var (_, device, rule) = await FreshAsync(tenant, "windows");
        var rules = new DeviceRules(pg.Rules);
        var acting = Admin(tenant);

        // a valid set is stored and read back with the rule
        var added = await rules.UpdateRuleAsync(acting, device, rule.RuleId,
            new RuleUpdateRequest(MuteWindows: [Lunch]));
        Assert.True(added.IsSuccess, added.Error);
        var read = Assert.Single((await rules.ListByDeviceAsync(acting, device)).Value);
        Assert.Single(read.MuteWindows!);
        Assert.Equal(Lunch, read.MuteWindows![0]);

        // same-day overlap → 422 by law, not by constraint error
        var overlap = await rules.UpdateRuleAsync(acting, device, rule.RuleId,
            new RuleUpdateRequest(MuteWindows: [Lunch, new(Thursday, 12 * 60, 14 * 60)]));
        Assert.False(overlap.IsSuccess);
        Assert.Equal(RuleErrors.MuteWindowOverlap, overlap.Error);

        // out-of-range day / inverted range → invalid, not a DB exception
        var badDay = await rules.UpdateRuleAsync(acting, device, rule.RuleId,
            new RuleUpdateRequest(MuteWindows: [new(9, 0, 60)]));
        Assert.Equal(RuleErrors.InvalidMuteWindow, badDay.Error);

        var badRange = await rules.UpdateRuleAsync(acting, device, rule.RuleId,
            new RuleUpdateRequest(MuteWindows: [new(Thursday, 800, 700)]));
        Assert.Equal(RuleErrors.InvalidMuteWindow, badRange.Error);

        // the cap: 21 non-overlapping windows is still too many
        var tooMany = Enumerable.Range(0, 21)
            .Select(i => new MuteWindow(0, i * 60, i * 60 + 30)).ToArray();
        var capped = await rules.UpdateRuleAsync(acting, device, rule.RuleId,
            new RuleUpdateRequest(MuteWindows: tooMany));
        Assert.Equal(RuleErrors.InvalidMuteWindow, capped.Error);

        // null = untouched; [] = cleared to an EMPTY list (not null)
        var untouched = await rules.UpdateRuleAsync(acting, device, rule.RuleId,
            new RuleUpdateRequest(ThresholdHigh: 9.5));
        Assert.True(untouched.IsSuccess, untouched.Error);
        Assert.Single(untouched.Value.MuteWindows!);

        var cleared = await rules.UpdateRuleAsync(acting, device, rule.RuleId,
            new RuleUpdateRequest(MuteWindows: []));
        Assert.True(cleared.IsSuccess, cleared.Error);
        Assert.NotNull(cleared.Value.MuteWindows);
        Assert.Empty(cleared.Value.MuteWindows!);
    }

    // ---- fact 7 — the §5.4 honesty sentence, pinned as law ---------------------

    [Fact]
    public void offline_is_never_muted()
    {
        Assert.False(MuteDecision.ShouldMute(AlertTypes.Offline, [Lunch], T0),
            "a maintenance window must never hide a dead device (plan/06 §5.4)");

        Assert.True(MuteDecision.ShouldMute(AlertTypes.Threshold, [Lunch], T0));
        Assert.False(MuteDecision.ShouldMute(AlertTypes.Threshold, null, T0));
        Assert.False(MuteDecision.ShouldMute(AlertTypes.Threshold, [], T0));
    }

    // ---- fact 8 — boundaries: [start, end) in UTC ------------------------------

    [Fact]
    public void window_boundaries_are_start_inclusive_end_exclusive()
    {
        var thursday11 = new DateTimeOffset(2026, 1, 15, 11, 0, 0, TimeSpan.Zero);
        var thursday1259 = new DateTimeOffset(2026, 1, 15, 12, 59, 0, TimeSpan.Zero);
        var thursday13 = new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero);
        var thursday1059 = new DateTimeOffset(2026, 1, 15, 10, 59, 0, TimeSpan.Zero);
        var friday12 = new DateTimeOffset(2026, 1, 16, 12, 0, 0, TimeSpan.Zero);

        Assert.True(MuteDecision.ShouldMute(AlertTypes.Threshold, [Lunch], thursday11));
        Assert.True(MuteDecision.ShouldMute(AlertTypes.Threshold, [Lunch], thursday1259));
        Assert.False(MuteDecision.ShouldMute(AlertTypes.Threshold, [Lunch], thursday13));
        Assert.False(MuteDecision.ShouldMute(AlertTypes.Threshold, [Lunch], thursday1059));
        Assert.False(MuteDecision.ShouldMute(AlertTypes.Threshold, [Lunch], friday12));
    }

    // ---- fact 9 — the engine's crossing carries the windows it will be judged by --

    [Fact]
    public async Task the_engine_crossing_carries_the_rules_mute_windows()
    {
        var tenant = await RegisterAsync("carry");

        // device A — a window written through the door rides the snapshot
        var (_, loud, loudRule) = await FreshAsync(tenant, "carryw");
        var rules = new DeviceRules(pg.Rules);
        var added = await rules.UpdateRuleAsync(Admin(tenant), loud, loudRule.RuleId,
            new RuleUpdateRequest(MuteWindows: [Lunch]));
        Assert.True(added.IsSuccess, added.Error);

        var recA = new Recorder();
        var engineA = new RulesEngine(pg.Rules, recA.Bus, NullLogger<RulesEngine>.Instance);
        await engineA.EvaluateBatchAsync(Batch(tenant, loud, T0, 10.0));
        await engineA.EvaluateBatchAsync(Batch(tenant, loud, T0.AddSeconds(61), 10.0));
        var carried = Assert.Single(recA.Crossings);
        Assert.Equal(Lunch, Assert.Single(carried.Rule.MuteWindows!));

        // device B — no windows is a REAL empty list, never null (D6d)
        var (_, quiet, _) = await FreshAsync(tenant, "carryn");
        var recB = new Recorder();
        var engineB = new RulesEngine(pg.Rules, recB.Bus, NullLogger<RulesEngine>.Instance);
        await engineB.EvaluateBatchAsync(Batch(tenant, quiet, T0, 10.0));
        await engineB.EvaluateBatchAsync(Batch(tenant, quiet, T0.AddSeconds(61), 10.0));
        var bare = Assert.Single(recB.Crossings);
        Assert.NotNull(bare.Rule.MuteWindows);
        Assert.Empty(bare.Rule.MuteWindows!);
    }
}
