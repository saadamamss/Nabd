namespace Nabd.IsolationSuite;

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Rules;
using Nabd.Rules.Contracts;
using Nabd.SharedKernel;
using Nabd.TelemetryStore.Contracts;
using Xunit;

// P4 spec 04 — the threshold engine's state machine (plan/06 §3.1–3.4, FR-30).
// RED-FIRST: `RulesEngine`, `IEventBus`/`InProcessEventBus`, `CommittedBatch`,
// `CrossingDetected` and `ConditionRestored` do not exist yet — the build fails
// on purpose until the implementation lands.
//
// The five facts pin the laws that make P4 honest:
//   1. duration_seconds is the noise killer — a spike that clears never fires
//   2. a sustained violation fires EXACTLY ONCE per episode
//   3. restore re-arms the rule — the second episode fires again
//   4. stragglers (older than last-evaluated / the 60 s watermark) never re-trigger
//   5. a restart mid-episode keeps violating_since — the clock survives
//   6. state WRITES == transitions (+ checkpoints), never per reading (§3.4)
//   7. the checkpoint persists then reloads the rule set (CRUD reaches the engine)
//   8. the `fired` flag survives a checkpoint reload — no ghost crossing (0024)
//   9. a restart AFTER firing still auto-resolves on the first normal reading (0024)
//
// Evaluation is driven by a `CommittedBatch` — the type the flush pump publishes
// only AFTER its INSERT commits (study step 2). The engine never sees a reading
// that is not durably stored; that leg is pinned in FlushPumpTests (fact 7).

