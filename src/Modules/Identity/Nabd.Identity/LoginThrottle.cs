using System.Collections.Concurrent;

namespace Nabd.Identity;

/// <summary>
/// Brute-force armor (P1 spec 03; plan/07 §3 row 5). In-memory sliding window keyed
/// by "ip|account": every FAILURE raises the streak; from the 5th failure onward each
/// further attempt answers a doubling band (1s, 2s, 4s…) reported in the response.
/// SUCCESS resets the streak. No permanent lockout — the window slides: 15 minutes of
/// failure-free time erases the record. Deliberate MVP honesty: in-memory only (P1 is
/// one process; HA scaling re-discusses at plan/07 §9's level), and throttled attempts
/// still count — a hammering bot escalates its own bands, a CAP at 2^12 stops the moon.
/// </summary>
public sealed class LoginThrottle
{
    private const int BandFromStreak = 5;      // 1s starts at the 5th recorded failure
    private const int BandCapExponent = 12;    // 2^12 = 4096s ceiling
    private static readonly TimeSpan SlideWindow = TimeSpan.FromMinutes(15);

    private sealed record Attempt(int Streak, DateTime LastFailUtc);
    private readonly ConcurrentDictionary<string, Attempt> _fails = new();

    public int DelaySeconds(string key)
    {
        if (!_fails.TryGetValue(key, out var a)) return 0;
        if (a.Streak < BandFromStreak) return 0;
        return 1 << Math.Min(a.Streak - BandFromStreak, BandCapExponent);
    }

    public void RecordFailure(string key)
    {
        var now = DateTime.UtcNow;
        _fails.AddOrUpdate(key,
            _ => new Attempt(1, now),
            (_, a) => (now - a.LastFailUtc >= SlideWindow) ? new Attempt(1, now) : new Attempt(a.Streak + 1, now));
    }

    public void RecordSuccess(string key) => _fails.TryRemove(key, out _);
}