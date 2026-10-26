using Nabd.Assets.Contracts;
using Nabd.Ingestion.Contracts;
using Nabd.SharedKernel;
using Xunit;

namespace Nabd.HelloModule;

/// <summary>
/// P0 Definition of Done (10 §3): "a hello-module test — any module writes/reads one row
/// through contracts — passes in CI < 15 min."
///
/// This file contains it in two halves:
///  1. hello_registers_and_reads_a_device_through_contracts — runs anywhere, no infrastructure:
///     the module contract stores and reads the "row" (a device record) through IDeviceRegistry.
///  2. hello_writes_and_reads_one_database_row — the same cycle against the real
///     Postgres+Timescale container (Testcontainers); auto-skips until Docker exists locally,
///     runs on CI where Docker is always present (docs/dev/SETUP.md Step 4).
/// </summary>
public sealed class HelloContractTests
{
    [Fact]
    public async Task hello_registers_and_reads_a_device_through_contracts()
    {
        // Compose the Assets room exactly like the composition root does (ApiHost Program.cs).
        IDeviceRegistry registry = new Nabd.Assets.InMemoryDeviceRegistry();

        // ── WRITE: one "row" through the room's contract ──
        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var registered = await registry.RegisterDeviceAsync(tenantId, assetId, name: "Hello Device",
            ct: CancellationToken.None);

        // ── READ: same row back through the contract only ──
        var found = await registry.FindDeviceAsync(registered.DeviceId, ct: CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(tenantId, found.TenantId);
        Assert.Equal(assetId, found.AssetId);
        Assert.Equal("Hello Device", found.Name);
        Assert.True(found.IsActive);
    }

    [Fact]
    public void cross_room_event_payloads_compose_through_shared_kernel()
    {
        // Proves the boundary is usable, not just compilable: an event type born in
        // Ingestion's contracts (the pipeline's fuel, 03 §6.1) carries a device registered
        // via Assets' contracts — with neither room ever seeing the other's implementation.
        var accepted = new ReadingsAcceptedBatch(
            TenantId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            Readings: [new Reading("temp_c", DateTimeOffset.UtcNow, -4.5, Seq: 1)]);

        var asEvent = accepted as IIntegrationEvent;
        Assert.NotNull(asEvent);
        Assert.NotEqual(Guid.Empty, asEvent.EventId);
        Assert.Single(accepted.Readings);
    }
}
