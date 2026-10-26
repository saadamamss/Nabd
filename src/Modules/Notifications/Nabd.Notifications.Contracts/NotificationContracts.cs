namespace Nabd.Notifications.Contracts;

public sealed record NotificationMessage(
    Guid TenantId, Guid AlertId, string Channel, string Locale, string Subject, string Body);

/// <summary>
/// Outbound port used by the notification hosts (06 §3.3, P4 scope P4: email today, Telegram in v0.2 scope).
/// The room owns channel config lookup (04 §9: encrypted SMTP/Telegram config).
/// </summary>
public interface INotificationSender
{
    /// <summary>Must never take the pipeline down: delivery failures are logged + retried by the room, never thrown to callers.</summary>
    Task SendAsync(NotificationMessage message, CancellationToken ct = default);
}
