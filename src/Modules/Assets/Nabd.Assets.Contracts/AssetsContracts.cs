using Nabd.SharedKernel;
namespace Nabd.Assets.Contracts;

/// <summary>Assets room's outbound events (03 §3).</summary>
public sealed record AssetAdded(Guid TenantId, Guid AssetId, Guid AssetTypeIdTemplate, string Name) : IntegrationEvent;

public sealed record DeviceRegistered(Guid TenantId, Guid DeviceId, Guid AssetId, string Name) : IntegrationEvent;

/// <summary>Emitted on revoke — the one event Ingestion listens for (05 §5: revocation must take effect fast).</summary>
public sealed record DeviceTokenRevoked(Guid TenantId, Guid DeviceId, DateTimeOffset RevokedAtUtc) : IntegrationEvent;

public sealed record DeviceSnapshot(Guid DeviceId, Guid TenantId, Guid AssetId, string Name, bool IsActive);

public sealed record ChannelDefinitionDto(string Key, string Name, string Unit, string DataType);

/// <summary>
/// What other rooms may ASK of Assets. The hello-module test (tests/Nabd.HelloModule)
/// proves its contract works with zero infrastructure.
/// </summary>
public interface IDeviceRegistry
{
    Task<DeviceSnapshot> RegisterDeviceAsync(Guid tenantId, Guid assetId, string name, CancellationToken ct = default);

    Task<DeviceSnapshot?> FindDeviceAsync(Guid deviceId, CancellationToken ct = default);
}

/// <summary>Channel templates power the generic template mechanism (04 §5, acceptance #9).</summary>
public interface IChannelTemplateLookup
{
    Task<IReadOnlyList<ChannelDefinitionDto>> GetChannelsForTemplateAsync(Guid assetTypeId, CancellationToken ct = default);
}
