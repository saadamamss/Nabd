namespace Nabd.Ingestion;

using Microsoft.Extensions.Logging;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// RED STUB (P3 spec 04 re-hang): the MQTT path's sink, now with TWO store-side
/// ports. The owner's real implementation (study/implementation-code.md) keeps
/// spec 03's law — pipeline verdict first, store port touched ONLY on accept —
/// and adds spec 04's:
///   • the store's received_at rides per reading (0003 column is NOT NULL);
///   • every ACCEPTED batch marks the device seen (IDeviceStatusTracker buffer);
///   • a store FAILURE propagates out — never swallowed — so the consumer's
///     catch leaves the message un-acked and the broker redelivers (NFR-04's
///     ack-after-commit: crash between commit and ack = idempotent re-insert).
/// </summary>
public sealed class PipelineIngestionSink(
    ValidationPipeline pipeline,
    IReadingsStore store,
    IDeviceStatusTracker deviceStatus,
    ILogger<PipelineIngestionSink> logger) : IIngestionSink
{
    public Task AcceptAsync(IngestEnvelope envelope, CancellationToken ct = default)
    {
        _ = pipeline; _ = store; _ = deviceStatus; _ = logger;
        throw new NotImplementedException("P3 spec 04: owner implements from study/implementation-code.md");
    }
}
