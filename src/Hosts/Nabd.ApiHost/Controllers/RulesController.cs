namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Identity.Contracts;
using Nabd.Rules.Contracts;

public sealed record CreateRuleJson(
    string? ChannelKey, string RuleType,
    double? ThresholdLow, double? ThresholdHigh,
    int? DurationSeconds, int? CooldownMinutes, string? Severity);

public sealed record UpdateRuleJson(
    double? ThresholdLow, double? ThresholdHigh,
    int? DurationSeconds, int? CooldownMinutes, string? Severity, bool? Enabled,
    IReadOnlyList<MuteWindow>? MuteWindows = null);

[ApiController]
public sealed class RulesController(IDeviceRules rules) : ControllerBase
{
    // D1 (owner decision): rules nest under the DEVICE — the caller's tid from
    // the token is the only tenant in play, same as devices.
    [Viewer(Operations.RuleRead)]
    [HttpGet("api/v1/devices/{deviceId:guid}/rules")]
    public async Task<IActionResult> List(Guid deviceId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await rules.ListByDeviceAsync(caller, deviceId, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [TenantAdmin(Operations.RuleCreate)]
    [HttpPost("api/v1/devices/{deviceId:guid}/rules")]
    public async Task<IActionResult> Create(Guid deviceId, [FromBody] CreateRuleJson body, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var request = new RuleCreateRequest(
            caller.TenantId, deviceId, body.ChannelKey, body.RuleType,
            body.ThresholdLow, body.ThresholdHigh,
            body.DurationSeconds ?? 60, body.CooldownMinutes ?? 60,
            body.Severity ?? "warn");
        var result = await rules.CreateRuleAsync(caller, request, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [TenantAdmin(Operations.RuleUpdate)]
    [HttpPatch("api/v1/devices/{deviceId:guid}/rules/{ruleId:guid}")]
    public async Task<IActionResult> Update(
        Guid deviceId, Guid ruleId, [FromBody] UpdateRuleJson body, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await rules.UpdateRuleAsync(caller, deviceId, ruleId,
            new RuleUpdateRequest(body.ThresholdLow, body.ThresholdHigh,
                body.DurationSeconds, body.CooldownMinutes, body.Severity, body.Enabled,
                body.MuteWindows), ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [TenantAdmin(Operations.RuleDelete)]
    [HttpDelete("api/v1/devices/{deviceId:guid}/rules/{ruleId:guid}")]
    public async Task<IActionResult> Delete(Guid deviceId, Guid ruleId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await rules.DeleteRuleAsync(caller, deviceId, ruleId, ct);
        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.Forbidden => StatusCode(403, new { code = error }),
        RuleErrors.DeviceNotFound or RuleErrors.RuleNotFound
            => NotFound(new { code = error }),
        RuleErrors.OfflineRefused or RuleErrors.DuplicateRule or RuleErrors.InvalidRule
            or RuleErrors.InvalidMuteWindow or RuleErrors.MuteWindowOverlap
            => UnprocessableEntity(new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}
