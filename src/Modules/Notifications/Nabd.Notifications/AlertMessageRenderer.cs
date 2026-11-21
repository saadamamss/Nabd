namespace Nabd.Notifications;

using System.Globalization;
using Npgsql;
using Nabd.Alerting.Contracts;

/// <summary>
/// Renders one alert into subject+body from `message_templates`, in the recipient's
/// language, on the requested channel (P4 spec 09: email has a subject, Telegram does not).
/// The template owns the product voice; the row owns the facts — that is why the
/// placeholders are few and named. A missing template is an ERROR (never a silent skip).
/// </summary>
public sealed class AlertMessageRenderer(NotificationsDb db)
{
    public async Task<(string Subject, string Body)> RenderAsync(
        AlertSnapshot alert, string templateKey, string channel, string locale,
        DateTimeOffset at, string? reason, CancellationToken ct = default)
    {
        var (subject, body) = await LoadAsync(templateKey, channel, locale, ct)
            ?? await LoadAsync(templateKey, channel, "en", ct)   // fallback: one language, never silence
            ?? throw new InvalidOperationException(
                $"message template missing: {templateKey}/{channel}/{locale} (and no en fallback).");

        var title = locale == "ar" ? alert.TitleAr : alert.TitleEn;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["severity"] = Severity(alert.Severity, locale),
            ["time"] = at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["alert_id"] = alert.AlertId.ToString(),
            ["gap"] = alert.Detail.OfflineGapMinutes is { } gap
                ? gap.ToString("0.#", CultureInfo.InvariantCulture)
                : "—",
            ["reason"] = reason ?? "—",
        };

        foreach (var (key, value) in values)
        {
            subject = subject.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
            body = body.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        }
        return (subject, body);
    }

    private async Task<(string Subject, string Body)?> LoadAsync(
        string key, string channel, string locale, CancellationToken ct)
    {
        await using var conn = await db.OpenPlatformReadAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT subject, body FROM notifications.message_templates
             WHERE key = @k AND channel = @ch AND locale = @l;
            """, conn);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("ch", channel);
        cmd.Parameters.AddWithValue("l", locale);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetString(1))   // Telegram: no subject
            : null;
    }

    private static string Severity(string severity, string locale) => (severity, locale) switch
    {
        ("critical", "ar") => "حرج",
        ("warn", "ar") => "تحذير",
        ("info", "ar") => "معلومة",
        ("critical", _) => "critical",
        ("warn", _) => "warn",
        _ => "info",
    };
}
