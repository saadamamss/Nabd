namespace Nabd.Simulator;

/// <summary>
/// The clean-stop law of --run-for (P3 spec 08): the due-check and the final
/// line. Pure and public so the suite can pin the format without spawning a
/// process — the process-level exit (code 0, graceful disconnect, no reconnect
/// loop) is the LIVE witness (spec 08 step 3, recorded in NOTES).
/// </summary>
public static class Dropout
{
    /// <summary>True once the --run-for bound is spent — the publish loop checks
    /// this BEFORE composing the next message, so the last seq is the last PUBLISHED one.</summary>
    public static bool Due(SimulatorOptions options, TimeSpan elapsed) =>
        options.RunFor is { } runFor && elapsed.TotalSeconds >= runFor;

    /// <summary>THE final line (plan/05 §10): names the last published seq —
    /// the offline watchdog (P4) consumes exactly this shape.</summary>
    public static string StopLine(double runForSeconds, long lastSeq) =>
        $"dropout: stopped after {runForSeconds:F0} s, last seq={lastSeq}";
}
