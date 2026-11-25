using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nabd.ApiHost.Auth;

namespace Nabd.ApiHost.Realtime;

/// <summary>
/// P5 spec 03 (FR-40, B6/B10) — `/hubs/dashboard`.
///
/// The tenant group comes from the VERIFIED token's `tid` claim, never from a
/// client argument: a client-chosen group name would be a tenant leak. There are
/// NO client→server methods at MVP — the hub is a one-way push lane.
///
/// The access token arrives as `?access_token=` because a browser cannot set an
/// Authorization header on the WebSocket handshake (B9); JwtWall accepts it on
/// `/hubs` paths only, so no ordinary API route gains a token-in-URL surface.
/// </summary>
[Authorize(Policy = Policies.HubTenant)]
public sealed class DashboardHub(ILogger<DashboardHub> logger) : Hub
{
    /// <summary>One group per tenant — the isolation law, one spelling.</summary>
    public static string GroupFor(Guid tenantId) => $"tenant:{tenantId}";

    public override async Task OnConnectedAsync()
    {
        var raw = Context.User?.FindFirst("tid")?.Value;
        if (!Guid.TryParse(raw, out var tenantId))
        {
            // A handshake whose token carries no tenant is refused outright —
            // and SAID OUT LOUD: an abort the client can only see as "connection
            // closed" is how a dashboard ends up retrying a refusal forever with
            // no one able to name the reason (2026-11-24).
            logger.LogWarning(
                "refusing hub connection {ConnectionId}: the access token carried no tenant id",
                Context.ConnectionId);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(tenantId));
        await base.OnConnectedAsync();
    }
}
