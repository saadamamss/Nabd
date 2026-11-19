using Microsoft.Extensions.Logging;
using Npgsql;
using Nabd.Rules.Contracts;
using Nabd.SharedKernel;
using Nabd.TelemetryStore.Contracts;

namespace Nabd.Rules;

/// <summary>
/// The threshold engine (P4 spec 04, plan/06 §3.1–3.4). Per rule:
/// NORMAL ⇄ VIOLATING(violating_since); duration_seconds is the noise killer;
/// one CrossingDetected per episode; restore re-arms. State lives in memory and
/// reaches Postgres ONLY on transitions + the 60 s checkpoint (§3.4) — never
/// per reading. Stragglers below the batch watermark or below last_evaluated
/// are history-only (§3.3).
/// </summary>
public sealed class RulesEngine : IDisposable
{
    private static readonly TimeSpan Watermark = TimeSpan.FromSeconds(60);

    private readonly RulesDb _db;
    private readonly IEventBus _bus;
    private readonly ILogger<RulesEngine> _logger;
    private readonly IDisposable _subscription;
    private readonly object _cacheGate = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(Guid Tenant, Guid Device), List<RuleState>> _cache = new();

    private long _stateWrites;
    private long _evaluated;
    private long _crossings;

    public RulesEngine(RulesDb db, IEventBus bus, ILogger<RulesEngine> logger)
    {
        _db = db;
        _bus = bus;
        _logger = logger;
        // The engine subscribes ITSELF: a singleton created by the host (the
        // checkpoint worker depends on it, so DI builds it at boot). Nothing else
        // must know the engine exists — the pump only knows CommittedBatch.
        _subscription = bus.Subscribe<CommittedBatch>(EvaluateBatchAsync);
    }

    public void Dispose() => _subscription.Dispose();

    /// <summary>Rows written to rules.rule_eval_state (transitions + checkpoint) — the §3.4 proof surface.</summary>
    public long StateWrites => Interlocked.Read(ref _stateWrites);
    public long Evaluated => Interlocked.Read(ref _evaluated);
    public long Crossings => Interlocked.Read(ref _crossings);

