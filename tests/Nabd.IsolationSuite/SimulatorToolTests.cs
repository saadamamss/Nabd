namespace Nabd.IsolationSuite;

using System.Text.Json;
using Nabd.Simulator;
using Xunit;

// P2 spec 06 — the simulator's PURE facts (no broker, no DB, no container):
// the payload-v1 wire contract and the fail-fast CLI. The live ladder (owner,
// 2026-11-12) witnessed the real device path end to end; these facts keep the
// two contracts that everything else leans on — what a device SAYS and what
// it REFUSES to run without.

public sealed class SimulatorToolTests
{
    // ---- payload v1: the wire contract (plan/05 §5) ---------------------------

    [Fact]
    public void payload_matches_the_wire_contract_byte_for_byte()
    {
        var ts = new DateTimeOffset(2026, 1, 15, 10, 30, 0, 123, TimeSpan.Zero);
        var payload = PayloadV1.Compose(14872, ts, [(5.1, "temperature")]);

        Assert.Equal(
            """{"v":1,"ts":"2026-01-15T10:30:00.123Z","seq":14872,"rr":[{"t":5.1,"c":"temperature"}]}""",
            payload);
    }

    [Fact]
    public void payload_forces_utc_a_local_clock_never_leaks()
    {
        // 13:30 +03:00 → the same instant as 10:30 Z — the offset must vanish.
        var local = new DateTimeOffset(2026, 1, 15, 13, 30, 0, 123, TimeSpan.FromHours(3));
        var payload = PayloadV1.Compose(1, local, [(1, "x")]);

        Assert.Contains("\"ts\":\"2026-01-15T10:30:00.123Z\"", payload);
        Assert.DoesNotContain("+03:00", payload);
    }

    [Fact]
    public void payload_carries_channels_in_the_given_order()
    {
        var payload = PayloadV1.Compose(7, DateTimeOffset.UtcNow,
            [(5.1, "temperature"), (0.62, "humidity")]);

        using var doc = JsonDocument.Parse(payload);
        var readings = doc.RootElement.GetProperty("rr");

        Assert.Equal(2, readings.GetArrayLength());
        Assert.Equal(("temperature", 5.1), (readings[0].GetProperty("c").GetString(), readings[0].GetProperty("t").GetDouble()));
        Assert.Equal(("humidity", 0.62), (readings[1].GetProperty("c").GetString(), readings[1].GetProperty("t").GetDouble()));
    }

    [Fact]
    public void payload_embeds_seq_verbatim()
    {
        var one = PayloadV1.Compose(1, DateTimeOffset.UtcNow, [(1, "x")]);
        var next = PayloadV1.Compose(2, DateTimeOffset.UtcNow, [(1, "x")]);

        Assert.Contains("\"seq\":1,", one);
        Assert.Contains("\"seq\":2,", next);
    }

    // ---- the scenario registry: steady only in v0 ------------------------------

    [Fact]
    public void steady_holds_its_values_across_ticks()
    {
        var scenario = ScenarioRegistry.Create("steady", [(5.1, "temperature")]);

        Assert.Equal(scenario.Tick(1), scenario.Tick(999));
        Assert.Equal([(5.1, "temperature")], scenario.Tick(1));
    }

    [Fact]
    public void an_unknown_scenario_is_refused_not_guessed()
    {
        var ex = Record.Exception(() =>
            ScenarioRegistry.Create("stead", [(1, "x")]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("v0 knows: steady", ex!.Message);
    }

    // ---- the CLI: fail fast at boot, never guess -------------------------------

    [Fact]
    public void parse_fills_the_defaults_around_the_required_core()
    {
        var options = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.1",
        ]);

        Assert.Equal("localhost", options.Host);
        Assert.Equal(1883, options.Port);
        Assert.Equal("steady", options.Scenario);
        Assert.Equal(1, options.Rate);
        Assert.Equal(Device, options.DeviceId);
        Assert.Equal(Token, options.Token);
        Assert.Equal(Tenant, options.TenantId);
    }

