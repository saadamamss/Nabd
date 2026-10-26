namespace Nabd.Ingestion;

/// <summary>
/// Ingestion module (03 §3): MQTT consumer + HTTP ingest, validation pipeline (05 §6),
/// error catalog, ack-after-commit (NFR-04).
/// P0: empty on purpose. P3 fills it. Also owns the ADR-005 spike decision (keep RabbitMQ or not).
/// Listens: DeviceTokenRevoked (Assets). Emits: ReadingsAccepted.
/// </summary>
public static class IngestionModule
{
    public const string RoomName = "Ingestion";
}
