namespace Nabd.IsolationSuite;

using System.Text;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Xunit;

// P4 spec 01 — STEP 1: the flush buffer's LAWS, before any pump exists.
// (a) R9: the SIZE trigger counts READINGS (the flush bound's unit), not
//     messages — one message may carry up to 32 of them; overshoot is legal
//     and bounded (FlushSize + MaxReadingsPerMessage - 1);
// (b) fact 9 (buffer half): a parse-rejected delivery NEVER enters the
//     accepted stream — and content validation is NOT the buffer's job
//     (an unknown channel parses fine; its verdict happens at the flush);
// (c) R10's re-buffer law: drain → requeue restores everything, pending
//     untouched (draining is not acking — the window law);
// (d) fact 16: the flush bounds are fail-shut (NFR-08 pattern).

public sealed class FlushBufferTests
{
    private static readonly Guid Device = Guid.NewGuid();
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static Func<CancellationToken, Task> NoAck => _ => Task.CompletedTask;

    private static IngestEnvelope Envelope(string json)
        => new(Device, $"t/t/dev/{Device}/v1", Encoding.UTF8.GetBytes(json), ReceivedAt);

    private static string Payload(string ts, long seq, params (double Value, string Channel)[] readings)
    {
        var rr = string.Join(",", readings.Select(r =>
            $"{{\"t\":{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"c\":\"{r.Channel}\"}}"));
        return $$"""{"v":1,"ts":"{{ts}}","seq":{{seq}},"rr":[{{rr}}]}""";
    }

    // ---- fact 1 (buffer half): the size trigger counts READINGS (R9) --------

    [Fact]
    public async Task the_size_trigger_counts_readings_not_messages()
    {
        var buffer = new FlushBuffer(flushSize: 200);

        // 66 messages × 3 readings = 198 readings — NOT crossed yet, with 66
        // messages. A message-counting trigger would have fired long ago.
        for (var seq = 1; seq <= 66; seq++)
            buffer.Offer(Envelope(Payload("2026-11-16T11:59:30.000Z", seq,
                (1.0, "t"), (2.0, "t"), (3.0, "t"))), NoAck);

        Assert.False(buffer.TakeSizeTrigger().IsCompleted, "198 < 200 — must NOT fire");
        Assert.Equal(198, buffer.BufferedReadings);
        Assert.Equal(66, buffer.PendingMessages);

        buffer.Offer(Envelope(Payload("2026-11-16T11:59:30.000Z", 67,
            (1.0, "t"), (2.0, "t"), (3.0, "t"))), NoAck);
        Assert.True(buffer.TakeSizeTrigger().IsCompleted, "201 >= 200 — crossed");
        Assert.Equal(201, buffer.BufferedReadings);
    }

    // ---- fact 1 (buffer half): overshoot is legal, bounded, trigger fires ----

    [Fact]
    public async Task a_crossing_mid_message_fires_the_trigger_and_overshoot_is_bounded()
    {
        var buffer = new FlushBuffer(flushSize: 200);
        for (var seq = 1; seq <= 199; seq++)
            buffer.Offer(Envelope(Payload("2026-11-16T11:59:30.000Z", seq, (1.0, "t"))), NoAck);
        Assert.False(buffer.TakeSizeTrigger().IsCompleted);

        // ONE 32-reading message lands the buffer at 231 — overshoot ≤ 32
        // (R9's bound: FlushSize + MaxReadingsPerMessage - 1); the trigger
        // fires ON the crossing, mid-message.
        var rr = string.Join(",", Enumerable.Range(0, 32).Select(_ => "{\"t\":1.0,\"c\":\"t\"}"));
        buffer.Offer(Envelope($$"""{"v":1,"ts":"2026-11-16T11:59:30.000Z","seq":200,"rr":[{{rr}}]}"""), NoAck);

        Assert.True(buffer.TakeSizeTrigger().IsCompleted, "231 >= 200 — crossed mid-message");
        Assert.Equal(231, buffer.BufferedReadings);
    }

    // ---- fact 9: a parse rejection NEVER enters the accepted stream ----------

