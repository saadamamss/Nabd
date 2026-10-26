namespace Nabd.TelemetryStore;

/// <summary>
/// TelemetryStore module (03 §3): readings hypertable, aggregates, retention jobs,
/// device_status (last-seen flushed in batches — never one UPDATE per reading, 04 §6).
/// P0: empty on purpose. P3 fills it: the batch writer is the heart of NFR-01/02.
/// Listens: ReadingsAccepted. Offers: IReadingsStore.
/// </summary>
public static class TelemetryStoreModule
{
    public const string RoomName = "TelemetryStore";
}
