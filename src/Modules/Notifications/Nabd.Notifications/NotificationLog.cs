namespace Nabd.Notifications;

using Npgsql;
using Nabd.Notifications.Contracts;

/// <summary>
/// The ONE home of the send evidence (P4 spec 09 extracted it from the email sender so
/// both channels write the same row shape). Tenant-scoped by law: RLS on
/// alerting.notification_log. `sent_at` exists only when the row says `sent` — a failed
/// row is evidence with a time of its own only through `id` ordering, never a fake stamp.
/// </summary>
public sealed class NotificationLog(NotificationsDb db)
{
    public async Task WriteAsync(
        NotificationMessage message, string status, int attempts, string? error,
        CancellationToken ct = default)
    {
        var (conn, tx) = await db.OpenTenantScopeAsync(message.TenantId, ct);
        try
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO alerting.notification_log
                    (tenant_id, alert_id, channel, recipient, template_key, locale,
                     status, attempts, error, sent_at)
                VALUES (@t, @a, @ch, @to, @k, @l, @s, @n, @e, @at);
                """, conn, tx);
            cmd.Parameters.AddWithValue("t", message.TenantId);
            cmd.Parameters.AddWithValue("a", message.AlertId);
            cmd.Parameters.AddWithValue("ch", message.Channel);
            cmd.Parameters.AddWithValue("to", message.Recipient);
            cmd.Parameters.AddWithValue("k", message.TemplateKey);
            cmd.Parameters.AddWithValue("l", message.Locale);
            cmd.Parameters.AddWithValue("s", status);
            cmd.Parameters.AddWithValue("n", attempts);
            cmd.Parameters.AddWithValue("e", (object?)Truncate(error) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("at",
                status == "sent" ? (object)DateTimeOffset.UtcNow : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;                          // the caller's outer catch logs it — never silent
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private static string? Truncate(string? text) =>
        text is null ? null : text.Length <= 300 ? text : text[..300];
}