    [Fact]
    public void a_parse_rejection_never_enters_the_accepted_stream()
    {
        var buffer = new FlushBuffer(flushSize: 200);
        buffer.Offer(Envelope("this is not json"), NoAck);

        Assert.Equal(1, buffer.ParseRejectsCount);
        Assert.Equal(0, buffer.AcceptedCount);
        Assert.Equal(0, buffer.BufferedReadings);   // unparseable bytes carry no lawful readings
        Assert.Equal(1, buffer.PendingMessages);    // still unacked — the pump will receipt + ack it
    }

    // ---- fact 9 (companion): CONTENT validation is NOT the buffer's job ------

    [Fact]
    public void content_validation_is_not_the_buffers_job()
    {
        var buffer = new FlushBuffer(flushSize: 200);

        // "pressure" is in no device's template — but the SHAPE is lawful.
        // The buffer accepts-parse; the E422 verdict happens at the flush
        // (facts 6/7's world, per-message and whole).
        buffer.Offer(Envelope(Payload("2026-11-16T11:59:30.000Z", 1, (1.0, "pressure"))), NoAck);
        Assert.Equal(1, buffer.AcceptedCount);

        // "1e400" parses to ±Infinity WITHOUT throwing (the parser's own
        // documented behavior) — again: accepted-parse here, E422_RANGE
        // at the flush. Nothing malformed-SHAPED is special-cased here.
        buffer.Offer(Envelope("""{"v":1,"ts":"2026-11-16T11:59:30.000Z","seq":2,"rr":[{"t":1e400,"c":"t"}]}"""), NoAck);
        Assert.Equal(2, buffer.AcceptedCount);
    }

    // ---- R10's re-buffer law: drain → requeue restores everything -------------

    [Fact]
    public void a_transient_abort_requeues_everything_untouched()
    {
        var buffer = new FlushBuffer(flushSize: 200);
        buffer.Offer(Envelope(Payload("2026-11-16T11:59:30.000Z", 1, (1.0, "t"), (2.0, "t"))), NoAck);

        var drained = buffer.DrainAccepted();
        Assert.Equal(0, buffer.AcceptedCount);
        Assert.Equal(0, buffer.BufferedReadings);
        Assert.Equal(1, buffer.PendingMessages);   // draining is NOT acking

        buffer.RequeueAccepted(drained);
        Assert.Equal(1, buffer.AcceptedCount);
        Assert.Equal(2, buffer.BufferedReadings);
        Assert.Equal(1, buffer.PendingMessages);   // requeue moves NOTHING — still unacked

        var rejects = buffer.DrainParseRejects();
        Assert.Empty(rejects);                     // the two streams never mix
        buffer.RequeueParseRejects(rejects);       // and requeueing nothing is a no-op
        Assert.Equal(2, buffer.BufferedReadings);
    }

    // ---- the window law: pending lowers ONLY on acks (R5/R7) ------------------

    [Fact]
    public void pending_lowers_only_on_acks()
    {
        var buffer = new FlushBuffer(flushSize: 200);
        buffer.Offer(Envelope(Payload("2026-11-16T11:59:30.000Z", 1, (1.0, "t"))), NoAck);
        buffer.Offer(Envelope(Payload("2026-11-16T11:59:30.000Z", 2, (1.0, "t"))), NoAck);
        Assert.Equal(2, buffer.PendingMessages);

        var drained = buffer.DrainAccepted();
        Assert.Equal(2, buffer.PendingMessages);   // drain ≠ ack
        buffer.RequeueAccepted(drained);           // abort: back they go, still pending
        Assert.Equal(2, buffer.PendingMessages);

        buffer.Acked(2);
        Assert.Equal(0, buffer.PendingMessages);   // exactly n acks lower it by n
    }

    // ---- fact 16: the flush bounds are fail-shut (NFR-08 pattern) --------------

    [Fact]
    public void the_flush_bounds_are_fail_shut()
    {
        Assert.Throws<InvalidOperationException>(() => FlushOptions.From(0, 100));
        Assert.Throws<InvalidOperationException>(() => FlushOptions.From(FlushOptions.MaxReceiveMaximum + 1, 100));
        Assert.Throws<InvalidOperationException>(() => FlushOptions.From(200, 0));
        Assert.Throws<InvalidOperationException>(() => FlushOptions.From(200, -5));

        var ok = FlushOptions.From(200, 100);
        Assert.Equal(200, ok.FlushSize);
        Assert.Equal(100, ok.FlushIntervalMs);
    }
}