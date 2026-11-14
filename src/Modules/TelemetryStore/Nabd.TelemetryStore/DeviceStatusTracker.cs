namespace Nabd.TelemetryStore;

using System.Collections.Concurrent;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// RED STUB (P3 spec 04 scaffolding): buffers device presence in memory and
/// flushes it as ONE bulk upsert per window (plan/05 §9 — never one UPDATE per
/// reading). The real one: a ConcurrentDictionary keyed by device (latest mark
/// wins), FlushNowAsync pays the DB once — INSERT … ON CONFLICT (device_id)
/// DO UPDATE with GREATEST() guards so an out-of-order mark never regresses
/// last_seen_at — then clears the buffer and bumps FlushCount.
/// </summary>
public sealed class DeviceStatusTracker(TelemetryStoreDb db) : IDeviceStatusTracker
{
    /// <summary>How many window edges have flushed (the "two windows → two flushes" fact reads this).</summary>
    public int FlushCount { get; private set; }

    public Task MarkSeenAsync(Guid deviceId, Guid tenantId, DateTimeOffset lastSeenUtc, DateTimeOffset lastEventTimeUtc, CancellationToken ct = default)
    {
        _ = db;
        throw new NotImplementedException("P3 spec 04: owner implements from study/implementation-code.md");
    }

    /// <summary>One window edge: pay the DB once for everything buffered, clear, count. Hosted worker calls this on its cadence.</summary>
    public Task FlushNowAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException("P3 spec 04: owner implements from study/implementation-code.md");
    }
}
