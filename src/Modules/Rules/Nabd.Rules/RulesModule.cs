namespace Nabd.Rules;

/// <summary>
/// Rules module (03 §3): threshold rules, offline watchdog (06 §4, every 30 s),
/// per-episode fired flag (06 §3.4), watermark handling.
/// P0: empty on purpose. P4 fills it.
/// Listens: ReadingsAccepted. Emits: CrossingDetected, OfflineDetected.
/// </summary>
public static class RulesModule
{
    public const string RoomName = "Rules";
}
