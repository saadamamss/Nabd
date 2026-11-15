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
        IScenario scenario;
        try
        {
            options = SimulatorOptions.Parse(args);
            scenario = ScenarioRegistry.Create(options.Scenario, options.Channels,
                options.SpikeTo, options.SpikeEvery, options.SpikeHoldFor);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2; // usage error — fail fast, never guess, never retry
        }

        var topic = $"t/{options.TenantId}/dev/{options.DeviceId}/v1"; // 05 §4 — the wire law, composed from parts

        Console.WriteLine($"nabd-simulator v0 — device {options.DeviceId} → {options.Host}:{options.Port} topic {topic} @ {options.Rate}/s ({options.Scenario})");

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        var factory = new MqttClientFactory();
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
                    .WithCredentials(options.DeviceId, options.Token)
                    .WithProtocolVersion(MqttProtocolVersion.V311) // plan/05 §3: 3.1.1 minimum; 5's features kept for the future
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                    .Build(), stopping.Token);

                if (connect.ResultCode != MqttClientConnectResultCode.Success)
                    throw new MqttCommunicationException($"broker refused connect: {connect.ResultCode}");

                attempt = 0; // a real connect resets the backoff ladder
                Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] connected — publishing to {topic}");

                while (true)
                {
                    // P3 spec 08: the clean stop (--run-for). Checked BEFORE composing,
                    // so the final line names the last PUBLISHED seq — never a ghost.
                    if (Dropout.Due(options, DateTimeOffset.UtcNow - startedAt))
                    {
                        await DisconnectQuietlyAsync(broker);   // graceful — no error spam, exit code 0
                        Console.WriteLine(Dropout.StopLine(options.RunFor!.Value, seq));
                        return 0;
                    }

                    seq++;
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(PayloadV1.Compose(seq, DateTimeOffset.UtcNow, scenario.Tick(seq)))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    // P3 spec 09: --dup K replays the SAME payload K times (the
                    // duplicate-flood rider) — K PUBACKs, one unique reading; the
                    // K-1 copies are conflicts by design (plan/09 §4.1 row 6).
                    for (var copy = 1; copy <= options.Dup; copy++)
                    {
                        await broker.PublishAsync(message, stopping.Token); // QoS1: completion = the broker's PUBACK received
                        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] seq={seq} → {topic}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1 / options.Rate), stopping.Token);
                }
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
                try { await Task.Delay(delay, stopping.Token); }
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
