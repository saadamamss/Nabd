namespace Nabd.IsolationSuite;

using Microsoft.Extensions.Logging.Abstractions;
using Nabd.Ingestion;
using Xunit;

/// <summary>
/// The reconciliation watchdog (P4 spec 02): delivered readings must reach a
/// durable outcome — stored, rejected-with-evidence, or handed back to the
/// broker — and any unexplained remainder while the pipeline is QUIET is an
/// alarm with the EXACT delta (memory lessons 30–34 turned into a guard).
///
/// THE EQUATION: deficit = delivered − stored − rejected − handedBack.
/// THE QUIET LAW: alarm only when the buffer is empty AND no flush attempt is
/// active — bounded work in flight is the grace, never a lie.
/// THE HAND-BACK LAW: shutdown-bound/cancelled readings re-ride the broker's
/// redelivery (QoS1) — the broker owns them again, so they are not a deficit.
/// </summary>
public sealed class ReconcileWatchdogTests
{
    private static ReconcileWatchdog Watchdog(ReconcileCounters ledger) =>
        new(ledger, NullLogger<ReconcileWatchdog>.Instance);

    // ---- fact 1 — the clean pipeline is quiet: no alarm, ever ---------------

    [Fact]
    public void calm_when_every_delivered_reading_is_accounted()
    {
        var ledger = new ReconcileCounters();
        ledger.Delivered(10);
        ledger.Stored(10);

        var tick = Watchdog(ledger).Tick(bufferQuiet: true, flushInFlight: false);

        Assert.Equal(ReconcileVerdict.Calm, tick.Verdict);
        Assert.Equal(0, ledger.Alarms);
    }

    // ---- fact 2 — vanished readings alarm with the EXACT delta --------------

    [Fact]
    public void alarmed_with_the_exact_delta_when_readings_vanish()
    {
        var ledger = new ReconcileCounters();
        ledger.Delivered(10);
        ledger.Stored(7);   // 3 readings: accepted, quiet pipeline, no outcome

        var tick = Watchdog(ledger).Tick(bufferQuiet: true, flushInFlight: false);

        Assert.Equal(ReconcileVerdict.Alarmed, tick.Verdict);
        Assert.Equal(3, tick.Delta);
        Assert.Equal(1, ledger.Alarms);
        Assert.Equal(3, ledger.LastAlarmDelta);
    }

    // ---- fact 3/4 — bounded work in flight is grace, not a lie --------------

    [Fact]
    public void calm_while_the_buffer_still_holds_work()
    {
        var ledger = new ReconcileCounters();
        ledger.Delivered(10);
        ledger.Stored(0);   // all 10 buffered, none flushed yet

        var tick = Watchdog(ledger).Tick(bufferQuiet: false, flushInFlight: false);

        Assert.Equal(ReconcileVerdict.Calm, tick.Verdict);
        Assert.Equal(0, ledger.Alarms);
    }

    [Fact]
    public void calm_while_a_flush_attempt_is_active()
    {
        var ledger = new ReconcileCounters();
        ledger.Delivered(10);
        ledger.Stored(0);   // drained into the attempt, not yet committed

        var tick = Watchdog(ledger).Tick(bufferQuiet: true, flushInFlight: true);

        Assert.Equal(ReconcileVerdict.Calm, tick.Verdict);
        Assert.Equal(0, ledger.Alarms);
    }

    // ---- fact 5 — the hand-back law (shutdown-bound = the broker's again) ----

    [Fact]
    public void handed_back_readings_never_alarm()
    {
        var ledger = new ReconcileCounters();
        ledger.Delivered(10);
        ledger.HandedBack(10);   // final-flush BOUND expired: zero acks, broker re-queues

        var tick = Watchdog(ledger).Tick(bufferQuiet: true, flushInFlight: false);

        Assert.Equal(ReconcileVerdict.Calm, tick.Verdict);
        Assert.Equal(0, ledger.Alarms);
    }

    // ---- fact 6 — rejections are accounting (evidence rows, never silent) ----

    [Fact]
    public void rejections_account_their_readings()
    {
        var ledger = new ReconcileCounters();
        ledger.Delivered(10);
        ledger.Rejected(10);   // raw_rejects with evidence — a durable outcome

        var tick = Watchdog(ledger).Tick(bufferQuiet: true, flushInFlight: false);

        Assert.Equal(ReconcileVerdict.Calm, tick.Verdict);
        Assert.Equal(0, ledger.Alarms);
    }

    // ---- fact 7 — the alarm persists until accounting closes it -------------

    [Fact]
    public void alarm_repeats_until_accounting_closes_it()
    {
        var ledger = new ReconcileCounters();
        ledger.Delivered(10);
        ledger.Stored(4);
        var watchdog = Watchdog(ledger);

        Assert.Equal(ReconcileVerdict.Alarmed, watchdog.Tick(true, false).Verdict);
        Assert.Equal(ReconcileVerdict.Alarmed, watchdog.Tick(true, false).Verdict);
        Assert.Equal(2, ledger.Alarms);

        ledger.Stored(6);   // the missing rows finally commit
        Assert.Equal(ReconcileVerdict.Calm, watchdog.Tick(true, false).Verdict);
        Assert.Equal(2, ledger.Alarms);
    }

    // ---- fact 8 — the health snapshot carries the arms -----------------------

    [Fact]
    public void snapshot_carries_the_health_arms()
    {
        var ledger = new ReconcileCounters();
        ledger.Delivered(10);
        ledger.Stored(6);
        ledger.Rejected(2);
        ledger.HandedBack(1);
        Watchdog(ledger).Tick(true, false);   // alarmed once (delta 1)

        var snap = ledger.Snapshot();

        Assert.Equal(10, snap.Delivered);
        Assert.Equal(6, snap.Stored);
        Assert.Equal(2, snap.Rejected);
        Assert.Equal(1, snap.HandedBack);
        Assert.Equal(1, snap.Deficit);
        Assert.Equal(1, snap.Alarms);
        Assert.Equal(1, snap.LastAlarmDelta);
    }

    // ---- fact 9 — negative delivered is impossible by construction ----------

    [Fact]
    public void negative_counts_are_refused_fail_shut()
    {
        var ledger = new ReconcileCounters();

        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Delivered(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Stored(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Rejected(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.HandedBack(-1));
    }
}
