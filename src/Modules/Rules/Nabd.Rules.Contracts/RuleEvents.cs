using Nabd.SharedKernel;
namespace Nabd.Rules.Contracts;

/// <summary>Rules room's outbound events (03 §3). Alerting listens; dedup/cooldown happen there (06 §5).</summary>
public sealed record CrossingDetected(
    Guid TenantId, Guid RuleId, Guid AssetId, Guid DeviceId,
    string ChannelKey, double ThresholdValue, string Direction, DateTimeOffset TimeUtc) : IntegrationEvent;

public sealed record OfflineDetected(
    Guid TenantId, Guid RuleId, Guid AssetId, Guid DeviceId, DateTimeOffset TimeUtc) : IntegrationEvent;
