namespace Nabd.TelemetryStore.Contracts;

public sealed record ReadingBatchDto(Guid TenantId, Guid DeviceId, IReadOnlyList<ReadingDto> Readings);

public sealed record ReadingDto(string ChannelKey, DateTimeOffset TimeUtc, double Value, long Seq);

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
