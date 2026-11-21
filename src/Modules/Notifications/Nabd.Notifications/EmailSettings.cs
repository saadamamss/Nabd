namespace Nabd.Notifications;

using System.Text.Json;
using Npgsql;

/// <summary>What the sender needs; the password exists only inside this record.</summary>
public sealed record EmailSettings(
    string Host, int Port, string? User, string? Password, string From, string SenderName);

/// <summary>The non-secret half of the row, as written by the secret tool.</summary>
internal sealed record EmailConfigJson(
    string? Host, int Port, string? User, string? From, string? SenderName);

/// <summary>
/// Reads `notifications.channel_config` row 'email' and opens its secret.
/// NULL = not configured / disabled — a state the caller logs, not a crash.
/// </summary>
public sealed class EmailChannelConfig(NotificationsDb db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EmailSettings?> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenPlatformReadAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT enabled, config, secret_enc, secret_nonce, secret_tag
              FROM notifications.channel_config
             WHERE channel = 'email';
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.GetBoolean(0)) return null;

        var config = JsonSerializer.Deserialize<EmailConfigJson>(reader.GetString(1), Json)
                     ?? new EmailConfigJson(null, 0, null, null, null);

        var password = SecretBox.Open(
            reader.GetFieldValue<byte[]>(2),
            reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<byte[]>(4),
            "email");

        if (config.Host is null || config.From is null) return null;
        return new EmailSettings(config.Host, config.Port == 0 ? 587 : config.Port,
            config.User, password, config.From, config.SenderName ?? "Nabd");
    }
}
