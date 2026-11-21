namespace Nabd.Notifications;

using Microsoft.Extensions.Logging;
using Nabd.Alerting.Contracts;
using Nabd.Identity.Contracts;
using Nabd.Notifications.Contracts;
using Nabd.SharedKernel;

/// <summary>
/// One alert → one Telegram message per linked, severity-subscribed user (FR-34/FR-38).
/// Two laws meet here: plan/06 §6 (ChannelRouting decides whether telegram is on the
/// severity's row) and the user's own pick (query filter). Rendering uses the same
/// message_templates row as email, in the user's locale, through the same renderer.
/// Delivery is enqueued (D9g) — publishing awaits subscribers, so the API must never run here.
/// </summary>
public sealed class TelegramNotifier : IDisposable
{
    private readonly IUserDirectory _people;
    private readonly TelegramChannels _channels;
    private readonly AlertMessageRenderer _renderer;
    private readonly TelegramQueue _queue;
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly IDisposable _raised;
    private readonly IDisposable _resolved;

    private long _dispatched;

    public TelegramNotifier(
        IEventBus bus, IUserDirectory people, TelegramChannels channels,
        AlertMessageRenderer renderer, TelegramQueue queue, ILogger<TelegramNotifier> logger)
    {
        _people = people;
        _channels = channels;
        _renderer = renderer;
        _queue = queue;
        _logger = logger;
        _raised = bus.Subscribe<AlertRaised>(OnRaisedAsync);
        _resolved = bus.Subscribe<AlertResolved>(OnResolvedAsync);
    }

    /// <summary>plan/06 §11: pushes this process handed to the channel.</summary>
    public long Dispatched => Interlocked.Read(ref _dispatched);

    public void Dispose()
    {
        _raised.Dispose();
        _resolved.Dispose();
    }

    private Task OnRaisedAsync(AlertRaised evt, CancellationToken ct)
    {
        var templateKey = evt.Alert.Type == AlertTypes.Offline ? "device_offline" : "alert_raised";
        return NotifyAsync(evt.Alert, templateKey, evt.Alert.OpenedAt, reason: null, ct);
    }

    private Task OnResolvedAsync(AlertResolved evt, CancellationToken ct) =>
        NotifyAsync(evt.Alert, "alert_resolved", evt.AtUtc, evt.Reason, ct);

    private async Task NotifyAsync(
        AlertSnapshot alert, string templateKey, DateTimeOffset at, string? reason,
        CancellationToken ct)
    {
        try
        {
            // plan/06 §6, one home: the platform table still decides whether telegram exists
            // for this severity (info never reaches here).
            if (!ChannelRouting.ChannelsFor(alert.Severity).Contains(AlertChannels.Telegram))
                return;

            var chats = await _channels.RecipientsAsync(alert.TenantId, alert.Severity, ct);
            if (chats.Count == 0) return;

            // Locale through Identity's port (the wall): id → language, joined in memory.
            var people = await _people.RecipientsAsync(alert.TenantId, ct);
            var locales = people.ToDictionary(p => p.UserId, p => p.Locale);

            foreach (var chat in chats)
            {
                var locale = locales.TryGetValue(chat.UserId, out var l) ? l : "en"; // D9h

                var (_, body) = await _renderer.RenderAsync(
                    alert, templateKey, AlertChannels.Telegram, locale, at, reason, ct);

                var message = new NotificationMessage(
                    alert.TenantId, alert.AlertId, AlertChannels.Telegram, locale,
                    chat.ChatId, Subject: "", Body: body, TemplateKey: templateKey);

                if (!_queue.TryWrite(message))
                {
                    _logger.LogError("telegram queue is full — dropped {TemplateKey} for alert {AlertId}",
                        templateKey, alert.AlertId);
                    continue;
                }
                Interlocked.Increment(ref _dispatched);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "telegram fan-out failed for alert {AlertId}", alert.AlertId);
        }
    }
}
