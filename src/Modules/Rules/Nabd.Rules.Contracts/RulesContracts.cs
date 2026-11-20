namespace Nabd.Rules.Contracts;

using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

/// <summary>
/// The Rules room's CRUD contract (P4 spec 03, FR-30/D1). The tenant filter is
/// asserted in the SERVICE (07 §5 layer 2) and AGAIN in the DB (0021 RLS) —
/// two layers, one law. `offline` is refused here BY LAW: those rows belong to
/// the device-offline flow (spec 06), not the user's hands.
/// </summary>
public interface IDeviceRules
{
    Task<Result<RuleSnapshot>> CreateRuleAsync(
        CallerContext acting, RuleCreateRequest request, CancellationToken ct = default);

    Task<Result<IReadOnlyList<RuleSnapshot>>> ListByDeviceAsync(
        CallerContext acting, Guid deviceId, CancellationToken ct = default);

    Task<Result<RuleSnapshot>> UpdateRuleAsync(
        CallerContext acting, Guid deviceId, Guid ruleId, RuleUpdateRequest request,
        CancellationToken ct = default);

    Task<Result<bool>> DeleteRuleAsync(
        CallerContext acting, Guid deviceId, Guid ruleId, CancellationToken ct = default);
}

/// <summary>
/// A weekly quiet range (plan/06 §5.4, owner D6a): UTC, day 0=Sunday…6=Saturday,
/// minutes-of-day, START INCLUSIVE / END EXCLUSIVE, start &lt; end (no midnight wrap in MVP).
/// </summary>
public sealed record MuteWindow(int DayOfWeek, int StartMinute, int EndMinute);

/// <summary>The honest shape of one rule (plan/04 §7 columns, verbatim).</summary>
public sealed record RuleSnapshot(
    Guid RuleId,
    Guid TenantId,
    Guid DeviceId,
    string? ChannelKey,
    string RuleType,
    double? ThresholdLow,
    double? ThresholdHigh,
    int DurationSeconds,
    int CooldownMinutes,
    string Severity,
    bool Enabled,
    /// <summary>The rule's mute windows AT SNAPSHOT TIME (D6d) — null means "not loaded",
    /// never "none"; an empty list is a real "no windows".</summary>
    IReadOnlyList<MuteWindow>? MuteWindows = null);

/// <summary>POST body. Defaults ride plan/06 §8 (60 / 60 / warn / enabled).</summary>
public sealed record RuleCreateRequest(
    Guid TenantId,
    Guid DeviceId,
    string? ChannelKey,
    string RuleType,
    double? ThresholdLow = null,
    double? ThresholdHigh = null,
    int DurationSeconds = 60,
    int CooldownMinutes = 60,
    string Severity = "warn");

/// <summary>PATCH body: only the legs present move; type/device/channel never do.</summary>
public sealed record RuleUpdateRequest(
    double? ThresholdLow = null,
    double? ThresholdHigh = null,
    int? DurationSeconds = null,
    int? CooldownMinutes = null,
    string? Severity = null,
    bool? Enabled = null,
    IReadOnlyList<MuteWindow>? MuteWindows = null);

public static class RuleErrors
{
    public const string RuleNotFound = "rules:rule_not_found";
    public const string DeviceNotFound = "rules:device_not_found";
    public const string OfflineRefused = "rules:offline_refused";
    public const string DuplicateRule = "rules:duplicate_rule";
    public const string InvalidRule = "rules:invalid_rule";
    public const string InvalidMuteWindow = "rules:invalid_mute_window";
    public const string MuteWindowOverlap = "rules:mute_window_overlap";
}
