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
/// P4 spec 01: the batching lever is wired — the O(1) consumer feeds the
/// FlushBuffer, the FlushPump owns every durable outcome (ONE resolution per
/// flush, one multi-row INSERT per flush, the ack law, the R6/R10 ladder,
/// Option A escalation), and the R11 shutdown order is orchestrated here
/// (amended 2026-11-17, ack overlap): consumer stops (unsubscribe → drain
/// in-flight PUBACK chunks → disconnect) THEN one bounded final flush, which
/// drains any ack still in flight again.
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
        // Fail-shut chain: ConsumerAccess throws at boot without its env (NFR-08).
        var access = new ConsumerAccess();

        // The Assets room's edges, wired by the host (the wall's legal crossing).
        var edges = new IngestionEdges(new AssetsDb());

        var pipeline = new ValidationPipeline(edges, edges);

        var batchSize = configuration.GetValue("Ingestion:BatchSize", 500);
        IReadingsStore store = new PostgresReadingsStore(telemetryDb, batchSize);

        // R1's flush bounds — fail-shut (FlushOptions.From throws at boot on
        // nonsense). AMENDED (sharding ruling 2026-11-16): Receive Maximum is
        // PER CONNECTION (its own knob, default 200) and ConsumerShards sets
        // how many MQTT connections share the subscription — the AGGREGATE
        // window is Shards × ReceiveMaximum, and FlushSize is only the flush
        // trigger's readings bound.
        var flush = FlushOptions.From(
            configuration.GetValue("Ingestion:FlushSize", 200),
            configuration.GetValue("Ingestion:FlushIntervalMs", 100),
            configuration.GetValue("Ingestion:ReceiveMaximum", 200),
            configuration.GetValue("Ingestion:ConsumerShards", 1));

        logger.LogInformation("ProcessingHost up — P4 spec 01 (sharded): the batching lever is wired — {Shards} consumer connection(s) × Receive Maximum {ReceiveMaximum} (aggregate window {Aggregate}), flush trigger {FlushSize} readings.",
            flush.ConsumerShards, flush.ReceiveMaximum, flush.AggregateWindow, flush.FlushSize);


        var buffer = new FlushBuffer(flush.FlushSize);

        // The TelemetryStore room's ports — the pump's four + the DI-shared tracker.
        var rawRejects = new RawRejectsStore(telemetryDb);   // 0019: rejections get a destination (spec 06)
        var counters = new RejectionCounters();              // per-process; the DB copy rides raw_rejects
        var flushCounters = new FlushCounters();             // the flush path's own observability (Option A's input)
        var pump = new FlushPump(
            buffer, flush, edges, pipeline, store, rawRejects, tracker, counters, flushCounters,
            loggerFactory.CreateLogger<FlushPump>());

        // The drain-before-disconnect seam (ack-overlap ruling 2026-11-17): the
        // consumer's graceful stop unsubscribes, DRAINS the pump's in-flight
        // PUBACK chunks while the connections are still open, then disconnects.
        var consumer = new EmqxIngestConsumer(
            access, buffer, flush, pump, loggerFactory.CreateLogger("Nabd.Ingestion.Consumer"));

        var pumpTask = pump.RunAsync(stoppingToken);
        try
        {
            // Returns after the graceful disconnect (R11: inflow stopped FIRST).
            await consumer.RunAsync(stoppingToken);
        }
        finally
        {
            // R11: ONE bounded final flush AFTER the inflow stopped — success
            // acks everything clean; failure/cancellation → zero acks → EMQX
            // re-queues them (CleanStart=false + 1 h session expiry).
            await pump.FinalFlushAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        await pumpTask;
    }
}