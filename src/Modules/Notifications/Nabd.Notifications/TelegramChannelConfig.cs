namespace Nabd.Notifications;

using System.Text.Json;
using Npgsql;

/// <summary>What the bot and the sender need; the token exists only inside this record.</summary>
public sealed record TelegramSettings(string BotToken, string? Username);

/// <summary>The non-secret half of the row, as written by the secret tool.</summary>
internal sealed record TelegramConfigJson(string? Username);

/// <summary>
/// Reads `notifications.channel_config` row 'telegram' and opens its secret with AAD
/// "telegram" (D9b/spec 08 D8b). NULL = not configured / disabled — a state the caller
/// logs, never a crash. A token sealed for `email` cannot be opened here: the AAD binds it.
/// </summary>
public sealed class TelegramChannelConfig(NotificationsDb db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TelegramSettings?> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenPlatformReadAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT enabled, config, secret_enc, secret_nonce, secret_tag
              FROM notifications.channel_config
             WHERE channel = 'telegram';
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.GetBoolean(0)) return null;

        var config = JsonSerializer.Deserialize<TelegramConfigJson>(reader.GetString(1), Json)
                     ?? new TelegramConfigJson(null);

        var token = SecretBox.Open(
            reader.GetFieldValue<byte[]>(2),
            reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<byte[]>(4),
            "telegram");

        if (string.IsNullOrWhiteSpace(token)) return null;
        return new TelegramSettings(token, config.Username);
    }
}
