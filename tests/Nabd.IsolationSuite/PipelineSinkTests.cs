namespace Nabd.IsolationSuite;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;
using Xunit;

// P3 spec 03 — the store-ordering LAW, made executable: the sink runs the
// pipeline FIRST and hands the store port a batch ONLY when the pipeline
// accepted. A rejected delivery reaches no INSERT — the same idea as the
// ADR-005 VETO's 200-after-commit: acknowledge/store only after the data is
// real. (The pure pipeline's 9 facts live in ValidationPipelineTests; this net
// pins the ORDERING around it.)

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

    private static PipelineIngestionSink Sink(RecordingStore store)
        => new(new ValidationPipeline(new FakeChannels(), new FakeIdentity()), store,
               NullLogger<PipelineIngestionSink>.Instance);

    private static IngestEnvelope Envelope(string json)
        => new(Device, $"t/{Tenant}/dev/{Device}/v1", Encoding.UTF8.GetBytes(json), ReceivedAt);

    private static string Payload(string ts, long seq, params (double Value, string Channel)[] readings)
    {
        var rr = string.Join(",", readings.Select(r => $"{{\"t\":{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"c\":\"{r.Channel}\"}}"));
        return $$"""{"v":1,"ts":"{{ts}}","seq":{{seq}},"rr":[{{rr}}]}""";
    }

    // ---- fact 1: an accepted delivery reaches the store EXACTLY once, shaped ---

    [Fact]
    public async Task an_accepted_delivery_reaches_the_store_once_and_shaped()
    {
        var store = new RecordingStore();
        await Sink(store).AcceptAsync(Envelope(Payload("2026-11-14T11:59:30.000Z", 7, (22.5, "temp"), (61.0, "hum"))));

        var batch = Assert.Single(store.Batches);
        Assert.Equal(Tenant, batch.TenantId);
        Assert.Equal(Device, batch.DeviceId);
        Assert.Equal(2, batch.Readings.Count);
        Assert.Equal("temp", batch.Readings[0].ChannelKey);
        Assert.Equal(22.5, batch.Readings[0].Value);
        Assert.Equal(7, batch.Readings[0].Seq);
    }

    // ---- fact 2: a rejected delivery NEVER reaches the store -------------------

    [Fact]
    public async Task a_rejected_delivery_never_reaches_the_store()
    {
        var store = new RecordingStore();

        await Sink(store).AcceptAsync(Envelope("this is not json"));
        await Sink(store).AcceptAsync(Envelope(Payload("2026-11-14T11:59:30.000Z", 1, (1.0, "pressure"))));

        Assert.Empty(store.Batches);   // two rejections, zero INSERTs — the law holds
    }
}
