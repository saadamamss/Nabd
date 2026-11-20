using Microsoft.Extensions.Logging;
using Npgsql;
using Nabd.Rules.Contracts;
using Nabd.SharedKernel;
using Nabd.TelemetryStore.Contracts;

namespace Nabd.Rules;

/// <summary>
/// The offline watchdog (P4 spec 07, FR-31; plan/06 §4, plan/04 §6, plan/05 §9).
///
/// One tick = one SECURITY DEFINER read of the fleet window (0025) + at most one
/// transaction of state transitions. Per device-offline rule:
///   NORMAL → VIOLATING  when now − last_seen &gt; offline_after_minutes: publish
///                        OfflineDetected once, persist violating_since = last words, fired = true.
///   VIOLATING → NORMAL  when the device is heard again: publish OnlineRestored with
///                        the TRUE gap (last words → first words again).
/// A quiet tick writes NOTHING. State is re-read every tick (no cache), so a
/// restart neither re-fires a dead device nor forgets a return — the row is the
/// memory, the same lesson the threshold engine earned with 0024.
///
/// Last-seen truth = max(device_status, this process's tracker): the flush window
/// never counts against the detection budget (plan/04 §6).
/// </summary>
public sealed class OfflineWatchdog(
    RulesDb db,
    IEventBus bus,
    IDeviceStatusTracker presence,
    ILogger<OfflineWatchdog> logger)
{
    private long _offlineDetections;
    private long _onlineRestorations;
    private long _stateWrites;

    /// <summary>plan/06 §11: `watchdog.device_offline` — this process's count.</summary>
    public long OfflineDetections => Interlocked.Read(ref _offlineDetections);

    /// <summary>plan/06 §11: `watchdog.device_online_restored`.</summary>
    public long OnlineRestorations => Interlocked.Read(ref _onlineRestorations);

    /// <summary>Transitions only — the §3.4 write discipline applied to this loop.</summary>
    public long StateWrites => Interlocked.Read(ref _stateWrites);

    public async Task TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            // Every comparison happens at the STORE's resolution (microseconds), never
            // at .NET's 100 ns ticks: the same instant round-trips up to 9 ticks short,
            // and a raw `>` then reads a device's own last words as a fresh return.
            // Found by the 2026-11-20 live drill — the alert resolved 90 s BEFORE it
            // opened, then re-detected into a cooldown suppression.
            now = Micros(now);

            var candidates = await LoadCandidatesAsync(ct);
            if (candidates.Count == 0) return;

            var writes = new List<StateWrite>();

            foreach (var candidate in candidates)
            {
                var rule = candidate.Rule;
                var dbMark = Micros(candidate.LastSeenAtUtc);
                var memoryMark = presence.LastSeen(rule.DeviceId) is { } mark ? Micros(mark) : (DateTimeOffset?)null;
                var lastSeen = memoryMark is { } fresh && fresh > dbMark ? fresh : dbMark;

                if (candidate.Offline)
                {
                    // the row already says "reported offline"; only a NEW mark is news
                    var lastWords = Micros(candidate.ViolatingSince ?? candidate.LastSeenAtUtc);
                    if (lastSeen <= lastWords) continue;      // still dead: no event, no write

                    if (candidate.Fired)
                    {
                        // §5.3 + §4: the true gap is last words → first words again
                        await bus.PublishAsync(
                            new OnlineRestored(rule, lastWords, lastSeen), ct);
                        Interlocked.Increment(ref _onlineRestorations);
                    }

                    writes.Add(new StateWrite(rule.RuleId, "normal", null, now, Fired: false));
                    continue;
                }

                if (now - lastSeen <= TimeSpan.FromMinutes(rule.OfflineAfterMinutes))
                    continue;                                  // still inside the window

                await bus.PublishAsync(new OfflineDetected(rule, lastSeen, now), ct);
                Interlocked.Increment(ref _offlineDetections);
                writes.Add(new StateWrite(rule.RuleId, "violating", lastSeen, now, Fired: true));
            }

            if (writes.Count > 0) await PersistAsync(writes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;                                            // a real shutdown
        }
        catch (Exception e)
        {
            // never kill the host over the watchdog; the next tick re-reads the rows.
            // Publishing BEFORE persisting is deliberate: a crash between the two
            // re-publishes, and Alerting's dedup absorbs it — the reverse order would
            // mark a device fired whose alert was never raised (silence forever).
            logger.LogError(e, "offline watchdog tick failed — state rows are re-read next tick");
        }
    }

    private async Task<List<Candidate>> LoadCandidatesAsync(CancellationToken ct)
    {
        var list = new List<Candidate>();
        await using var conn = await db.OpenPreAuthConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, device_id, rule_id, severity, cooldown_minutes,
                   duration_seconds, offline_after_minutes, last_seen_at,
                   state_last_state, violating_since, fired
            FROM rules.offline_watchdog_candidates()
            """, conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Candidate(
                Rule: new RuleSnapshot(
                    RuleId: reader.GetGuid(2),
                    TenantId: reader.GetGuid(0),
                    DeviceId: reader.GetGuid(1),
                    ChannelKey: null,
                    RuleType: "offline",
                    ThresholdLow: null,
                    ThresholdHigh: null,
                    DurationSeconds: reader.GetInt32(5),
                    CooldownMinutes: reader.GetInt32(4),
                    Severity: reader.GetString(3),
                    Enabled: true,
                    // offline never consults windows (§5.4) — null = "not loaded", honestly
                    MuteWindows: null,
                    OfflineAfterMinutes: reader.GetInt32(6)),
                LastSeenAtUtc: reader.GetFieldValue<DateTimeOffset>(7),
                StateLast: reader.IsDBNull(8) ? null : reader.GetString(8),
                ViolatingSince: reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                Fired: reader.GetBoolean(10)));
        }
        return list;
    }

    private async Task PersistAsync(List<StateWrite> writes, CancellationToken ct)
    {
        await using var conn = await db.OpenPreAuthConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var write in writes)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO rules.rule_eval_state
                    (rule_id, last_state, violating_since, last_evaluated_at, fired)
                VALUES (@r, @ls, @vs, @le, @f)
                ON CONFLICT (rule_id) DO UPDATE SET
                    last_state = EXCLUDED.last_state,
                    violating_since = EXCLUDED.violating_since,
                    last_evaluated_at = EXCLUDED.last_evaluated_at,
                    fired = EXCLUDED.fired
                """, conn, tx);
            cmd.Parameters.AddWithValue("r", write.RuleId);
            cmd.Parameters.AddWithValue("ls", write.LastState);
            cmd.Parameters.AddWithValue("vs",
                write.ViolatingSince is { } since ? (object)since.UtcDateTime : DBNull.Value);
            cmd.Parameters.AddWithValue("le", write.LastEvaluatedAt.UtcDateTime);
            cmd.Parameters.AddWithValue("f", write.Fired);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        Interlocked.Add(ref _stateWrites, writes.Count);
    }

    /// <summary>
    /// `timestamptz` keeps whole microseconds; .NET keeps 100 ns ticks. Truncate to the
    /// store's resolution so an unchanged instant never compares as "newer".
    /// </summary>
    private static DateTimeOffset Micros(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    private sealed record Candidate(
        RuleSnapshot Rule,
        DateTimeOffset LastSeenAtUtc,
        string? StateLast,
        DateTimeOffset? ViolatingSince,
        bool Fired)
    {
        public bool Offline => StateLast == "violating";
    }

    private sealed record StateWrite(
        Guid RuleId,
        string LastState,
        DateTimeOffset? ViolatingSince,
        DateTimeOffset LastEvaluatedAt,
        bool Fired);
}
