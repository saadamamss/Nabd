namespace Nabd.Notifications;

using Microsoft.Extensions.Logging;
using Nabd.Alerting.Contracts;
using Nabd.Identity.Contracts;
using Nabd.Notifications.Contracts;
using Nabd.SharedKernel;

/// <summary>
/// Listens where Alerting publishes (plan/06 §5.1) and turns one alert into one email
/// per tenant user, in that user's language. Severity routing is plan/06 §6: info never
/// leaves the dashboard. Delivery is enqueued (D8j): publishing awaits its subscribers,
/// so SMTP must never run on the alert path.
/// </summary>
public sealed class EmailNotifier : IDisposable
{
    private readonly IUserDirectory _people;
    private readonly AlertMessageRenderer _renderer;
    private readonly EmailQueue _queue;
    private readonly ILogger<EmailNotifier> _logger;
    private readonly IDisposable _raised;
    private readonly IDisposable _resolved;

    private long _dispatched;

    public EmailNotifier(
        IEventBus bus, IUserDirectory people, AlertMessageRenderer renderer,
        EmailQueue queue, ILogger<EmailNotifier> logger)
    {
        _people = people;
        _renderer = renderer;
        _queue = queue;
        _logger = logger;
        _raised = bus.Subscribe<AlertRaised>(OnRaisedAsync);
        _resolved = bus.Subscribe<AlertResolved>(OnResolvedAsync);
    }

    /// <summary>plan/06 §11: emails this process handed to the channel.</summary>
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
            // plan/06 §6 already has one home (Alerting.Contracts.ChannelRouting): ask it
            // whether email is on this severity's row — no second copy of the table here.
            if (!ChannelRouting.ChannelsFor(alert.Severity).Contains(AlertChannels.Email))
                return;

            var people = await _people.RecipientsAsync(alert.TenantId, ct);
            if (people.Count == 0)
            {
                _logger.LogWarning("alert {AlertId} has no recipients in tenant {TenantId}",
                    alert.AlertId, alert.TenantId);
                return;
            }

            foreach (var person in people)
            {
                var (subject, body) = await _renderer.RenderAsync(
                    alert, templateKey, AlertChannels.Email, person.Locale, at, reason, ct);

                var message = new NotificationMessage(
                    alert.TenantId, alert.AlertId, "email", person.Locale,
                    person.Email, subject, body, templateKey);

                // OFF the event-bus thread: publishing awaits its subscribers, so an SMTP
                // timeout inside this loop would stall the alert path (plan/06 §5.1).
                if (!_queue.TryWrite(message))
                {
                    _logger.LogError("email queue is full — dropped {TemplateKey} for alert {AlertId}",
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
            _logger.LogError(e, "email fan-out failed for alert {AlertId}", alert.AlertId);
        }
    }
}
