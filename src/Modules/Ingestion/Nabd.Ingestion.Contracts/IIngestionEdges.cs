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
