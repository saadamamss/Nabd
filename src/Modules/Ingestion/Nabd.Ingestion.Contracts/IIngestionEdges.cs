namespace Nabd.Ingestion.Contracts;

/// <summary>
/// The pipeline's channel-validation edge (stage 4): the lawful channel keys of
/// ONE device, read from its template's channel_definitions. An edge, not core —
/// the DB lookup is injected so the pure pipeline stays testable without
/// Postgres (03 §4). Unknown device → null (the pipeline treats that as
/// E422_UNKNOWN_CHANNEL-class rejection; the doors normally catch it first).
/// </summary>
public interface IDeviceChannels
{
    /// <summary>The device's lawful channel keys, or null when the device is unknown/unactive.</summary>
    Task<IReadOnlySet<string>?> GetChannelKeysAsync(Guid deviceId, CancellationToken ct = default);
}

/// <summary>
/// The pipeline's identity-fill edge (stage 5): device → its tenant. The MQTT
/// path's identity is already proven by the doors (stage 1/2 happened there);
/// what the pipeline still needs is the tenant each row belongs to. The HTTP
/// path resolves device+tenant from its token at its own door (spec 05) and
/// hands both to the pipeline the same way.
/// </summary>
public interface IIngestionIdentity
{
    /// <summary>The device's tenant, or null when the device is unknown.</summary>
    Task<Guid?> ResolveTenantIdAsync(Guid deviceId, CancellationToken ct = default);
}


/// <summary>
/// ONE resolved device as the set-based edge answers (P4 spec 01, 0020):
/// the tenant plus the template's lawful channel keys. An EMPTY keys set is
/// a live device whose template has no channels — the flush treats it
/// UNIFORMLY with a missing row (E422-class; anti-enumeration, 0017's
/// coalesce-'{}' law, set-shaped).
/// </summary>
public sealed record DeviceCatalogRow(Guid DeviceId, Guid TenantId, IReadOnlySet<string> ChannelKeys);

/// <summary>
/// The flush path's identity edge (P4 spec 01, Move B): ONE set-based call
/// resolves EVERY device in a flush (0020's third twin of the narrow-exception
/// law — 0014/0016/0017's shape: SECURITY DEFINER, the RLS wall absolute for
/// direct access). Missing from the returned map = unknown / revoked /
/// deactivated — read UNIFORMLY (per-message E422 receipt, no existence
/// hints). Resolution is per flush, NO cache (R3): revocation propagation
/// stays fresh (≪ 60 s bound).
/// </summary>
public interface IIngestionSetResolver
{
    /// <summary>Resolve many devices in ONE call. Unknown/revoked devices are simply absent from the map.</summary>
    Task<IReadOnlyDictionary<Guid, DeviceCatalogRow>> ResolveDevicesAsync(IEnumerable<Guid> deviceIds, CancellationToken ct = default);
}