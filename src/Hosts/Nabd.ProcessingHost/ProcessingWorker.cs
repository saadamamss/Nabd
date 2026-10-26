namespace Nabd.ProcessingHost;

/// <summary>
/// Processing Host worker (03 §5): eventually hosts the MQTT consumer, the
/// queue consumer, the batch writer, the rules engine and the offline watchdog (06 §4).
/// P0: a heartbeat worker only — proves the host boots and restarts independently of the API.
/// Room wiring (composition root) lands here as rooms get implemented, per 03 §4 rule 6.
/// </summary>
public sealed class ProcessingWorker(ILogger<ProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ProcessingHost up. Rooms to be wired in later phases: " +
            "Ingestion(MQTT consumer, P3) → TelemetryStore(batch writer, P3) → Rules(engine, watchdog every 30s, P4) → " +
            "Alerting(lifecycle, P4) → Notifications(email/Telegram, P4). One slice per day per 12 §4.");

        // P0 heartbeat: a placeholder tick so logs prove liveness in docker health checks (08 §6).
        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("ProcessingHost heartbeat");
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
