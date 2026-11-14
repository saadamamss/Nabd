namespace Nabd.TelemetryStore;

using Nabd.TelemetryStore.Contracts;

/// <summary>
/// RED STUB (P3 spec 04 scaffolding): the real batch writer — the owner's
/// implementation replaces this from study/implementation-code.md. The real one
/// writes each chunk as ONE multi-row parameterized
/// INSERT … SELECT FROM unnest(…) ON CONFLICT (device_id, channel_key, time, seq)
/// DO NOTHING (0003's readings_identity_uq — idempotent by construction; retries
/// and redeliveries are free). COPY is deliberately not used: it cannot skip
/// duplicates (plan/05 §6).
/// </summary>
public sealed class PostgresReadingsStore(TelemetryStoreDb db, int batchSize = 500) : IReadingsStore
{
    public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
    {
        _ = db; _ = batchSize;
        throw new NotImplementedException("P3 spec 04: owner implements from study/implementation-code.md");
    }

    public Task<IReadOnlyList<ReadingValueDto>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        throw new NotImplementedException("History reads are a later spec — the write path is spec 04's scope.");
    }
}
