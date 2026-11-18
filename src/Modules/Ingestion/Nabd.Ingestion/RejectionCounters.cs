namespace Nabd.Ingestion;

using System.Collections.Concurrent;

/// <summary>
/// The per-reason rejection counter (P3 spec 06, plan/09 §8's honest floor):
/// one Interlocked.Add per rejection, a Snapshot() copy for the health/ready
/// JSON. Per-process by nature — the MQTT path's authoritative count rides
/// telemetry.raw_rejects (0019), which no process can lose. Prometheus later.
///
/// P4 spec 02: an optional reconcile ledger rides along — every rejection is
/// ALSO accounted there in the UNIT LAW's unit (readings for parsed
/// messages, 1 per unparseable payload), keeping the deficit equation exact.
/// </summary>
public sealed class RejectionCounters
{
    private readonly ConcurrentDictionary<string, long> _counts = new();
    private readonly ReconcileCounters? _reconcile;

    public RejectionCounters(ReconcileCounters? reconcile = null) => _reconcile = reconcile;

    /// <summary>
    /// One rejection event of `code`, accounting `readings` units on the
    /// deficit ledger (the caller passes the message's reading count for
    /// parsed messages; per-payload/per-row reasons keep the 1).
    /// </summary>
    public void Increment(string code, int readings = 1)
    {
        _counts.AddOrUpdate(code, 1, (_, n) => n + 1);
        _reconcile?.Rejected(readings);
    }

    public IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(_counts);
}