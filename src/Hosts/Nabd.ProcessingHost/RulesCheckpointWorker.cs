using Nabd.Rules;
using Nabd.SharedKernel;

namespace Nabd.ProcessingHost;

/// <summary>
/// P4 spec 04 (§3.4): the 60 s checkpoint — persist rule state and reload the rule
/// set. A failed checkpoint is LOGGED and retried next tick; it must never kill
/// the host (an alerting memory bug is not a reason to stop ingesting).
/// </summary>
public sealed class RulesCheckpointWorker(RulesEngine engine, ILogger<RulesCheckpointWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await engine.CheckpointAsync(stoppingToken);
                logger.LogInformation(
                    "rules checkpoint: evaluated_total={Evaluated} crossing_detected={Crossings} state_writes={Writes}",
                    engine.Evaluated, engine.Crossings, engine.StateWrites);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "rules checkpoint failed — state stays in memory, retried next tick");
            }
        }
    }
}
