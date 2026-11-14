namespace Nabd.TelemetryStore;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The flush cadence worker, P3 spec 05: MOVED from ProcessingHost into the
/// room — the HTTP door (ApiHost) now marks device presence too (plan/05 §9
/// is transport-blind), so BOTH hosts need the same cadence, and hosts may
/// not reference each other. The room owns the buffer, the write AND now the
/// cadence mechanics; each host just registers it. One tick = at most one DB
/// round-trip, whatever the message rate is (Ingestion:FlushSeconds, default 20).
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
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await tracker.FlushNowAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown — the timer's normal death, not an error
        }
    }
}
