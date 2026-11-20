using Nabd.Rules;

namespace Nabd.ProcessingHost;

/// <summary>
/// P4 spec 07 (plan/06 §4): the watchdog's own 30 s loop — a separate task, so a
/// slow tick never touches the ingestion critical path. A failed tick is logged
/// by the watchdog itself (state is re-read next tick); this loop never dies.
/// </summary>
public sealed class OfflineWatchdogWorker(
    OfflineWatchdog watchdog,
    IConfiguration configuration,
    ILogger<OfflineWatchdogWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Watchdog:OfflineIntervalSeconds", 30);
        logger.LogInformation("offline watchdog cadence: every {Seconds}s", seconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await watchdog.TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
