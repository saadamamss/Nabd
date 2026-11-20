namespace Nabd.Alerting.Contracts;

using Nabd.Identity.Contracts;
using Nabd.Rules.Contracts;
using Nabd.SharedKernel;

/// <summary>The three states of plan/06 §5.1 — one vocabulary, one home.</summary>
public static class AlertStatuses
{
    public const string Open = "open";
    public const string Acknowledged = "acknowledged";
    public const string Resolved = "resolved";
    public static readonly IReadOnlyList<string> All = [Open, Acknowledged, Resolved];
}

/// <summary>plan/06 §6 table — the only severities the DB accepts.</summary>
public static class AlertSeverities
{
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Critical = "critical";
    public static readonly IReadOnlyList<string> All = [Info, Warn, Critical];
}

/// <summary>plan/04 §8: threshold alerts come from the engine; offline alerts from spec 07.</summary>
public static class AlertTypes
{
    public const string Threshold = "threshold";
    public const string Offline = "offline";
}

/// <summary>plan/06 §6 — exactly three channels at MVP.</summary>
public static class AlertChannels
{
    public const string Dashboard = "dashboard";
    public const string Email = "email";
    public const string Telegram = "telegram";
}

/// <summary>
/// plan/06 §6, verbatim: info → dashboard only · warn → all three · critical →
/// email + Telegram (cooldown halved elsewhere). An unknown severity THROWS:
/// a severity without a routing row is a bug, never a silent fall-through.
/// Specs 08/09 (Notifications) consume this table; 06 pins it.
/// </summary>
public static class ChannelRouting
{
    public static IReadOnlyList<string> ChannelsFor(string severity) => severity switch
    {
        AlertSeverities.Info => [AlertChannels.Dashboard],
        AlertSeverities.Warn => [AlertChannels.Dashboard, AlertChannels.Email, AlertChannels.Telegram],
        AlertSeverities.Critical => [AlertChannels.Email, AlertChannels.Telegram],
        _ => throw new ArgumentOutOfRangeException(
            nameof(severity), severity, "severity without a routing row (plan/06 §6)"),
    };
}

/// <summary>
/// plan/06 §5.4 as a pure law (D6d): a threshold crossing inside a weekly UTC
/// window is muted — START inclusive / END exclusive. OFFLINE IS NEVER MUTED:
/// "offline detection always runs, so a faked maintenance window cannot hide a
/// dead device." Spec 07 wires the offline path through this same function.
/// </summary>
public static class MuteDecision
{
    public static bool ShouldMute(
        string alertType, IReadOnlyList<MuteWindow>? windows, DateTimeOffset at)
    {
        if (alertType != AlertTypes.Threshold) return false;   // the honesty sentence
        if (windows is null || windows.Count == 0) return false;

        var utc = at.ToUniversalTime();
        var minute = utc.Hour * 60 + utc.Minute;

        foreach (var w in windows)
            if (w.DayOfWeek == (int)utc.DayOfWeek
                && minute >= w.StartMinute && minute < w.EndMinute)
                return true;
        return false;
    }
}

/// <summary>
/// The observed evidence of one alert, typed (never raw JSON on the wire).
/// `Value` is the readings value that crossed; the thresholds are the rule's own,
/// copied at the moment of the crossing (a later PATCH must not rewrite history).
/// </summary>
public sealed record AlertDetail(
    double Value,
    string ChannelKey,
    string RuleType,
    double? ThresholdLow,
    double? ThresholdHigh,
    Guid DeviceId);

/// <summary>One alert row, as the doors and the fan-out see it (plan/04 §8).</summary>
public sealed record AlertSnapshot(
    Guid AlertId,
    Guid TenantId,
    Guid AssetId,
    Guid RuleId,
    string Type,
    string Severity,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? AcknowledgedAt,
    Guid? AcknowledgedBy,
    string TitleEn,
    string TitleAr,
    AlertDetail Detail);

/// <summary>List filter (FR-42's future dashboard asks exactly these three + a cap).</summary>
public sealed record AlertFilter(
    string? Status = null,
    Guid? AssetId = null,
    string? Severity = null,
    int Limit = 100);

/// <summary>Door error codes — the controller's only translation table (lesson #16).</summary>
public static class AlertErrors
{
    public const string AlertNotFound = "alerting:alert_not_found";   // also the cross-tenant answer (404-hide)
    public const string InvalidStatus = "alerting:invalid_status";    // ack/resolve a resolved alert
    public const string InvalidFilter = "alerting:invalid_filter";    // bad status/severity/limit
    public const string InvalidReason = "alerting:invalid_reason";    // > 200 chars
    public const string AssetNotFound = "alerting:asset_not_found";   // the device's asset vanished/foreign
}

/// <summary>The Alerting room's public door (P4 spec 05). Nothing here touches the bus:
/// the lifecycle consumer is a separate class, so the API host never subscribes to events
/// it cannot receive (the two hosts share no bus — D4).</summary>
public interface IAlerting
{
    Task<Result<IReadOnlyList<AlertSnapshot>>> ListAsync(
        CallerContext acting, AlertFilter filter, CancellationToken ct = default);

    Task<Result<AlertSnapshot>> GetAsync(
        CallerContext acting, Guid alertId, CancellationToken ct = default);

    /// <summary>Viewer+ (plan/06 §5.1). Idempotent: a second ACK logs nothing new.</summary>
    Task<Result<AlertSnapshot>> AcknowledgeAsync(
        CallerContext acting, Guid alertId, CancellationToken ct = default);

    /// <summary>TenantAdmin (07 §5). `reason` blank = "manual".</summary>
    Task<Result<AlertSnapshot>> ResolveAsync(
        CallerContext acting, Guid alertId, string? reason, CancellationToken ct = default);
}
