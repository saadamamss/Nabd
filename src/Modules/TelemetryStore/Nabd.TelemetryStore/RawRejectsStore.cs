namespace Nabd.TelemetryStore;

using Nabd.TelemetryStore.Contracts;

/// <summary>
/// RED STUB (P3 spec 06 scaffolding): the rejection-forensics writer — one
/// parameterized INSERT into telemetry.raw_rejects (0019), append-only by
/// grant (INSERT+SELECT, no UPDATE/DELETE). The real one is ~15 lines; the
/// wall is that it NEVER swallows a failure: a raw-rejects failure on the
/// MQTT path propagates like the readings store's does (no ack → redelivery).
/// </summary>
public sealed class RawRejectsStore(TelemetryStoreDb db) : IRawRejectStore
{
    public Task StoreAsync(RawRejectDto reject, CancellationToken ct = default)
    {
        _ = db;
        throw new NotImplementedException("P3 spec 06: owner implements from study/implementation-code.md");
    }
}
