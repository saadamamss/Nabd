namespace Nabd.Notifications;

using System.Threading.Channels;
using Nabd.Notifications.Contracts;

/// <summary>
/// The hand-off between the alert path and the Telegram path (D9g). Bounded: full means
/// drop the NEWEST and say so — an unbounded queue hides a dead API behind growing memory,
/// and blocking would stall alerting.
/// </summary>
public sealed class TelegramQueue
{
    private readonly Channel<NotificationMessage> _channel =
        Channel.CreateBounded<NotificationMessage>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,   // TryWrite returns false when full
        });

    public bool TryWrite(NotificationMessage message) => _channel.Writer.TryWrite(message);

    public IAsyncEnumerable<NotificationMessage> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// D9g's clock, injectable exactly like the email one: production backs off 2 s / 10 s;
/// the net injects milliseconds instead of pretending Telegram is instant.
/// </summary>
public sealed class TelegramRetryPolicy(TimeSpan[]? delays = null)
{
    public static TelegramRetryPolicy Default { get; } = new();

    public TimeSpan[] Delays { get; } = delays
        ?? [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    public int MaxAttempts => Delays.Length + 1;
}
