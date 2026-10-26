namespace Nabd.Realtime;

/// <summary>
/// Realtime module (03 §3): SignalR hub lives in the API Host; this module pushes
/// asset status changes + alerts to live dashboards (P5).
/// Listens: AssetStatusChanged, AlertRaised. Offers: IRealtimeBroadcaster.
/// </summary>
public static class RealtimeModule
{
    public const string RoomName = "Realtime";
}
