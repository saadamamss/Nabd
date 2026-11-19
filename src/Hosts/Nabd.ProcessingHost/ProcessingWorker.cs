namespace Nabd.ProcessingHost;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Nabd.Assets;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;
using Nabd.SharedKernel;

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
    IEventBus bus,
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

        var flush = FlushOptions.From(
            configuration.GetValue("Ingestion:FlushSize", 200),
            configuration.GetValue("Ingestion:FlushIntervalMs", 100),
            configuration.GetValue("Ingestion:ReceiveMaximum", 200),
            configuration.GetValue("Ingestion:ConsumerShards", 1));

        // P4 spec 02: ONE reconcile ledger shared by the buffer (delivered),
        // the rejection counters (rejected) and the flush counters (stored) —
        // the watchdog reads it on its own cadence.
        var reconcile = new ReconcileCounters();

        logger.LogInformation(
            "ProcessingHost up — P4 spec 01 (sharded) + spec 02 (reconcile guard): {Shards} connection(s) × RM {ReceiveMaximum} (aggregate {Aggregate}), flush trigger {FlushSize}, reconcile tick 60 s.",
            flush.ConsumerShards, flush.ReceiveMaximum, flush.AggregateWindow, flush.FlushSize);

        var buffer = new FlushBuffer(flush.FlushSize, reconcile);

        var rawRejects = new RawRejectsStore(telemetryDb);
        var counters = new RejectionCounters(reconcile);
        var flushCounters = new FlushCounters(reconcile);
        var pump = new FlushPump(
            buffer, flush, edges, pipeline, store, rawRejects, tracker, counters, flushCounters,
            reconcile,
            bus,
            loggerFactory.CreateLogger<FlushPump>());

        var consumer = new EmqxIngestConsumer(
            access, buffer, flush, pump, loggerFactory.CreateLogger("Nabd.Ingestion.Consumer"));

        // P4 spec 02: the watchdog's own loop — 60 s ticks, read-only, its own
        // task. A slow watchdog never touches the ingestion critical path.
        var watchdog = new ReconcileWatchdog(reconcile, loggerFactory.CreateLogger<ReconcileWatchdog>());
        var watchdogTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
                catch (OperationCanceledException) { break; }
                watchdog.Tick(buffer.IsQuiet, pump.FlushInFlight);
            }
        }, stoppingToken);

        var pumpTask = pump.RunAsync(stoppingToken);
        try
        {
            await consumer.RunAsync(stoppingToken);
        }
        finally
        {
            await pump.FinalFlushAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        await pumpTask;
        await watchdogTask;
    }
}
