namespace Nabd.Notifications;

using Microsoft.Extensions.Logging;
using Nabd.Notifications.Contracts;

/// <summary>
/// The email channel behind INotificationSender (D8f: System.Net.Mail). One attempt per
/// pass with a policy-driven backoff (D8g: 2 s then 10 s; tests inject milliseconds),
/// one log row per (alert, channel, recipient) through NotificationLog. Never throws
/// into the caller; never logs a secret.
/// </summary>
public sealed class EmailNotificationSender(
    EmailChannelConfig config,
    IEmailTransport transport,
    EmailRetryPolicy policy,
    NotificationLog log,
    ILogger<EmailNotificationSender> logger) : INotificationSender
{
    private long _sent;
    private long _failed;

    /// <summary>plan/06 §11 observability — this process's email counters.</summary>
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
                    "email channel is not configured (notifications.channel_config)", ct);
                Interlocked.Increment(ref _failed);
                logger.LogWarning("email skipped: channel not configured (alert {AlertId})", message.AlertId);
                return;
            }

            for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
            {
                try
                {
                    await transport.SendAsync(
                        settings, message.Recipient, message.Subject, message.Body, ct);
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
                    logger.LogWarning("email attempt {Attempt} failed for alert {AlertId}: {Reason}",
                        attempt, message.AlertId, e.Message);
                    await Task.Delay(policy.Delays[attempt - 1], ct);
                }
                catch (Exception e)
                {
                    await log.WriteAsync(message, "failed", attempt, e.Message, ct);
                    Interlocked.Increment(ref _failed);
                    logger.LogError("email failed after {Attempts} attempts for alert {AlertId}: {Reason}",
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
            logger.LogError(e, "email channel error for alert {AlertId}", message.AlertId);
        }
    }
}
