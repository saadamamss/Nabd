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

// P4 spec 05 — the alert lifecycle (FR-32/FR-33, plan/06 §5.1–5.3, plan/04 §8).
//
// RED-FIRST: `AlertingDb`, `AlertService`, `AlertLifecycle`, `IAlerting`,
// `AlertSnapshot`, `AlertFilter` and `AlertErrors` do not exist yet — the build
// fails on purpose until the owner implants them from
// study/implementation-code.md (RULES §9.1: production code is his hand).
//
// The laws this net pins (each one is a sentence from the Blueprint):
//   1. plan/04 §8 shape — alerts + alert_events + notification_log, walled.
//   2. FR-33 — alerts belong to the ASSET; a foreign tenant sees nothing (RLS fail-shut).
//   3. §5.1 — a crossing opens ONE alert carrying the observed value.
//   4. §5.2 — a second crossing while open updates detail, never re-notifies.
//   5. §5.3 — ConditionRestored auto-resolves the open alert.
//   6. §5.2 — inside cooldown: suppressed-but-logged (nothing silent, §7).
//   7. §5.1 — cooldown spent: re-raise UPSERTS the same row (one story thread).
//   8. §5.2 — critical cooldown is halved (the ×0.5 law).
//   9. §5.1 — ACK records who/when; the ladder's roles are the matrix's.
//  10. doors — unknown id 404-hides; bad filters are 422 (never a 500).
//
// The lifecycle is driven through the REAL D4 bus (publish CrossingDetected /
// ConditionRestored exactly as the engine does), never by calling internals.

