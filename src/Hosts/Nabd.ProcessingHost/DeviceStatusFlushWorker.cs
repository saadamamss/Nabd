namespace Nabd.ProcessingHost;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nabd.TelemetryStore;

/// <summary>
/// Hosted flush cadence (plan/05 §9, spec 04 design point 4): the host owns
/// WHEN (one window edge every Ingestion:FlushSeconds, default 20); the
/// TelemetryStore room owns HOW (DeviceStatusTracker's bulk upsert). One tick
/// = at most one DB round-trip, whatever the message rate is.
/// </summary>
public sealed class DeviceStatusFlushWorker(
    DeviceStatusTracker tracker,
    IConfiguration configuration,
    ILogger<DeviceStatusFlushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Ingestion:FlushSeconds", 20);
        logger.LogInformation("DeviceStatus flush cadence: every {Seconds}s", seconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await tracker.FlushNowAsync(stoppingToken);
        }
    }
}