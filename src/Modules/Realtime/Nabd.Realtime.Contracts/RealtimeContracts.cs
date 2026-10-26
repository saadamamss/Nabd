namespace Nabd.Realtime.Contracts;

/// <summary>
/// Push to live dashboards: asset status changes + alert raised (03 §3, P5 scope).
/// SignalR hub lives in the API Host behind this contract — rooms stay hub-agnostic.
/// </summary>
public interface IRealtimeBroadcaster
{
    Task BroadcastToTenantAsync(Guid tenantId, string channel, object payload, CancellationToken ct = default);
}

public static class RealtimeChannels
{
    public const string AssetStatus = "asset-status";
    public const string Alerts = "alerts";
}
