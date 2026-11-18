namespace Nabd.Ingestion;

using System.Collections.Concurrent;
using Nabd.Ingestion.Contracts;

/// <summary>
/// One buffered MQTT delivery awaiting its flush (P4 spec 01, Move A).
/// The raw envelope rides ALWAYS — its payload bytes are the receipt's
/// forensics input. The parsed batch rides only when stage 3 accepted the
/// shape (null = parse-rejected: the pump receipts it via raw_rejects and
/// acks it WITHOUT any resolution — its verdict was final at parse time).
/// Ack is the consumer's deferred PUBACK as a plain delegate: the pump
/// invokes it ONLY after the message's durable outcome commits (the ack
/// law), and the buffer never sees an MQTT type (the room wall stays clean).
/// </summary>
public sealed record PendingDelivery(
    IngestEnvelope Envelope,
    ReadingBatch? Parsed,
    Func<CancellationToken, Task> Ack);

/// <summary>
/// The flush buffer (P4 spec 01, Move A): the delivery handler's ONLY
/// destination — parse (CPU) happens here in Offer, then the delivery waits
/// for the pump. NO DB anywhere in this class: the handler thread pays
/// O(enqueue) and returns, which is what keeps the broker's window open
/// (R5/R7 — pending ≡ unacked, and an O(1) handler never blocks dispatch).
///
/// Two streams, two counters, one trigger:
///   _accepted          parsed-OK deliveries, awaiting resolve + validate + store
///   _parseRejects      DLQ_INVALID deliveries, awaiting receipt + ack
///   _pendingMessages   received-but-NOT-acked (the R7 window alarm reads this;
///                      lowered ONLY by Acked — drain and requeue move nothing)
///   _bufferedReadings  readings sitting in the queues — the SIZE trigger counts
///                      THESE (R9: the flush bound counts readings, not messages;
///                      one message may carry up to 32)
///   _sizeTrigger       completes when bufferedReadings crosses FlushSize — the
///                      pump's immediate wake (the interval tick is the other wake)
///
/// Re-buffer law (R10): a transient abort re-Enqueues at the TAIL — row order
/// carries no law (event time + seq ride per row; ON CONFLICT is order-free).
/// Content validation is NOT this class's job: an unknown channel parses fine
/// and rides the accepted stream — its E422 verdict happens at the flush
/// (per-message, whole — facts 6/7's world).
/// </summary>
public sealed class FlushBuffer
{
    private readonly ConcurrentQueue<PendingDelivery> _accepted = new();
    private readonly ConcurrentQueue<PendingDelivery> _parseRejects = new();
    private readonly int _flushSize;
    private long _pendingMessages;
    private long _bufferedReadings;
    private TaskCompletionSource _sizeTrigger = NewTrigger();
    private readonly ReconcileCounters _reconcile;
    public FlushBuffer(int flushSize, ReconcileCounters reconcile)
    {
        if (flushSize < 1)
            throw new ArgumentOutOfRangeException(nameof(flushSize), "FlushSize must be >= 1 (fail-shut).");
        _flushSize = flushSize;
        _reconcile = reconcile ?? throw new ArgumentNullException(nameof(reconcile));
    }

    /// <summary>
    /// Offer ONE delivery: parse (CPU only) + enqueue + counters + trigger.
    /// Returns nothing — the pump owns every outcome from here on.
    /// </summary>
    public void Offer(IngestEnvelope envelope, Func<CancellationToken, Task> ack)
    {
        ReadingBatch? parsed = null;
        if (PayloadV1Parser.TryParse(envelope.Payload, PayloadV1Parser.MaxReadingsPerMessage, out var batch, out _))
            parsed = batch;

        _reconcile.Delivered(parsed?.Readings.Count ?? 1);

        (parsed is null ? _parseRejects : _accepted).Enqueue(new PendingDelivery(envelope, parsed, ack));
        Interlocked.Increment(ref _pendingMessages);
        var readings = Interlocked.Add(ref _bufferedReadings, parsed?.Readings.Count ?? 0);
        if (readings >= _flushSize) _sizeTrigger.TrySetResult();
    }

    /// <summary>
    /// P4 spec 02 (the QUIET law's input): no accepted readings AND no
    /// parse-rejects sitting in the queues — the pump's own sleep condition,
    /// verbatim, one definition.
    /// </summary>
    public bool IsQuiet => BufferedReadings == 0 && ParseRejectsCount == 0;

    /// <summary>
    /// The pump's immediate wake: the task that completed when the size bound
    /// was crossed (a FRESH task each call — single caller: the pump).
    /// </summary>
    public Task TakeSizeTrigger()
    {
        var trigger = Interlocked.Exchange(ref _sizeTrigger, NewTrigger());
        return trigger.Task;
    }

    /// <summary>Received-but-not-acked messages — the R7 window alarm's input.</summary>
    public long PendingMessages => Interlocked.Read(ref _pendingMessages);

    /// <summary>Readings currently sitting in the queues — the size trigger's unit.</summary>
    public long BufferedReadings => Interlocked.Read(ref _bufferedReadings);

    public int AcceptedCount => _accepted.Count;
    public int ParseRejectsCount => _parseRejects.Count;

    public List<PendingDelivery> DrainAccepted() => Drain(_accepted);
    public List<PendingDelivery> DrainParseRejects() => Drain(_parseRejects);

    /// <summary>
    /// Transient abort (R10): the drained snapshot returns to the queue's
    /// tail — nothing acked, nothing receipted, nothing lost. Pending NEVER
    /// moves (draining is not acking); the readings count comes back.
    /// </summary>
    public void RequeueAccepted(IReadOnlyList<PendingDelivery> entries) => Requeue(entries, _accepted);

    /// <summary>Same as RequeueAccepted, for the parse-reject stream (receipt failure = transient).</summary>
    public void RequeueParseRejects(IReadOnlyList<PendingDelivery> entries) => Requeue(entries, _parseRejects);

    /// <summary>
    /// The pump reports n messages acked — the ONLY thing that lowers pending.
    /// This is the window law in one line: the broker reopens dispatch by
    /// exactly these n slots (R5/R7).
    /// </summary>
    public void Acked(int messages) => Interlocked.Add(ref _pendingMessages, -messages);

    private List<PendingDelivery> Drain(ConcurrentQueue<PendingDelivery> queue)
    {
        var drained = new List<PendingDelivery>();
        while (queue.TryDequeue(out var entry))
        {
            drained.Add(entry);
            if (entry.Parsed is not null)
                Interlocked.Add(ref _bufferedReadings, -entry.Parsed.Readings.Count);
        }
        return drained;
    }

    private void Requeue(IReadOnlyList<PendingDelivery> entries, ConcurrentQueue<PendingDelivery> queue)
    {
        long readings = 0;
        foreach (var entry in entries)
        {
            queue.Enqueue(entry);
            readings += entry.Parsed?.Readings.Count ?? 0;
        }
        var buffered = Interlocked.Add(ref _bufferedReadings, readings);
        if (buffered >= _flushSize) _sizeTrigger.TrySetResult();
    }

    private static TaskCompletionSource NewTrigger() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}