using System.Collections.Concurrent;
using Nabd.Assets.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Assets;

/// <summary>
/// Assets module marker (03 §3): assets, devices, device types/templates, token issue/revoke.
/// Offers: IDeviceRegistry, IChannelTemplateLookup. Emits: AssetAdded, DeviceRegistered, DeviceTokenRevoked.
/// P0 carries the InMemoryDeviceRegistry stub (the DoD's hello subject); P2 fills the rest (FR-15…17).
/// </summary>
public static class AssetsModule
{
    public const string RoomName = "Assets";
}

/// <summary>
/// P0 stub of the device registry. In-memory on purpose: it lets the hello-module test
/// AND the API diagnostic endpoint work before the database arrives.
/// P2 replaces this with the SQL-backed implementation (FR-17, device tokens via HMAC hash — 07 §4).
/// The room boundary is already real: nothing outside tests this class directly,
/// only IDeviceRegistry (contracts) — enforced by tests/Nabd.ArchTests.
/// </summary>
public sealed class InMemoryDeviceRegistry : IDeviceRegistry
{
    private readonly ConcurrentDictionary<Guid, DeviceSnapshot> _devices = new();

    public Task<DeviceSnapshot> RegisterDeviceAsync(Guid tenantId, Guid assetId, string name, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("TenantId required", nameof(tenantId));
        if (assetId == Guid.Empty) throw new ArgumentException("AssetId required", nameof(assetId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Device name required", nameof(name));

        var device = new DeviceSnapshot(Guid.NewGuid(), tenantId, assetId, name.Trim(), IsActive: true);
        _devices[device.DeviceId] = device;
        return Task.FromResult(device);
    }

    public Task<DeviceSnapshot?> FindDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        Task.FromResult(_devices.TryGetValue(deviceId, out var snapshot) ? snapshot : null);

    internal int Count => _devices.Count;
}
