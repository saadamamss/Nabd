namespace Nabd.Notifications;

using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>One inbound bot message, as the worker needs it.</summary>
public sealed record TelegramUpdate(long UpdateId, string ChatId, string? Text);

/// <summary>
/// The ONE seam where this room touches Telegram (D9f). The net fakes it — no network,
/// no token, no polling. `HttpTelegramTransport` is production.
/// </summary>
public interface ITelegramTransport
{
    Task SendMessageAsync(
        string botToken, string chatId, string text, CancellationToken ct = default);

    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        string botToken, long offset, int timeoutSeconds, CancellationToken ct = default);
}

/// <summary>
/// Telegram Bot API over HttpClient (zero new dependency). The bot token travels in the
/// URL path by the API's design — so no log line and no exception message below ever
/// includes the URL. Plain text (no parse_mode): alert titles contain no markup we must escape.
/// </summary>
public sealed class HttpTelegramTransport : ITelegramTransport, IDisposable
{
    private const string Base = "https://api.telegram.org";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };

    public void Dispose() => _http.Dispose();

    public async Task SendMessageAsync(
        string botToken, string chatId, string text, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            text,
            disable_web_page_preview = true,
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(
            $"{Base}/bot{botToken}/sendMessage", content, ct);
        using var _ = await ReadApiAsync(response, "sendMessage", ct);
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        string botToken, long offset, int timeoutSeconds, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(
            $"{Base}/bot{botToken}/getUpdates?offset={offset}&timeout={timeoutSeconds}", ct);
        using var doc = await ReadApiAsync(response, "getUpdates", ct);

        var updates = new List<TelegramUpdate>();
        if (!doc.RootElement.TryGetProperty("result", out var result)) return updates;

        foreach (var update in result.EnumerateArray())
        {
            if (!update.TryGetProperty("update_id", out var id)) continue;
            if (!update.TryGetProperty("message", out var message)) continue;
            if (!message.TryGetProperty("chat", out var chat)
                || !chat.TryGetProperty("id", out var chatId)) continue;

            var text = message.TryGetProperty("text", out var t) ? t.GetString() : null;
            updates.Add(new TelegramUpdate(
                id.GetInt64(),
                chatId.GetInt64().ToString(CultureInfo.InvariantCulture),
                text));
        }
        return updates;
    }

    /// <summary>
    /// Parses `{ ok, description, result }`. A refusal is an exception whose message
    /// carries the API's own description only — never the request URL (it holds the token).
    /// </summary>
    private static async Task<JsonDocument> ReadApiAsync(
        HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"telegram {operation} returned non-JSON ({(int)response.StatusCode}).");
        }

        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var description = doc.RootElement.TryGetProperty("description", out var d)
                ? d.GetString()
                : null;
            doc.Dispose();
            throw new InvalidOperationException(
                $"telegram {operation} refused: {description ?? ((int)response.StatusCode).ToString()}");
        }
        return doc;
    }
}
