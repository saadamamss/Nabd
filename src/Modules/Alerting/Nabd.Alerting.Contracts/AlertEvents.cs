using Nabd.SharedKernel;

namespace Nabd.Alerting.Contracts;

/// <summary>
/// P4 spec 05 (plan/06 §5.1): the fan-out event. It carries the WHOLE stored row
/// (snapshot) so Notifications (spec 08) and Realtime never read another room's
/// schema — and so the P0 placeholder's scattered fields cannot drift again.
/// Published ONLY after the alert row committed (no receipt before the durable outcome).
/// </summary>
public sealed record AlertRaised(AlertSnapshot Alert) : IntegrationEvent;

/// <summary>ACK recorded: who and when (plan/06 §5.1).</summary>
public sealed record AlertAcknowledged(
    Guid TenantId, Guid AlertId, Guid UserId, DateTimeOffset AtUtc) : IntegrationEvent;

/// <summary>
/// RESOLVED (manual or §5.3 auto). `Reason` is the documented why. `Alert` is the row AFTER
/// resolution — the same law as AlertRaised: a subscriber must never read the Alerting schema
/// to learn what it is notifying about (spec 08's resolve email).
/// </summary>
public sealed record AlertResolved(
    Guid TenantId, Guid AlertId, DateTimeOffset AtUtc, string Reason,
    AlertSnapshot Alert) : IntegrationEvent;
