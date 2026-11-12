namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;

public sealed record CreateDeviceJson(string Name);

[ApiController]
public sealed class DevicesController(IDeviceLifecycle devices) : ControllerBase
{
    // routes carry the asset (creation nests under it) or the device id —
    // the caller's tid (from the token) is the only tenant in play, same as assets.
    [TenantAdmin(Operations.DeviceRegister)]
    [HttpPost("api/v1/assets/{assetId:guid}/devices")]
    public async Task<IActionResult> Create(Guid assetId, [FromBody] CreateDeviceJson body, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var request = new DeviceCreateRequest(caller.TenantId, assetId, body.Name);
        var result = await devices.CreateDeviceAsync(caller, request, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [Viewer(Operations.DeviceRead)]
    [HttpGet("api/v1/assets/{assetId:guid}/devices")]
    public async Task<IActionResult> List(Guid assetId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await devices.ListByAssetAsync(caller, assetId, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [TenantAdmin(Operations.DeviceTokenReissue)]
    [HttpPost("api/v1/devices/{deviceId:guid}/token/reissue")]
    public async Task<IActionResult> Reissue(Guid deviceId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await devices.ReissueTokenAsync(caller, deviceId, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [TenantAdmin(Operations.DeviceRevoke)]
    [HttpDelete("api/v1/devices/{deviceId:guid}")]
    public async Task<IActionResult> Revoke(Guid deviceId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await devices.RevokeDeviceAsync(caller, deviceId, ct);

        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.Forbidden => StatusCode(403, new { code = error }),
        IdentityErrors.CrossTenantTarget or DeviceErrors.DeviceNotFound or DeviceErrors.AssetNotFound
            => NotFound(new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}