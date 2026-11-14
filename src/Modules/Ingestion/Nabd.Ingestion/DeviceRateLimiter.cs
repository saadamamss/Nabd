namespace Nabd.Ingestion;

/// <summary>
/// RED STUB (P3 spec 05 scaffolding): the HTTP door's per-device quota — an
/// in-memory sliding window per device (plan/05 §4: 30 req/min at MVP). The
/// real one: a ConcurrentDictionary of device → queue of timestamps, Allow()
/// trims the window, answers false past the cap, and reports the seconds
/// until the OLDEST stamp exits the window (the honest Retry-After).
/// Single-instance honest; distributed limiting is a staging-era concern.
/// </summary>
public sealed class DeviceRateLimiter(int maxPerMinute = 30)
{
    public bool Allow(Guid deviceId, out int retryAfterSeconds)
    {
        _ = maxPerMinute; _ = deviceId;
        retryAfterSeconds = 0;
        throw new NotImplementedException("P3 spec 05: owner implements from study/implementation-code.md");
    }
}
