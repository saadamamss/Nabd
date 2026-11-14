namespace Nabd.ProcessingHost;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nabd.Assets;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// Processing Host worker (03 §5): the composition root's long-running loop.
/// P3 spec 03: consumer → sink (pipeline + store port) — the validation
/// pipeline is LIVE on the MQTT path. The store port is the LoggingReadingsStore
/// placeholder until spec 04's batch writer lands at THIS wiring line only.
/// </summary>
public sealed class ProcessingWorker(
    ILogger<ProcessingWorker> logger,
    ILoggerFactory loggerFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ProcessingHost up — P3 spec 03: wiring the validation pipeline.");

        // Fail-shut chain: ConsumerAccess throws at boot without its env (NFR-08).
        var access = new ConsumerAccess();

        // The Assets room's edges, wired by the host (the wall's legal crossing).
        var edges = new IngestionEdges(new AssetsDb());

        var pipeline = new ValidationPipeline(edges, edges);
        IReadingsStore store = new LoggingReadingsStore(
            loggerFactory.CreateLogger<LoggingReadingsStore>());   // spec 04 replaces HERE only
        IIngestionSink sink = new PipelineIngestionSink(
            pipeline, store, loggerFactory.CreateLogger<PipelineIngestionSink>());

        var consumer = new EmqxIngestConsumer(
            access, sink, loggerFactory.CreateLogger("Nabd.Ingestion.Consumer"));

        await consumer.RunAsync(stoppingToken);
    }
}