namespace Nabd.TelemetryStore.Contracts;

public sealed record ReadingBatchDto(Guid TenantId, Guid DeviceId, IReadOnlyList<ReadingDto> Readings);

/// <summary>ReceivedAtUtc rides per reading: the store's received_at column is NOT NULL (0003), and the pipeline's stage 5 stamps it on every normalized reading.</summary>
public sealed record ReadingDto(string ChannelKey, DateTimeOffset TimeUtc, double Value, long Seq, DateTimeOffset ReceivedAtUtc);

public sealed record HistoryQuery(Guid TenantId, Guid DeviceId, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

public sealed record ReadingValueDto(string ChannelKey, DateTimeOffset TimeUtc, double Value);

/// <summary>
/// The TelemetryStore room's inbound port (03 §3 "offers to others"):
/// deterministic writes (idempotent — 04 §6 unique key) and history reads for dashboards.
/// Implementation lives behind the room wall (Nabd.TelemetryStore impl).
/// </summary>
public interface IReadingsStore
{
    /// <summary>Idempotent store: INSERT ... ON CONFLICT DO NOTHING on (device_id, channel_key, time, seq) (FR-23); ack only after commit (NFR-04).</summary>
    Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default);

    Task<IReadOnlyList<ReadingValueDto>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default);
}

/// <summary>
/// The TelemetryStore room's device-presence port (plan/05 §9): marks are buffered
/// in memory and flushed as ONE bulk upsert per window — never one UPDATE per
/// reading. The flush CADENCE is hosted in ProcessingHost; the buffer and the
/// write belong to this room.
/// </summary>
public interface IDeviceStatusTracker
{
    /// <summary>Buffer a device's latest presence. O(1), in-memory; the oldest flush window pays the DB, nobody else.</summary>
    Task MarkSeenAsync(Guid deviceId, Guid tenantId, DateTimeOffset lastSeenUtc, DateTimeOffset lastEventTimeUtc, CancellationToken ct = default);
}
