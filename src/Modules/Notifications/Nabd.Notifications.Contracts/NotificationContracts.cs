namespace Nabd.Notifications.Contracts;

using Nabd.Identity.Contracts;   // CallerContext (spec 09)
using Nabd.SharedKernel;

/// <summary>
/// One outbound message (P4 spec 08): the recipient is part of the message's identity
/// (not of its prose), and the chosen template key is part of the durable evidence.
/// </summary>
public sealed record NotificationMessage(
    Guid TenantId, Guid AlertId, string Channel, string Locale,
    string Recipient, string Subject, string Body, string TemplateKey);

/// <summary>
/// Outbound port used by the notification hosts (06 §3.3). The room owns channel config
/// lookup (04 §9: encrypted SMTP/Telegram config).
/// </summary>
public interface INotificationSender
{
    /// <summary>Must never take the pipeline down: delivery failures are logged + retried by the room, never thrown to callers.</summary>
    Task SendAsync(NotificationMessage message, CancellationToken ct = default);
}

/// <summary>
/// P4 spec 09 (FR-38): the one-time ticket shown to its user. The plaintext code exists
/// only in this reply — the row keeps its HMAC. `BotUsername` is non-secret config (may be null).
/// </summary>
public sealed record TelegramLinkTicket(string Code, DateTimeOffset ExpiresAt, string? BotUsername);

/// <summary>
/// The channel as its owner sees it. No chat id leaves the room: linking is proven by
/// `Linked`, and the id is an address, not a UI fact.
/// </summary>
public sealed record TelegramChannelState(
    bool Linked, DateTimeOffset? VerifiedAt, IReadOnlyList<string> Severities);

/// <summary>Door error codes — the controller's only translation table (lesson #16).</summary>
public static class NotificationErrors
{
    public const string InvalidSeverity = "notifications:invalid_severity";  // info / unknown
    public const string InvalidCode = "notifications:invalid_code";          // reserved for future doors
}

/// <summary>
/// The user-facing door of the Telegram channel (spec 09). The implementation asserts
/// `Operations.TelegramManage` on every call; the room owns the row, the user owns the chat.
/// </summary>
public interface ITelegramChannels
{
    Task<Result<TelegramLinkTicket>> IssueLinkCodeAsync(CallerContext acting, CancellationToken ct = default);
    Task<Result<TelegramChannelState>> GetAsync(CallerContext acting, CancellationToken ct = default);
    Task<Result<TelegramChannelState>> SetSeveritiesAsync(
        CallerContext acting, IReadOnlyList<string> severities, CancellationToken ct = default);
    Task<Result<TelegramChannelState>> UnlinkAsync(CallerContext acting, CancellationToken ct = default);
}
