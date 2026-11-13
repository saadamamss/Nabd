namespace Nabd.IsolationSuite;

using System.Text;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Xunit;

// P3 spec 03 — the validation pipeline's table-driven net (pure core + fake
// edges: NO Postgres, NO broker). Every catalog code gets a fact proving its
// disposition, the clock rules get their boundary facts (4 min OK / 6 min
// clamped / 29 days OK / 31 days flagged), and one law above all: malformed
// data NEVER reaches the store — the pipeline returns a rejection, the store
// port (spec 04) is never handed anything on a rejected result.

public sealed class ValidationPipelineTests
{
    private static readonly Guid Device = Guid.NewGuid();
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    // ---- fake edges: the pipeline's only two injections -----------------------

    private sealed class FakeChannels : IDeviceChannels
    {
        private readonly IReadOnlySet<string> _keys;
        public FakeChannels(params string[] keys) => _keys = new HashSet<string>(keys);
        public Task<IReadOnlySet<string>?> GetChannelKeysAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>?>(deviceId == Device ? _keys : null);
    }

    private sealed class FakeIdentity : IIngestionIdentity
    {
        public Task<Guid?> ResolveTenantIdAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult<Guid?>(deviceId == Device ? Tenant : null);
    }

    private static ValidationPipeline Pipeline(params string[] channels)
        => new(new FakeChannels(channels.Length == 0 ? ["temp", "hum"] : channels), new FakeIdentity());

    // ---- payload composition (byte-exact with the simulator's PayloadV1) -----

    private static string Payload(string ts, long seq, params (double Value, string Channel)[] readings)
    {
        var rr = string.Join(",", readings.Select(r => $"{{\"t\":{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"c\":\"{r.Channel}\"}}"));
        return $$"""{"v":1,"ts":"{{ts}}","seq":{{seq}},"rr":[{{rr}}]}""";
    }

    private static IngestEnvelope Envelope(string json)
        => new(Device, $"t/{Tenant}/dev/{Device}/v1", Encoding.UTF8.GetBytes(json), ReceivedAt);

    // ---- fact 1: the happy path — a lawful batch normalizes fully -------------

    [Fact]
    public async Task a_lawful_batch_normalizes_with_identity_and_clock_filled()
    {
        var ts = "2026-11-13T11:59:30.000Z";
        var result = await Pipeline().RunAsync(Envelope(Payload(ts, 42, (22.5, "temp"), (61.0, "hum"))));

        Assert.True(result.IsAccepted, $"expected accept, got {result.Rejected?.Code}: {result.Rejected?.Detail}");
        Assert.Equal(2, result.Accepted!.Count);
        Assert.All(result.Accepted, r =>
        {
            Assert.Equal(Tenant, r.TenantId);
            Assert.Equal(Device, r.DeviceId);
            Assert.Equal(ReceivedAt, r.ReceivedAtUtc);
            Assert.Equal(42, r.Seq);
            Assert.Equal(DateTimeOffset.Parse(ts), r.TimeUtc);
        });
        Assert.Equal(22.5, result.Accepted[0].Value);
        Assert.Empty(result.Warnings);
    }

    // ---- fact 2: structural violations die at stage 3 as DLQ_INVALID ----------

    [Fact]
    public async Task structurally_broken_payloads_are_DLQ_INVALID()
    {
        var notJson = await Pipeline().RunAsync(Envelope("this is not json"));
        var wrongVersion = await Pipeline().RunAsync(Envelope(Payload("2026-11-13T11:59:30.000Z", 1, (1.0, "temp")).Replace("\"v\":1", "\"v\":2")));
        var missingSeq = await Pipeline().RunAsync(Envelope("""{"v":1,"ts":"2026-11-13T11:59:30.000Z","rr":[{"t":1.0,"c":"temp"}]}"""));
        var missingRr = await Pipeline().RunAsync(Envelope("""{"v":1,"ts":"2026-11-13T11:59:30.000Z","seq":1}"""));
        var emptyRr = await Pipeline().RunAsync(Envelope(Payload("2026-11-13T11:59:30.000Z", 1)));

        Assert.True(notJson.Rejected is { Code: IngestionErrors.DLQ_INVALID }, $"{notJson.Rejected?.Code}");
        Assert.True(wrongVersion.Rejected is { Code: IngestionErrors.DLQ_INVALID }, $"{wrongVersion.Rejected?.Code}");
        Assert.True(missingSeq.Rejected is { Code: IngestionErrors.DLQ_INVALID }, $"{missingSeq.Rejected?.Code}");
        Assert.True(missingRr.Rejected is { Code: IngestionErrors.DLQ_INVALID }, $"{missingRr.Rejected?.Code}");
        Assert.True(emptyRr.Rejected is { Code: IngestionErrors.DLQ_INVALID }, $"{emptyRr.Rejected?.Code}");
    }

    // ---- fact 3: seq sanity is ONLY the integer bound (owner ruling 2026-11-13):
    //      no monotonic enforcement — negative and >= 2^62 die structurally; a
    //      RESET seq with a NEW timestamp passes (field devices reboot).

