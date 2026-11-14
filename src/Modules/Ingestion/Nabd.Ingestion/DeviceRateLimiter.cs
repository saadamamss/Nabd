namespace Nabd.Ingestion;

using System.Collections.Concurrent;

/// <summary>
/// The HTTP door's per-device quota (plan/05 §4: 30 req/min at MVP) — an
/// in-memory SLIDING WINDOW keyed by device. Single-instance honest; a second
/// API instance would double the quota — distributed limiting is a
/// staging-era concern (plan/05 §4), noted, not built.
/// The window is a Queue of timestamps per device: old stamps drain, a stamp
/// per allowed call, and the Retry-After is the honest time until the OLDEST
/// stamp leaves the window.
/// </summary>
public sealed class DeviceRateLimiter(int maxPerMinute = 30)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    public bool Allow(Guid deviceId, out int retryAfterSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var stamps = _hits.GetOrAdd(deviceId, _ => new Queue<DateTimeOffset>());

        bool allowed;
        lock (stamps)
        {
            // drain: stamps older than the window no longer count
            while (stamps.Count > 0 && now - stamps.Peek() > Window)
            {
                stamps.Dequeue();
            }

            if (stamps.Count >= maxPerMinute)
            {
                // the honest hint: exactly when the oldest stamp exits the window
                retryAfterSeconds = (int)Math.Ceiling(
                    (stamps.Peek() + Window - now).TotalSeconds);
                allowed = false;
            }
            else
            {
                stamps.Enqueue(now);
                retryAfterSeconds = 0;
                allowed = true;
            }
        }

        return allowed;
    }
}