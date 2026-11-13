namespace Nabd.Ingestion;

using Microsoft.Extensions.Logging;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// RED STUB (P3 spec 03 scaffolding): the MQTT path's sink — the owner's real
/// implementation replaces this from study/implementation-code.md. The real one
/// encodes the store-ordering LAW: pipeline verdict first; the store port is
/// invoked ONLY on an accepted result — a rejection returns without ever
/// touching the store (nothing malformed can reach an INSERT, NFR-04's gate).
/// </summary>
public sealed class PipelineIngestionSink(
    ValidationPipeline pipeline,
    IReadingsStore store,
    ILogger<PipelineIngestionSink> logger) : IIngestionSink
{
    public Task AcceptAsync(IngestEnvelope envelope, CancellationToken ct = default)
    {
        _ = pipeline;
        _ = store;
        _ = logger;
        return Task.CompletedTask;
    }
}
