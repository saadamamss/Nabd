namespace Nabd.IsolationSuite;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;
using Npgsql;
using Xunit;

// P3 spec 06 — the durability net (plan/05 §7: NOTHING silently dropped, the
// VETO-shaped destination): (a) every rejection reason increments its counter;
// (b) a malformed-but-AUTHENTICATED MQTT batch lands in telemetry.raw_rejects
// WITH its raw bytes + reason — at the REAL DB seam (Testcontainers + real
// migration files, 0019 included); (c) the forensics outlive the credential
// (revocation never erases them); (d) the health/ready JSON carries the
// counters (through the HTTP host, HttpIngestWallTests shares that door).

public sealed class DurabilityNetTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly byte[] Raw = Encoding.UTF8.GetBytes("""{"v":1,"ts":"NOT","seq":"junk","rr":}""");

    // ---- fact 1: every rejection reason increments ITS counter -----------------

    [Fact]
    public void every_rejection_reason_increments_its_counter()
    {
        var counters = new RejectionCounters();

        counters.Increment(IngestionErrors.E422_RANGE);
        counters.Increment(IngestionErrors.E422_RANGE);
        counters.Increment(IngestionErrors.DLQ_INVALID);

        var snap = counters.Snapshot();
        Assert.Equal(2, snap[IngestionErrors.E422_RANGE]);
        Assert.Equal(1, snap[IngestionErrors.DLQ_INVALID]);
    }

    // ---- fact 2: a malformed-but-authenticated batch lands RAW, never silent;
    // ---- and an ACCEPTED batch that cannot store fails LOUD (transient abort,
    // ---- zero acks) — the ack law through the pump, not a thrown sink ----------

    [Fact]
    public async Task a_malformed_but_authenticated_mqtt_batch_lands_in_raw_rejects_with_its_raw_bytes()
    {
        var (device, tenant) = await SeedDeviceAsync();
        var rawRejects = new RawRejectsStore(new TelemetryStoreDb());
        var readings = new ThrowingReadingsStore();   // the store NEVER answers in this fact
        var (buffer, pump, acks, tracker) = PumpWithRealRawStore(
            rawRejects, readings,
            rows: new DeviceCatalogRow(device, tenant, Keys("temp")));

        // The device is AUTHENTICATED (its identity resolved — the resolver's
        // answer), then the parse fails: the DLQ_INVALID-class shape.
        var received = DateTimeOffset.UtcNow;
        buffer.Offer(new IngestEnvelope(device, $"t/x/dev/{device}/v1", Raw, received), acks.Ack);
        Assert.Equal(FlushOutcome.Completed, await pump.FlushOnceAsync(CancellationToken.None));

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT reason, payload, device_id, received_at
            FROM telemetry.raw_rejects WHERE device_id = @d
            """, conn);
        cmd.Parameters.AddWithValue("d", device);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "the rejection MUST have a destination — no silent drop");
        Assert.Equal(IngestionErrors.DLQ_INVALID, r.GetString(0));
        Assert.Equal(Raw, r.GetFieldValue<byte[]>(1));           // the raw bytes, verbatim
        Assert.Equal(device, r.GetGuid(2));
        Assert.Equal(received, r.GetFieldValue<DateTimeOffset>(3).ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.False(await r.ReadAsync());

        Assert.Empty(tracker.Marks);                             // presence untouched, same law
        Assert.Equal(1, acks.Count);                             // the reject was receipted AND acked

        // an ACCEPTED batch that cannot store fails LOUD: abort-all, zero NEW
        // acks, the snapshot re-queued (R10) — the loud half of the old sink law
        buffer.Offer(Envelope(device, Payload("2026-11-14T12:00:00Z", 1, (22.5, "temp"))), acks.Ack);
        Assert.Equal(FlushOutcome.TransientAbort, await pump.FlushOnceAsync(CancellationToken.None));
        Assert.Equal(1, acks.Count);                             // ZERO NEW acks — nothing was committed
        Assert.Equal(1, buffer.AcceptedCount);                   // back at the tail
    }

    // ---- fact 3: forensics OUTLIVE the credential ------------------------------

    [Fact]
    public async Task raw_rejects_forensics_outlive_the_credential()
    {
        var (device, tenant) = await SeedDeviceAsync();
        var rawRejects = new RawRejectsStore(new TelemetryStoreDb());
        var (buffer, pump, acks, _) = PumpWithRealRawStore(
            rawRejects, new ThrowingReadingsStore(),
            rows: new DeviceCatalogRow(device, tenant, Keys("temp")));

        buffer.Offer(new IngestEnvelope(device, "t/x/dev/y/v1", Raw, DateTimeOffset.UtcNow), acks.Ack);
        Assert.Equal(FlushOutcome.Completed, await pump.FlushOnceAsync(CancellationToken.None));

        // revoke THROUGH THE REAL lifecycle (app lane): the credential dies —
        // the forensics must NOT (no cascade, no cleanup on the reject path).
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var revoke = await devices.RevokeDeviceAsync(
            new CallerContext(Guid.NewGuid(), tenant, ["tenant_admin"]), device);
        Assert.True(revoke.IsSuccess, $"SETUP failed (revoke): {revoke.Error}");

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT r.reason, d.is_active
            FROM telemetry.raw_rejects r
            JOIN assets.devices d ON d.id = r.device_id
            WHERE r.device_id = @d
            """, conn);
        cmd.Parameters.AddWithValue("d", device);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "the forensics row must outlive the revoked credential");
        Assert.Equal(IngestionErrors.DLQ_INVALID, r.GetString(0));
        Assert.False(r.GetBoolean(1));   // the device IS revoked — and the row still joins to it
    }

    // ---- fact 4: the health/ready JSON shows the counters -----------------------
    // (lives with the door's facts — it rides the HTTP host's process counters:
    // see HttpIngestWallTests.health_ready_json_carries_the_rejection_counters)

    // ---- helpers ----------------------------------------------------------------

    /// <summary>The pump with its REAL receipt destination (the fact's seam):
    /// real RawRejectsStore over the real DB; everything else in-memory fakes.</summary>
    private static (FlushBuffer Buffer, FlushPump Pump, AckRecorder Acks, RecordingTracker Tracker)
        PumpWithRealRawStore(IRawRejectStore rawRejects, IReadingsStore readings, params DeviceCatalogRow[] rows)
    {
        var options = new FlushOptions(FlushSize: 10, FlushIntervalMs: 60_000);
        var reconcile = new ReconcileCounters();   // P4 spec 02: required by the buffer (fail-shut)
        var buffer = new FlushBuffer(options.FlushSize, reconcile);
        var acks = new AckRecorder();
        var tracker = new RecordingTracker();
        var pump = new FlushPump(
            buffer, options, new FakeResolver(rows),
            new ValidationPipeline(new StubChannels(), new StubIdentity()),
            readings, rawRejects, tracker,
            new RejectionCounters(), new FlushCounters(),
            reconcile,
            NullLogger<FlushPump>.Instance);
        return (buffer, pump, acks, tracker);
    }

    private async Task<(Guid Device, Guid Tenant)> SeedDeviceAsync()
    {
        var tenant = Guid.NewGuid();
        var template = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var device = Guid.NewGuid();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var batch = new NpgsqlCommand("""
            INSERT INTO identity.tenants (id, name) VALUES (@t, 'dur-seed');
            INSERT INTO assets.asset_type_templates (id, key, name_ar, name_en) VALUES (@tpl, @tk, 'قالب', 'Durability Template');
            INSERT INTO assets.assets (id, tenant_id, template_id, name) VALUES (@a, @t, @tpl, 'Durability Asset');
            INSERT INTO assets.devices (id, tenant_id, asset_id, template_id, name, token_hash)
                VALUES (@d, @t, @a, @tpl, 'Durability Device', @hash);
            """, conn);
        batch.Parameters.AddWithValue("t", tenant);
        batch.Parameters.AddWithValue("tpl", template);
        batch.Parameters.AddWithValue("tk", $"du-{Guid.NewGuid():N}");
        batch.Parameters.AddWithValue("a", asset);
        batch.Parameters.AddWithValue("d", device);
        batch.Parameters.AddWithValue("hash", $"dur-{Guid.NewGuid():N}");
        await batch.ExecuteNonQueryAsync();
        return (device, tenant);
    }

    private static IngestEnvelope Envelope(Guid device, string json) =>
        new(device, $"t/x/dev/{device}/v1", Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow);

    private static string Payload(string ts, long seq, params (double Value, string Channel)[] readings)
    {
        var rr = string.Join(",", readings.Select(r => $$"""{"t":{{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"c":"{{r.Channel}}"}"""));
        return $$"""{"v":1,"ts":"{{ts}}","seq":{{seq}},"rr":[{{rr}}]}""";
    }

    private sealed class ThrowingReadingsStore : IReadingsStore
    {
        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default) =>
            throw new InvalidOperationException("the accept path must NOT run in a rejection fact");

        public Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default) =>
            throw new InvalidOperationException("the accept path must NOT run in a rejection fact");
            
        public Task<IReadOnlyList<ReadingValueDto>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReadingValueDto>>([]);
    }

    private sealed class RecordingTracker : IDeviceStatusTracker
    {
        public List<(Guid Device, Guid Tenant, DateTimeOffset Seen, DateTimeOffset Event)> Marks { get; } = [];
        public Task MarkSeenAsync(Guid deviceId, Guid tenantId, DateTimeOffset lastSeenUtc, DateTimeOffset lastEventTimeUtc, CancellationToken ct = default)
        {
            Marks.Add((deviceId, tenantId, lastSeenUtc, lastEventTimeUtc));
            return Task.CompletedTask;
        }
    }

    private sealed class AckRecorder
    {
        private readonly object _lock = new();
        private int _count;
        public int Count { get { lock (_lock) return _count; } }
        public Task Ack(CancellationToken ct)
        {
            lock (_lock) _count++;
            return Task.CompletedTask;
        }
    }

    /// <summary>The set resolver's answer: ONLY the given rows come back —
    /// everything else is ABSENT (0020's uniform law).</summary>
    private sealed class FakeResolver(params DeviceCatalogRow[] rows) : IIngestionSetResolver
    {
        private readonly Dictionary<Guid, DeviceCatalogRow> _rows = rows.ToDictionary(r => r.DeviceId);
        public Task<IReadOnlyDictionary<Guid, DeviceCatalogRow>> ResolveDevicesAsync(
            IEnumerable<Guid> deviceIds, CancellationToken ct = default)
        {
            var answer = new Dictionary<Guid, DeviceCatalogRow>();
            foreach (var id in deviceIds)
                if (_rows.TryGetValue(id, out var row)) answer[id] = row;
            return Task.FromResult<IReadOnlyDictionary<Guid, DeviceCatalogRow>>(answer);
        }
    }

    // The pump's direct core never consults these — stubs satisfy the ctor only.
    private sealed class StubChannels : IDeviceChannels
    {
        public Task<IReadOnlySet<string>?> GetChannelKeysAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>?>(null);
    }

    private sealed class StubIdentity : IIngestionIdentity
    {
        public Task<Guid?> ResolveTenantIdAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);
    }

    private static IReadOnlySet<string> Keys(params string[] k) => new HashSet<string>(k);
}
