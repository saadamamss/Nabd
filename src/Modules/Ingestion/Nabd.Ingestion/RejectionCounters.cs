namespace Nabd.Ingestion;

using System.Collections.Concurrent;

/// <summary>
/// The per-reason rejection counter (P3 spec 06, plan/09 §8's honest floor):
/// one Interlocked.Add per rejection, a Snapshot() copy for the health/ready
/// JSON. Per-process by nature — the MQTT path's authoritative count rides
/// telemetry.raw_rejects (0019), which no process can lose. Prometheus later.
/// </summary>
public sealed class RejectionCounters
{
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public void Increment(string code) =>
        _counts.AddOrUpdate(code, 1, (_, n) => n + 1);

    public IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(_counts);
}