    [Fact]
    public async Task seq_sanity_is_the_bound_only_not_monotonicity()
    {
        var negative = await Pipeline().RunAsync(Envelope(Payload("2026-11-13T11:59:30.000Z", -1, (1.0, "temp"))));
        var huge = await Pipeline().RunAsync(Envelope(Payload("2026-11-13T11:59:30.000Z", (long)1 << 62, (1.0, "temp"))));
        var resetSeq = await Pipeline().RunAsync(Envelope(Payload("2026-11-13T11:00:00.000Z", 3, (1.0, "temp"))));

        Assert.True(negative.Rejected is { Code: IngestionErrors.DLQ_INVALID });
        Assert.True(huge.Rejected is { Code: IngestionErrors.DLQ_INVALID });
        Assert.True(resetSeq.IsAccepted, "a small (reset) seq with a fresh timestamp is LAWFUL — no monotonic enforcement (owner ruling)");
    }

    // ---- fact 4: channel validation — unknown channel is E422_UNKNOWN_CHANNEL --

    [Fact]
    public async Task an_unknown_channel_is_E422_UNKNOWN_CHANNEL()
    {
        var result = await Pipeline().RunAsync(Envelope(Payload("2026-11-13T11:59:30.000Z", 1, (1.0, "pressure"))));
        Assert.True(result.Rejected is { Code: IngestionErrors.E422_UNKNOWN_CHANNEL }, $"{result.Rejected?.Code}: {result.Rejected?.Detail}");
    }

    // ---- fact 5: non-finite values never pass (JSON cannot carry NaN; huge
    //      exponents may die at parse OR at range — the CONTRACT is: not accepted)

    [Fact]
    public async Task non_finite_values_are_never_accepted()
    {
        var json = """{"v":1,"ts":"2026-11-13T11:59:30.000Z","seq":1,"rr":[{"t":1e400,"c":"temp"}]}""";
        var huge = await Pipeline().RunAsync(Envelope(json));
        Assert.False(huge.IsAccepted);
        Assert.True(huge.Rejected!.Code is IngestionErrors.DLQ_INVALID or IngestionErrors.E422_RANGE, huge.Rejected.Code);
    }

    // ---- fact 6: the per-message channel-count bound (≤ 32 lawful shapes) ------

    [Fact]
    public async Task more_than_32_channels_per_message_is_structurally_rejected()
    {
        var keys = Enumerable.Range(0, 40).Select(i => $"ch{i:00}").ToArray();
        var pipe = Pipeline(keys);
        var lawful32 = await pipe.RunAsync(Envelope(Payload("2026-11-13T11:59:30.000Z", 1, keys.Take(32).Select(k => (1.0, k)).ToArray())));
        var overflow33 = await pipe.RunAsync(Envelope(Payload("2026-11-13T11:59:30.000Z", 2, keys.Take(33).Select(k => (1.0, k)).ToArray())));

        Assert.True(lawful32.IsAccepted, $"32 channels is lawful: {lawful32.Rejected?.Code} {lawful32.Rejected?.Detail}");
        Assert.True(overflow33.Rejected is { Code: IngestionErrors.DLQ_INVALID }, $"{overflow33.Rejected?.Code}");
    }

    // ---- fact 7: clock — future > 5 min clamps to received time (with E422_TS),
    //      future ≤ 5 min passes untouched (power-loss clock reset is common)

    [Fact]
    public async Task a_future_timestamp_beyond_5_minutes_is_clamped_with_a_warning()
    {
        var far = await Pipeline().RunAsync(Envelope(Payload("2026-11-13T12:10:00.000Z", 1, (1.0, "temp"))));   // +10 min
        var near = await Pipeline().RunAsync(Envelope(Payload("2026-11-13T12:03:00.000Z", 2, (1.0, "temp"))));  // +3 min

        Assert.True(far.IsAccepted);
        Assert.Equal(ReceivedAt, far.Accepted![0].TimeUtc);                       // clamped to received_at
        Assert.Contains(IngestionErrors.E422_TS, far.Warnings);

        Assert.True(near.IsAccepted);
        Assert.Equal(DateTimeOffset.Parse("2026-11-13T12:03:00.000Z"), near.Accepted![0].TimeUtc); // untouched
        Assert.DoesNotContain(IngestionErrors.E422_TS, near.Warnings);
    }

    // ---- fact 8: clock — older than 30 days accepted but FLAGGED; 29 days clean

    [Fact]
    public async Task a_timestamp_older_than_30_days_is_accepted_but_flagged()
    {
        var old = await Pipeline().RunAsync(Envelope(Payload("2026-10-10T12:00:00.000Z", 1, (1.0, "temp"))));   // 34 days
        var boundary = await Pipeline().RunAsync(Envelope(Payload("2026-10-15T12:00:00.000Z", 2, (1.0, "temp")))); // 29 days

        Assert.True(old.IsAccepted);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T12:00:00.000Z"), old.Accepted![0].TimeUtc);  // accepted as-is
        Assert.Contains(IngestionErrors.E422_TS, old.Warnings);

        Assert.True(boundary.IsAccepted);
        Assert.DoesNotContain(IngestionErrors.E422_TS, boundary.Warnings);
    }

    // ---- fact 9: an unknown DEVICE dies at the identity/channel edge (defense in
    //      depth — the doors normally catch it first; the pipeline never trusts)

    [Fact]
    public async Task an_unknown_device_is_rejected_never_normalized()
    {
        var stranger = new IngestEnvelope(Guid.NewGuid(), "t/x/dev/y/v1", Encoding.UTF8.GetBytes(Payload("2026-11-13T11:59:30.000Z", 1, (1.0, "temp"))), ReceivedAt);
        var result = await Pipeline().RunAsync(stranger);

        Assert.False(result.IsAccepted);
        Assert.True(result.Rejected!.Code is IngestionErrors.E422_UNKNOWN_CHANNEL, $"{result.Rejected.Code}");
    }
}
