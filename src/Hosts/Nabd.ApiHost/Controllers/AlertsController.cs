namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Alerting.Contracts;
using Nabd.Identity.Contracts;

[ApiController]
public sealed class AlertsController(IAlerting alerts) : ControllerBase
{
    // FR-42's future dashboard reads exactly this door (status/asset/severity).
    [Viewer(Operations.AlertRead)]
    [HttpGet("api/v1/alerts")]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] Guid? assetId,
        [FromQuery] string? severity,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await alerts.ListAsync(caller,
            new AlertFilter(status, assetId, severity, limit ?? 100), ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [Viewer(Operations.AlertRead)]
    [HttpGet("api/v1/alerts/{alertId:guid}")]
    public async Task<IActionResult> Get(Guid alertId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await alerts.GetAsync(caller, alertId, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    // plan/06 §5.1: Viewer+ acknowledges (the first responder is often not an admin).
    [Viewer(Operations.AlertAck)]
    [HttpPost("api/v1/alerts/{alertId:guid}/ack")]
    public async Task<IActionResult> Acknowledge(Guid alertId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await alerts.AcknowledgeAsync(caller, alertId, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    // TenantAdmin resolves (07 §5 layer 2); `reason` is the documented why (§5.3).
    [TenantAdmin(Operations.AlertResolve)]
    [HttpPost("api/v1/alerts/{alertId:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid alertId, [FromQuery] string? reason, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await alerts.ResolveAsync(caller, alertId, reason, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.Forbidden => StatusCode(403, new { code = error }),
        AlertErrors.AlertNotFound => NotFound(new { code = error }),
        AlertErrors.InvalidStatus or AlertErrors.InvalidFilter or AlertErrors.InvalidReason
            => UnprocessableEntity(new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}