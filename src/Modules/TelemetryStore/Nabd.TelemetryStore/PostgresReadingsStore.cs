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

    public async Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default)
    {
        if (readings.Count == 0) return;

        try
        {
            // The open is INSIDE the classification scope too: a dead port /
            // unreachable host throws from HERE (the driver's NpgsqlException),
            // and the wall law says Ingestion never sees an Npgsql type — so
            // connection failures wrap exactly like statement failures (R10).
            await using var conn = await db.OpenConnectionAsync(ct);
            foreach (var chunk in readings.Chunk(batchSize))
            {
                // ONE statement per chunk = its own atomic commit — the SAME
                // unnest pattern StoreBatchAsync proved (spec 04), widened to
                // per-row tenants/devices (a flush spans both). DO NOTHING
                // keeps it idempotent: retries and redeliveries are free.
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
                cmd.Parameters.AddWithValue("tenants", chunk.Select(r => r.TenantId).ToArray());
                cmd.Parameters.AddWithValue("devices", chunk.Select(r => r.DeviceId).ToArray());
                cmd.Parameters.AddWithValue("channels", chunk.Select(r => r.ChannelKey).ToArray());
                cmd.Parameters.AddWithValue("values", chunk.Select(r => r.Value).ToArray());
                cmd.Parameters.AddWithValue("seqs", chunk.Select(r => r.Seq).ToArray());

                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;   // cooperative shutdown — NEVER wrapped, NEVER a verdict:
                     // the pump's shutdown path must tell cancel apart from fail
        }
        catch (Npgsql.PostgresException pge)
        {
            // THE one classification point (R10): class 22 data exceptions +
            // 23514 check violations are the only deterministic row-content
            // failures; everything else is transient (abort-all, re-buffer).
            throw new StoreFlushException(pge.SqlState, IsDeterministicSqlState(pge.SqlState), pge);
        }
        catch (Npgsql.NpgsqlException e)
        {
            // driver/connection failure — no SQLSTATE exists; transient by
            // definition (an unknown error is transient, never a receipt)
            throw new StoreFlushException(string.Empty, isDeterministic: false, e);
        }
    }

    /// <summary>
    /// R10's deterministic set: class 22 (data exceptions — 22003 overflow,
    /// 22008, 22P02, …) + 23514 (check violation). NOT deterministic: 23503
    /// (missing device — the NEXT flush re-resolves it away, R3), 40001,
    /// 53100, and everything unknown.
    /// </summary>
    internal static bool IsDeterministicSqlState(string sqlState) =>
        sqlState.StartsWith("22", StringComparison.Ordinal) || sqlState == "23514";
}
