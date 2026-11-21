namespace Nabd.Notifications;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nabd.Notifications.Contracts;

/// <summary>
/// The one place this process touches SMTP (P4 spec 08, D8j). It drains the bounded
/// email queue off the alert path; the sender already promises not to throw, and this
/// loop guards that promise anyway. A BackgroundService in the room (the TelemetryStore
/// flush worker is the same shape) so the host only registers it.
/// </summary>
public sealed class EmailDispatchWorker(
    EmailQueue queue,
    INotificationSender sender,
    ILogger<EmailDispatchWorker> logger) : BackgroundService
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
                    // The sender promises not to throw; if that promise ever breaks,
                    // the queue must not die silently.
                    logger.LogError(e, "email dispatch failed for alert {AlertId}", message.AlertId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }
}