public sealed class AlertLifecycleTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // A fixed clock in the PAST: event times drive the machine (cooldown math), while
    // manual ack/resolve stamp wall-clock now — a past T0 keeps the audit order stable.
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static CallerContext Admin(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private static CallerContext Viewer(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["viewer"]);

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Alerts {label}",
            Slug: $"al-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@al.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    /// <summary>The fridge shape: temperature has limits 2–8 (the seeding pair's
    /// limited channel), door has none — the mint seeds ONE rule for temperature.</summary>
    private async Task<Guid> EnsureTemplateAsync(string label)
    {
        var tplKey = $"al-{label}-{Uid()}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
                VALUES (@k, 'قالب', 'Alert Template');
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

    /// <summary>A device inside an EXISTING tenant with EXACTLY the fact's rule: the
    /// mint's seeded outside_range rule is removed first, so the event stream is one
    /// rule's story.</summary>
    private async Task<(Guid AssetId, Guid DeviceId, RuleSnapshot Rule)> FreshAsync(
        TenantRegistrationResult tenant, string label,
        string severity = "warn", int cooldownMinutes = 60)
    {
        var (asset, device) = await MintAsync(tenant, label);
        var rules = new DeviceRules(pg.Rules);

        foreach (var seeded in (await rules.ListByDeviceAsync(Admin(tenant), device)).Value)
            Assert.True((await rules.DeleteRuleAsync(Admin(tenant), device, seeded.RuleId)).IsSuccess);

        var created = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, device, "temperature", "above",
            ThresholdHigh: 9.0, DurationSeconds: 60,
            CooldownMinutes: cooldownMinutes, Severity: severity));
        Assert.True(created.IsSuccess, created.Error);
        return (asset, device, created.Value);
    }

    private static CrossingDetected Crossing(RuleSnapshot rule, Guid deviceId, DateTimeOffset at, double value)
        => new(rule, new CommittedReading(deviceId, "temperature", value, at));

    private static ConditionRestored Restored(RuleSnapshot rule, Guid deviceId, DateTimeOffset at, double value)
        => new(rule, new CommittedReading(deviceId, "temperature", value, at));

    /// <summary>Collects the lifecycle's own public events; the bus is the real D4 one.</summary>
    private sealed class Recorder
    {
        public List<AlertRaised> Raised { get; } = [];
        public List<AlertResolved> Resolved { get; } = [];
        public IEventBus Bus { get; } = new InProcessEventBus();

        public Recorder()
        {
            Bus.Subscribe<AlertRaised>((e, _) => { Raised.Add(e); return Task.CompletedTask; });
            Bus.Subscribe<AlertResolved>((e, _) => { Resolved.Add(e); return Task.CompletedTask; });
        }
    }

    private (AlertService Service, AlertLifecycle Lifecycle, Recorder Rec) Rig()
    {
        var rec = new Recorder();
        var service = new AlertService(pg.Alerts);
        var lifecycle = new AlertLifecycle(service, rec.Bus, NullLogger<AlertLifecycle>.Instance);
        return (service, lifecycle, rec);
    }

    private async Task<List<(string Event, string? Reason, Guid? Actor)>> EventsAsync(Guid alertId)
    {
        var events = new List<(string, string?, Guid?)>();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT event, reason, actor_user_id FROM alerting.alert_events WHERE alert_id = @a ORDER BY at, event;", conn);
        cmd.Parameters.AddWithValue("a", alertId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            events.Add((reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2)));
        return events;
    }

    // ---- fact 1 — the plan/04 §8 shape, three walled tables --------------------

    [Fact]
    public async Task the_three_tables_exist_with_the_plan_04_s8_columns()
    {
        var columns = new Dictionary<string, List<string>>();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'alerting'
              AND table_name IN ('alerts', 'alert_events', 'notification_log')
            ORDER BY table_name, ordinal_position;
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            columns.TryAdd(table, []);
            columns[table].Add(reader.GetString(1));
        }

        Assert.Equal([
            "id", "tenant_id", "asset_id", "rule_id", "type", "severity", "status",
            "opened_at", "resolved_at", "acknowledged_at", "acknowledged_by",
            "title", "detail"], columns["alerts"]);
        Assert.Equal([
            "id", "tenant_id", "alert_id", "event", "actor_user_id",
            "reason", "detail", "at"], columns["alert_events"]);
        // spec 08 (0026) widened the send log into evidence: the template, the language,
        // the attempt count and the error are now part of the row. Still an exact list —
        // a drift in either direction fails this fact.
        Assert.Equal([
            "id", "tenant_id", "alert_id", "channel", "recipient", "status", "sent_at",
            "template_key", "locale", "attempts", "error"],
            columns["notification_log"]);
    }

    // ---- fact 2 — the wall: fail-shut, cross-tenant invisible ------------------

    [Fact]
    public async Task the_wall_refuses_every_cross_tenant_reach()
    {
        var tenantA = await RegisterAsync("wallA");
        var tenantB = await RegisterAsync("wallB");
        var (_, deviceB, ruleB) = await FreshAsync(tenantB, "wallB2");
        await FreshAsync(tenantA, "wallA2");
        var (serviceB, _, recB) = Rig();

        await recB.Bus.PublishAsync(Crossing(ruleB, deviceB, T0, 10.0));
        var alertB = Assert.Single((await serviceB.ListAsync(Admin(tenantB), new AlertFilter())).Value);

        // 2a: the app role WITHOUT a tenant scope sees ZERO rows (missing_ok → no match)
        await using var naked = await pg.Assets.OpenPreAuthConnectionAsync();
        await using (var alertCount = new NpgsqlCommand("SELECT count(*) FROM alerting.alerts;", naked))
            Assert.Equal(0L, (long)(await alertCount.ExecuteScalarAsync())!);
        await using (var eventCount = new NpgsqlCommand("SELECT count(*) FROM alerting.alert_events;", naked))
            Assert.Equal(0L, (long)(await eventCount.ExecuteScalarAsync())!);

        // 2b: tenant A cannot GET tenant B's alert by guessing its id (404-hide)
        var serviceA = new AlertService(pg.Alerts);
        var stolen = await serviceA.GetAsync(Admin(tenantA), alertB.AlertId);
        Assert.False(stolen.IsSuccess, "cross-tenant get must not succeed");
        Assert.Equal(AlertErrors.AlertNotFound, stolen.Error);

        // 2c: and the LIST is empty — no existence hints
        var listed = await serviceA.ListAsync(Admin(tenantA), new AlertFilter());
        Assert.True(listed.IsSuccess, listed.Error);
        Assert.Empty(listed.Value);
    }

    // ---- fact 3 — one crossing, one alert, carrying the observed value ---------

    [Fact]
    public async Task a_crossing_opens_exactly_one_alert_with_the_observed_value()
    {
        var tenant = await RegisterAsync("open");
        var (assetId, device, rule) = await FreshAsync(tenant, "open");
        var (service, _, rec) = Rig();

        await rec.Bus.PublishAsync(Crossing(rule, device, T0, 10.5));

        var alerts = (await service.ListAsync(Viewer(tenant), new AlertFilter())).Value;
        var alert = Assert.Single(alerts);

        Assert.Equal(assetId, alert.AssetId);          // FR-33: the alert belongs to the asset
        Assert.Equal(rule.RuleId, alert.RuleId);
        Assert.Equal(AlertTypes.Threshold, alert.Type);
        Assert.Equal("warn", alert.Severity);
        Assert.Equal(AlertStatuses.Open, alert.Status);
        Assert.Equal(T0, alert.OpenedAt);
        Assert.Null(alert.ResolvedAt);
        Assert.Equal(10.5, alert.Detail.Value);
        Assert.Equal("temperature", alert.Detail.ChannelKey);
        Assert.Equal(9.0, alert.Detail.ThresholdHigh!.Value);
        Assert.False(string.IsNullOrWhiteSpace(alert.TitleEn));
        Assert.False(string.IsNullOrWhiteSpace(alert.TitleAr));   // plan/04 §8: per-locale title

        var raised = Assert.Single(rec.Raised);
        Assert.Equal(alert.AlertId, raised.Alert.AlertId);        // the fan-out carries the row

        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened"], events.Select(e => e.Event).ToArray());
    }

    // ---- fact 4 — dedup while open: detail updates, notification does not ------

    [Fact]
    public async Task a_second_crossing_while_open_updates_detail_and_never_notifies_again()
    {
        var tenant = await RegisterAsync("dedup");
        var (_, device, rule) = await FreshAsync(tenant, "dedup");
        var (service, _, rec) = Rig();

        await rec.Bus.PublishAsync(Crossing(rule, device, T0, 10.0));
        await rec.Bus.PublishAsync(Crossing(rule, device, T0.AddMinutes(5), 11.0));

        var alert = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);
        Assert.Equal(11.0, alert.Detail.Value);        // latest value, same thread
        Assert.Equal(T0, alert.OpenedAt);              // the episode's clock never resets
        Assert.Single(rec.Raised);                     // one notification per episode

        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened", "deduplicated"], events.Select(e => e.Event).ToArray());
        Assert.Equal("open", events[1].Reason);
    }

    // ---- fact 5 — §5.3: restore auto-resolves --------------------------------

    [Fact]
    public async Task a_restore_auto_resolves_the_open_alert()
    {
        var tenant = await RegisterAsync("resolve");
        var (_, device, rule) = await FreshAsync(tenant, "resolve");
        var (service, _, rec) = Rig();

        await rec.Bus.PublishAsync(Crossing(rule, device, T0, 10.0));
        var opened = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);

        await rec.Bus.PublishAsync(Restored(rule, device, T0.AddMinutes(10), 5.0));

        var resolved = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);
        Assert.Equal(AlertStatuses.Resolved, resolved.Status);
        Assert.Equal(T0.AddMinutes(10), resolved.ResolvedAt!.Value);

        var history = await EventsAsync(opened.AlertId);
        Assert.Equal(["opened", "resolved"], history.Select(e => e.Event).ToArray());
        Assert.Equal("condition_restored", history[1].Reason);

        var restored = Assert.Single(rec.Resolved);
        Assert.Equal(opened.AlertId, restored.AlertId);
        Assert.Equal("condition_restored", restored.Reason);
    }

    // ---- fact 6 — inside cooldown: suppressed, but logged (nothing silent) -----

    [Fact]
    public async Task a_crossing_inside_the_cooldown_is_suppressed_but_logged()
    {
        var tenant = await RegisterAsync("cooldown");
        var (_, device, rule) = await FreshAsync(tenant, "cooldown");
        var (service, _, rec) = Rig();

        await rec.Bus.PublishAsync(Crossing(rule, device, T0, 10.0));
        await rec.Bus.PublishAsync(Restored(rule, device, T0.AddMinutes(10), 5.0));
        await rec.Bus.PublishAsync(Crossing(rule, device, T0.AddMinutes(20), 10.0));   // 10m < 60m

        var alert = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);
        Assert.Equal(AlertStatuses.Resolved, alert.Status);   // stays resolved
        Assert.Single(rec.Raised);                            // no second notification

        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened", "resolved", "suppressed"], events.Select(e => e.Event).ToArray());
        Assert.Equal("cooldown", events[2].Reason);
    }

    // ---- fact 7 — cooldown spent: re-raise upserts the SAME row ----------------

    [Fact]
    public async Task a_crossing_after_the_cooldown_reopens_the_same_row()
    {
        var tenant = await RegisterAsync("reraise");
        var (_, device, rule) = await FreshAsync(tenant, "reraise");
        var (service, _, rec) = Rig();

        await rec.Bus.PublishAsync(Crossing(rule, device, T0, 10.0));
        var first = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);

        await rec.Bus.PublishAsync(Restored(rule, device, T0.AddMinutes(10), 5.0));
        await rec.Bus.PublishAsync(Crossing(rule, device, T0.AddMinutes(80), 10.0));   // 70m > 60m

        var alerts = (await service.ListAsync(Admin(tenant), new AlertFilter())).Value;
        var again = Assert.Single(alerts);                    // ONE row, one story thread
        Assert.Equal(first.AlertId, again.AlertId);
        Assert.Equal(AlertStatuses.Open, again.Status);
        Assert.Equal(T0.AddMinutes(80), again.OpenedAt);      // the new episode's clock
        Assert.Null(again.ResolvedAt);

        Assert.Equal(2, rec.Raised.Count);                    // the engineer hears it again
        Assert.Equal(first.AlertId, rec.Raised[1].Alert.AlertId);

        var events = await EventsAsync(first.AlertId);
        Assert.Equal(["opened", "resolved", "reopened"], events.Select(e => e.Event).ToArray());
    }

    // ---- fact 8 — critical cools at half (§5.2 ×0.5) --------------------------

    [Fact]
    public async Task a_critical_rule_cools_at_half_the_configured_cooldown()
    {
        var tenant = await RegisterAsync("critical");
        var (_, device, rule) = await FreshAsync(tenant, "critical", severity: "critical", cooldownMinutes: 60);
        var (service, _, rec) = Rig();

        await rec.Bus.PublishAsync(Crossing(rule, device, T0, 10.0));
        await rec.Bus.PublishAsync(Restored(rule, device, T0.AddMinutes(10), 5.0));

        // 25 min after resolution < 30 min (halved cooldown) → still suppressed
        await rec.Bus.PublishAsync(Crossing(rule, device, T0.AddMinutes(35), 10.0));
        Assert.Single(rec.Raised);
        Assert.Equal(AlertStatuses.Resolved,
            Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value).Status);

        // 30 min after resolution = the halved bar spent → re-raised
        await rec.Bus.PublishAsync(Crossing(rule, device, T0.AddMinutes(40), 10.0));
        var alert = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);
        Assert.Equal(AlertStatuses.Open, alert.Status);
        Assert.Equal(2, rec.Raised.Count);
    }

    // ---- fact 9 — the ack/resolve ladder: roles + the audit trail -------------

    [Fact]
    public async Task the_ack_ladder_records_who_and_when_and_refuses_the_wrong_role()
    {
        var tenant = await RegisterAsync("ack");
        var (_, device, rule) = await FreshAsync(tenant, "ack");
        var (service, _, rec) = Rig();

        await rec.Bus.PublishAsync(Crossing(rule, device, T0, 10.0));
        var alert = Assert.Single((await service.ListAsync(Admin(tenant), new AlertFilter())).Value);

        // viewer may ACK (plan/06 §5.1) — the matrix asserts it in the service
        var acked = await service.AcknowledgeAsync(Viewer(tenant), alert.AlertId);
        Assert.True(acked.IsSuccess, acked.Error);
        Assert.Equal(AlertStatuses.Acknowledged, acked.Value.Status);
        Assert.Equal(tenant.AdminUser.UserId, acked.Value.AcknowledgedBy);
        Assert.NotNull(acked.Value.AcknowledgedAt);

        // ACK is idempotent: a second one changes nothing and logs nothing new
        var twice = await service.AcknowledgeAsync(Viewer(tenant), alert.AlertId);
        Assert.True(twice.IsSuccess, twice.Error);
        Assert.Equal(AlertStatuses.Acknowledged, twice.Value.Status);

        // viewer may NOT resolve — TenantAdmin owns the close (07 §5 layer 2)
        var forbidden = await service.ResolveAsync(Viewer(tenant), alert.AlertId, "closing it");
        Assert.False(forbidden.IsSuccess, "viewer must not resolve alerts");
        Assert.Equal(IdentityErrors.Forbidden, forbidden.Error);

        // TenantAdmin resolves with a documented reason
        var resolved = await service.ResolveAsync(Admin(tenant), alert.AlertId, "fixed by hand");
        Assert.True(resolved.IsSuccess, resolved.Error);
        Assert.Equal(AlertStatuses.Resolved, resolved.Value.Status);
        Assert.NotNull(resolved.Value.ResolvedAt);

        // a resolved alert cannot be resolved twice (no fake audit rows)
        var again = await service.ResolveAsync(Admin(tenant), alert.AlertId, "again");
        Assert.False(again.IsSuccess);
        Assert.Equal(AlertErrors.InvalidStatus, again.Error);

        var events = await EventsAsync(alert.AlertId);
        Assert.Equal(["opened", "acknowledged", "resolved"], events.Select(e => e.Event).ToArray());
        Assert.Equal(tenant.AdminUser.UserId, events[1].Actor!.Value);
        Assert.Equal("fixed by hand", events[2].Reason);
        Assert.Equal(tenant.AdminUser.UserId, events[2].Actor!.Value);
    }

    // ---- fact 10 — the doors' negative laws: 404-hide, 422-not-500 ------------

    [Fact]
    public async Task unknown_ids_are_not_found_and_bad_filters_are_refused()
    {
        var tenant = await RegisterAsync("doors");
        var service = new AlertService(pg.Alerts);

        var missing = await service.GetAsync(Admin(tenant), Guid.NewGuid());
        Assert.False(missing.IsSuccess);
        Assert.Equal(AlertErrors.AlertNotFound, missing.Error);

        var badStatus = await service.ListAsync(Admin(tenant), new AlertFilter(Status: "exploded"));
        Assert.False(badStatus.IsSuccess);
        Assert.Equal(AlertErrors.InvalidFilter, badStatus.Error);

        var badSeverity = await service.ListAsync(Admin(tenant), new AlertFilter(Severity: "urgent"));
        Assert.False(badSeverity.IsSuccess);
        Assert.Equal(AlertErrors.InvalidFilter, badSeverity.Error);

        var badLimit = await service.ListAsync(Admin(tenant), new AlertFilter(Limit: 0));
        Assert.False(badLimit.IsSuccess);
        Assert.Equal(AlertErrors.InvalidFilter, badLimit.Error);
    }
}
