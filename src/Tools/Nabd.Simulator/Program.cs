using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Nabd.Simulator;

/// <summary>
/// Simulator (05 §10 contract): a *real* MQTT device — same doors, same tokens, same topics.
/// Scenarios per 05 §10 / 09 §2: steady, spike, dropout, replay, warmup-demo.
/// P0: prints the plan. P2 gives it MQTT (MQTTnet) + a real device token (FR-17).
/// Being "just another device" is what makes demos able to never diverge from the tested path (risk P4, 11 §5).
/// FLEET MODE (owner ruling 2026-11-17): --devices-file lets ONE process carry
/// many devices, one connection and one pacing loop each — the instrumented
/// gate caught the 120-process fleet itself burning 4.3 of 8 cores on
/// scheduler overhead while the broker idled at 71 % of one core.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        const string banner = """
            ┌──────────────────────────────────────────────────────┐
            │  Nabd Simulator — P0 stub                            │
            │  Planned scenarios: steady · spike · dropout ·       │
            │  replay · warmup-demo (05 §10)                       │
            │  P2: connects as a real device (MQTT/TLS + token)    │
            └──────────────────────────────────────────────────────┘
            """;
        Console.WriteLine(banner);
        Console.WriteLine("Phase wiring: P2 MQTT + tokens → P3 spike/dropout/replay → P4 warmup-demo (the launch story, 10 §3).");

        SimulatorOptions options;
        try
        {
            options = SimulatorOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2; // usage error — fail fast, never guess, never retry
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        var factory = new MqttClientFactory();

        if (options.Devices is not null)
        {
            Console.WriteLine(
                $"nabd-simulator v0 — FLEET {options.Devices.Count} device(s) → {options.Host}:{options.Port} @ {options.Rate}/s/device ({options.Scenario})");
            var tasks = options.Devices
                .Select(d => RunDeviceAsync(options, factory, d.DeviceId, d.Token, stopping.Token))
                .ToArray();
            var results = await Task.WhenAll(tasks);
            return results.Min();
        }

        return await RunDeviceAsync(options, factory, options.DeviceId!, options.Token!, stopping.Token);
    }

    /// <summary>
    /// ONE device's whole life — the pre-fleet Main loop, unchanged in its
    /// laws (deadline pacing, the seq-hold law, --dup, the firmware backoff),
    /// now parameterized by (deviceId, token) so a fleet task and the
    /// single-device run share the exact same wire behavior.
    /// An isolated device failure never takes the process down — its task
    /// rides the backoff, exactly like a single-device run.
    /// </summary>
    private static async Task<int> RunDeviceAsync(
        SimulatorOptions options, MqttClientFactory factory,
        string deviceId, string token, CancellationToken stopping)
    {
        var topic = $"t/{options.TenantId}/dev/{deviceId}/v1"; // 05 §4 — the wire law, composed from parts
        var scenario = ScenarioRegistry.Create(options.Scenario, options.Channels,
            options.SpikeTo, options.SpikeEvery, options.SpikeHoldFor,
            options.Rate, options.WarmupChannel, options.WarmupPeak);

        Console.WriteLine($"nabd-simulator v0 — device {deviceId} → {options.Host}:{options.Port} topic {topic} @ {options.Rate}/s ({options.Scenario})");

        // P4 spec 10: the warmup story's own markers. Derived once, from the
        // same pure profile the wire uses — never re-derived from wall clock.
        var warmup = (scenario as WarmupDemoScenario)?.Profile;
        var crossedAt = warmup?.FirstAbove(WarmupDemo.LimitCelsius);
        var windowAt = warmup?.FirstWindowMet(WarmupDemo.LimitCelsius, WarmupDemo.SignalWindowSeconds);
        string? warmupPhase = null;

        var random = new Random();
        var seq = 0L;
        var attempt = 0;
        var startedAt = DateTimeOffset.UtcNow;

        while (!stopping.IsCancellationRequested)
        {
            var broker = factory.CreateMqttClient();
            try
            {
                var connect = await broker.ConnectAsync(new MqttClientOptionsBuilder()
                    .WithTcpServer(options.Host, options.Port)
                    .WithCredentials(deviceId, token)
                    .WithProtocolVersion(MqttProtocolVersion.V311) // plan/05 §3: 3.1.1 minimum; 5's features kept for the future
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                    .Build(), stopping);

                if (connect.ResultCode != MqttClientConnectResultCode.Success)
                    throw new MqttCommunicationException($"broker refused connect: {connect.ResultCode}");

                attempt = 0; // a real connect resets the backoff ladder
                Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] connected — publishing to {topic}");

                // DEADLINE PACING (owner-ruled pressure fix, 2026-11-16): the old
                // law slept the FULL interval AFTER each publish — every cycle
                // paid interval + PUBACK RTT, so a 20/s device delivered ~15.4/s
                // under load and the fleet capped at ~1,830/s (both referee runs).
                // The deadline schedule publishes at start + n/rate regardless of
                // RTT: a slow PUBACK shifts the next publish, never accumulates
                // drift; after an outage the schedule catches up (no sleep while
                // overdue — a burst, then back on the grid).
                //
                // THE GRID ANCHOR (owner ruling 2026-11-17, official referee
                // failed at 1,998.5/s = 905/1,200,000 short of the profile):
                // with --start-epoch the grid anchors at the FLEET's shared
                // epoch, not each device's connect — the owed ticks burst at
                // connect and the flight publishes the profile EXACTLY.
                var nextDue = options.StartEpoch is null
                    ? DateTimeOffset.UtcNow
                    : DateTimeOffset.FromUnixTimeMilliseconds((long)(options.StartEpoch.Value * 1000));

                while (!stopping.IsCancellationRequested)
                {
                    // P3 spec 08: the clean stop (--run-for). Checked BEFORE composing,
                    // so the final line names the last PUBLISHED seq — never a ghost.
                    if (Dropout.Due(options, DateTimeOffset.UtcNow - startedAt))
                    {
                        await DisconnectQuietlyAsync(broker);   // graceful — no error spam, exit code 0
                        Console.WriteLine(options.Scenario == "warmup-demo"
                            ? WarmupDemo.StopLine(options.RunFor!.Value, seq)
                            : Dropout.StopLine(options.RunFor!.Value, seq));
                        return 0;
                    }

                    // THE SEQ-HOLD LAW (L2 live test, owner-ruled 2026-11-16): a
                    // broker kick mid-publish (deny_action=disconnect) throws
                    // BEFORE the seq is owned — the outer catch reconnects and
                    // the loop re-publishes THE SAME seq (same payload → the
                    // store dedups by the id-key, stored once). QoS1's
                    // at-least-once on the wire becomes exactly-once in the
                    // store; a kicked message is a LOST message no more.
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(PayloadV1.Compose(seq + 1, DateTimeOffset.UtcNow, scenario.Tick(seq + 1)))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    // P3 spec 09: --dup K replays the SAME payload K times (the
                    // duplicate-flood rider) — K PUBACKs, one unique reading; the
                    // K-1 copies are conflicts by design (plan/09 §4.1 row 6).
                    for (var copy = 1; copy <= options.Dup; copy++)
                    {
                        await broker.PublishAsync(message, stopping); // QoS1: completion = the broker's PUBACK received
                        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] seq={seq + 1} → {topic}");
                    }
                    seq++;   // ownership lands with the last copy's PUBACK

                    // P4 spec 10: the owner watches the story unfold on the wire —
                    // phase changes once each, then the crossing and the firing
                    // moment (all three derived from the published seq).
                    if (warmup is not null)
                    {
                        var phase = warmup.PhaseAt(seq);
                        if (phase != warmupPhase)
                        {
                            warmupPhase = phase;
                            Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] warmup: phase={phase}");
                        }
                        if (seq == crossedAt)
                            Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] warmup: crossed {WarmupDemo.LimitCelsius:F1}°C at seq={seq}");
                        if (seq == windowAt)
                            Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] warmup: {WarmupDemo.SignalWindowSeconds:F0} s above {WarmupDemo.LimitCelsius:F1}°C at seq={seq}");
                    }

                    nextDue = nextDue.AddSeconds(1 / options.Rate);
                    var paceDelay = nextDue - DateTimeOffset.UtcNow;
                    if (paceDelay > TimeSpan.Zero)
                        await Task.Delay(paceDelay, stopping);
                }
                await DisconnectQuietlyAsync(broker);
                return 0;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                await DisconnectQuietlyAsync(broker);
                return 0;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = Backoff(attempt, random); // 05 §13 firmware law — jittered, never a hot loop
                Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {ex.Message} — attempt {attempt}: reconnecting in {delay.TotalSeconds:F1}s");
                try { await Task.Delay(delay, stopping); }
                catch (OperationCanceledException) { await DisconnectQuietlyAsync(broker); return 0; }
            }
            finally
            {
                broker.Dispose();
            }
        }

        return 0;
    }

    private static async Task DisconnectQuietlyAsync(IMqttClient broker)
    {
        try
        {
            await broker.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
                .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                .Build(), CancellationToken.None);
        }
        catch
        {
            // best effort — the socket may already be gone; never mask the exit
        }
    }

    private static TimeSpan Backoff(int attempt, Random random)
    {
        var seconds = Math.Min(Math.Pow(2, Math.Min(attempt - 1, 6)), 60.0);
        return TimeSpan.FromSeconds(Math.Max(0.5, seconds * (0.8 + 0.4 * random.NextDouble())));
    }
}
