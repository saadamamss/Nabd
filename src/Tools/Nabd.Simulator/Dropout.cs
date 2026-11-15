namespace Nabd.Simulator;

/// <summary>
/// The clean-stop law of --run-for (P3 spec 08): the due-check and the final
/// line. Pure and public so the suite can pin the format without spawning a
/// process — the process-level exit (code 0, graceful disconnect, no reconnect
/// loop) is the LIVE witness (spec 08 step 3, recorded in NOTES).
/// </summary>
public static class Dropout
{
    public static bool Due(SimulatorOptions options, TimeSpan elapsed) =>
        throw new NotImplementedException("P3 spec 08: owner implements from study/implementation-code.md");

    public static string StopLine(double runForSeconds, long lastSeq) =>
        throw new NotImplementedException("P3 spec 08: owner implements from study/implementation-code.md");
}