    public async Task EvaluateBatchAsync(CommittedBatch batch, CancellationToken ct = default)
    {
        if (batch.Readings.Count == 0) return;

        await _gate.WaitAsync(ct);
        try
        {
            var states = await GetOrLoadAsync(batch.TenantId, batch.DeviceId, ct);
            var windowStart = batch.Readings.Max(r => r.TimeUtc) - Watermark;

            foreach (var reading in batch.Readings.OrderBy(r => r.TimeUtc))
            {
                if (reading.TimeUtc < windowStart) continue;   // §3.3: straggler — history only

                foreach (var state in states)
                {
                    if (state.Rule.ChannelKey != reading.ChannelKey) continue;
                    if (state.LastEvaluated is { } last && reading.TimeUtc <= last) continue;

                    state.LastEvaluated = reading.TimeUtc;
                    Interlocked.Increment(ref _evaluated);
                    await EvaluateOneAsync(state, reading, ct);
                }
            }

            await PersistDirtyAsync(batch.TenantId, states, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The bus swallows subscriber failures by law (§D4) — but this room
            // must not lose them silently: state stays in memory with Dirty set,
            // so the next batch / checkpoint retries the write.
            _logger.LogError(e,
                "rule evaluation failed for device {DeviceId} — in-memory state kept for retry", batch.DeviceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The 60 s checkpoint (plan/06 §3.4): persist every loaded rule's state, then
    /// drop the cache so the next batch reloads the rule set — CRUD edits reach
    /// the engine within one checkpoint interval (the two hosts share no bus).
    /// </summary>
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // one transaction per tenant, not per device
            foreach (var tenant in _cache.GroupBy(kv => kv.Key.Tenant))
                await WriteStatesAsync(tenant.Key, tenant.SelectMany(kv => kv.Value).ToList(), ct);

            // ONLY after every write committed. A failed checkpoint must leave the
            // cache intact — evicting first would silently forget an in-memory
            // violating_since that never reached Postgres.
            lock (_cacheGate) _cache.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- the machine (plan/06 §3.2 verbatim) ---------------------------------

    private async Task EvaluateOneAsync(RuleState state, CommittedReading reading, CancellationToken ct)
    {
        var violating = Matches(state.Rule, reading.Value);

        if (violating && !state.Violating)
        {
            state.Violating = true;
            state.ViolatingSince = reading.TimeUtc;
            state.Fired = false;
            state.Dirty = true;               // NORMAL → VIOLATING (persist the transition)
            return;
        }

        if (violating)
        {
            if (!state.Fired
                && state.ViolatingSince is { } since
                && reading.TimeUtc - since >= TimeSpan.FromSeconds(state.Rule.DurationSeconds))
            {
                await _bus.PublishAsync(new CrossingDetected(state.Rule, reading), ct);
                Interlocked.Increment(ref _crossings);
                state.Fired = true;           // once per episode
                state.Dirty = true;
            }
            return;
        }

        if (!state.Violating) return;         // normal reading, normal state: nothing

        // back inside range — restore ONLY an episode that actually fired.
        // §5.3 resolves an OPEN alert; a silent spike opened nothing, so sending
        // "restored" for it would describe an alert that never existed.
        if (state.Fired)
            await _bus.PublishAsync(new ConditionRestored(state.Rule, reading), ct);

        state.Violating = false;
        state.ViolatingSince = null;
        state.Fired = false;                  // re-armed for the next episode
        state.Dirty = true;                   // VIOLATING → NORMAL (or silent clear)
    }

    private static bool Matches(RuleSnapshot rule, double value) => rule.RuleType switch
    {
        "above" => value > rule.ThresholdHigh!.Value,
        "below" => value < rule.ThresholdLow!.Value,
        "outside_range" => value < rule.ThresholdLow!.Value || value > rule.ThresholdHigh!.Value,
        _ => false,                           // offline rules have no channel reading
    };

    // ---- persistence ---------------------------------------------------------

    private async Task PersistDirtyAsync(Guid tenantId, List<RuleState> states, CancellationToken ct)
    {
        var dirty = states.Where(s => s.Dirty).ToList();
        if (dirty.Count == 0) return;         // §3.4: no transition = no write
        await WriteStatesAsync(tenantId, dirty, ct);
    }

    private async Task WriteStatesAsync(Guid tenantId, List<RuleState> states, CancellationToken ct)
    {
        if (states.Count == 0) return;

        var (conn, tx) = await _db.OpenTenantScopeAsync(tenantId, ct);
        try
        {
            foreach (var state in states)
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO rules.rule_eval_state (rule_id, last_state, violating_since, last_evaluated_at)
                    VALUES (@r, @ls, @vs, @le)
                    ON CONFLICT (rule_id) DO UPDATE SET
                        last_state = EXCLUDED.last_state,
                        violating_since = EXCLUDED.violating_since,
                        last_evaluated_at = EXCLUDED.last_evaluated_at
                    """, conn, tx);
                cmd.Parameters.AddWithValue("r", state.Rule.RuleId);
                cmd.Parameters.AddWithValue("ls", state.Violating ? "violating" : "normal");
                cmd.Parameters.AddWithValue("vs", (object?)state.ViolatingSince?.UtcDateTime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("le", (state.LastEvaluated ?? DateTimeOffset.UtcNow).UtcDateTime);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);

            // Tell the truth: flags and counters move ONLY after the commit. Clearing
            // Dirty inside the loop meant a rollback left rows "clean" but unwritten.
            Interlocked.Add(ref _stateWrites, states.Count);
            foreach (var state in states) state.Dirty = false;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---- loading -------------------------------------------------------------

    private async Task<List<RuleState>> GetOrLoadAsync(Guid tenantId, Guid deviceId, CancellationToken ct)
    {
        lock (_cacheGate)
            if (_cache.TryGetValue((tenantId, deviceId), out var cached)) return cached;

        var list = new List<RuleState>();
        var (conn, tx) = await _db.OpenTenantScopeAsync(tenantId, ct);
        try
        {
            await using (var cmd = new NpgsqlCommand("""
                SELECT r.id, r.tenant_id, r.device_id, r.channel_key, r.rule_type,
                       r.threshold_low, r.threshold_high, r.duration_seconds,
                       r.cooldown_minutes, r.severity, r.enabled,
                       s.last_state, s.violating_since, s.last_evaluated_at
                FROM rules.rules r
                LEFT JOIN rules.rule_eval_state s ON s.rule_id = r.id
                WHERE r.device_id = @d AND r.enabled
                ORDER BY r.channel_key, r.rule_type
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("d", deviceId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    list.Add(new RuleState
                    {
                        Rule = new RuleSnapshot(
                            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            reader.GetString(4),
                            reader.IsDBNull(5) ? null : reader.GetDouble(5),
                            reader.IsDBNull(6) ? null : reader.GetDouble(6),
                            reader.GetInt32(7), reader.GetInt32(8),
                            reader.GetString(9), reader.GetBoolean(10)),
                        Violating = !reader.IsDBNull(11) && reader.GetString(11) == "violating",
                        ViolatingSince = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                        LastEvaluated = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                    });
                }
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            await conn.DisposeAsync();
        }

        lock (_cacheGate)
        {
            if (_cache.TryGetValue((tenantId, deviceId), out var raced)) return raced;
            _cache[(tenantId, deviceId)] = list;
            return list;
        }
    }

    private sealed class RuleState
    {
        public required RuleSnapshot Rule { get; init; }
        public bool Violating { get; set; }
        public DateTimeOffset? ViolatingSince { get; set; }
        public DateTimeOffset? LastEvaluated { get; set; }
        public bool Fired { get; set; }
        public bool Dirty { get; set; }
    }
}
