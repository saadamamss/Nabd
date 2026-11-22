namespace Nabd.IsolationSuite;

using Nabd.Simulator;
using Xunit;

// P4 spec 10 (D10h) — the `warmup-demo` profile, pure facts. No broker, no DB,
// no wall clock: the story is a function of (seq, tick rate) only.
//
// RED-FIRST: `WarmupDemo`, `WarmupDemoScenario`, the `warmup-demo` branch of
// `ScenarioRegistry.Create`, and the `WarmupChannel`/`WarmupPeak` options do
// not exist yet — the build fails on purpose until the owner implants the code
// from study/implementation-code.md (RULES §9.1). The agent owns this net.
//
// The laws this net pins:
//   1. the four scripted phases start at the exact seconds (0/60/66/126).
//   2. the door beat sits above the limit for exactly 6 s, then returns to the
//      baseline — a 6 s excursion cannot satisfy a 60 s window.
//   3. at 1/s the first ramp sample above 8 °C is t=97 (seq 98; t=96 is exactly
//      8.0 and the rule is strict `>`) and the firing sample is t=157 (seq 158).
//   4. at 5/s the same 60 s window holds; the sampled crossing may move by less
//      than one sample interval because the exact crossing is not on the grid.
//   5. the same scripted second yields the same value at any rate; other
//      channels ride constant; no wall clock and no --start-epoch enters.
//   6. the parser refuses the three lies: no --run-for, absent --warmup-channel,
//      and peak <= baseline; a valid warmup run still accepts --start-epoch.
//   7. the registry lists the fourth scenario without touching steady/spike/
//      dropout, and the stop line names `warmup-demo`, never `dropout`.

public sealed class WarmupDemoTests
{
    private static readonly (double Value, string Channel)[] Fridge = [(4.0, "temperature")];

    private static WarmupDemo Demo(double rate = 1) => new(4.0, 12.0, rate);

    // ---- fact 1 — the scripted phases start at their seconds -------------------

    [Fact]
    public void phases_are_stable_door_ramp_hold_at_their_scripted_seconds()
    {
        var demo = Demo();

        Assert.Equal("stable", demo.PhaseAt(1));    // t=0
        Assert.Equal("stable", demo.PhaseAt(60));   // t=59
        Assert.Equal("door", demo.PhaseAt(61));     // t=60
        Assert.Equal("door", demo.PhaseAt(66));     // t=65
        Assert.Equal("ramp", demo.PhaseAt(67));     // t=66
        Assert.Equal("ramp", demo.PhaseAt(126));    // t=125
        Assert.Equal("hold", demo.PhaseAt(127));    // t=126
    }

    // ---- fact 2 — the door beat is short by construction ------------------------

    [Fact]
    public void the_door_beat_is_six_seconds_above_the_limit_and_then_returns()
    {
        var demo = Demo();

        for (var seq = 61; seq <= 66; seq++)
            Assert.True(demo.ValueAt(seq) > WarmupDemo.LimitCelsius,
                $"door sample seq {seq} must be above {WarmupDemo.LimitCelsius}");

        // t=66 is the ramp's first sample: back at the baseline, the excursion over
        Assert.Equal(4.0, demo.ValueAt(67), 6);

        // and the point of the beat: it can never fill the rule's window
        Assert.True(WarmupDemo.DoorSeconds < WarmupDemo.SignalWindowSeconds);
    }

    // ---- fact 3 — 1/s: crossing t=97, firing t=157 ------------------------------

    [Fact]
    public void at_one_hertz_the_crossing_is_t97_and_the_firing_sample_is_t157()
    {
        var demo = Demo(1);

        var first = demo.FirstAbove(WarmupDemo.LimitCelsius);
        var fire = demo.FirstWindowMet(WarmupDemo.LimitCelsius, WarmupDemo.SignalWindowSeconds);

        Assert.Equal(98L, first!.Value);
        Assert.Equal(97.0, demo.SecondsAt(first.Value), 6);

        // t=96 is EXACTLY 8.0 — the rule is strict `>`, so 97 is the first violating sample
        Assert.False(demo.ValueAt(97) > WarmupDemo.LimitCelsius);

        Assert.Equal(158L, fire!.Value);
        Assert.Equal(157.0, demo.SecondsAt(fire.Value), 6);
        Assert.Equal(60.0, demo.SecondsAt(fire.Value) - demo.SecondsAt(first.Value), 6);
    }

    // ---- fact 4 — 5/s tells the same 60 s story on a different grid -------------

