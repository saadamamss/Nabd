namespace Nabd.Ingestion;

using Microsoft.Extensions.Logging;

public enum ReconcileVerdict { Calm, Alarmed }

/// <summary>One watchdog tick's verdict. Delta carries the deficit either way.</summary>
public sealed record ReconcileTick(ReconcileVerdict Verdict, long Delta);

/// <summary>
/// The reconciliation watchdog (P4 spec 02). The host calls Tick on its own
/// cadence (60 s): a deficit that survives while the pipeline is QUIET (buffer
/// empty AND no flush attempt active) has no bounded-work explanation — alarm
/// with the exact delta. Read-only observation: a slow or failing watchdog
/// NEVER touches the ingestion critical path (NFR-01).
/// </summary>
public sealed class ReconcileWatchdog
{
    private readonly ReconcileCounters _ledger;
    private readonly ILogger<ReconcileWatchdog> _logger;

    public ReconcileWatchdog(ReconcileCounters ledger, ILogger<ReconcileWatchdog> logger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ReconcileTick Tick(bool bufferQuiet, bool flushInFlight)
    {
        var deficit = _ledger.Deficit;
        if (bufferQuiet && !flushInFlight && deficit != 0)
        {
            var snap = _ledger.Snapshot();
            _ledger.Alarm(deficit);
            _logger.LogError(
                "reconcile ALARM: {Delta} delivered reading(s) carry NO durable outcome while the pipeline is QUIET — accepted-but-unaccounted is data at risk (snapshot: delivered={Delivered} stored={Stored} rejected={Rejected} handedBack={HandedBack})",
                deficit, snap.Delivered, snap.Stored, snap.Rejected, snap.HandedBack);
            return new ReconcileTick(ReconcileVerdict.Alarmed, deficit);
        }
        return new ReconcileTick(ReconcileVerdict.Calm, deficit);
    }
}