namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Assets.Contracts;

// P2 spec 05 — the broker's two doors. ALWAYS 200 with an explicit result
// (EMQX 5.x reads 4xx/5xx as "ignore" and continues the chain — a 401 deny
// would ADMIT the client once allow_anonymous=false is the only other guard;
// explicit deny bodies are the only safe shape). Uniform deny: unknown device,
// wrong token, revoked, malformed — same body, no existence hints.
// NO ingestion here: authenticated messages sit in EMQX unread until P3.

[ApiController]
[Route("internal/emqx")]
public sealed class InternalEmqxController(IDeviceLifecycle devices) : ControllerBase
{
    public sealed record EmqxAuthRequest(string? Username, string? Password);
    public sealed record EmqxAclRequest(string? Username, string? Topic, string? Action);

    [Internal]
    [HttpPost("auth")]
    public async Task<IActionResult> Auth([FromBody] EmqxAuthRequest body, CancellationToken ct)
    {
        var allow = Guid.TryParse(body.Username, out var deviceId)
                    && !string.IsNullOrEmpty(body.Password)
                    && await devices.BrokerAuthenticateAsync(deviceId, body.Password, ct);
        return Ok(new { result = allow ? "allow" : "deny" });
    }

    [Internal]
    [HttpPost("acl")]
    public async Task<IActionResult> Acl([FromBody] EmqxAclRequest body, CancellationToken ct)
    {
        var allow = Guid.TryParse(body.Username, out var deviceId)
                    && await devices.AuthorizePublishAsync(
                        deviceId, body.Topic ?? "", body.Action ?? "", ct);
        return Ok(new { result = allow ? "allow" : "deny" });
    }
}