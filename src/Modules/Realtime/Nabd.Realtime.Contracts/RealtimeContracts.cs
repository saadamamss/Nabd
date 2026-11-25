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

    public static readonly IReadOnlyList<string> All = [AssetStatus, Alerts];
}

/// <summary>
/// The `alerts` channel's payload hint (P5 spec 03, B10): `{ kind, alert }`, where
/// `kind` is `raised` or `changed`. It is deliberately a HINT, not the full
/// <c>AlertSnapshot</c>: the NOTIFY that wakes the listener carries committed alert
/// columns only, and good UI practice is to refetch the row you are about to show.
/// The alerts page (spec 05) uses the id to refresh its own detail.
/// </summary>
public sealed record AlertChangeHint(
    Guid AlertId,
    Guid AssetId,
    string Severity,
    string Status,
    DateTimeOffset Since);
