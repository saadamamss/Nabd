namespace Nabd.IsolationSuite;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;
using Xunit;

// P3 spec 03+04 — the sink's LAWS, made executable: (a) the sink runs the
// pipeline FIRST and hands the store port a batch ONLY when the pipeline
// accepted — a rejected delivery reaches no INSERT (nothing malformed can reach
// an INSERT, NFR-04's gate); (b) every ACCEPTED batch marks the device seen
// (plan/05 §9, buffered — one bulk flush per window); (c) a store FAILURE
// propagates OUT of the sink — never swallowed — so the consumer's catch leaves
// the message un-acked and the broker redelivers (ack-after-commit, NFR-04).

public sealed class PipelineSinkTests
{
    private static readonly Guid Device = Guid.NewGuid();
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingStore : IReadingsStore
    {
        public List<ReadingBatchDto> Batches { get; } = [];
        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReadingValueDto>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReadingValueDto>>([]);
    }

    private sealed class ThrowingStore : IReadingsStore
    {
        public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated commit failure");

        public Task<IReadOnlyList<ReadingValueDto>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReadingValueDto>>([]);
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

    private sealed class FakeChannels : IDeviceChannels
    {
        public Task<IReadOnlySet<string>?> GetChannelKeysAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>?>(deviceId == Device ? new HashSet<string>(["temp", "hum"]) : null);
    }

    private sealed class FakeIdentity : IIngestionIdentity
    {
        public Task<Guid?> ResolveTenantIdAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult<Guid?>(deviceId == Device ? Tenant : null);
    }

    private static (PipelineIngestionSink Sink, RecordingTracker Tracker) Sink(IReadingsStore store)
    {
        var tracker = new RecordingTracker();
        return (new PipelineIngestionSink(
                    new ValidationPipeline(new FakeChannels(), new FakeIdentity()),
                    store, tracker, NullLogger<PipelineIngestionSink>.Instance), tracker);
    }

    private static IngestEnvelope Envelope(string json)
        => new(Device, $"t/{Tenant}/dev/{Device}/v1", Encoding.UTF8.GetBytes(json), ReceivedAt);

    private static string Payload(string ts, long seq, params (double Value, string Channel)[] readings)
    {
        var rr = string.Join(",", readings.Select(r => $"{{\"t\":{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"c\":\"{r.Channel}\"}}"));
        return $$"""{"v":1,"ts":"{{ts}}","seq":{{seq}},"rr":[{{rr}}]}""";
    }

    // ---- fact 1: an accepted delivery reaches the store EXACTLY once, shaped, ---
    // ---- and marks the device seen once ---------------------------------------

    [Fact]
    public async Task an_accepted_delivery_reaches_the_store_once_and_shaped_and_marks_seen()
    {
        var store = new RecordingStore();
        var (sink, tracker) = Sink(store);
        await sink.AcceptAsync(Envelope(Payload("2026-11-14T11:59:30.000Z", 7, (22.5, "temp"), (61.0, "hum"))));

        var batch = Assert.Single(store.Batches);
        Assert.Equal(Tenant, batch.TenantId);
        Assert.Equal(Device, batch.DeviceId);
        Assert.Equal(2, batch.Readings.Count);
        Assert.Equal("temp", batch.Readings[0].ChannelKey);
        Assert.Equal(22.5, batch.Readings[0].Value);
        Assert.Equal(7, batch.Readings[0].Seq);
        Assert.Equal(ReceivedAt, batch.Readings[0].ReceivedAtUtc);   // received_at rides per reading (0003 NOT NULL)

        var mark = Assert.Single(tracker.Marks);
        Assert.Equal((Device, Tenant, ReceivedAt, DateTimeOffset.Parse("2026-11-14T11:59:30.000Z")), mark);
    }

    // ---- fact 2: a rejected delivery NEVER reaches the store, NEVER marks -------

    [Fact]
    public async Task a_rejected_delivery_never_reaches_the_store_nor_marks_seen()
    {
        var store = new RecordingStore();
        var (sink, tracker) = Sink(store);

        await sink.AcceptAsync(Envelope("this is not json"));
        await sink.AcceptAsync(Envelope(Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "pressure"))));

        Assert.Empty(store.Batches);    // two rejections, zero INSERTs — the law holds
        Assert.Empty(tracker.Marks);    // and nothing unseen ever "was seen"
    }

    // ---- fact 3: a store failure PROPAGATES — the ack law's half ---------------

    [Fact]
    public async Task a_store_failure_propagates_out_of_the_sink_never_swallowed()
    {
        var (sink, tracker) = Sink(new ThrowingStore());

        // No try/catch inside the sink: the consumer's catch is the one and only
        // handler — the message stays un-acked and the broker redelivers, and the
        // idempotent re-insert makes that redelivery harmless (NFR-04).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sink.AcceptAsync(Envelope(Payload("2026-11-14T11:59:30.000Z", 7, (22.5, "temp")))));

        Assert.Empty(tracker.Marks);    // no presence recorded for data that never landed
    }
}
