namespace Nabd.Notifications;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The bot's ear (D9f): long-poll getUpdates, verify a code, reply. The offset is
/// in-memory; a restart may replay one update, and a one-time code makes that harmless.
/// Non-code messages are ignored on purpose — no echo, no spam loop.
/// Bilingual reply: the chat's locale is not known before the link completes.
/// </summary>
public sealed class TelegramBotWorker(
    TelegramChannelConfig config,
    ITelegramTransport transport,
    TelegramChannels channels,
    ILogger<TelegramBotWorker> logger) : BackgroundService
{
    private const int PollSeconds = 25;
    private long _offset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await config.LoadAsync(stoppingToken);
                if (settings is null)
                {
                    // not configured yet: wait, don't spin (the secret door may run later)
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                var updates = await transport.GetUpdatesAsync(
                    settings.BotToken, _offset, PollSeconds, stoppingToken);

                foreach (var update in updates)
                {
                    _offset = Math.Max(_offset, update.UpdateId + 1);
                    await HandleAsync(settings, update, stoppingToken);
                }

                backoff = TimeSpan.FromSeconds(5);       // a clean poll resets the ladder
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // The message never carries the token (transport law); say what failed, not where.
                logger.LogError(e, "telegram bot poll failed; retrying in {Backoff}", backoff);
                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 120));
            }
        }
    }

    private async Task HandleAsync(
        TelegramSettings settings, TelegramUpdate update, CancellationToken ct)
    {
        var code = TelegramLinkCodes.Extract(update.Text);
        if (code is null) return;                        // not a link attempt: stay silent

        var userId = await channels.VerifyAsync(code, update.ChatId, ct);
        var reply = userId is null
            ? "الرمز غير صالح أو انتهت صلاحيته.\nInvalid or expired code."
            : "تم ربط حسابك بنبض ✅\nYour Nabd account is linked ✅";

        await transport.SendMessageAsync(settings.BotToken, update.ChatId, reply, ct);
    }
}
