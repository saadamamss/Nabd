namespace Nabd.Simulator;

/// <summary>
/// Simulator (05 §10 contract): a *real* MQTT device — same doors, same tokens, same topics.
/// Scenarios per 05 §10 / 09 §2: steady, spike, dropout, replay, warmup-demo.
/// P0: prints the plan. P2 gives it MQTT (MQTTnet) + a real device token (FR-17).
/// Being "just another device" is what makes demos able to never diverge from the tested path (risk P4, 11 §5).
/// </summary>
internal static class Program
{
    private static void Main()
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
    }
}
