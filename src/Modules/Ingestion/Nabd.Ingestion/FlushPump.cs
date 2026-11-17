namespace Nabd.Ingestion;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;

/// <summary>The outcome of ONE flush attempt — the loop's steering input.</summary>
public enum FlushOutcome
{
    /// <summary>The buffer was empty — nothing to do.</summary>
    Nothing,

    /// <summary>Every drained message reached a durable outcome (stored or receipted) and was acked.</summary>
    Completed,

    /// <summary>R10: zero acks, zero NEW receipts — the drained snapshot was re-queued to the tail; retry with backoff.</summary>
    TransientAbort,

    /// <summary>Shutdown/cancellation mid-flush: zero acks — EMQX re-queues via session expiry (CleanStart=false, 1 h).</summary>
    Cancelled,
}

/// <summary>
/// THE FLUSH PUMP (P4 spec 01, Moves B+C+E): the ONE task that owns every
/// durable outcome on the MQTT path. The delivery handler is O(1) (parse +
/// enqueue — zero DB); this pump pays the DB ONCE per flush:
///
///   Phase R — parse-reject receipts: each raw bytes + DLQ_INVALID lands in
///             telemetry.raw_rejects (0019), acked AFTER its receipt commits.
///   Phase V — ONE set-based resolution for EVERY device in the flush (0020 —
///             the lever's core claim, fact 6). Absent row = unknown/revoked/
///             deactivated → uniform E422 receipt + ack, other devices proceed.
///   Phase J — per-message verdicts: ValidateResolved (the extracted core) on
///             the resolved identity; a message with ANY invalid reading is
///             rejected WHOLE (one receipt, none of its readings stored).
///   Phase S — StoreFlushAsync for ALL rows of ALL messages (one multi-row
///             INSERT per chunk, cross-tenant legal). Ack after commit.
///
/// THE LAWS, test-pinned:
/// - Ack law: every message acked ONLY after its durable outcome commits; a
///   transient failure = ZERO acks for every message still pending.
/// - R10 transient/deterministic: the poison ladder descends ONLY on
///   deterministic row-content failures (StoreFlushException.IsDeterministic
///   — SQLSTATE class 22 + 23514). ANY transient/unknown failure aborts the
///   ENTIRE flush at whatever level it occurs: zero acks, zero NEW receipts,
///   the whole snapshot re-queued to the buffer's TAIL, retried on the next
///   tick with backoff (1 s → ×2, cap 10 s, reset on success).
/// - R6 ladder: flush → per-device → row-by-row; rows that still fail get a
///   DLQ_STORE_POISON receipt and ARE acked (stored-or-receipted, never looped).
/// - R7 backpressure alarm: pending > AggregateWindow + in-flight-ack slack +
///   MaxReadingsPerMessage is a flow-control BUG, logged loud — never a tuning knob.
/// - Option A escalation: every transient abort logs WARNING with the
///   consecutive count; ≥ 3 → stall WARNING; ≥ 30 → ERROR; ≥ 60 → CRITICAL;
///   reset on success. Display channel = the ProcessingHost log stream.
/// - Presence: MarkSeen stays O(1) in-memory AFTER the rows land — the status
///   flush cadence is DeviceStatusFlushWorker's own (F3: readings-only pump).
/// </summary>
public sealed class FlushPump(
    FlushBuffer buffer,
    FlushOptions options,
    IIngestionSetResolver resolver,
    ValidationPipeline pipeline,
    IReadingsStore store,
    IRawRejectStore rawRejects,
    IDeviceStatusTracker deviceStatus,
    RejectionCounters counters,
    FlushCounters flushCounters,
    ReconcileCounters? reconcile,
    ILogger<FlushPump> logger)
{
    /// <summary>Serializes flush attempts — the timer wake, the size wake, and the final flush never interleave.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    // ---- THE OVERLAPPING ACK WINDOW (owner ruling 2026-11-17) ------------------
    // The sharded gate failed at 958 rows/s with the ack chunks awaited SERIALLY
    // inside the flush: every fleet-time flush drained the full 800-message
    // aggregate window in 685-1,139 ms (13 chunks × ~50-85 ms PUBACK RTT), the
    // broker dispatched only when PUBACKs arrived, and the mqueue sat pinned at
    // 1,000 dropping ~49% of everything. The law now: DriveAsync LAUNCHES
    // bounded chunks of 64 and RETURNS — acks fly while the NEXT flush drains
    // and commits. A sliding SemaphoreSlim of 4 in-flight chunks keeps the
    // convoy dead (the 2,000-concurrent-ack-task measurement was superlinear)
    // and each connection's mailbox burst bounded at one chunk.
    private const int AckChunkSize = 64;
    private const int MaxInFlightAckChunks = 4;          // the owner's "2–4 chunks in flight" ruling
    private readonly SemaphoreSlim _ackWindow = new(MaxInFlightAckChunks, MaxInFlightAckChunks);
    private readonly object _ackTrack = new();
    private readonly List<Task> _ackTasks = [];          // in-flight chunk tasks (for the shutdown drain)
    private volatile bool _flushInFlight;
    private readonly HashSet<PendingDelivery> _accounted = new(ReferenceEqualityComparer.Instance);

    /// <summary>P4 spec 02: true while a flush attempt is between drain and commit — the QUIET law's grace input.</summary>
    public bool FlushInFlight => _flushInFlight;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // THE EAGER-DRAIN LAW (restored 2026-11-16 by owner order, after run
            // 3's referee): a flush attempt fires EVERY loop iteration — whatever
            // is buffered flushes immediately, however small; only an EMPTY buffer
            // waits (the size trigger or the interval tick, whichever first —
            // R1's latency bound). WHY the revert: run 3 measured the coalescing
            // alternative (flush only on the size crossing or the tick) as a
            // REGRESSION — the in-flight window turns over ONLY at ack time, the
            // sparse ack cadence starved the broker's dispatch, the consumer's
            // mqueue filled, and EMQX dropped 1,045,953 of 1,154,599 publishes
            // (172 rows/s stored vs the eager pump's 1,674/s). Small batches are
            // the price of an always-open window; the ceiling attack moves to the
            // window size, behind the owner-ruled diagnostic.
            var outcome = await FlushOnceAsync(stoppingToken);
            if (outcome is FlushOutcome.Cancelled) break;

            if (outcome is FlushOutcome.TransientAbort)
            {
                // R10's backoff: 1 s → ×2 per consecutive abort, cap 10 s (Option A retries forever).
                var delay = TimeSpan.FromMilliseconds(Math.Min(
                    1000 * Math.Pow(2, Math.Clamp(flushCounters.ConsecutiveAborts - 1, 0, 4)), 10_000));
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;   // retry immediately after the backoff — the snapshot sits at the tail
            }

            // Empty buffer: sleep until the next wake — a size crossing (200
            // readings) or the interval tick (R1's latency bound), whichever
            // first. Anything buffered at wake flushes on the next iteration.
            if (buffer.BufferedReadings == 0 && buffer.ParseRejectsCount == 0)
            {
                try { await Task.WhenAny(buffer.TakeSizeTrigger(), Task.Delay(options.FlushIntervalMs, stoppingToken)); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// ONE flush attempt: drain both streams, drive every durable outcome.
    /// NEVER throws — cancellation comes back as FlushOutcome.Cancelled.
    /// </summary>
    public async Task<FlushOutcome> FlushOnceAsync(CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return FlushOutcome.Cancelled; }

        _accounted.Clear();
        _flushInFlight = true;
        try
        {
            var rejects = buffer.DrainParseRejects();
            var accepted = buffer.DrainAccepted();
            if (rejects.Count == 0 && accepted.Count == 0) return FlushOutcome.Nothing;

            var clock = Stopwatch.StartNew();
            FlushOutcome outcome;
            try { outcome = await DriveAsync(rejects, accepted, ct); }
            catch (OperationCanceledException)
            {
                // Cancellation mid-phase that escaped a helper: shutdown is never a
                // verdict — zero NEW acks happened on this path; drained entries
                // ride the broker's redelivery (replay idempotent).
                outcome = FlushOutcome.Cancelled;
                HandBackDrained(rejects, accepted);
            }
            catch (Exception e)
            {
                // THE NEVER-THROWS LAW: an unexpected exception must NEVER kill the
                // pump task — it is classified transient (R10: unknown = transient),
                // logged with the escalation ladder, and the flush ends aborted.
                // Drained entries ride the broker's redelivery (replay idempotent).
                outcome = RegisterAbort("flush attempt crashed (unknown exception)", e);
                HandBackDrained(rejects, accepted);
            }

            logger.LogInformation(
                "flush #{Flush} outcome={Outcome} rows={Rows} ms={Duration} descents={Descents} poison={Poison} pending={Pending} buffered={Buffered}",
                flushCounters.Flushes, outcome, flushCounters.RowsStored, clock.ElapsedMilliseconds,
                flushCounters.LadderDescents, flushCounters.PoisonReceipts,
                buffer.PendingMessages, buffer.BufferedReadings);

            // R7 (AMENDED twice — sharding 2026-11-16, ack overlap 2026-11-17):
            // pending ≡ unacked is STRUCTURALLY bounded by the AGGREGATE window
            // (Shards × Receive Maximum) PLUS the in-flight-ack slack — messages
            // whose commit landed and whose PUBACK chunk is still flying are
            // not yet retired from pending, lawfully. Beyond that sum is a
            // flow-control BUG.
            var ackFlightSlack = MaxInFlightAckChunks * AckChunkSize;
            if (buffer.PendingMessages > options.AggregateWindow + ackFlightSlack + PayloadV1Parser.MaxReadingsPerMessage)
                logger.LogWarning(
                    "flow-control ALARM: pending={Pending} exceeds the aggregate window ({Shards}×{ReceiveMaximum})+{AckFlight}+{Slack} — the window law is broken (a BUG, not a tuning knob)",
                    buffer.PendingMessages, options.ConsumerShards, options.ReceiveMaximum, ackFlightSlack,
                    PayloadV1Parser.MaxReadingsPerMessage);

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MarkAccounted(PendingDelivery entry) => _accounted.Add(entry);

    private void HandBackDrained(List<PendingDelivery> rejects, List<PendingDelivery> accepted)
    {
        if (reconcile is null) return;
        var units = 0L;
        foreach (var r in rejects) if (!_accounted.Contains(r)) units += 1;
        foreach (var a in accepted) if (!_accounted.Contains(a)) units += a.Parsed!.Readings.Count;

        if (units > 0) reconcile.HandedBack((int)units);
    }

    /// <summary>
    /// R11's bounded shutdown drain (~5 s from the worker): drain until the
    /// buffer is empty, the bound expires, or an abort refuses to heal.
    /// Success → everything acked → clean. Failure/cancel → zero acks remain →
    /// EMQX re-queues (CleanStart=false + 1 h session expiry) and redelivers
    /// on next boot. Fact 14.
    /// </summary>
    public async Task FinalFlushAsync(TimeSpan bound, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(bound);

        while (buffer.ParseRejectsCount + buffer.AcceptedCount > 0)
        {
            FlushOutcome outcome;
            try { outcome = await FlushOnceAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                logger.LogWarning("final flush BOUND expired with {Accepted} accepted + {Rejected} rejects still buffered — zero acks; the broker re-queues them (session expiry)",
                    buffer.AcceptedCount, buffer.ParseRejectsCount);
                reconcile?.HandedBack((int)(buffer.BufferedReadings + buffer.ParseRejectsCount));
                return;
            }

            if (outcome is FlushOutcome.Cancelled)
            {
                logger.LogWarning("final flush cancelled with {Accepted} accepted + {Rejected} rejects still buffered — zero acks; the broker re-queues them (session expiry)",
                    buffer.AcceptedCount, buffer.ParseRejectsCount);
                reconcile?.HandedBack((int)(buffer.BufferedReadings + buffer.ParseRejectsCount));
                return;
            }
            if (outcome is FlushOutcome.TransientAbort)
            {
                logger.LogWarning("final flush hit a transient abort — no retries during shutdown; zero acks remain; the broker re-queues them (session expiry)");
                reconcile?.HandedBack((int)(buffer.BufferedReadings + buffer.ParseRejectsCount));
                return;
            }
        }

        // THE SHUTDOWN DRAIN (owner ruling 2026-11-17): the loop's last flushes
        // may have left ack chunks in flight — wait for them HERE, while every
        // shard's connection is still open, so the PUBACKs actually fly. The
        // bound above covers the wait too: an ack that cannot complete in time
        // fails on the disconnect and rides the broker's redelivery (idempotent).
        await DrainAcksAsync(timeout.Token);
    }

    // ---- the flush attempt's three phases -------------------------------------

    private async Task<FlushOutcome> DriveAsync(List<PendingDelivery> rejects, List<PendingDelivery> accepted, CancellationToken ct)
    {
        // Phase R — the destination law (spec 06), timed by the pump now: each
        // parse-reject's raw bytes land in raw_rejects and it is acked AFTER
        // its receipt commits. A receipt-write failure is TRANSIENT (fact 13):
        // no committed receipt = no durable outcome = no ack — abort-all.
        for (var i = 0; i < rejects.Count; i++)
        {
            var reject = rejects[i];
            var receipt = await TryReceiptAsync(reject, IngestionErrors.DLQ_INVALID,
                "payload failed stage 3 (structure) — the raw bytes are the forensics", ct);
            if (receipt.Outcome is ReceiptResult.Cancelled)
            {
                buffer.RequeueParseRejects(rejects[i..]);
                buffer.RequeueAccepted(accepted);
                return FlushOutcome.Cancelled;
            }
            if (receipt.Outcome is ReceiptResult.Transient)
            {
                buffer.RequeueParseRejects(rejects[i..]);
                buffer.RequeueAccepted(accepted);
                return RegisterAbort("receipt write failed (transient)", receipt.Error!);
            }

            counters.Increment(IngestionErrors.DLQ_INVALID);
            await AckAsync(reject);   // after the receipt committed — the ack law
        }

        if (accepted.Count == 0) { flushCounters.FlushCompleted(0); return FlushOutcome.Completed; }

        // Phase V — ONE resolution for the whole flush (fact 6, the lever's core
        // claim). Resolver failure = transient (abort-all, requeue, no receipts).
        var ids = accepted.Select(p => p.Envelope.DeviceId).Distinct().ToArray();
        IReadOnlyDictionary<Guid, DeviceCatalogRow> resolved;
        try { resolved = await resolver.ResolveDevicesAsync(ids, ct); }
        catch (OperationCanceledException)
        {
            buffer.RequeueAccepted(accepted);
            return FlushOutcome.Cancelled;
        }
        catch (Exception e)
        {
            buffer.RequeueAccepted(accepted);
            return RegisterAbort("resolution failed (transient)", e);
        }

        // Phase J — per-message verdicts (isolation: a bad message dies ALONE —
        // facts 7/8; its neighbors in the same flush proceed untouched).
        var work = new List<PumpWork>();
        for (var i = 0; i < accepted.Count; i++)
        {
            var entry = accepted[i];
            if (!resolved.TryGetValue(entry.Envelope.DeviceId, out var catalog))
            {
                var receipt = await TryReceiptAsync(entry, IngestionErrors.E422_UNKNOWN_CHANNEL,
                    "unknown device or channel catalog (uniform rejection — no existence hints)", ct);
                if (receipt.Outcome is ReceiptResult.Cancelled)
                {
                    buffer.RequeueAccepted(accepted[i..]);
                    buffer.RequeueAccepted(work.Select(w => w.Entry).ToList());
                    return FlushOutcome.Cancelled;
                }
                if (receipt.Outcome is ReceiptResult.Transient)
                {
                    buffer.RequeueAccepted(accepted[i..]);
                    buffer.RequeueAccepted(work.Select(w => w.Entry).ToList());
                    return RegisterAbort("receipt write failed (transient)", receipt.Error!);
                }
                counters.Increment(IngestionErrors.E422_UNKNOWN_CHANNEL, entry.Parsed!.Readings.Count);
                await AckAsync(entry);
                continue;
            }

            var result = pipeline.ValidateResolved(entry.Envelope, catalog.TenantId, catalog.ChannelKeys);
            if (!result.IsAccepted)
            {
                var receipt = await TryReceiptAsync(entry, result.Rejected!.Code, result.Rejected.Detail, ct);
                if (receipt.Outcome is ReceiptResult.Cancelled)
                {
                    buffer.RequeueAccepted(accepted[i..]);
                    buffer.RequeueAccepted(work.Select(w => w.Entry).ToList());
                    return FlushOutcome.Cancelled;
                }
                if (receipt.Outcome is ReceiptResult.Transient)
                {
                    buffer.RequeueAccepted(accepted[i..]);
                    buffer.RequeueAccepted(work.Select(w => w.Entry).ToList());
                    return RegisterAbort("receipt write failed (transient)", receipt.Error!);
                }
                counters.Increment(result.Rejected.Code, entry.Parsed!.Readings.Count);
                await AckAsync(entry);
                continue;
            }

            work.Add(new PumpWork(entry, result.Accepted!, result.Warnings));
        }

        if (work.Count == 0) { flushCounters.FlushCompleted(0); return FlushOutcome.Completed; }

        // Phase S — the store (Move C): ALL rows of ALL messages, one multi-row
        // INSERT per chunk, cross-tenant legal, ON CONFLICT DO NOTHING.
        var rows = work.SelectMany(w => w.Rows).ToList();
        try
        {
            await store.StoreFlushAsync(rows.Select(ToDto).ToList(), ct);
        }
        catch (StoreFlushException sfe) when (sfe.IsDeterministic)
        {
            return await LadderAsync(work, sfe, ct);
        }
        catch (OperationCanceledException)
        {
            buffer.RequeueAccepted(work.Select(w => w.Entry).ToList());
            return FlushOutcome.Cancelled;
        }
        catch (Exception e)
        {
            // R10: an unknown/ambiguous error class is TRANSIENT — abort-all,
            // zero acks, requeue to the tail, retry on the next tick.
            buffer.RequeueAccepted(work.Select(w => w.Entry).ToList());
            return RegisterAbort("store failed (transient/unknown)", e);
        }

        // Every row landed in one commit — LAUNCH the acks (the overlapping
        // window above), then mark presence (in-memory, O(1); the DB status
        // cadence stays DeviceStatusFlushWorker's).
        //
        // THE ACK-RETIRE LAW (unchanged in substance, now per chunk): each
        // chunk retires its messages' pending contribution when it COMPLETES —
        // success or failure alike (a failed ack rides the guaranteed
        // redelivery whose re-offer re-adds pending under its own copy).
        // Retiring on completion is what keeps the window alarm honest after
        // a connection kill (measured without it: 1,750 zombie pending) —
        // and retiring PER CHUNK is what keeps it honest while chunks are
        // still in flight (the R7 bound below carries the ack-flight slack).
        await LaunchAckChunksAsync(work);

        foreach (var w in work)
        {
            var first = w.Rows[0];
            await deviceStatus.MarkSeenAsync(w.Entry.Envelope.DeviceId, first.TenantId,
                w.Entry.Envelope.ReceivedAtUtc, first.TimeUtc, ct);
            logger.LogDebug(
                "flush STORED device={DeviceId} rows={Count} seq={Seq} warnings={Warnings}",
                w.Entry.Envelope.DeviceId, w.Rows.Count, w.Rows[0].Seq,
                w.Warnings.Count == 0 ? "none" : string.Join(",", w.Warnings));
        }

        flushCounters.FlushCompleted(rows.Count);
        return FlushOutcome.Completed;
    }

    /// <summary>
    /// R6/R10's poison ladder: flush → per-device → row-by-row. Descends ONLY on
    /// deterministic failures; ANY transient error aborts the ENTIRE flush
    /// (zero acks for the non-finalized, requeue, retry with backoff).
    /// </summary>
    private async Task<FlushOutcome> LadderAsync(List<PumpWork> work, StoreFlushException cause, CancellationToken ct)
    {
        flushCounters.LadderDescent();
        logger.LogWarning(
            "flush ladder DESCENT (deterministic, SQLSTATE {SqlState}) — retrying per device",
            cause.SqlState);

        // Flatten rows with their owning message and a global index for the
        // landed-set bookkeeping.
        var flat = new List<(NormalizedReading Row, PumpWork Owner)>(work.Sum(w => w.Rows.Count));
        foreach (var w in work)
            foreach (var row in w.Rows)
                flat.Add((row, w));

        var landed = new HashSet<int>();
        var receipted = new HashSet<PumpWork>();   // poison-receipted messages (finalized)

        var byDevice = flat.Select((x, Index) => (x.Row, x.Owner, Index))
            .GroupBy(t => t.Row.DeviceId);

        foreach (var group in byDevice)
        {
            var groupRows = group.Select(t => ToDto(t.Row)).ToList();
            try
            {
                await store.StoreFlushAsync(groupRows, ct);
                foreach (var t in group) landed.Add(t.Index);
                continue;
            }
            catch (OperationCanceledException)
            {
                buffer.RequeueAccepted(NotFinalized(work, receipted));
                return FlushOutcome.Cancelled;
            }
            catch (StoreFlushException g) when (g.IsDeterministic)
            {
                // Level 3: row-by-row — each row its own statement.
                flushCounters.LadderDescent();
                logger.LogWarning("flush ladder DESCENT level=row-by-row for device {DeviceId} (SQLSTATE {SqlState})", group.Key, g.SqlState);
            }
            catch (Exception e)
            {
                buffer.RequeueAccepted(NotFinalized(work, receipted));
                return RegisterAbort("ladder per-device failed (transient/unknown)", e);
            }

            foreach (var t in group)
            {
                try
                {
                    await store.StoreFlushAsync([ToDto(t.Row)], ct);
                    landed.Add(t.Index);
                }
                catch (OperationCanceledException)
                {
                    buffer.RequeueAccepted(NotFinalized(work, receipted));
                    return FlushOutcome.Cancelled;
                }
                catch (StoreFlushException row) when (row.IsDeterministic)
                {
                    // Poison: the row fails identically on every retry. Its message
                    // gets ONE DLQ_STORE_POISON receipt (raw bytes verbatim) and IS
                    // acked — stored-or-receipted, never looped (fact 11).
                    if (receipted.Add(t.Owner))
                    {
                        var receipt = await TryReceiptAsync(t.Owner.Entry, IngestionErrors.DLQ_STORE_POISON,
                            $"a reading failed deterministically at the ladder's floor (SQLSTATE {row.SqlState}); the row would poison every retry", ct);
                        if (receipt.Outcome is ReceiptResult.Cancelled)
                        {
                            buffer.RequeueAccepted(NotFinalized(work, receipted));
                            return FlushOutcome.Cancelled;
                        }
                        if (receipt.Outcome is ReceiptResult.Transient)
                        {
                            buffer.RequeueAccepted(NotFinalized(work, receipted));
                            return RegisterAbort("poison receipt write failed (transient)", receipt.Error!);
                        }
                        flushCounters.PoisonReceipt();
                        counters.Increment(IngestionErrors.DLQ_STORE_POISON);
                        // The receipt IS the message's final outcome — ack NOW
                        // (stored-or-receipted, never looped: an unacked poison
                        // message would be redelivered forever).
                        await AckAsync(t.Owner.Entry);
                    }
                }
                catch (Exception e)
                {
                    buffer.RequeueAccepted(NotFinalized(work, receipted));
                    return RegisterAbort("ladder row-by-row failed (transient/unknown)", e);
                }
            }
        }

        // Non-aborted ladder end: every message is finalized — fully landed
        // (acked here) or poison-receipted (acked at its receipt commit).
        foreach (var w in work)
        {
            if (receipted.Contains(w)) continue;
            var allLanded = w.Rows.Select((_, i) => landed.Contains(FlatIndexOf(work, w, i))).All(b => b);
            if (!allLanded)
            {
                // Unreachable by construction (every row's fate is landed, poison,
                // or an abort) — but a bookkeeping bug must NEVER masquerade as an
                // ack: treat it as transient and requeue (safety over silence).
                logger.LogError("ladder bookkeeping: message {DeviceId} has unlanded rows without a receipt — requeueing (safety over silence)", w.Entry.Envelope.DeviceId);
                buffer.RequeueAccepted([w.Entry]);
                continue;
            }

            await AckAsync(w.Entry);
            var first = w.Rows[0];
            await deviceStatus.MarkSeenAsync(w.Entry.Envelope.DeviceId, first.TenantId,
                w.Entry.Envelope.ReceivedAtUtc, first.TimeUtc, ct);
            logger.LogInformation("flush STORED (ladder) device={DeviceId} rows={Count} warnings={Warnings}",
                w.Entry.Envelope.DeviceId, w.Rows.Count,
                w.Warnings.Count == 0 ? "none" : string.Join(",", w.Warnings));
        }

        flushCounters.FlushCompleted(landed.Count);
        return FlushOutcome.Completed;
    }

    // ---- helpers ----------------------------------------------------------------

    private static int FlatIndexOf(List<PumpWork> work, PumpWork owner, int rowIndex)
    {
        var index = 0;
        foreach (var w in work)
        {
            if (ReferenceEquals(w, owner)) return index + rowIndex;
            index += w.Rows.Count;
        }
        return -1;
    }

    private static List<PendingDelivery> NotFinalized(IEnumerable<PumpWork> work, HashSet<PumpWork> receipted) =>
        work.Where(w => !receipted.Contains(w)).Select(w => w.Entry).ToList();

    private static FlushReadingDto ToDto(NormalizedReading r) =>
        new(r.TenantId, r.DeviceId, r.ChannelKey, r.TimeUtc, r.Value, r.Seq, r.ReceivedAtUtc);

    // ---- the overlapping ack window ---------------------------------------------

    /// <summary>
    /// LAUNCHES this flush's ack chunks through the sliding window (≤ 4 in
    /// flight) and RETURNS — the caller proceeds to the next flush while the
    /// PUBACKs fly. The slot wait takes NO token: the commit already landed,
    /// so the ack law wants the ATTEMPT — a cancellable wait here stranded
    /// committed messages un-launched, invisible to every drain (observed in
    /// the loop-overlap test). Shutdown safety comes from the bounded window
    /// and the drains (consumer stop + FinalFlush), not from abandoning.
    /// </summary>
    private async Task LaunchAckChunksAsync(List<PumpWork> work)
    {
        foreach (var chunk in work.Chunk(AckChunkSize))
        {
            await _ackWindow.WaitAsync();                    // bounded: frees as soon as a chunk completes
            var task = AckChunkCoreAsync(chunk);
            lock (_ackTrack) { _ackTasks.Add(task); }
            _ = task.ContinueWith(
                t => { _ackWindow.Release(); lock (_ackTrack) { _ackTasks.Remove(t); } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// ONE chunk of 64 PUBACKs — fires concurrently (the connection's own send
    /// lock serializes the actual writes; the broker mailbox never sees more
    /// than one chunk per connection at a moment). NEVER throws.
    /// </summary>
    private async Task AckChunkCoreAsync(IReadOnlyList<PumpWork> chunk)
    {
        try
        {
            await Task.WhenAll(chunk.Select(async w =>
            {
                try { await w.Entry.Ack(CancellationToken.None); }
                catch (Exception e)
                {
                    logger.LogWarning(e, "ack FAILED after a committed outcome — the broker will redeliver; replay is idempotent");
                }
            }));
        }
        finally
        {
            // THE ACK-RETIRE LAW: this chunk's messages all committed — the
            // contribution retires on EVERY completion, success or failure alike.
            buffer.Acked(chunk.Count);
        }
    }

    /// <summary>
    /// The shutdown/determinism seam: waits for every launched chunk to
    /// complete (the PUBACKs actually fly while the connections are still
    /// open), then returns. Honors the token BETWEEN snapshots; non-throwing —
    /// a bound expiry just leaves the remaining chunks to fail on disconnect
    /// and ride the redelivery (idempotent replay, never a loss).
    /// </summary>
    public async Task DrainAcksAsync(CancellationToken ct)
    {
        while (true)
        {
            Task[] snapshot;
            lock (_ackTrack) { snapshot = [.. _ackTasks]; }
            if (snapshot.Length == 0) return;
            try { await Task.WhenAll(snapshot).WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                logger.LogWarning("ack drain BOUND expired with {Count} chunk(s) still in flight — their PUBACKs fail on disconnect and ride the broker's redelivery (idempotent)",
                    snapshot.Length);
                return;
            }
        }
    }

    private async Task AckAsync(PendingDelivery entry)
    {
        MarkAccounted(entry);
        try
        {
            await entry.Ack(CancellationToken.None);
        }
        catch (Exception e)
        {
            // An ack failure AFTER a committed outcome: the broker redelivers
            // (in-session retry_interval / session resume), the replay is
            // idempotent (DO NOTHING / duplicate receipt row) — never a data
            // loss, never a pump crash.
            logger.LogWarning(e, "ack FAILED after a committed outcome — the broker will redeliver; replay is idempotent");
        }
        // THE ACK-RETIRE LAW (pressure diagnosis 2026-11-16): pending ≡ the
        // broker's inflight MIRROR, so the contribution retires on EVERY
        // committed outcome — a successful ack reopens the slot; a FAILED ack
        // rides the guaranteed redelivery, whose re-offer re-adds its own
        // pending. Retiring on failure too is what keeps the window alarm
        // honest after a connection kill (measured without it: 1,750 zombie
        // pending inflated the counter permanently).
        buffer.Acked(1);
    }

    private FlushOutcome RegisterAbort(string reason, Exception error)
    {
        var consecutive = flushCounters.TransientAbort();
        logger.LogWarning(error,
            "flush ABORTED (transient — zero acks, zero receipts, snapshot re-queued) reason={Reason} consecutive={Consecutive}",
            reason, consecutive);
        if (consecutive >= 3)
            logger.LogWarning(
                "flush STALL consecutive={Consecutive} — retrying indefinitely at the backoff cap (Option A: no maximum, no invented fate)",
                consecutive);
        if (consecutive >= 30)
            logger.LogError("flush STALL consecutive={Consecutive} (~{Minutes} min at the 10 s cap)", consecutive, consecutive * 10 / 60);
        if (consecutive >= 60)
            logger.LogCritical(
                "flush STALL consecutive={Consecutive} — the MQTT ingestion path is DOWN ~{Minutes} min; display channel = THIS log stream (external exposure: watchdog spec)",
                consecutive, consecutive * 10 / 60);
        return FlushOutcome.TransientAbort;
    }

    private sealed record ReceiptOutcome(ReceiptResult Outcome, Exception? Error);
    private enum ReceiptResult { Committed, Transient, Cancelled }

    private async Task<ReceiptOutcome> TryReceiptAsync(PendingDelivery entry, string code, string detail, CancellationToken ct)
    {
        try
        {
            await rawRejects.StoreAsync(new RawRejectDto(
                entry.Envelope.ReceivedAtUtc,      // time = received at (forensics, not a time series)
                entry.Envelope.ReceivedAtUtc,
                TenantId: null,                    // identity may precede/fail resolution — nullable by design (spec 06)
                DeviceId: entry.Envelope.DeviceId,
                code,
                entry.Envelope.Payload), ct);
            return new ReceiptOutcome(ReceiptResult.Committed, null);
        }
        catch (OperationCanceledException)
        {
            return new ReceiptOutcome(ReceiptResult.Cancelled, null);
        }
        catch (Exception e)
        {
            // R10: a receipt write that itself fails is TRANSIENT — no committed
            // receipt = no durable outcome = no ack (fact 13).
            return new ReceiptOutcome(ReceiptResult.Transient, e);
        }
    }

    private sealed class PumpWork(PendingDelivery entry, IReadOnlyList<NormalizedReading> rows, IReadOnlyList<string> warnings)
    {
        public PendingDelivery Entry { get; } = entry;
        public IReadOnlyList<NormalizedReading> Rows { get; } = rows;
        public IReadOnlyList<string> Warnings { get; } = warnings;
    }
}
