namespace Nabd.Ingestion;

/// <summary>
/// The reconciliation ledger (P4 spec 02): the running deficit between what
/// the pipeline ACCEPTED for a durable outcome and what actually reached one
/// (stored / rejected-with-evidence / handed back to the broker). The
/// watchdog reads it every tick; the alarm law turns an unexplained deficit
/// on a QUIET pipeline into an error log with the EXACT delta.
///
/// THE UNIT LAW: readings for parsed messages, ONE unit per unparseable
/// payload (its DLQ_INVALID receipt is per-payload). Delivered and accounted
/// always use the same unit per message — the equation stays exact.
/// All Interlocked: written from the consumer/pump paths, read by the
/// watchdog and health snapshots at any moment. Per-process by nature.
/// </summary>
public sealed class ReconcileCounters
{
    private long _delivered;
    private long _stored;
    private long _rejected;
    private long _handedBack;
    private long _alarms;
    private long _lastAlarmDelta;

    public void Delivered(int readings) => Add(ref _delivered, readings);
    public void Stored(int rows) => Add(ref _stored, rows);
    public void Rejected(int readings) => Add(ref _rejected, readings);
    public void HandedBack(int units) => Add(ref _handedBack, units);

    private static void Add(ref long field, int n)
    {
        if (n < 0)
            throw new ArgumentOutOfRangeException(nameof(n), n,
                "negative counts are a bug, not a measurement (fail-shut)");
        if (n > 0) Interlocked.Add(ref field, n);
    }

    /// <summary>THE EQUATION: accepted-but-unaccounted readings right now.</summary>
    public long Deficit =>
        Interlocked.Read(ref _delivered) - Interlocked.Read(ref _stored)
        - Interlocked.Read(ref _rejected) - Interlocked.Read(ref _handedBack);

    /// <summary>The watchdog's alarm: the delta is pinned BEFORE the count rises.</summary>
    public void Alarm(long delta)
    {
        Interlocked.Exchange(ref _lastAlarmDelta, delta);
        Interlocked.Increment(ref _alarms);
    }

    public long Alarms => Interlocked.Read(ref _alarms);
    public long LastAlarmDelta => Interlocked.Read(ref _lastAlarmDelta);

    public ReconcileSnapshot Snapshot() => new(
        Interlocked.Read(ref _delivered),
        Interlocked.Read(ref _stored),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _handedBack),
        Deficit,
        Interlocked.Read(ref _alarms),
        Interlocked.Read(ref _lastAlarmDelta));
}

/// <summary>One frozen read of the ledger — the health/observability arm.</summary>
public sealed record ReconcileSnapshot(
    long Delivered, long Stored, long Rejected, long HandedBack,
    long Deficit, long Alarms, long LastAlarmDelta);