using Microsoft.AspNetCore.Mvc;
using Nabd.Admin.Contracts;
using Nabd.ApiHost.Auth;
using Nabd.Identity.Contracts;

namespace Nabd.ApiHost.Controllers;

[ApiController]
[Route("api/v1/platform")]
public sealed class AdminController(IPlatformTenantAdmin admin) : ControllerBase
{
    [PlatformAdmin(Operations.TenantList)]
    [HttpGet("tenants")]
    public async Task<IActionResult> ListTenants(CancellationToken ct = default)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();
        var result = await admin.ListTenantsAsync(caller, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [PlatformAdmin(Operations.TenantDisable)]
    [HttpPost("tenants/{tenantId:guid}/disable")]
    public async Task<IActionResult> DisableTenant(Guid tenantId, CancellationToken ct = default)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await admin.DisableTenantAsync(caller, tenantId, ct);

        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    [PlatformAdmin(Operations.TenantEnable)]
    [HttpPost("tenants/{tenantId:guid}/enable")]
    public async Task<IActionResult> EnableTenant(Guid tenantId, CancellationToken ct = default)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await admin.EnableTenantAsync(caller, tenantId, ct);

        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.TenantNotFound => NotFound(new { code = error }),
        IdentityErrors.CrossTenantTarget => NotFound(new { code = error }),
        IdentityErrors.Forbidden => StatusCode(403, new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}