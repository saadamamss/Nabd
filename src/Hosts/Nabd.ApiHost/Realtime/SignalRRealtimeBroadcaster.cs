using Microsoft.AspNetCore.SignalR;
using Nabd.Realtime.Contracts;

namespace Nabd.ApiHost.Realtime;

/// <summary>
/// The realtime contract's only implementation (P5 spec 03, B10). The method name
/// IS the channel constant, so the server and every client speak one vocabulary.
/// </summary>
public sealed class SignalRRealtimeBroadcaster(IHubContext<DashboardHub> hub)
    : IRealtimeBroadcaster
{
    public Task BroadcastToTenantAsync(
        Guid tenantId, string channel, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(DashboardHub.GroupFor(tenantId)).SendAsync(channel, payload, ct);
}
