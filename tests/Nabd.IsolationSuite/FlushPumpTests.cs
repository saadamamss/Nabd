namespace Nabd.IsolationSuite;

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Nabd.SharedKernel;
using Nabd.TelemetryStore.Contracts;
using Xunit;

// P4 spec 01 — the flush pump's LAWS, made executable (the retired sink's net
// grew into this): (a) ONE flush = ONE resolution (fact 6) + one multi-row
// INSERT (Move C) + acks ONLY after the outcome commits (the ack law, fact 1);
// (b) per-flush isolation — a bad message dies ALONE (facts 7/8), never
// poisoning its neighbors; (c) R10's classification — ANY transient/unknown
// failure aborts ALL: zero acks, zero NEW receipts, the snapshot back at the
// buffer's tail, retried with backoff (facts 4/13/14); (d) the R6 poison
// ladder descends ONLY on deterministic row-content failures, and its floor
// receipt+acks a poison message (stored-or-receipted, never looped, fact 11);
// (e) the consumer's wall is STRUCTURAL: no port to the store, the resolver,
// or the receipts table reaches its constructor (fact 10) — and the flow
// window rides the CONNECT's Receive Maximum = FlushSize (facts 15/16).

public sealed class FlushPumpTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid DeviceA = Guid.NewGuid();
    private static readonly Guid DeviceB = Guid.NewGuid();
    private static readonly Guid DeviceUnknown = Guid.NewGuid();
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    // ---- fakes ------------------------------------------------------------------

    /// <summary>The resolver's set answer: ONLY the rows it is given come back — an
    /// unknown/revoked device is simply ABSENT (0020's uniform law, no hints).</summary>
    private sealed class FakeResolver(params DeviceCatalogRow[] rows) : IIngestionSetResolver
    {
        private readonly Dictionary<Guid, DeviceCatalogRow> _rows = rows.ToDictionary(r => r.DeviceId);
        public List<Guid[]> Calls { get; } = [];

        public Task<IReadOnlyDictionary<Guid, DeviceCatalogRow>> ResolveDevicesAsync(
            IEnumerable<Guid> deviceIds, CancellationToken ct = default)
        {
            var asked = deviceIds.ToArray();
            Calls.Add(asked);
            var answer = new Dictionary<Guid, DeviceCatalogRow>();
            foreach (var id in asked)
                if (_rows.TryGetValue(id, out var row)) answer[id] = row;
            return Task.FromResult<IReadOnlyDictionary<Guid, DeviceCatalogRow>>(answer);
        }
    }

    private sealed class AckRecorder
    {
        private readonly object _lock = new();
        private readonly List<DateTimeOffset> _acks = [];
        public int Count { get { lock (_lock) return _acks.Count; } }
        public Task Ack(CancellationToken ct)
        {
            lock (_lock) _acks.Add(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }
    }

    /// <summary>Records every flush's payload; ALSO the ack law's probe: how many
    /// acks had fired WHEN the store was entered (must always be the pre-commit 0).</summary>
    private sealed class RecordingStore : IReadingsStore
    {
        public AckRecorder? Acks;
        public List<IReadOnlyList<FlushReadingDto>> Flushes { get; } = [];
        public List<int> AcksWhenStoreCalled { get; } = [];

        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
            => throw new InvalidOperationException("the HTTP accept path must not run in a pump fact");

        public Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default)
        {
            lock (this) AcksWhenStoreCalled.Add(Acks?.Count ?? -1);
            Flushes.Add(readings);
            return Task.CompletedTask;
        }
    }

    /// <summary>R6/R10's ladder world: a batch holding the poison value fails
    /// DETERMINISTICALLY (SQLSTATE 23514 — a check violation); a clean batch lands.</summary>
    private sealed class PoisonStore(double poisonValue) : IReadingsStore
    {
        public List<FlushReadingDto> Landed { get; } = [];

        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
            => throw new InvalidOperationException("the HTTP accept path must not run in a pump fact");

        public Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default)
        {
            if (readings.Any(r => r.Value == poisonValue))
                throw new StoreFlushException("23514", isDeterministic: true,
                    new InvalidOperationException("simulated check violation"));
            Landed.AddRange(readings);
            return Task.CompletedTask;
        }
    }

    /// <summary>R10's classification probe: call 1 fails deterministically
    /// (the ladder's descent trigger), every later call fails transiently.</summary>
    private sealed class DeterministicThenTransientStore : IReadingsStore
    {
        private int _calls;
        public List<IReadOnlyList<FlushReadingDto>> Flushes { get; } = [];

        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
            => throw new InvalidOperationException("the HTTP accept path must not run in a pump fact");

        public Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
                throw new StoreFlushException("23514", isDeterministic: true,
                    new InvalidOperationException("simulated check violation"));
            throw new InvalidOperationException("simulated transient outage");
        }
    }

    /// <summary>The store NEVER answers — the final-flush bound's world.</summary>
    private sealed class BlockingStore : IReadingsStore
    {
        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
            => Task.Delay(Timeout.InfiniteTimeSpan, ct);

        public Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default)
            => Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private sealed class ThrowingStore : IReadingsStore
    {
        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated commit failure");

        public Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated commit failure");
    }

    private sealed class FlakyStore : IReadingsStore
    {
        private int _calls;
        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
            => throw new InvalidOperationException("the HTTP accept path must not run in a pump fact");

        public Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("simulated transient outage");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTracker : IDeviceStatusTracker
    {
        public List<(Guid Device, Guid Tenant, DateTimeOffset Seen, DateTimeOffset Event)> Marks { get; } = [];
        public Task MarkSeenAsync(Guid deviceId, Guid tenantId, DateTimeOffset lastSeenUtc, DateTimeOffset lastEventTimeUtc, CancellationToken ct = default)
        {
            Marks.Add((deviceId, tenantId, lastSeenUtc, lastEventTimeUtc));
            return Task.CompletedTask;
        }

        public DateTimeOffset? LastSeen(Guid deviceId) =>
            Marks.Where(m => m.Device == deviceId).Select(m => (DateTimeOffset?)m.Seen).Max();
    }

    private sealed class FakeRawRejects : IRawRejectStore
    {
        public List<RawRejectDto> Rejects { get; } = [];
        public bool Throw { get; set; }
        public Task StoreAsync(RawRejectDto reject, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("simulated receipt-write failure");
            Rejects.Add(reject);
            return Task.CompletedTask;
        }
    }

    // The pipeline's edges are NEVER consulted on the pump's direct-core path —
    // stubs exist only to satisfy the constructor.
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

    private sealed class StubAccess : IConsumerAccess
    {
        public string Username => "nabd-consumer";
        public byte[] Secret => [0x01, 0x02, 0x03];
        public bool IsConsumerUsername(string? username) => username == Username;
        public bool Authenticate(string username, string? presentedSecret) => true;
        public bool AuthorizeSubscribe(string username, string topic) => true;
        public bool AuthorizePublish(string username, string topic) => false;
    }

    // ---- the harness ------------------------------------------------------------

    private static (FlushPump Pump, FlushBuffer Buffer, AckRecorder Acks, FakeRawRejects RawRejects,
        RecordingTracker Tracker, RejectionCounters Counters, FlushCounters FlushCounters, FakeResolver Resolver)
        BuildPump(IReadingsStore store, FlushOptions? options = null, params DeviceCatalogRow[] rows)
        => BuildPump(store, options, NullLogger<FlushPump>.Instance, null, rows);

    private static (FlushPump Pump, FlushBuffer Buffer, AckRecorder Acks, FakeRawRejects RawRejects,
        RecordingTracker Tracker, RejectionCounters Counters, FlushCounters FlushCounters, FakeResolver Resolver)
        BuildPump(IReadingsStore store, FlushOptions? options, ILogger<FlushPump> logger, params DeviceCatalogRow[] rows)
        => BuildPump(store, options, logger, null, rows);

    private static (FlushPump Pump, FlushBuffer Buffer, AckRecorder Acks, FakeRawRejects RawRejects,
        RecordingTracker Tracker, RejectionCounters Counters, FlushCounters FlushCounters, FakeResolver Resolver)
        BuildPump(IReadingsStore store, FlushOptions? options, ILogger<FlushPump> logger, IEventBus? bus,
            params DeviceCatalogRow[] rows)
    {
        var o = options ?? new FlushOptions(FlushSize: 10, FlushIntervalMs: 60_000);
        var reconcile = new ReconcileCounters();   // P4 spec 02: required by the buffer (fail-shut)
        var buffer = new FlushBuffer(o.FlushSize, reconcile);
        var acks = new AckRecorder();
        var rawRejects = new FakeRawRejects();
        var tracker = new RecordingTracker();
        var counters = new RejectionCounters();
        var flushCounters = new FlushCounters();
        var resolver = new FakeResolver(rows);
        var pump = new FlushPump(
            buffer, o, resolver,
            new ValidationPipeline(new StubChannels(), new StubIdentity()),
            store, rawRejects, tracker, counters, flushCounters,
            reconcile,
            bus ?? new InProcessEventBus(),
            logger);
        return (pump, buffer, acks, rawRejects, tracker, counters, flushCounters, resolver);
    }

    /// <summary>A logger that keeps the WARNING lines — the flow-control alarm's assertion surface.</summary>
    private sealed class CapturingLogger : ILogger<FlushPump>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static IngestEnvelope Envelope(Guid device, string json) =>
        new(device, $"t/{TenantA}/dev/{device}/v1", Encoding.UTF8.GetBytes(json), ReceivedAt);

    private static string Payload(string ts, long seq, params (double Value, string Channel)[] readings)
    {
        var rr = string.Join(",", readings.Select(r => $"{{\"t\":{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"c\":\"{r.Channel}\"}}"));
        return $$"""{"v":1,"ts":"{{ts}}","seq":{{seq}},"rr":[{{rr}}]}""";
    }

    private static DeviceCatalogRow Catalog(Guid tenant, Guid device, params string[] keys) =>
        new(device, tenant, new HashSet<string>(keys));

    // ---- fact 1: ONE flush, ONE insert, shaped rows, acks AFTER the commit ------

    [Fact]
    public async Task one_flush_stores_all_rows_shaped_and_acks_only_after_the_commit()
    {
        var store = new RecordingStore();
        var (pump, buffer, acks, _, tracker, _, flushCounters, _) = BuildPump(store, null, Catalog(TenantA, DeviceA, "temp", "hum"), Catalog(TenantB, DeviceB, "temp"));
        store.Acks = acks;

        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 7, (22.5, "temp"), (61.0, "hum"))), acks.Ack);
        buffer.Offer(Envelope(DeviceB, Payload("2026-11-14T11:59:31.000Z", 8, (30.0, "temp"))), acks.Ack);

        var outcome = await pump.FlushOnceAsync(CancellationToken.None);
        await pump.DrainAcksAsync(CancellationToken.None);   // the launched chunks retire (overlap law)

        Assert.Equal(FlushOutcome.Completed, outcome);
        var flush = Assert.Single(store.Flushes);            // ONE multi-row INSERT per flush (Move C)
        Assert.Equal(3, flush.Count(r => true));
        Assert.Equal(0, Assert.Single(store.AcksWhenStoreCalled));   // ZERO acks when the store was entered — acks ride AFTER commit
        Assert.Equal(2, acks.Count);                          // BOTH messages finalized, both acked

        var rowA = flush.Single(r => r.DeviceId == DeviceA && r.ChannelKey == "temp");
        Assert.Equal(TenantA, rowA.TenantId);                 // tenant rides PER ROW (R4)
        Assert.Equal(22.5, rowA.Value);
        Assert.Equal(7, rowA.Seq);
        Assert.Equal(DateTimeOffset.Parse("2026-11-14T11:59:30.000Z"), rowA.TimeUtc);
        Assert.Equal(ReceivedAt, rowA.ReceivedAtUtc);         // server receive time on EVERY row (0003 NOT NULL)

        Assert.Equal(2, tracker.Marks.Count(m => true));                 // presence AFTER landing, per device
        Assert.All(tracker.Marks, m => Assert.Equal(ReceivedAt, m.Seen));
        Assert.Equal(0, buffer.PendingMessages);              // the window law: acked = pending lowered
        Assert.Equal(0, buffer.BufferedReadings);
        Assert.Equal(3, flushCounters.RowsStored);
        Assert.Equal(1, flushCounters.Flushes);
        Assert.Equal(0, flushCounters.ConsecutiveAborts);
    }

    // ---- fact 2: an empty buffer flushes to NOTHING -----------------------------

    [Fact]
    public async Task an_empty_buffer_flushes_to_nothing()
    {
        var (pump, buffer, _, _, _, _, _, _) = BuildPump(new RecordingStore());

        var outcome = await pump.FlushOnceAsync(CancellationToken.None);
        Assert.Equal(FlushOutcome.Nothing, outcome);
        Assert.Equal(0, buffer.PendingMessages);
    }

    // ---- fact 2b: the EAGER-DRAIN law (revert of step 9, owner-ordered after
    // ---- run 3: coalescing starved the window's ack turnover → 1,045,953 broker
    // ---- drops). A small backlog flushes IMMEDIATELY — never inside a window ---

    [Fact]
    public async Task a_small_backlog_flushes_immediately_without_waiting_for_the_interval()
    {
        var store = new RecordingStore();
        var (pump, buffer, acks, _, _, _, _, _) = BuildPump(store,
            new FlushOptions(FlushSize: 10, FlushIntervalMs: 300),
            Catalog(TenantA, DeviceA, "temp"));

        var firstAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))),
            ct => { firstAck.TrySetResult(); return acks.Ack(ct); });

        using var cts = new CancellationTokenSource();
        var pumpTask = pump.RunAsync(cts.Token);

        // The flush commits on the FIRST loop iteration — well inside the 300 ms
        // interval. (The coalescing law this replaced needed the tick: its
        // regression sat the broker's window closed while the buffer waited.)
        await firstAck.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
        cts.Cancel();
        await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
        await pump.DrainAcksAsync(CancellationToken.None);   // the launched chunk retires (overlap law)

        var flush = Assert.Single(store.Flushes);
        Assert.Equal(1, flush.Count(r => true));
        Assert.Equal(1, acks.Count);
    }

    // ---- fact 3: the interval tick wakes the pump BELOW the size bound ----------
    [Fact]
    public async Task the_interval_tick_flushes_below_the_size_bound()
    {
        var store = new RecordingStore();
        var (pump, buffer, acks, _, _, _, _, _) = BuildPump(store,
            new FlushOptions(FlushSize: 100, FlushIntervalMs: 40), Catalog(TenantA, DeviceA, "temp"));

        var firstAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))),
            ct => { firstAck.TrySetResult(); return acks.Ack(ct); });

        using var cts = new CancellationTokenSource();
        var pumpTask = pump.RunAsync(cts.Token);

        await firstAck.Task.WaitAsync(TimeSpan.FromSeconds(5));   // the tick fired, the flush committed, the ack rode
        cts.Cancel();
        await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
        await pump.DrainAcksAsync(CancellationToken.None);   // the launched chunk retires (overlap law)

        var flush = Assert.Single(store.Flushes);
        Assert.Single(flush);
        Assert.Equal(1, acks.Count);
    }

    // ---- fact 4: a TRANSIENT store failure aborts ALL — requeue, zero acks,
    // ---- then the retry heals (R10's world, end to end) -------------------------

    [Fact]
    public async Task a_transient_store_failure_aborts_requeues_zero_acks_then_heals()
    {
        var store = new FlakyStore();
        var (pump, buffer, acks, rawRejects, _, _, flushCounters, _) = BuildPump(store, null, Catalog(TenantA, DeviceA, "temp"));

        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))), acks.Ack);

        var abort = await pump.FlushOnceAsync(CancellationToken.None);
        Assert.Equal(FlushOutcome.TransientAbort, abort);
        Assert.Equal(0, acks.Count);                          // ZERO acks — the broker keeps the message
        Assert.Empty(rawRejects.Rejects);            // ZERO NEW receipts (R10)
        Assert.Equal(1, buffer.AcceptedCount);                // the snapshot sits at the tail
        Assert.Equal(1, buffer.BufferedReadings);
        Assert.Equal(1, buffer.PendingMessages);              // requeue is NOT acking
        Assert.Equal(1, flushCounters.ConsecutiveAborts);     // Option A's stall clock started

        var healed = await pump.FlushOnceAsync(CancellationToken.None);
        await pump.DrainAcksAsync(CancellationToken.None);   // the launched chunk retires (overlap law)
        Assert.Equal(FlushOutcome.Completed, healed);
        Assert.Equal(1, acks.Count);
        Assert.Equal(0, flushCounters.ConsecutiveAborts);     // success RESETS the stall clock
    }

    // ---- fact 5 (spec fact 6): ONE resolution per flush — and a FRESH one per
    // ---- flush (no cache: a revocation takes effect the very next flush) --------

    [Fact]
    public async Task one_resolution_per_flush_and_a_fresh_one_every_flush()
    {
        var (pump, buffer, acks, _, _, _, _, resolver) = BuildPump(new RecordingStore(),
            null, Catalog(TenantA, DeviceA, "temp"), Catalog(TenantA, DeviceB, "temp"));

        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))), acks.Ack);
        buffer.Offer(Envelope(DeviceB, Payload("2026-11-14T11:59:31.000Z", 2, (2.0, "temp"))), acks.Ack);

        Assert.Equal(FlushOutcome.Completed, await pump.FlushOnceAsync(CancellationToken.None));
        await pump.DrainAcksAsync(CancellationToken.None);   // the launched chunks retire (overlap law)
        var call = Assert.Single(resolver.Calls);
        Assert.Equal(2, call.Count());                          // MANY devices, ONE call
        Assert.Contains(DeviceA, call);
        Assert.Contains(DeviceB, call);

        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:32.000Z", 3, (3.0, "temp"))), acks.Ack);
        Assert.Equal(FlushOutcome.Completed, await pump.FlushOnceAsync(CancellationToken.None));
        await pump.DrainAcksAsync(CancellationToken.None);   // the launched chunk retires (overlap law)
        Assert.Equal(2, resolver.Calls.Count);                 // NO cross-flush cache — every flush re-resolves
        Assert.Equal(3, acks.Count);
    }

    // ---- fact 6 (spec fact 7): an unknown device dies ALONE — uniform E422
    // ---- receipt + ack; its neighbors in the SAME flush proceed untouched -------

    [Fact]
    public async Task an_unknown_device_is_rejected_uniformly_and_its_neighbors_proceed()
    {
        var store = new RecordingStore();
        var (pump, buffer, acks, rawRejects, tracker, counters, _, _) = BuildPump(store, null, Catalog(TenantA, DeviceA, "temp"));

        buffer.Offer(Envelope(DeviceUnknown, Payload("2026-11-14T11:59:30.000Z", 9, (1.0, "temp"))), acks.Ack);
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:31.000Z", 10, (2.0, "temp"))), acks.Ack);

        Assert.Equal(FlushOutcome.Completed, await pump.FlushOnceAsync(CancellationToken.None));
        await pump.DrainAcksAsync(CancellationToken.None);   // the launched chunk retires (overlap law)

        var flush = Assert.Single(store.Flushes);
        Assert.All(flush, r => Assert.NotEqual(DeviceUnknown, r.DeviceId));   // the stranger stored NOTHING
        Assert.Single(flush);

        var receipt = Assert.Single(rawRejects.Rejects);
        Assert.Equal(IngestionErrors.E422_UNKNOWN_CHANNEL, receipt.Reason);
        Assert.Equal(DeviceUnknown, receipt.DeviceId);
        Assert.Null(receipt.TenantId);                         // identity never resolved — nullable by design (spec 06)

        Assert.Equal(2, acks.Count);                           // BOTH messages finalized: stranger receipted, neighbor stored
        var mark = Assert.Single(tracker.Marks);
        Assert.Equal(DeviceA, mark.Device);
        Assert.Equal(1, counters.Snapshot()[IngestionErrors.E422_UNKNOWN_CHANNEL]);
    }

    // ---- fact 7 (spec fact 9): a parse-reject is receipted WITH its raw bytes,
    // ---- acked, and NEVER touches resolution or the store -----------------------

    [Fact]
    public async Task a_parse_reject_gets_a_DLQ_INVALID_receipt_then_the_ack()
    {
        var (pump, buffer, acks, rawRejects, _, counters, _, _) = BuildPump(new RecordingStore());
        var raw = Encoding.UTF8.GetBytes("this is not json");

        buffer.Offer(new IngestEnvelope(DeviceA, $"t/{TenantA}/dev/{DeviceA}/v1", raw, ReceivedAt), acks.Ack);

        Assert.Equal(FlushOutcome.Completed, await pump.FlushOnceAsync(CancellationToken.None));

        var receipt = Assert.Single(rawRejects.Rejects);
        Assert.Equal(IngestionErrors.DLQ_INVALID, receipt.Reason);
        Assert.Equal(raw, receipt.Payload);                    // the raw bytes, verbatim — the forensics
        Assert.Equal(DeviceA, receipt.DeviceId);
        Assert.Null(receipt.TenantId);
        Assert.Equal(ReceivedAt, receipt.ReceivedAtUtc);
        Assert.Equal(1, acks.Count);                           // acked AFTER the receipt committed
        Assert.Equal(1, counters.Snapshot()[IngestionErrors.DLQ_INVALID]);
        Assert.Equal(0, buffer.PendingMessages);
    }

    // ---- fact 8 (spec fact 13): a RECEIPT-write failure is transient — no
    // ---- committed receipt = no durable outcome = NO ack (abort-all) ------------

    [Fact]
    public async Task a_receipt_write_failure_aborts_with_zero_acks()
    {
        var (pump, buffer, acks, rawRejects, _, _, flushCounters, _) = BuildPump(new RecordingStore());
        rawRejects.Throw = true;

        buffer.Offer(new IngestEnvelope(DeviceA, $"t/{TenantA}/dev/{DeviceA}/v1",
            Encoding.UTF8.GetBytes("junk"), ReceivedAt), acks.Ack);

        Assert.Equal(FlushOutcome.TransientAbort, await pump.FlushOnceAsync(CancellationToken.None));
        Assert.Equal(0, acks.Count);
        Assert.Equal(1, buffer.ParseRejectsCount);             // back at the tail, retried later
        Assert.Equal(1, flushCounters.ConsecutiveAborts);
    }

    // ---- fact 9 (spec fact 11): the R6 poison ladder — flush → per-device →
    // ---- row-by-row; the floor receipt+acks the poison message (never looped) ---

    [Fact]
    public async Task the_ladder_descends_and_a_poison_row_gets_its_receipt_and_ack()
    {
        var store = new PoisonStore(poisonValue: 999.0);
        var (pump, buffer, acks, rawRejects, tracker, counters, flushCounters, _) = BuildPump(store,
            null, Catalog(TenantA, DeviceA, "temp"), Catalog(TenantB, DeviceB, "temp"));

        // A: two clean rows. B: one clean + one poison (999 would violate a check).
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"), (2.0, "temp"))), acks.Ack);
        buffer.Offer(Envelope(DeviceB, Payload("2026-11-14T11:59:31.000Z", 2, (3.0, "temp"), (999.0, "temp"))), acks.Ack);

        Assert.Equal(FlushOutcome.Completed, await pump.FlushOnceAsync(CancellationToken.None));

        // Landed: A's 2 rows + B's clean row. The poison row NEVER lands.
        Assert.Equal(3, store.Landed.Count(r => true));
        Assert.DoesNotContain(store.Landed, r => r.Value == 999.0);

        var receipt = Assert.Single(rawRejects.Rejects);
        Assert.Equal(IngestionErrors.DLQ_STORE_POISON, receipt.Reason);
        Assert.Equal(DeviceB, receipt.DeviceId);

        Assert.Equal(2, acks.Count);                           // BOTH finalized: A stored, B receipted — never looped
        Assert.Equal(0, buffer.PendingMessages);
        Assert.Equal(2, flushCounters.LadderDescents);         // level=per-device AND level=row-by-row
        Assert.Equal(1, flushCounters.PoisonReceipts);
        Assert.Equal(3, flushCounters.RowsStored);
        Assert.Equal(1, counters.Snapshot()[IngestionErrors.DLQ_STORE_POISON]);
    }

    // ---- fact 10: a TRANSIENT failure inside the ladder aborts the ENTIRE
    // ---- flush — zero acks, zero receipts, everything back at the tail (R10) ----

    [Fact]
    public async Task a_transient_failure_inside_the_ladder_aborts_everything()
    {
        var store = new DeterministicThenTransientStore();
        var (pump, buffer, acks, rawRejects, tracker, _, flushCounters, _) = BuildPump(store,
            null, Catalog(TenantA, DeviceA, "temp"), Catalog(TenantB, DeviceB, "temp"));

        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))), acks.Ack);
        buffer.Offer(Envelope(DeviceB, Payload("2026-11-14T11:59:31.000Z", 2, (2.0, "temp"))), acks.Ack);

        Assert.Equal(FlushOutcome.TransientAbort, await pump.FlushOnceAsync(CancellationToken.None));

        Assert.Equal(0, acks.Count);
        Assert.Empty(rawRejects.Rejects);                      // the unknown error class is NEVER a receipt
        Assert.Empty(tracker.Marks);                           // presence never lies
        Assert.Equal(2, buffer.AcceptedCount);                 // the snapshot is whole, at the tail
        Assert.Equal(1, flushCounters.ConsecutiveAborts);      // exactly ONE abort (the deterministic descent is not an abort)
    }

    // ---- fact 11 (spec fact 14, pump half): the final flush drains and acks
    // ---- everything within its bound --------------------------------------------

    [Fact]
    public async Task the_final_flush_drains_and_acks_within_the_bound()
    {
        var (pump, buffer, acks, _, _, _, _, _) = BuildPump(new RecordingStore(), null, Catalog(TenantA, DeviceA, "temp"));

        for (var i = 1; i <= 3; i++)
            buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", i, (1.0 * i, "temp"))), acks.Ack);

        await pump.FinalFlushAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, buffer.AcceptedCount);
        Assert.Equal(3, acks.Count);
        Assert.Equal(0, buffer.PendingMessages);
    }

    // ---- fact 12 (spec fact 14, pump half): an expiring/cancelled final flush
    // ---- leaves ZERO acks — the broker re-queues, nothing is lost or lied about -

    [Fact]
    public async Task a_final_flush_that_cannot_commit_leaves_zero_acks()
    {
        var (pump, buffer, acks, _, _, _, _, _) = BuildPump(new BlockingStore(), null, Catalog(TenantA, DeviceA, "temp"));

        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))), acks.Ack);

        await pump.FinalFlushAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Equal(0, acks.Count);                           // zero acks — the honest outcome
        Assert.Equal(1, buffer.AcceptedCount);                 // still buffered; the broker redelivers on next boot
    }

    // ---- fact 13 (spec fact 15, AMENDED by the sharding ruling 2026-11-16):
    // ---- the window is PER CONNECTION now — each shard carries ITS OWN
    // ---- Receive Maximum and ITS OWN stable client id (the session-resume
    // ---- identity). The aggregate window = shards × RM. ----------------------

    [Fact]
    public void the_connect_carries_the_per_connection_receive_maximum_and_the_shard_client_identity()
    {
        foreach (var receiveMaximum in new[] { 1, 200, 65535 })
        {
            var options = EmqxIngestConsumer.BuildOptions(
                new StubAccess(), receiveMaximum, "nabd-consumer-3", "127.0.0.1", 1883, "s3cret");

            Assert.Equal(receiveMaximum, options.ReceiveMaximum);
            Assert.False(options.CleanSession);                // CleanStart=false ALWAYS — resumable session (drill 1)
            Assert.Equal(3600U, options.SessionExpiryInterval);// the broker queues QoS1 for 1 h while we're down
            Assert.Equal("nabd-consumer-3", options.ClientId); // THIS shard's stable session identity
        }
    }

    [Fact]
    public void the_consumer_subscribes_through_the_shared_subscription_filter()
    {
        // The wire shape (owner ruling, 2026-11-16): N connections share the
        // group "consumers" — EMQX round-robins deliveries across them, so no
        // single connection's broker process carries the whole fleet.
        Assert.Equal("$share/consumers/t/+/dev/+/v1", EmqxIngestConsumer.SubscribedFilter);
    }

    // ---- the subscribe DOOR rules the shared shape (the consumer's allow-set
    // ---- grows by exactly one form: $share/<group>/<the same 5-segment law>) ---

    [Theory]
    [InlineData("$share/consumers/t/+/dev/+/v1", true)]
    [InlineData("$share/consumers/t/2a66/dev/9f0e/v1", true)]
    [InlineData("t/+/dev/+/v1", true)]                       // the bare form stays lawful (single-consumer world)
    [InlineData("$share/consumer+/t/+/dev/+/v1", false)]     // a wildcard in the group name
    [InlineData("$share//t/+/dev/+/v1", false)]              // an empty group name
    [InlineData("$share/a/b/t/+/dev/+/v1", false)]           // a two-level group
    [InlineData("$share/consumers/t/+/dev/+/v", false)]      // the filter shape itself is broken
    [InlineData("$share/$group/t/+/dev/+/v1", false)]        // a $-prefixed group (broker namespaces)
    public void the_subscribe_door_rules_the_shared_subscription_shape(string topic, bool allowed)
    {
        Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_USER", "nabd-consumer");
        Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET", "s3cret-test");
        try
        {
            var access = new ConsumerAccess();
            Assert.Equal(allowed, access.AuthorizeSubscribe("nabd-consumer", topic));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_USER", null);
            Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET", null);
        }
    }

    // ---- R7 AMENDED (sharding ruling): pending ≡ unacked is bounded by the
    // ---- AGGREGATE window (shards × RM), never by FlushSize alone. The alarm
    // ---- must trip INSIDE the aggregate bound — it is the flow-control BUG
    // ---- detector, and a toothless bound would hide exactly the jam this
    // ---- slice exists to prevent. --------------------------------------------

    [Fact]
    public async Task the_window_alarm_trips_inside_the_aggregate_window_bound()
    {
        // Aggregate window = 1 shard × RM 10 → the alarm bound is
        // 10 + (4 chunks × 64 ack flight) + 32 slack = 298 pending. 310 offered
        // exceeds it: the alarm is the flow-control BUG detector, and its bound
        // MUST stay tighter than what the system can lawfully reach.
        var logger = new CapturingLogger();
        var options = FlushOptions.From(10, 60_000, receiveMaximum: 10, consumerShards: 1);
        var store = new ThrowingStore();                     // every flush aborts — pending never lowers
        var (pump, buffer, acks, _, _, _, _, _) = BuildPump(store, options, logger, Catalog(TenantA, DeviceA, "temp"));

        for (var i = 0; i < 310; i++)
            buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", i, (22.5, "temp"))), acks.Ack);

        await pump.FlushOnceAsync(CancellationToken.None);   // aborts — pending stays 310, above the 298 bound

        Assert.Contains(logger.Warnings, w => w.Contains("flow-control ALARM"));
        Assert.Contains(logger.Warnings, w => w.Contains("aggregate window"));
    }

    [Fact]
    public async Task the_window_alarm_stays_silent_inside_the_aggregate_window_bound()
    {
        // Same pending (50), but the aggregate window is wide enough
        // (RM 200 × 1 shard → bound 488) — no alarm, no noise.
        var logger = new CapturingLogger();
        var options = FlushOptions.From(10, 60_000, receiveMaximum: 200, consumerShards: 1);
        var store = new ThrowingStore();
        var (pump, buffer, acks, _, _, _, _, _) = BuildPump(store, options, logger, Catalog(TenantA, DeviceA, "temp"));

        for (var i = 0; i < 50; i++)
            buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", i, (22.5, "temp"))), acks.Ack);

        await pump.FlushOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("flow-control ALARM"));
    }

    // ---- the ack-retire law (observed in the pressure diagnosis): a FAILED ack
    // ---- after a committed outcome must STILL retire the message's pending
    // ---- contribution — the broker redelivers it, and the redelivery re-adds
    // ---- pending under its own copy. Without the retire, a connection kill
    // ---- inflates pending PERMANENTLY (measured: 1,750 zombie pending) and
    // ---- the window alarm becomes a liar. ------------------------------------

    [Fact]
    public async Task a_failed_ack_still_retires_its_pending_contribution()
    {
        var store = new RecordingStore();
        var (pump, buffer, _, _, _, _, _, _) = BuildPump(store, null, Catalog(TenantA, DeviceA, "temp"));

        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 7, (22.5, "temp"))),
            _ => throw new IOException("connection dead mid-ack"));   // the ack will FAIL
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:31.000Z", 8, (30.0, "temp"))),
            _ => throw new IOException("connection dead mid-ack"));

        var outcome = await pump.FlushOnceAsync(CancellationToken.None);
        await pump.DrainAcksAsync(CancellationToken.None);   // the launched chunks retire (overlap law)

        Assert.Equal(FlushOutcome.Completed, outcome);
        Assert.Single(store.Flushes);                        // the commit happened
        Assert.Equal(0, buffer.PendingMessages);             // BOTH retire — the redeliveries re-add their own
    }

    // ---- THE OVERLAPPING ACK WINDOW (owner ruling 2026-11-17, after the
    // ---- sharded gate failed at 958 rows/s: the serial chunked acks made the
    // ---- flush cycle ≈ 0.8 s for the full 800-message window, the broker
    // ---- dispatched only on PUBACK arrival, and the mqueue sat pinned at
    // ---- 1,000 dropping ~49% of everything). The pump LAUNCHES bounded
    // ---- chunks and returns — acks fly while the NEXT flush drains. ---------

    [Fact]
    public async Task a_flush_returns_with_its_acks_still_in_flight_and_the_drain_completes_them()
    {
        var store = new RecordingStore();
        var (pump, buffer, _, _, _, _, _, _) = BuildPump(store, null, Catalog(TenantA, DeviceA, "temp"));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long attempted = 0;
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))),
            _ => { Interlocked.Increment(ref attempted); return gate.Task.WaitAsync(CancellationToken.None); });

        var flush = await pump.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(FlushOutcome.Completed, flush);         // the COMMIT is the verdict
        Assert.Equal(1, store.Flushes.Count(r => true));     // the store ran
        Assert.True(!gate.Task.IsCompleted);               // the ack is still IN FLIGHT
        Assert.Equal(1, Interlocked.Read(ref attempted));    // ...but it was ATTEMPTED (launched)
        Assert.Equal(1, buffer.PendingMessages);             // the mirror: not retired until the ack completes

        gate.TrySetResult();                                 // the PUBACK flies
        await pump.DrainAcksAsync(CancellationToken.None);

        Assert.Equal(0, buffer.PendingMessages);             // retired on completion (the ack-retire law)
    }

    [Fact]
    public async Task the_ack_window_bounds_the_chunks_in_flight()
    {
        var store = new RecordingStore();
        var (pump, buffer, _, _, _, _, _, _) = BuildPump(store, null, Catalog(TenantA, DeviceA, "temp"));

        // 300 messages = 5 chunks of 64; EVERY ack blocks on one gate. The
        // sliding window admits exactly 4 chunks (4 × 64 = 256 attempted);
        // chunk 5's launch waits for a slot — the pump CANNOT outrun the acks.
        const int messages = 300;
        const int windowLimit = 4 * 64;                      // MaxInFlightAckChunks × AckChunkSize (pump consts)
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = 0L;
        for (var i = 0; i < messages; i++)
        {
            var seq = i;
            buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", seq, (1.0, "temp"))), _ =>
            {
                Interlocked.Increment(ref attempted);
                return gate.Task.WaitAsync(CancellationToken.None);
            });
        }
        var flush = pump.FlushOnceAsync(CancellationToken.None);

        await Task.Delay(150);                               // scheduling slop for the 4 admitted chunks
        Assert.False(flush.IsCompleted);                     // chunk 5 waits for a slot — the flush cannot return
        var attemptedNow = Interlocked.Read(ref attempted);
        Assert.True(attemptedNow <= windowLimit,
            $"expected ≤ {windowLimit} acks attempted while the gate is closed, got {attemptedNow}");

        gate.TrySetResult();                                 // open the window
        Assert.Equal(FlushOutcome.Completed, await flush.WaitAsync(TimeSpan.FromSeconds(10)));
        await pump.DrainAcksAsync(CancellationToken.None);

        Assert.Equal(messages, Interlocked.Read(ref attempted));   // EVERY message eventually acked
        Assert.Equal(0, buffer.PendingMessages);
    }

    [Fact]
    public async Task graceful_shutdown_drains_the_acks_in_flight_before_returning()
    {
        var store = new RecordingStore();
        var (pump, buffer, _, _, _, _, _, _) = BuildPump(store, null, Catalog(TenantA, DeviceA, "temp"));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))),
            _ => gate.Task.WaitAsync(CancellationToken.None));

        await pump.FlushOnceAsync(CancellationToken.None);    // the chunk launches, its ack blocks

        var shutdown = pump.FinalFlushAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(150);
        Assert.False(shutdown.IsCompleted);                  // the drain WAITS for the in-flight ack
        Assert.True(!gate.Task.IsCompleted);

        gate.TrySetResult();                                 // the PUBACK flies; the drain may now finish
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, buffer.PendingMessages);             // retired BEFORE the shutdown returned —
                                                             // the disconnect (next) cannot strand a PUBACK
    }

    [Fact]
    public async Task the_pump_loop_overlaps_the_ack_phase_with_the_next_flush()
    {
        var store = new RecordingStore();
        var (pump, buffer, acks, _, _, _, _, _) = BuildPump(store,
            new FlushOptions(FlushSize: 10, FlushIntervalMs: 30), Catalog(TenantA, DeviceA, "temp"));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "temp"))),
            _ => gate.Task.WaitAsync(CancellationToken.None));

        using var cts = new CancellationTokenSource();
        var loop = pump.RunAsync(cts.Token);

        // Message 2 arrives while message 1's ack is STILL blocked: the eager
        // loop must flush it (its own commit) without waiting for ack 1.
        buffer.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:31.000Z", 2, (2.0, "temp"))), acks.Ack);

        var deadline = TimeSpan.FromSeconds(5);
        while (store.Flushes.Count < 2)
            await Task.Delay(10, CancellationToken.None).WaitAsync(deadline);

        Assert.True(!gate.Task.IsCompleted);               // ack 1 never finished — the loop moved on anyway
        Assert.Equal(2, store.Flushes.Count);                // flush #2 committed during ack 1's flight

        cts.Cancel();
        await loop.WaitAsync(deadline);
        gate.TrySetResult();
        await pump.DrainAcksAsync(CancellationToken.None);

        Assert.Equal(1, acks.Count);                         // msg2's ack; msg1's is the GATE delegate, not the recorder —
                                                             // its completion is proven by the retire below
        Assert.Equal(0, buffer.PendingMessages);             // BOTH retired — msg1's rode the gate completion
    }

    // ---- fact 14 (spec fact 10): the consumer's wall is STRUCTURAL — no port
    // ---- to the store, the resolver, or the receipts reaches its constructor ----

    [Fact]
    public void the_consumer_has_no_port_to_the_store_resolution_or_receipts()
    {
        var ctor = Assert.Single(typeof(EmqxIngestConsumer).GetConstructors());
        var parameters = ctor.GetParameters();

        Assert.All(parameters, p => Assert.True(
            (p.ParameterType.Namespace ?? "").StartsWith("Nabd.Ingestion")
            || p.ParameterType == typeof(ILogger)
            || (p.ParameterType.Namespace ?? "").StartsWith("Microsoft.Extensions.Logging"),
            $"consumer ctor parameter {p.ParameterType} is not an Ingestion-room type — a wall breach"));

        // The TelemetryStore room's surfaces can NEVER be constructor-reachable.
        Assert.DoesNotContain(parameters, p => (p.ParameterType.Namespace ?? "").StartsWith("Nabd.TelemetryStore"));

        // The buffer's own wall (AMENDED P4 spec 02): its constructor takes
        // ONLY the flush size (an int) + the Ingestion-room reconcile ledger
        // (required, fail-shut) — still no store/resolution/receipt port.
        var bufferCtor = Assert.Single(typeof(FlushBuffer).GetConstructors());
        Assert.Equal([typeof(int), typeof(ReconcileCounters)], bufferCtor.GetParameters().Select(p => p.ParameterType).ToArray());

        // And the delivery envelope carries no room types at all (pure data).
        var envelopeCtor = Assert.Single(typeof(IngestEnvelope).GetConstructors());
        Assert.All(envelopeCtor.GetParameters(), p => Assert.NotEqual(typeof(FlushPump), p.ParameterType));
    }

    // ---- fact 15 (spec fact 16): FlushOptions.From is fail-shut on nonsense -----

    [Fact]
    public void flush_options_from_is_fail_shut()
    {
        Assert.ThrowsAny<Exception>(() => FlushOptions.From(0, 100));        // size < 1
        Assert.ThrowsAny<Exception>(() => FlushOptions.From(65536, 100));    // size > MQTT5's wire cap
        Assert.ThrowsAny<Exception>(() => FlushOptions.From(200, 0));        // interval < 1 ms
        Assert.Equal((200, 100), (FlushOptions.From(200, 100).FlushSize, FlushOptions.From(200, 100).FlushIntervalMs));
        Assert.Equal(1, FlushOptions.From(1, 1).FlushSize);                  // the lawful floor
        Assert.Equal(65535, FlushOptions.From(65535, 1000).FlushSize);       // the lawful ceiling
    }

    // ---- fact 17 — spec 04: committed batches exist only AFTER the commit ------

    [Fact]
    public async Task committed_batches_are_published_only_after_the_store_commits()
    {
        var bus = new InProcessEventBus();
        var seen = new List<CommittedBatch>();
        bus.Subscribe<CommittedBatch>((b, _) => { seen.Add(b); return Task.CompletedTask; });

        // a store that NEVER commits → the engine (the subscriber) must hear NOTHING
        var (pumpFail, bufferFail, acksFail, _, _, _, _, _) =
            BuildPump(new ThrowingStore(), null, NullLogger<FlushPump>.Instance, bus,
                Catalog(TenantA, DeviceA, "temp"));
        for (var i = 0; i < 3; i++)
            bufferFail.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", i, (22.5, "temp"))), acksFail.Ack);
        await pumpFail.FlushOnceAsync(CancellationToken.None);
        Assert.Empty(seen);

        // a committing store → exactly ONE batch for the device, carrying its rows
        var (pumpOk, bufferOk, acksOk, _, _, _, _, _) =
            BuildPump(new RecordingStore(), null, NullLogger<FlushPump>.Instance, bus,
                Catalog(TenantA, DeviceA, "temp"));
        bufferOk.Offer(Envelope(DeviceA, Payload("2026-11-14T11:59:30.000Z", 1, (22.5, "temp"))), acksOk.Ack);
        await pumpOk.FlushOnceAsync(CancellationToken.None);

        var batch = Assert.Single(seen);
        Assert.Equal(TenantA, batch.TenantId);
        Assert.Equal(DeviceA, batch.DeviceId);
        var reading = Assert.Single(batch.Readings);
        Assert.Equal("temp", reading.ChannelKey);
        Assert.Equal(22.5, reading.Value);
    }
}
