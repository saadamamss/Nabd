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
    
    /// <summary>
    /// The flush path's store port (P4 spec 01, Move C): ONE multi-row INSERT
    /// per chunk across ALL devices and tenants of a flush (parallel unnest
    /// arrays — 0003's identity key is conflict-safe cross-tenant). Idempotent
    /// by the same law as StoreBatchAsync (DO NOTHING — retries and broker
    /// redeliveries are free). NEVER throws Npgsql types: driver failures
    /// surface as StoreFlushException (R10 classification inside);
    /// OperationCanceledException passes UNWRAPPED.
    /// </summary>
    Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default);
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

/// <summary>
/// One MQTT-path rejection as it lands in telemetry.raw_rejects (migration 0019,
/// P3 spec 06): the RAW bytes verbatim + the catalog reason — recovery first,
/// forensics always (plan/05 §7: nothing silently dropped). Identity may be
/// partial: tenant/device NULLABLE by design (a rejection can precede identity
/// resolution). NOT a hypertable — forensics, not a time series.
/// </summary>
public sealed record RawRejectDto(
    DateTimeOffset TimeUtc,
    DateTimeOffset ReceivedAtUtc,
    Guid? TenantId,
    Guid? DeviceId,
    string Reason,
    byte[] Payload);

/// <summary>
/// The TelemetryStore room's rejection-forensics port (P3 spec 06): append-only
/// INSERTs (0019 gives nabd_app no UPDATE/DELETE on raw forensics).
/// </summary>
public interface IRawRejectStore
{
    Task StoreAsync(RawRejectDto reject, CancellationToken ct = default);
}


/// <summary>
/// ONE reading as the flush carries it (P4 spec 01, Move C): tenant and device
/// ride PER ROW — a flush spans many devices and tenants, and the single
/// INSERT is cross-tenant by law (R4: tenant_id is a plain column — 0003/0018;
/// the identity key (device_id, channel_key, time, seq) is conflict-safe).
/// </summary>
public sealed record FlushReadingDto(
    Guid TenantId, Guid DeviceId, string ChannelKey,
    DateTimeOffset TimeUtc, double Value, long Seq, DateTimeOffset ReceivedAtUtc);

/// <summary>
/// The ONLY durable-write failure surface the flush path ever sees besides
/// OperationCanceledException (P4 spec 01, R10): the room wall law says
/// Ingestion NEVER references Npgsql — so the store classifies the driver's
/// exception ONCE, here, carrying the SQLSTATE and the R10 verdict: SQLSTATE
/// class 22 (data exceptions) + 23514 (check violation) are the ONLY
/// deterministic row-content failures (the poison ladder's world); everything
/// else — known or unknown — is transient (abort-all, re-buffer, zero acks).
/// An EMPTY SqlState = a driver/connection failure (no SQLSTATE exists) —
/// transient by definition. OperationCanceledException is NEVER wrapped
/// (cooperative shutdown is not a verdict).
/// </summary>
public sealed class StoreFlushException(string sqlState, bool isDeterministic, Exception innerException)
    : Exception($"readings flush failed (SQLSTATE '{sqlState}', deterministic={isDeterministic})", innerException)
{
    public string SqlState { get; } = sqlState;
    public bool IsDeterministic { get; } = isDeterministic;
}

/// <summary>
/// P4 spec 04: the COMMITTED fact — published by the flush pump only after its
/// INSERT returned. The threshold engine consumes exactly this type, so a
/// reading that is not durable can never raise an alert (R-law). It also rides
/// inside CrossingDetected/ConditionRestored so Alerting gets the observed value.
/// </summary>
public sealed record CommittedReading(
    Guid DeviceId, string ChannelKey, double Value, DateTimeOffset TimeUtc);

public sealed record CommittedBatch(
    Guid TenantId, Guid DeviceId, IReadOnlyList<CommittedReading> Readings);
