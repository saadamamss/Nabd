namespace Nabd.Ingestion.Contracts;

/// <summary>
/// One authenticated MQTT delivery, handed from the consumer loop (transport)
/// to the ingestion sink (pipeline) — the seam that makes the MQTT path
/// testable without a broker (03 §4: rooms behind contracts).
///
/// The consumer fills it from the broker's delivery; the sink knows nothing
/// about MQTT. Identity is pre-resolved: the broker's auth door already
/// admitted this device, so DeviceId comes from the topic's 4th segment,
/// not from any credential re-check (the doors own identity; the pipeline
/// owns validation — plan/05 §5's division of labor).
/// </summary>
public sealed record IngestEnvelope(
    Guid DeviceId,
    /// <summary>The exact topic the message arrived on (for diagnostics and rejects).</summary>
    string Topic,
    /// <summary>The raw payload bytes — validation/parsing happens downstream (spec 03).</summary>
    byte[] Payload,
    /// <summary>When the broker delivered it to the consumer (the MQTT path's received-at stamp).</summary>
    DateTimeOffset ReceivedAtUtc);
