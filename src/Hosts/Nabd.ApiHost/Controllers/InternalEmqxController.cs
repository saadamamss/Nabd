namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Assets.Contracts;
using Nabd.Ingestion.Contracts;

// P2 spec 05 + P3 spec 02 — the broker's doors, now THREE identities deep.
// ALWAYS 200 with an explicit result (EMQX 5.x reads 4xx/5xx as "ignore" and
// continues the chain — a 401 deny would ADMIT the client once
// allow_anonymous=false is the only other guard; explicit deny bodies are the
// only safe shape). Uniform deny: unknown device, wrong token, revoked,
// malformed, wrong consumer secret — same body, no existence hints, and no
// "consumer vs device" distinction visible to the broker.
// Dispatch order is a CONTRACT: the consumer question comes FIRST, because
// the consumer's username never parses as a Guid (enforced by ConsumerAccess's
// constructor guard) — if the device question ran first it would swallow the
// consumer into a permanent deny; if the consumer question ran on a Guid
// username it would shadow a real device. The guard makes the order safe.

[ApiController]
[Route("internal/emqx")]
public sealed class InternalEmqxController(IDeviceLifecycle devices, IConsumerAccess consumers) : ControllerBase
{
    public sealed record EmqxAuthRequest(string? Username, string? Password);
    public sealed record EmqxAclRequest(string? Username, string? Topic, string? Action);

    [Internal]
    [HttpPost("auth")]
    public async Task<IActionResult> Auth([FromBody] EmqxAuthRequest body, CancellationToken ct)
    {
        if (consumers.IsConsumerUsername(body.Username))
        {
            var consumerAllow = consumers.Authenticate(body.Username ?? "", body.Password);
            return Ok(new { result = consumerAllow ? "allow" : "deny" });
        }

        var allow = Guid.TryParse(body.Username, out var deviceId)
                    && !string.IsNullOrEmpty(body.Password)
                    && await devices.BrokerAuthenticateAsync(deviceId, body.Password, ct);
        return Ok(new { result = allow ? "allow" : "deny" });
    }

    [Internal]
    [HttpPost("acl")]
    public async Task<IActionResult> Acl([FromBody] EmqxAclRequest body, CancellationToken ct)
    {
        if (consumers.IsConsumerUsername(body.Username))
        {
            var subscribe = string.Equals(body.Action, "subscribe", StringComparison.OrdinalIgnoreCase);
            var consumerAllow = subscribe && consumers.AuthorizeSubscribe(body.Username ?? "", body.Topic ?? "");
            return Ok(new { result = consumerAllow ? "allow" : "deny" });
        }

        var allow = Guid.TryParse(body.Username, out var deviceId)
                    && await devices.AuthorizePublishAsync(
                        deviceId, body.Topic ?? "", body.Action ?? "", ct);
        return Ok(new { result = allow ? "allow" : "deny" });
    }
}