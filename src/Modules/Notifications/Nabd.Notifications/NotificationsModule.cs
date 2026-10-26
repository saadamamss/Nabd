namespace Nabd.Notifications;

/// <summary>
/// Notifications module (03 §3): channels config (SMTP, Telegram bot — 04 §9 encrypted),
/// bilingual templates (FR-34/38), send helper with retry.
/// P0: empty on purpose. P4 fills it (bilingual email; Telegram push per v0.2).
/// Listens: AlertRaised, AlertResolved.
/// </summary>
public static class NotificationsModule
{
    public const string RoomName = "Notifications";
}
