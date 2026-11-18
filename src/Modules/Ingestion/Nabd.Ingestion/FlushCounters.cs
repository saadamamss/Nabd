namespace Nabd.Ingestion;

/// <summary>
/// The flush path's own counters (P4 spec 01 observability): flushes, rows
/// stored, ladder descents, poison receipts, transient aborts, and the
/// CONSECUTIVE abort counter that drives Option A's escalation ladder and the
/// backoff. All Interlocked — the pump is single-tasked but the counters may
/// be read from logs/health at any moment. ConsecutiveAborts RESETS on any
/// successful flush (Option A's law: the stall measures the CURRENT outage).
///
/// P4 spec 02: an optional reconcile ledger rides along — every committed
/// row is ALSO accounted there (the deficit's "stored" leg), so no call site
/// can forget it.
/// </summary>
public sealed class FlushCounters
{
    private readonly ReconcileCounters? _reconcile;
    private long _flushes;
    private long _rowsStored;
    private long _ladderDescents;
    private long _poisonReceipts;
    private long _transientAborts;
    private long _consecutiveAborts;

    public FlushCounters(ReconcileCounters? reconcile = null) => _reconcile = reconcile;

    /// <summary>A flush attempt completed — every drained message reached a durable outcome. Resets the stall clock.</summary>
    public void FlushCompleted(int rowsLanded)
    {
        Interlocked.Increment(ref _flushes);
        if (rowsLanded > 0)
        {
            Interlocked.Add(ref _rowsStored, rowsLanded);
            _reconcile?.Stored(rowsLanded);   // the deficit's "stored" leg
        }
        Interlocked.Exchange(ref _consecutiveAborts, 0);
    }

    /// <summary>The poison ladder descended one level (per-device or row-by-row).</summary>
    public void LadderDescent() => Interlocked.Increment(ref _ladderDescents);

    /// <summary>A poison row got its DLQ_STORE_POISON receipt (stored-or-receipted, never looped).</summary>
    public void PoisonReceipt() => Interlocked.Increment(ref _poisonReceipts);

    /// <summary>A transient abort — returns the NEW consecutive count (the escalation ladder's input).</summary>
    public long TransientAbort()
    {
        Interlocked.Increment(ref _transientAborts);
        return Interlocked.Increment(ref _consecutiveAborts);
    }

    public long Flushes => Interlocked.Read(ref _flushes);
    public long RowsStored => Interlocked.Read(ref _rowsStored);
    public long LadderDescents => Interlocked.Read(ref _ladderDescents);
    public long PoisonReceipts => Interlocked.Read(ref _poisonReceipts);
    public long TransientAborts => Interlocked.Read(ref _transientAborts);
    public long ConsecutiveAborts => Interlocked.Read(ref _consecutiveAborts);
}
