namespace Nabd.Alerting;

/// <summary>
/// Alerting module (03 §3): alert lifecycle OPEN→ACK→RESOLVED (06 §5), dedup + cooldown,
/// re-raise escalation, mute windows.
/// P0: empty on purpose. P4 fills it.
/// Listens: CrossingDetected, OfflineDetected. Emits: AlertRaised/Acknowledged/Resolved.
/// </summary>
public static class AlertingModule
{
    public const string RoomName = "Alerting";
}
