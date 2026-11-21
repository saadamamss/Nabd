namespace Nabd.Notifications;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Drains the Telegram queue off the alert path (D9g). The sender promises not to throw;
/// this loop guards the promise anyway, so a broken promise cannot kill the queue silently.
/// </summary>
public sealed class TelegramDispatchWorker(
    TelegramQueue queue,
    TelegramNotificationSender sender,
    ILogger<TelegramDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await sender.SendAsync(message, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "telegram dispatch failed for alert {AlertId}", message.AlertId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }
}
