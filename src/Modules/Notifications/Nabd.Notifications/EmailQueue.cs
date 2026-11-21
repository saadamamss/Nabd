namespace Nabd.Notifications;

using System.Threading.Channels;
using Nabd.Notifications.Contracts;

/// <summary>
/// The hand-off between the alert path and the SMTP path (D8j). Bounded: when full it
/// drops the NEWEST message and says so — an unbounded queue would hide a dead SMTP
/// behind growing memory, and blocking would stall the alert path.
/// </summary>
public sealed class EmailQueue
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
/// D8g's clock, made injectable: production backs off 2 s / 10 s; a test injects
/// milliseconds instead of pretending SMTP is instant.
/// </summary>
public sealed class EmailRetryPolicy(TimeSpan[]? delays = null)
{
    public static EmailRetryPolicy Default { get; } = new();

    public TimeSpan[] Delays { get; } = delays
        ?? [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    public int MaxAttempts => Delays.Length + 1;
}
