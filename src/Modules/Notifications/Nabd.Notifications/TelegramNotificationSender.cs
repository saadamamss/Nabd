namespace Nabd.Notifications;

using Microsoft.Extensions.Logging;
using Nabd.Notifications.Contracts;

/// <summary>
/// The Telegram channel behind INotificationSender's law (D9g): one attempt per pass
/// with the injected backoff, one NotificationLog row per send, never a throw into the
/// caller, never the bot token or the chat id in a log line.
/// </summary>
public sealed class TelegramNotificationSender(
    TelegramChannelConfig config,
    ITelegramTransport transport,
    TelegramRetryPolicy policy,
    NotificationLog log,
    ILogger<TelegramNotificationSender> logger) : INotificationSender
{
    private long _sent;
    private long _failed;

    public long Sent => Interlocked.Read(ref _sent);
    public long Failed => Interlocked.Read(ref _failed);

    public async Task SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        try
        {
            var settings = await config.LoadAsync(ct);
            if (settings is null)
            {
                await log.WriteAsync(message, "failed", 0,
                    "telegram channel is not configured (notifications.channel_config)", ct);
                Interlocked.Increment(ref _failed);
                logger.LogWarning("telegram skipped: channel not configured (alert {AlertId})",
                    message.AlertId);
                return;
            }

            for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
            {
                try
                {
                    await transport.SendMessageAsync(
                        settings.BotToken, message.Recipient, message.Body, ct);
                    await log.WriteAsync(message, "sent", attempt, null, ct);
                    Interlocked.Increment(ref _sent);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e) when (attempt < policy.MaxAttempts)
                {
                    // alert id + attempt only: the chat id lives in notification_log.recipient
                    logger.LogWarning("telegram attempt {Attempt} failed for alert {AlertId}: {Reason}",
                        attempt, message.AlertId, e.Message);
                    await Task.Delay(policy.Delays[attempt - 1], ct);
                }
                catch (Exception e)
                {
                    await log.WriteAsync(message, "failed", attempt, e.Message, ct);
                    Interlocked.Increment(ref _failed);
                    logger.LogError("telegram failed after {Attempts} attempts for alert {AlertId}: {Reason}",
                        attempt, message.AlertId, e.Message);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // config/key errors land here: logged, never thrown into the alert path
            Interlocked.Increment(ref _failed);
            logger.LogError(e, "telegram channel error for alert {AlertId}", message.AlertId);
        }
    }
}
