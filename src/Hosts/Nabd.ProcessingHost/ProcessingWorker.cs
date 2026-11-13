namespace Nabd.ProcessingHost;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;

/// <summary>
/// Processing Host worker (03 §5): the composition root's long-running loop.
/// P3 spec 02: wires the MQTT consumer — door identity (ConsumerAccess, fail-shut
/// at construction) → sink (LoggingIngestionSink until spec 03/04's pipeline) →
/// RunAsync, which lives until the host stops. Later phases wire more rooms here
/// (batch writer, rules engine, watchdog) — one slice at a time, per 12 §4.
/// </summary>
public sealed class ProcessingWorker(
    ILogger<ProcessingWorker> logger,
    ILoggerFactory loggerFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ProcessingHost up — P3 spec 02: wiring the MQTT consumer.");

        // Fail-shut at boot: a missing env var throws HERE, at process start —
        // the consumer never half-boots without its identity (NFR-08).
        var access = new ConsumerAccess();
        IIngestionSink sink = new LoggingIngestionSink(
            loggerFactory.CreateLogger<LoggingIngestionSink>());
        var consumer = new EmqxIngestConsumer(
            access, sink, loggerFactory.CreateLogger("Nabd.Ingestion.Consumer"));

        await consumer.RunAsync(stoppingToken);
    }
}