public sealed class RulesEngineTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // A fixed clock: duration and watermark are decided by READING times, never by
    // wall-clock, so every fact below is deterministic.
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static CallerContext Admin(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Engine {label}",
            Slug: $"en-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@en.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    /// <summary>The fridge shape (temperature 2–8 with limits, door without) — the
    /// same discriminating pair the seeding fact uses.</summary>
    private async Task<Guid> EnsureTemplateAsync(string label)
    {
        var tplKey = $"en-{label}-{Uid()}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
                VALUES (@k, 'قالب', 'Engine Template');
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

    private async Task<Guid> MintedDeviceAsync(TenantRegistrationResult tenant, string label)
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
        return minted.Value.Device.DeviceId;
    }

    /// <summary>A device whose rule set is EXACTLY the fact's rule: the mint's
    /// seeded outside_range rule is deleted first, so the event stream carries one
    /// rule's story (the seed would otherwise fire on the same hot readings).</summary>
    private async Task<(TenantRegistrationResult Tenant, Guid DeviceId, Guid RuleId)> FreshAsync(string label)
    {
        var tenant = await RegisterAsync(label);
        var deviceId = await MintedDeviceAsync(tenant, label);
        var rules = new DeviceRules(pg.Rules);

        var seeded = await rules.ListByDeviceAsync(Admin(tenant), deviceId);
        Assert.True(seeded.IsSuccess, seeded.Error);
        foreach (var row in seeded.Value)
            Assert.True((await rules.DeleteRuleAsync(Admin(tenant), deviceId, row.RuleId)).IsSuccess);

        var created = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, deviceId, "temperature", "above",
            ThresholdHigh: 9.0, DurationSeconds: 60, CooldownMinutes: 60, Severity: "warn"));
        Assert.True(created.IsSuccess, created.Error);
        return (tenant, deviceId, created.Value.RuleId);
    }

    private static CommittedBatch Batch(
        TenantRegistrationResult tenant, Guid deviceId, DateTimeOffset time, params double[] values)
        => new(tenant.Tenant.TenantId, deviceId,
            values.Select(v => new CommittedReading(deviceId, "temperature", v, time)).ToList());

    /// <summary>Collects the engine's public events; the bus is the real D4 one.</summary>
    private sealed class Recorder
    {
        public List<CrossingDetected> Crossings { get; } = [];
        public List<ConditionRestored> Restored { get; } = [];
        public IEventBus Bus { get; }

        public Recorder(IEventBus bus)
        {
            Bus = bus;
            bus.Subscribe<CrossingDetected>((e, _) => { Crossings.Add(e); return Task.CompletedTask; });
            bus.Subscribe<ConditionRestored>((e, _) => { Restored.Add(e); return Task.CompletedTask; });
        }
    }

    private async Task<(string LastState, DateTimeOffset? ViolatingSince)> ReadStateAsync(Guid ruleId)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT last_state, violating_since FROM rules.rule_eval_state WHERE rule_id = @r;", conn);
        cmd.Parameters.AddWithValue("r", ruleId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return ("", null);
        return (reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    /// <summary>The durable half of fact 8/9: the episode's fired flag lives in the row,
    /// so a reload or a new process cannot forget that this alert was already raised.</summary>
    private async Task<bool> ReadFiredAsync(Guid ruleId)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT fired FROM rules.rule_eval_state WHERE rule_id = @r;", conn);
        cmd.Parameters.AddWithValue("r", ruleId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- fact 1 — duration is the noise killer: a spike that clears never fires ----

    [Fact]
    public async Task a_spike_that_clears_before_duration_never_fires()
    {
        var (tenant, device, ruleId) = await FreshAsync("spike");
        var rec = new Recorder(new InProcessEventBus());
        var engine = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);

        await engine.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));                 // over 9 → VIOLATING
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(30), 5.0));    // clears at 30s < 60s

        Assert.Empty(rec.Crossings);   // the door-opening spike: no alert
        Assert.Empty(rec.Restored);

        var state = await ReadStateAsync(ruleId);
        Assert.Equal("normal", state.LastState);   // it went back, silently
    }

    // ---- fact 2 — a sustained violation fires exactly once per episode ------------

    [Fact]
    public async Task a_sustained_violation_fires_exactly_once()
    {
        var (tenant, device, ruleId) = await FreshAsync("sustained");
        var rec = new Recorder(new InProcessEventBus());
        var engine = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);

        await engine.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(59), 10.0));   // not yet
        Assert.Empty(rec.Crossings);

        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(61), 10.0));   // duration elapsed
        Assert.Single(rec.Crossings);
        Assert.Equal(ruleId, rec.Crossings[0].Rule.RuleId);
        Assert.Equal(10.0, rec.Crossings[0].Reading.Value);

        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddMinutes(15), 11.0));   // still the same episode
        Assert.Single(rec.Crossings);
    }

    // ---- fact 3 — restore re-arms: the second episode fires again ------------------

    [Fact]
    public async Task restoring_rearms_the_rule_for_the_next_episode()
    {
        var (tenant, device, ruleId) = await FreshAsync("rearm");
        var rec = new Recorder(new InProcessEventBus());
        var engine = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);

        await engine.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(61), 10.0));
        Assert.Single(rec.Crossings);

        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(120), 5.0));   // back inside range
        Assert.Single(rec.Restored);
        Assert.Equal(ruleId, rec.Restored[0].Rule.RuleId);

        // second episode — the rule is armed again
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddMinutes(5), 10.0));
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddMinutes(5).AddSeconds(61), 10.0));
        Assert.Equal(2, rec.Crossings.Count);
        Assert.Single(rec.Restored);
    }

    // ---- fact 4 — stragglers never re-trigger (06 §3.3) ---------------------------

    [Fact]
    public async Task out_of_order_stragglers_never_retrigger_or_restore()
    {
        var (tenant, device, _) = await FreshAsync("straggler");
        var rec = new Recorder(new InProcessEventBus());
        var engine = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);

        await engine.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(61), 10.0));
        Assert.Single(rec.Crossings);

        // an old reading (t0+5) arriving after we already evaluated t0+61: it is
        // older than last_evaluated → history only, no restore, no new crossing
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(5), 5.0));
        Assert.Single(rec.Crossings);
        Assert.Empty(rec.Restored);

        // inside ONE batch: max is t0+200, so the 60 s watermark (t0+140) makes the
        // t0+1 normal reading a straggler — the violation must not be "resolved"
        // by out-of-order history
        await engine.EvaluateBatchAsync(new CommittedBatch(
            tenant.Tenant.TenantId, device,
            [
                new CommittedReading(device, "temperature", 5.0, T0.AddSeconds(1)),
                new CommittedReading(device, "temperature", 10.0, T0.AddSeconds(200)),
            ]));
        Assert.Single(rec.Crossings);
        Assert.Empty(rec.Restored);

        // a FRESH normal reading (inside the window) is the honest resolution
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(300), 5.0));
        Assert.Single(rec.Restored);
    }

    // ---- fact 5 — a restart mid-episode keeps violating_since ---------------------

    [Fact]
    public async Task a_restart_mid_episode_keeps_the_violation_clock()
    {
        var (tenant, device, ruleId) = await FreshAsync("restart");
        var rec = new Recorder(new InProcessEventBus());

        // first engine: enters VIOLATING at t0 and is "killed" before duration elapses
        var first = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);
        await first.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));
        Assert.Empty(rec.Crossings);

        var persisted = await ReadStateAsync(ruleId);
        Assert.Equal("violating", persisted.LastState);
        Assert.Equal(T0, persisted.ViolatingSince);

        // new process (fresh in-memory state) hydrates from rule_eval_state
        var second = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);
        await second.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(61), 10.0));

        Assert.Single(rec.Crossings);   // fired at 61 s of the ORIGINAL episode, not 61 s of a new one
        Assert.Equal(ruleId, rec.Crossings[0].Rule.RuleId);
    }

    // ---- fact 6 — state writes track transitions, never readings (§3.4) ----------

    [Fact]
    public async Task state_writes_track_transitions_not_readings()
    {
        var (tenant, device, _) = await FreshAsync("writes");
        var rec = new Recorder(new InProcessEventBus());
        var engine = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);

        // 11 readings inside one episode; only NORMAL→VIOLATING and the fired
        // transition deserve a write — at 2,000 readings/s a per-reading write
        // would add 2,000 UPDATEs/s for nothing.
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));
        for (var s = 1; s <= 8; s++)
            await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(s * 7), 10.0));
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(61), 10.0));
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(70), 10.0));

        Assert.Single(rec.Crossings);
        Assert.Equal(2, engine.StateWrites);
    }

    // ---- fact 7 — the checkpoint persists, then reloads the rule set --------------

    [Fact]
    public async Task checkpoint_persists_state_and_reloads_the_rule_set()
    {
        var (tenant, device, _) = await FreshAsync("checkpoint");
        var rec = new Recorder(new InProcessEventBus());
        var engine = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);

        // entering VIOLATING already wrote its transition (1 row)
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));
        Assert.Equal(1, engine.StateWrites);

        // a rule arrives AFTER this device's rule set was cached (same channel,
        // different type — one rule per channel+type by law)
        var rules = new DeviceRules(pg.Rules);
        var added = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, device, "temperature", "below",
            ThresholdLow: 1.0, DurationSeconds: 60, CooldownMinutes: 60, Severity: "warn"));
        Assert.True(added.IsSuccess, added.Error);

        // the checkpoint persists the in-memory state AND evicts the cache
        await engine.CheckpointAsync();
        Assert.Equal(2, engine.StateWrites);   // transition row + checkpoint row

        // after eviction the next batch reloads BOTH rules — no cross-host bus (D4),
        // so CRUD reaches the engine through this reload, not by invalidation
        var evaluatedBefore = engine.Evaluated;
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(120), 5.0));
        Assert.Equal(evaluatedBefore + 2, engine.Evaluated);
        Assert.Empty(rec.Crossings);
    }

    // ---- fact 8 — the fired flag survives a checkpoint reload (0024) --------------

    [Fact]
    public async Task the_fired_flag_survives_a_checkpoint_reload()
    {
        var (tenant, device, ruleId) = await FreshAsync("fired-checkpoint");
        var rec = new Recorder(new InProcessEventBus());
        var engine = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);

        await engine.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(61), 10.0));
        Assert.Single(rec.Crossings);

        // the crossing's write carries fired=true — the row, not the cache, is the truth
        Assert.True(await ReadFiredAsync(ruleId),
            "the crossing must persist fired=true in rules.rule_eval_state");

        // the 60 s checkpoint persists and EVICTS the cache (plan/06 §3.4)
        await engine.CheckpointAsync();

        // same episode, still violating: the reloaded state must remember it fired.
        // Without the flag this reading re-published CrossingDetected (the 60 s ghosts
        // seen in the spec 06 drill at 17:42:26 and 17:43:26).
        await engine.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(120), 10.0));

        Assert.Single(rec.Crossings);   // one real crossing, no ghost from the reload
        Assert.Empty(rec.Restored);
        Assert.True(await ReadFiredAsync(ruleId));
    }

    // ---- fact 9 — a restart after firing still auto-resolves on return (0024) -----

    [Fact]
    public async Task a_restart_after_firing_still_resolves_on_the_first_normal_reading()
    {
        var (tenant, device, ruleId) = await FreshAsync("fired-restart");
        var rec = new Recorder(new InProcessEventBus());

        var first = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);
        await first.EvaluateBatchAsync(Batch(tenant, device, T0, 10.0));
        await first.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(61), 10.0));
        Assert.Single(rec.Crossings);
        first.Dispose();   // the process dies with the episode ON and the alert OPEN

        Assert.True(await ReadFiredAsync(ruleId),
            "the fired flag must be durable before the restart matters");

        // new process, first reading back inside range — no violating reading in
        // between, so the durable flag is the ONLY evidence that this episode fired.
        // Without it the engine stays silent and the open alert never resolves
        // (the deterministic 17:38:31 restart in the spec 06 drill).
        var second = new RulesEngine(pg.Rules, rec.Bus, NullLogger<RulesEngine>.Instance);
        await second.EvaluateBatchAsync(Batch(tenant, device, T0.AddSeconds(120), 5.0));

        Assert.Single(rec.Restored);    // §5.3: the OPEN alert auto-resolves
        Assert.Equal(ruleId, rec.Restored[0].Rule.RuleId);
        second.Dispose();
    }
}
