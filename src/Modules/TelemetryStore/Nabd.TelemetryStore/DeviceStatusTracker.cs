namespace Nabd.TelemetryStore;

using System.Collections.Concurrent;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// Device presence, plan/05 §9: marks buffer in memory (latest mark wins per
/// device) and ONE window edge pays the DB — a single bulk UPSERT, never one
/// UPDATE per reading. The flush CADENCE is hosted in ProcessingHost
/// (DeviceStatusFlushWorker); the buffer and the write live HERE, in the room.
/// </summary>
public sealed class DeviceStatusTracker(TelemetryStoreDb db) : IDeviceStatusTracker
{
    private readonly ConcurrentDictionary<Guid, (Guid Tenant, DateTimeOffset Seen, DateTimeOffset Event)> _buffer = new();

    // P4 spec 07 (plan/04 §6): the process's permanent memory of last words. The
    // flush clears _buffer only — this map is what keeps the flush cadence out of
    // the detection budget. Bounded by the fleet, not by the stream.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSeen = new();

    /// <summary>How many window edges have flushed (the "two windows → two flushes" fact reads this).</summary>
    public int FlushCount { get; private set; }

    public Task MarkSeenAsync(Guid deviceId, Guid tenantId, DateTimeOffset lastSeenUtc, DateTimeOffset lastEventTimeUtc, CancellationToken ct = default)
    {
        // O(1), lock-free. An older mark never overwrites a newer one in the
        // buffer — and the SQL's GREATEST() guards the DB side too (fact 7).
        _buffer.AddOrUpdate(deviceId,
            _ => (tenantId, lastSeenUtc, lastEventTimeUtc),
            (_, prev) => lastSeenUtc > prev.Seen ? (tenantId, lastSeenUtc, lastEventTimeUtc) : prev);

        _lastSeen.AddOrUpdate(deviceId,
            lastSeenUtc,
            (_, prev) => lastSeenUtc > prev ? lastSeenUtc : prev);
        return Task.CompletedTask;
    }

    /// <summary>P4 spec 07: the watchdog's in-memory truth between flushes. FlushNowAsync
    /// clears the WRITE buffer only; this map is the process's memory of last words.</summary>
    public DateTimeOffset? LastSeen(Guid deviceId) =>
        _lastSeen.TryGetValue(deviceId, out var seen) ? seen : null;

    /// <summary>One window edge: pay the DB once for everything buffered, clear, count.</summary>
    public async Task FlushNowAsync(CancellationToken ct = default)
    {
        FlushCount++;
        if (_buffer.IsEmpty)
        {
            return;
        }

        var marks = _buffer.ToArray();
        _buffer.Clear();

        // Honest bad-day note: clear-before-write means a crash HERE loses this
        // window's marks. device_status is operational presence, not readings —
        // the next window re-marks every live device anyway. Record, don't hide.
        await using var conn = await db.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO telemetry.device_status (device_id, tenant_id, last_seen_at, last_event_time)
                SELECT * FROM unnest(@d::uuid[], @t::uuid[], @ls::timestamptz[], @le::timestamptz[])
                ON CONFLICT (device_id) DO UPDATE SET
                    tenant_id = EXCLUDED.tenant_id,
                    last_seen_at = GREATEST(telemetry.device_status.last_seen_at, EXCLUDED.last_seen_at),
                    last_event_time = GREATEST(telemetry.device_status.last_event_time, EXCLUDED.last_event_time)
                """, conn);

            cmd.Parameters.AddWithValue("d", marks.Select(m => m.Key).ToArray());
            cmd.Parameters.AddWithValue("t", marks.Select(m => m.Value.Tenant).ToArray());
            cmd.Parameters.AddWithValue("ls", marks.Select(m => m.Value.Seen).ToArray());
            cmd.Parameters.AddWithValue("le", marks.Select(m => m.Value.Event).ToArray());

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
}