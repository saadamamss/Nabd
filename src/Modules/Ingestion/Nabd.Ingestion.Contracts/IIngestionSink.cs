namespace Nabd.Ingestion.Contracts;

/// <summary>
/// The consumer's only downstream dependency (P3 spec 02): the pipeline entry
/// a delivery is handed to. The MQTT consumer's durability contract lives HERE:
/// the consumer acknowledges the broker only AFTER the sink returns normally —
/// a sink exception means NO ack, the broker redelivers, and nothing is lost
/// (NFR-04's ack-after-durability, at the MQTT hop).
///
/// P3 spec 02 ships a LoggingIngestionSink (accepts and logs — the pipeline
/// internals are spec 03/04's slices); the interface is the permanent seam,
/// so the real pipeline replaces the stub without touching the consumer.
/// </summary>
public interface IIngestionSink
{
    Task AcceptAsync(IngestEnvelope envelope, CancellationToken ct = default);
}
