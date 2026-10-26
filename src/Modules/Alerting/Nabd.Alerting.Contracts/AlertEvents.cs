using Nabd.SharedKernel;
namespace Nabd.Alerting.Contracts;

/// <summary>Alert lifecycle events (03 §3; 06 §5 OPEN→ACK→RESOLVED). Notifications + Realtime listen.</summary>
public sealed record AlertRaised(
    Guid TenantId, Guid AlertId, Guid AssetId, Guid RuleId,
    string Severity, string Title, string TitleEn, DateTimeOffset RaisedAtUtc) : IntegrationEvent;

public sealed record AlertAcknowledged(Guid TenantId, Guid AlertId, Guid UserId, DateTimeOffset AtUtc) : IntegrationEvent;

public sealed record AlertResolved(Guid TenantId, Guid AlertId, DateTimeOffset AtUtc) : IntegrationEvent;
