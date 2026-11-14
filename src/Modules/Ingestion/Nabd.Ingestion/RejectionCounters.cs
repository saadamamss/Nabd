namespace Nabd.Ingestion;

using System.Collections.Concurrent;

/// <summary>
/// RED STUB (P3 spec 06 scaffolding): the per-reason rejection counter — an
/// in-memory ConcurrentDictionary keyed by the catalog code. The real one:
/// Increment(code) is one Interlocked.Add, Snapshot() copies for the
/// health/ready JSON. Per-process honest: each host counts its own path's
/// rejections; the MQTT path's authoritative count rides raw_rejects (the DB),
/// which no process can lose. Prometheus arrives later (plan/09 §8).
/// </summary>
public sealed class RejectionCounters
{
    public void Increment(string code) =>
        throw new NotImplementedException("P3 spec 06: owner implements from study/implementation-code.md");

    public IReadOnlyDictionary<string, long> Snapshot() =>
        throw new NotImplementedException("P3 spec 06: owner implements from study/implementation-code.md");
}
