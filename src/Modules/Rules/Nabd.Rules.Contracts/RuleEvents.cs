using Nabd.SharedKernel;
using Nabd.TelemetryStore.Contracts;

namespace Nabd.Rules.Contracts;

/// <summary>
/// One crossing per violation episode — emitted once, re-armed on restore
/// (plan/06 §3.2/§3.4). The payload is the blueprint's own shape:
/// CrossingDetected(rule, reading). The reading travels INSIDE the event
/// because Alerting must open the alert with the observed value, and Rules may
/// not read the readings table (the wall).
/// </summary>
public sealed record CrossingDetected(RuleSnapshot Rule, CommittedReading Reading) : IntegrationEvent;

/// <summary>
/// The episode ended after a FIRED crossing; Alerting may auto-resolve
/// (plan/06 §5.3, spec 05). A silent spike never emits this: nothing was opened,
/// so there is nothing to restore.
/// </summary>
public sealed record ConditionRestored(RuleSnapshot Rule, CommittedReading Reading) : IntegrationEvent;

/// <summary>
/// The offline watchdog's verdict (P4 spec 07, plan/06 §4): the silence passed the
/// device's own window. `LastSeenAtUtc` is when the device was last heard; the gap
/// is DetectedAtUtc - LastSeenAtUtc. The snapshot rides whole, so Alerting opens the
/// alert from the event and never reads the Rules schema (D7d).
/// </summary>
public sealed record OfflineDetected(
    RuleSnapshot Rule,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset DetectedAtUtc) : IntegrationEvent;

/// <summary>
/// The device spoke again after a FIRED offline detection (plan/06 §5.3): Alerting
/// auto-resolves the open alert with the TRUE gap (last seen before → first seen
/// after). A device that never fired produces no event — nothing was opened.
/// </summary>
public sealed record OnlineRestored(
    RuleSnapshot Rule,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset SeenAgainAtUtc) : IntegrationEvent;