    [Fact]
    public void five_hertz_preserves_the_sixty_second_window_in_event_time()
    {
        var one = Demo(1);
        var five = Demo(5);

        var firstOne = one.FirstAbove(8.0)!.Value;
        var firstFive = five.FirstAbove(8.0)!.Value;
        var fireOne = one.FirstWindowMet(8.0, 60)!.Value;
        var fireFive = five.FirstWindowMet(8.0, 60)!.Value;

        // the window itself is exactly 60 s on BOTH grids
        Assert.Equal(60.0, one.SecondsAt(fireOne) - one.SecondsAt(firstOne), 6);
        Assert.Equal(60.0, five.SecondsAt(fireFive) - five.SecondsAt(firstFive), 6);

        // the sampled crossing moves by less than one sample interval: the exact
        // continuous crossing (t=96) is on the 1/s grid but not on the 5/s grid
        Assert.True(Math.Abs(one.SecondsAt(firstOne) - five.SecondsAt(firstFive)) < 1.0);
        Assert.Equal(482L, firstFive);
        Assert.Equal(782L, fireFive);
    }

    // ---- fact 5 — the profile is a function of seq only ------------------------

    [Fact]
    public void the_same_scripted_second_yields_the_same_value_at_any_rate()
    {
        var one = Demo(1);
        var five = Demo(5);

        // t=100 is representable on both grids: 1/s seq 101, 5/s seq 501
        Assert.Equal(one.ValueAt(101), five.ValueAt(501), 6);
        Assert.Equal(one.PhaseAt(101), five.PhaseAt(501));

        // a pure function: repeated calls cannot drift
        Assert.Equal(one.ValueAt(158), one.ValueAt(158), 12);
        var scenario = new WarmupDemoScenario("temperature", 12.0, 1, Fridge);
        Assert.Equal(scenario.Tick(158), scenario.Tick(158));

        // one channel warms, the others ride constant
        var two = new WarmupDemoScenario(
            "temperature", 12.0, 1, [(4.0, "temperature"), (50.0, "humidity")]);
        Assert.Equal((50.0, "humidity"), two.Tick(200).Single(r => r.Channel == "humidity"));
        Assert.Equal(one.ValueAt(200), two.Tick(200).Single(r => r.Channel == "temperature").Value, 6);
    }

    // ---- fact 6 — the parser's contract ----------------------------------------

    [Fact]
    public void the_parser_refuses_missing_run_for_missing_channel_and_peak_at_or_below_baseline()
    {
        static string[] Args(params string[] extra) =>
        [
            "--device", Guid.NewGuid().ToString(),
            "--token", "suite-token",
            "--tenant", Guid.NewGuid().ToString(),
            "--channel", "temperature=4",
            "--scenario", "warmup-demo",
            .. extra,
        ];

        var missingRunFor = Assert.Throws<ArgumentException>(() =>
            SimulatorOptions.Parse(Args()));
        Assert.Contains("--run-for", missingRunFor.Message);

        var missingChannel = Assert.Throws<ArgumentException>(() =>
            SimulatorOptions.Parse(Args("--run-for", "310", "--warmup-channel", "humidity")));
        Assert.Contains("humidity", missingChannel.Message);

        var lowPeak = Assert.Throws<ArgumentException>(() =>
            SimulatorOptions.Parse(Args("--run-for", "310", "--warmup-peak", "4")));
        Assert.Contains("above the baseline", lowPeak.Message);

        // a valid run, and --start-epoch is NOT part of the profile
        var ok = SimulatorOptions.Parse(
            Args("--run-for", "310", "--warmup-peak", "12.5", "--start-epoch", "1700000000"));
        Assert.Equal("warmup-demo", ok.Scenario);
        Assert.Equal("temperature", ok.WarmupChannel);
        Assert.Equal(12.5, ok.WarmupPeak);
        Assert.Equal(1700000000.0, ok.StartEpoch!.Value, 6);
    }

    // ---- fact 7 — the registry and the stop line -------------------------------

    [Fact]
    public void the_registry_lists_warmup_demo_and_the_stop_line_names_it()
    {
        var warm = Assert.IsType<WarmupDemoScenario>(
            ScenarioRegistry.Create("warmup-demo", Fridge, tickRate: 1));
        Assert.Equal(4.0, warm.Profile.ValueAt(1), 6);

        // the three older scenarios keep their exact shapes
        Assert.IsType<SteadyScenario>(ScenarioRegistry.Create("steady", Fridge));
        Assert.IsType<SpikeScenario>(ScenarioRegistry.Create("spike", Fridge, 10.0, 3, 2));
        Assert.IsType<SteadyScenario>(ScenarioRegistry.Create("dropout", Fridge));

        Assert.Equal("warmup-demo: stopped after 310 s, last seq=310",
            WarmupDemo.StopLine(310, 310));
        // the dropout line is byte-pinned by spec 08 — never relabelled
        Assert.Equal("dropout: stopped after 30 s, last seq=1172", Dropout.StopLine(30, 1172));
    }
}
