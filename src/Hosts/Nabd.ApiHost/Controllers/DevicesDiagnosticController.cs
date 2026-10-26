using Microsoft.AspNetCore.Mvc;
using Nabd.Assets.Contracts;

namespace Nabd.ApiHost.Controllers;

/// <summary>
/// Diagnostic controller for P0: proves rooms + hosts + contracts are wired end-to-end.
/// Real asset/device APIs (CRUD, tokens, archive) are the P2 slice set (10 §3).
/// Auth is added in P1 — this endpoint is development-only until then.
/// </summary>
[ApiController]
[Route("api/diag/devices")]
public sealed class DevicesDiagnosticController(IDeviceRegistry registry) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest request, CancellationToken ct)
    {
        var device = await registry.RegisterDeviceAsync(request.TenantId, request.AssetId, request.Name, ct);
        return CreatedAtAction(nameof(Get), new { deviceId = device.DeviceId }, device);
    }

    [HttpGet("{deviceId:guid}")]
    public async Task<IActionResult> Get(Guid deviceId, CancellationToken ct)
    {
        var device = await registry.FindDeviceAsync(deviceId, ct);
        return device is null ? NotFound() : Ok(device);
    }
}

public sealed record RegisterDeviceRequest(Guid TenantId, Guid AssetId, string Name);
