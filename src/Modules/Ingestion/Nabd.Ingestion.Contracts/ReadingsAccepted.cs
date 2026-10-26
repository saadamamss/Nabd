using Nabd.SharedKernel;
namespace Nabd.Ingestion.Contracts;

/// <summary>
/// The one event that unlocks the whole pipeline (03 §6.1): a validated batch of readings
/// is accepted and may be stored. Emitted by the Ingestion room; consumed by the
/// TelemetryStore room (impl side) and the Rules room.
/// Payload v1 (05 §4): per-reading channel/time/value + device seq.
/// </summary>
public sealed record Reading(
    string ChannelKey,
    DateTimeOffset TimeUtc,
    double Value,
    /// <summary>Device sequence; 0 when the device/gateway sends none (FR-23: NOT NULL DEFAULT 0).</summary>
    long Seq);

public sealed record ReadingsAcceptedBatch(
    Guid TenantId,
    Guid DeviceId,
    IReadOnlyList<Reading> Readings) : IntegrationEvent;
