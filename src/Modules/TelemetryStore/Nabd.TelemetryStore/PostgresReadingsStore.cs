namespace Nabd.TelemetryStore;

using Nabd.TelemetryStore.Contracts;

/// <summary>
/// The real batch writer (P3 spec 04, FR-23 + NFR-04). Each chunk is ONE
/// multi-row parameterized INSERT … SELECT FROM unnest(…) with
/// ON CONFLICT (device_id, channel_key, time, seq) DO NOTHING — 0003's
/// readings_identity_uq (NULLS NOT DISTINCT). Idempotent by construction:
/// retries and broker redeliveries are free. COPY is deliberately NOT used —
/// it cannot skip duplicates (plan/05 §6). The batch-size knob
/// (Ingestion:BatchSize, default 500 — plan/09 §4.2) chunks transparently;
/// measure later, don't tune now.
/// </summary>
public sealed class PostgresReadingsStore(TelemetryStoreDb db, int batchSize = 500) : IReadingsStore
{
    public async Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        foreach (var chunk in batch.Readings.Chunk(batchSize))
        {
            // ONE statement per chunk = its own atomic commit. Ack (the caller's
            // job) fires only after this method returns — i.e., after EVERY chunk
            // committed. A crash between commit and ack = redelivery = no-op here.
            await using var cmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO telemetry.readings
                    (time, received_at, tenant_id, device_id, channel_key, value, seq)
                SELECT * FROM unnest(
                    @times::timestamptz[], @received::timestamptz[], @tenants::uuid[],
                    @devices::uuid[], @channels::text[], @values::float8[], @seqs::bigint[])
                ON CONFLICT (device_id, channel_key, time, seq) DO NOTHING
                """, conn);

            cmd.Parameters.AddWithValue("times", chunk.Select(r => r.TimeUtc).ToArray());
            cmd.Parameters.AddWithValue("received", chunk.Select(r => r.ReceivedAtUtc).ToArray());
            cmd.Parameters.AddWithValue("tenants", chunk.Select(_ => batch.TenantId).ToArray());
            cmd.Parameters.AddWithValue("devices", chunk.Select(_ => batch.DeviceId).ToArray());
            cmd.Parameters.AddWithValue("channels", chunk.Select(r => r.ChannelKey).ToArray());
            cmd.Parameters.AddWithValue("values", chunk.Select(r => r.Value).ToArray());
            cmd.Parameters.AddWithValue("seqs", chunk.Select(r => r.Seq).ToArray());

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public Task<IReadOnlyList<ReadingValueDto>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        // History reads are a later spec — the write path is spec 04's scope.
        throw new NotImplementedException("History reads are a later spec.");
    }
}
