namespace Nabd.ProcessingHost;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Nabd.Assets;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// Processing Host worker (03 §5): the composition root's long-running loop.
/// P3 spec 04: the REAL batch store is wired — LoggingReadingsStore is retired.
/// The placeholder was replaced at THIS wiring line only; the pipeline, sink
/// and tests never changed (the seam held).
/// </summary>
public sealed class ProcessingWorker(
    IConfiguration configuration,
    DeviceStatusTracker tracker,
    TelemetryStoreDb telemetryDb,
    ILogger<ProcessingWorker> logger,
    ILoggerFactory loggerFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ProcessingHost up — P3 spec 04: the real batch store is wired.");

        // Fail-shut chain: ConsumerAccess throws at boot without its env (NFR-08).
        var access = new ConsumerAccess();

        // The Assets room's edges, wired by the host (the wall's legal crossing).
        var edges = new IngestionEdges(new AssetsDb());

        var pipeline = new ValidationPipeline(edges, edges);

        // The tracker + DB door come from DI (Program.cs) — ONE shared instance,
        // the same buffer the flush worker drains on its cadence.
        var batchSize = configuration.GetValue("Ingestion:BatchSize", 500);
        IReadingsStore store = new PostgresReadingsStore(telemetryDb, batchSize);

        // The TelemetryStore room's ports — the sink's four + the DI-shared tracker.
        var rawRejects = new RawRejectsStore(telemetryDb);   // 0019: rejections get a destination (spec 06)
        var counters = new RejectionCounters();              // per-process; the DB copy rides raw_rejects
        IIngestionSink sink = new PipelineIngestionSink(
            pipeline, store, tracker, rawRejects, counters,
            loggerFactory.CreateLogger<PipelineIngestionSink>());

        var consumer = new EmqxIngestConsumer(
            access, sink, loggerFactory.CreateLogger("Nabd.Ingestion.Consumer"));

        await consumer.RunAsync(stoppingToken);
    }
}