namespace Nabd.IsolationSuite;

using Npgsql;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;
using Xunit;
using Xunit.Abstractions;

// P3 spec 04 — the batch store's laws at the REAL DB seam (Testcontainers +
// real migration files, per the fixture's law: never a paraphrase). Seeding is
// the owner lane (test bookkeeping, same class as the fixture's ALTER ROLE);
// every WRITE goes through the real store on the APP lane — nabd_app with the
// 0018 grants, so a missing grant fails these facts, not silently passes.

public sealed class TelemetryStoreTests(PostgresFixture pg, ITestOutputHelper out_) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Base = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    // ---- seed helper (owner lane): tenant → template → asset → device ---------
    private async Task<Guid> SeedDeviceAsync()
    {
        var tenant = Guid.NewGuid();
        var template = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var device = Guid.NewGuid();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var batch = new NpgsqlCommand("""
            INSERT INTO identity.tenants (id, name) VALUES (@t, 'store-seed');
            INSERT INTO assets.asset_type_templates (id, key, name_ar, name_en) VALUES (@tpl, @tk, 'قالب', 'Store Template');
            INSERT INTO assets.assets (id, tenant_id, template_id, name) VALUES (@a, @t, @tpl, 'Store Asset');
            INSERT INTO assets.devices (id, tenant_id, asset_id, template_id, name, token_hash)
                VALUES (@d, @t, @a, @tpl, 'Store Device', 'store-seed-hash');
            """, conn);
        batch.Parameters.AddWithValue("t", tenant);
        batch.Parameters.AddWithValue("tpl", template);
        batch.Parameters.AddWithValue("tk", $"st-{Guid.NewGuid():N}");
        batch.Parameters.AddWithValue("a", asset);
        batch.Parameters.AddWithValue("d", device);
        await batch.ExecuteNonQueryAsync();
        return device;
    }

    private static ReadingBatchDto Batch(Guid device, params (string Channel, int N, double Value, long Seq)[] readings)
        => new(Guid.NewGuid(), device, readings.Select(r =>
            new ReadingDto(r.Channel, Base.AddSeconds(r.N), r.Value, r.Seq, ReceivedAt)).ToList());

    private static readonly DateTimeOffset ReceivedAt = Base.AddSeconds(1);

    private PostgresReadingsStore Store(int batchSize = 500) => new(new TelemetryStoreDb(), batchSize);

    // ---- fact 1: IDEMPOTENCY CORE — same batch stored 10× = one row set --------

    [Fact]
    public async Task the_same_batch_stored_ten_times_lands_exactly_one_row_set()
    {
        var device = await SeedDeviceAsync();
        var batch = Batch(device, ("temp", 0, 22.5, 7), ("hum", 1, 61.0, 7));

        for (var i = 0; i < 10; i++)
        {
            await Store().StoreBatchAsync(batch);
        }

        Assert.Equal(2, await RowCount(device));    // 10 replays, 2 rows — ON CONFLICT DO NOTHING is the whole law
        Assert.Equal(22.5, await SingleValue(device, "temp"));
    }

    // ---- fact 2: MULTI-ROW SHAPE — N channels × M readings = M right rows ------

    [Fact]
    public async Task a_multi_reading_batch_lands_as_rows_with_every_column_right()
    {
        var device = await SeedDeviceAsync();
        var batch = Batch(device, ("temp", 0, 22.5, 1), ("temp", 1, 23.0, 2), ("hum", 2, 61.0, 3));
        var tenantId = batch.TenantId;

        await Store().StoreBatchAsync(batch);

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, channel_key, time, value, seq, received_at
            FROM telemetry.readings WHERE device_id = @d ORDER BY time
            """, conn);
        cmd.Parameters.AddWithValue("d", device);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid Tenant, string Ch, DateTimeOffset T, double V, long Seq, DateTimeOffset Recv)>();
        while (await r.ReadAsync())
        {
            rows.Add((r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetDouble(3), r.GetInt64(4), r.GetFieldValue<DateTimeOffset>(5)));
        }

        Assert.Equal(3, rows.Count);
        Assert.All(rows, x => Assert.Equal(tenantId, x.Tenant));
        Assert.All(rows, x => Assert.Equal(ReceivedAt, x.Recv));
        Assert.Equal(("temp", 22.5, 1L), (rows[0].Ch, rows[0].V, rows[0].Seq));
        Assert.Equal(("hum", 61.0, 3L), (rows[2].Ch, rows[2].V, rows[2].Seq));
    }

    // ---- fact 3: seq=0 COLLAPSE — NULLS NOT DISTINCT law at the seam -----------

    [Fact]
    public async Task seq0_rows_with_identical_device_channel_time_collapse_to_one()
    {
        var device = await SeedDeviceAsync();
        var batch = Batch(device, ("temp", 0, 22.5, 0), ("temp", 0, 99.0, 0));   // same time, seq=0, different values

        await Store().StoreBatchAsync(batch);
        await Store().StoreBatchAsync(batch);

        // Same (device, channel, time, seq=0) → the NULLS NOT DISTINCT key sees ONE
        // row; the second insert is a silent no-op (HTTP-style seq-omitted rows).
        Assert.Equal(1, await RowCount(device));
    }

    // ---- fact 4: THE KNOB — batch size chunks transparently ---------------------

    [Fact]
    public async Task the_batch_size_knob_chunks_without_losing_rows()
    {
        var device = await SeedDeviceAsync();
        var batch = Batch(device,
            ("temp", 0, 1.0, 1), ("temp", 1, 2.0, 2), ("temp", 2, 3.0, 3),
            ("temp", 3, 4.0, 4), ("temp", 4, 5.0, 5));

        await Store(batchSize: 2).StoreBatchAsync(batch);   // 5 readings, chunks of 2 → 3 INSERTs

        Assert.Equal(5, await RowCount(device));            // chunking is invisible above the seam
    }

    // ---- fact 5: DUPLICATE-FLOOD SHAPE — the ON CONFLICT path, timed ------------

    [Fact]
    public async Task the_duplicate_flood_path_is_measured_not_tuned()
    {
        var device = await SeedDeviceAsync();
        var batch = Batch(device, ("temp", 0, 22.5, 1), ("hum", 1, 61.0, 1), ("door", 2, 1.0, 1));

        // clean run (3 inserts) × 20, then replay run (all conflicts) × 200
        var clean = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            await Store().StoreBatchAsync(batch);
        }
        clean.Stop();

        var replay = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            await Store().StoreBatchAsync(batch);
        }
        replay.Stop();

        out_.WriteLine($"[measure] clean 3-ins: {clean.ElapsedMilliseconds} ms /20 runs; " +
                       $"replay all-conflict: {replay.ElapsedMilliseconds} ms /200 runs " +
                       $"({(double)replay.ElapsedMilliseconds / 200:F3} ms/run vs {(double)clean.ElapsedMilliseconds / 20:F3})");
        Assert.Equal(3, await RowCount(device));   // 220 calls, 3 rows — measurement rides a correctness fact
    }

    // ---- fact 6: FLUSH WINDOW — 100 marks inside one window = ONE flush ---------

    [Fact]
    public async Task one_hundred_marks_inside_one_window_flush_as_one_bulk_upsert()
    {
        var device = await SeedDeviceAsync();
        var tracker = new DeviceStatusTracker(new TelemetryStoreDb());

        for (var i = 0; i < 100; i++)
        {
            await tracker.MarkSeenAsync(device, Guid.NewGuid(), Base.AddSeconds(i), Base.AddSeconds(i));
        }
        await tracker.FlushNowAsync();  // one window edge

        Assert.Equal(1, tracker.FlushCount);
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT last_seen_at FROM telemetry.device_status WHERE device_id = @d", conn);
        cmd.Parameters.AddWithValue("d", device);
        var seen = (DateTimeOffset)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(Base.AddSeconds(99), seen);    // the LATEST mark won the buffer
    }

    // ---- fact 7: GREATEST guard — an out-of-order mark never regresses ----------

    [Fact]
    public async Task an_out_of_order_mark_never_regresses_last_seen_at()
    {
        var device = await SeedDeviceAsync();
        var tracker = new DeviceStatusTracker(new TelemetryStoreDb());

        await tracker.MarkSeenAsync(device, Guid.NewGuid(), Base, Base);
        await tracker.FlushNowAsync();                      // window 1: seen = Base
        await tracker.MarkSeenAsync(device, Guid.NewGuid(), Base.AddSeconds(-5), Base.AddSeconds(-5));  // OLDER
        await tracker.FlushNowAsync();                      // window 2: regression attempt

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT last_seen_at FROM telemetry.device_status WHERE device_id = @d", conn);
        cmd.Parameters.AddWithValue("d", device);
        var seen = (DateTimeOffset)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(Base, seen);   // GREATEST() held the line
        Assert.Equal(2, tracker.FlushCount);
    }

    // ---- helpers ----------------------------------------------------------------

    private async Task<long> RowCount(Guid device)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM telemetry.readings WHERE device_id = @d", conn);
        cmd.Parameters.AddWithValue("d", device);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<double> SingleValue(Guid device, string channel)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT value FROM telemetry.readings WHERE device_id = @d AND channel_key = @c", conn);
        cmd.Parameters.AddWithValue("d", device);
        cmd.Parameters.AddWithValue("c", channel);
        return (double)(await cmd.ExecuteScalarAsync())!;
    }
}
