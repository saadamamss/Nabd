namespace Nabd.Ingestion.Contracts;

/// <summary>
/// One reading as the pipeline hands it to the store (stage 5's output, stage 6's
/// input): identity and clock fully resolved — tenant filled from the device,
/// event time sanitized per the clock rules (plan/05 §8), received_at stamped by
/// the server on EVERY row (device clocks are never trusted alone).
/// This is the shape spec 04's batch insert persists.
/// </summary>
public sealed record NormalizedReading(
    Guid TenantId,
    Guid DeviceId,
    string ChannelKey,
    /// <summary>The event time AFTER sanitization (clamp applied where the rules say so).</summary>
    DateTimeOffset TimeUtc,
    double Value,
    /// <summary>Device sequence — NOT enforced monotonic (owner ruling 2026-11-13); dedup lives in the identity key.</summary>
    long Seq,
    /// <summary>Server receive time — set for every row, never taken from the device.</summary>
    DateTimeOffset ReceivedAtUtc);
