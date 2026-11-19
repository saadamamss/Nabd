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

/// <summary>Offline watchdog verdict (plan/06 §4 — spec 07's lane; shape unchanged).</summary>
public sealed record OfflineDetected(
    Guid TenantId, Guid RuleId, Guid AssetId, Guid DeviceId, DateTimeOffset TimeUtc) : IntegrationEvent;