    [Fact]
    public void parse_reads_repeatable_channels_in_order()
    {
        var options = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.1", "--channel", "humidity=40",
        ]);

        Assert.Equal([(5.1, "temperature"), (40, "humidity")], options.Channels);
    }

    [Fact]
    public void parse_refuses_an_unknown_argument()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse(["--topik", "anything"]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("unknown argument", ex!.Message);
    }

    [Fact]
    public void parse_refuses_a_missing_required_arg()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--device", Device, "--tenant", Tenant, "--channel", "x=1", // --token missing
        ]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("required", ex!.Message);
    }

    [Fact]
    public void parse_refuses_a_mangled_device_id()
    {
        // the live-ladder lesson (2026-11-12): one extra char slipped into a uuid
        // passed silently through mosquitto — the tool catches it at boot instead.
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--device", Tenant + "0", // uuid-shaped, but NOT a uuid (one char over)
            "--token", Token, "--tenant", Tenant, "--channel", "x=1",
        ]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("--device", ex!.Message);
    }

    [Fact]
    public void parse_refuses_a_mangled_tenant_id()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", "beta-tenant", "--channel", "x=1",
        ]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("--tenant", ex!.Message);
    }

    [Fact]
    public void parse_refuses_a_non_numeric_channel_value()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=cold",
        ]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("key=number", ex!.Message);
    }

    private const string Device = "90b6f7c0-6394-4924-892b-677981fdae2b";
    private const string Tenant = "d3f448c2-af51-48a8-9a72-b23b6ba6e297";
    private const string Token = "not-a-real-token-just-for-parsing";

    // ---- P3 spec 08: spike + dropout -------------------------------------------

    [Fact]
    public void spike_peaks_on_schedule_and_holds_baseline_between_plateaus()
    {
        var scenario = ScenarioRegistry.Create("spike", [(5.0, "temperature")],
            spikeTo: 42.5, spikeEvery: 5, spikeHoldFor: 2);

        Assert.Equal([(42.5, "temperature")], scenario.Tick(1));   // the plateau heads each block
        Assert.Equal([(42.5, "temperature")], scenario.Tick(2));
        Assert.Equal([(5.0, "temperature")], scenario.Tick(3));    // back to baseline
        Assert.Equal([(5.0, "temperature")], scenario.Tick(5));
        Assert.Equal([(42.5, "temperature")], scenario.Tick(6));   // next block's plateau
        Assert.Equal([(5.0, "temperature")], scenario.Tick(8));
    }

    [Fact]
    public void spike_peak_lands_in_the_payloads_t_field()
    {
        var scenario = ScenarioRegistry.Create("spike", [(5.0, "temperature")],
            spikeTo: 42.5, spikeEvery: 5, spikeHoldFor: 2);
        var json = PayloadV1.Compose(7, DateTimeOffset.UtcNow, scenario.Tick(7));

        Assert.Contains("42.5", json);
    }

    [Fact]
    public void parse_reads_the_spike_and_run_for_flags()
    {
        var spike = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0",
            "--scenario", "spike", "--spike-to", "42.5", "--every", "5", "--hold-for", "2",
        ]);
        Assert.Equal(42.5, spike.SpikeTo);
        Assert.Equal(5, spike.SpikeEvery);
        Assert.Equal(2, spike.SpikeHoldFor);

        var bounded = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0", "--run-for", "30",
        ]);
        Assert.Equal(30.0, bounded.RunFor);
    }

    [Fact]
    public void spike_refuses_to_start_without_its_three_flags()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0", "--scenario", "spike",
        ]));

        Assert.IsType<ArgumentException>(ex);
    }

    [Fact]
    public void dropout_refuses_to_start_without_run_for()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0", "--scenario", "dropout",
        ]));

        Assert.IsType<ArgumentException>(ex);
    }

    [Fact]
    public void dropout_publishes_normally_until_the_clean_stop()
    {
        // dropout = steady values; the stop is --run-for's job, not a value pattern
        var scenario = ScenarioRegistry.Create("dropout", [(5.0, "temperature")]);

        Assert.Equal(scenario.Tick(1), scenario.Tick(999));
    }

    [Fact]
    public void dropout_stops_on_schedule_and_names_the_last_seq()
    {
        var options = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0", "--run-for", "30",
        ]);

        Assert.False(Dropout.Due(options, TimeSpan.FromSeconds(29)));
        Assert.True(Dropout.Due(options, TimeSpan.FromSeconds(30)));
        Assert.Equal("dropout: stopped after 30 s, last seq=1172", Dropout.StopLine(30, 1172));
    }

    // ---- P3 spec 09: the duplicate-flood rider's --dup --------------------------

    [Fact]
    public void parse_reads_dup_and_defaults_to_one()
    {
        var plain = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0",
        ]);
        Assert.Equal(1, plain.Dup);

        var rider = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0", "--dup", "10",
        ]);
        Assert.Equal(10, rider.Dup);
    }

    [Fact]
    public void parse_refuses_a_non_positive_dup()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0", "--dup", "0",
        ]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("--dup", ex!.Message);
    }

    // ---- THE FLEET MODE (--devices-file, owner ruling 2026-11-17): one ------
    // ---- PROCESS carries many devices — the instrumented gate caught the ----
    // ---- 120-process fleet itself burning 4.3 of 8 cores (sched overhead) ---
    // ---- while EMQX idled at 71% of ONE core. Fewer processes, same devices -

    [Fact]
    public void parse_reads_a_devices_file_into_the_fleet_list()
    {
        var file = Path.Combine(Path.GetTempPath(), $"nabd-sim-{Guid.NewGuid():N}.tsv");
        File.WriteAllLines(file,
        [
            $"{Device}\t{Token}",
            $"{Tenant}\tsecond-token",
            "",                                  // blank line — skipped, never guessed
            "# a comment — skipped",
        ]);
        try
        {
            var options = SimulatorOptions.Parse([
                "--devices-file", file, "--tenant", Tenant,
                "--channel", "temperature=5.0",
            ]);

            Assert.Equal([(Device, Token), (Tenant, "second-token")], options.Devices);
            Assert.Null(options.DeviceId);       // the single-device core is ABSENT in fleet mode
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void parse_refuses_devices_file_combined_with_the_single_device_core()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--devices-file", "/tmp/whatever.tsv", "--device", Device,
            "--tenant", Tenant, "--channel", "x=1",
        ]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("--devices-file", ex!.Message);
    }

    [Fact]
    public void parse_refuses_a_devices_file_without_the_tenant()
    {
        var ex = Record.Exception(() =>
            SimulatorOptions.Parse(["--devices-file", "/tmp/whatever.tsv", "--channel", "x=1"]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("--tenant", ex!.Message);
    }

    [Fact]
    public void parse_refuses_an_unreadable_devices_file()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--devices-file", "/nonexistent/no-such-file.tsv", "--tenant", Tenant, "--channel", "x=1",
        ]));

        Assert.IsType<ArgumentException>(ex);  // fail fast AS a usage error — the caller's catch exits 2
        Assert.Contains("--devices-file", ex!.Message);
    }

    [Fact]
    public void parse_refuses_an_empty_devices_file()
    {
        var file = Path.Combine(Path.GetTempPath(), $"nabd-sim-{Guid.NewGuid():N}.tsv");
        File.WriteAllLines(file, ["# only a comment", ""]);
        try
        {
            var ex = Record.Exception(() => SimulatorOptions.Parse([
                "--devices-file", file, "--tenant", Tenant, "--channel", "x=1",
            ]));

            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("no devices", ex!.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void parse_refuses_a_devices_file_line_missing_its_token()
    {
        var file = Path.Combine(Path.GetTempPath(), $"nabd-sim-{Guid.NewGuid():N}.tsv");
        File.WriteAllLines(file, [$"{Device}\t{Token}", Tenant]);   // second line: no tab, no token
        try
        {
            var ex = Record.Exception(() => SimulatorOptions.Parse([
                "--devices-file", file, "--tenant", Tenant, "--channel", "x=1",
            ]));

            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("dev<TAB>token", ex!.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void parse_refuses_a_devices_file_with_a_mangled_device_id()
    {
        var file = Path.Combine(Path.GetTempPath(), $"nabd-sim-{Guid.NewGuid():N}.tsv");
        File.WriteAllLines(file, [$"{Tenant}0\t{Token}"]);          // uuid-shaped, NOT a uuid
        try
        {
            var ex = Record.Exception(() => SimulatorOptions.Parse([
                "--devices-file", file, "--tenant", Tenant, "--channel", "x=1",
            ]));

            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("uuid", ex!.Message);
        }
        finally { File.Delete(file); }
    }

    // ---- THE GRID ANCHOR (--start-epoch, owner ruling 2026-11-17 after the ----
    // ---- official referee failed at 1,998.5/s: the deadline grid anchored  ----
    // ---- at each device's CONNECT (~0.45 s late), so the fleet published  ----
    // ---- 905/1,200,000 short of the profile — the harness undershooting,  ----
    // ---- the pipeline 100.0000%. The anchor makes the fleet publish the   ----
    // ---- profile EXACTLY; the catch-up burst clears the connect ramp.     ----

    [Fact]
    public void parse_reads_the_start_epoch_and_defaults_to_null()
    {
        var anchored = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0", "--start-epoch", "1764000000.5",
        ]);
        Assert.Equal(1_764_000_000.5, anchored.StartEpoch);

        var plain = SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0",
        ]);
        Assert.Null(plain.StartEpoch);       // no anchor → the grid starts at connect (single-device law)
    }

    [Fact]
    public void parse_refuses_a_non_numeric_start_epoch()
    {
        var ex = Record.Exception(() => SimulatorOptions.Parse([
            "--device", Device, "--token", Token, "--tenant", Tenant,
            "--channel", "temperature=5.0", "--start-epoch", "now",
        ]));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains("--start-epoch", ex!.Message);
    }
}
