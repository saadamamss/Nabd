namespace Nabd.ApiHost.Controllers;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nabd.Identity.Contracts;


public sealed record DevCallerJson(Guid UserId, Guid TenantId, string[] RoleCodes);

[ApiController]
[Route("api/v1/tenants")]
public sealed class IdentityController(ITenantRegistration registration, IUserManagement users) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterTenantRequest request, CancellationToken ct)
    {
        var result = await registration.RegisterTenantAsync(request, ct);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return result.Error switch
        {
            IdentityErrors.SlugTaken or IdentityErrors.EmailTaken => Conflict(new { code = result.Error }),
            _ => BadRequest(new { code = result.Error }),
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    
    [ApiExplorerSettings(IgnoreApi = true)]
    private CallerContext? ReadDevCaller()
    {
        if (Environment.GetEnvironmentVariable("NABD_DEV_FAKE_AUTH") != "true")
        {
            return null;
        }

        var header = Request.Headers["X-Nabd-Dev-Caller"].FirstOrDefault();

        if (string.IsNullOrEmpty(header)) return null;

        try
        {
            var json = JsonSerializer.Deserialize<DevCallerJson>(header, JsonOpts);
            if (json is null) return null;

            return new CallerContext(json.UserId, json.TenantId, json.RoleCodes);
        }
        catch (JsonException)
        {
            return null;
        }

    }

    [HttpPost("{tenantId:guid}/users")]
    public async Task<IActionResult> CreateUser(Guid tenantId, [FromBody] NewUserRequest body , CancellationToken ct)
    {
        var caller = ReadDevCaller();
        if (caller is null) return Unauthorized();

        var request = body with { TargetTenantId = tenantId };

        var result = await users.CreateUserAsync(caller, request, ct);

        if (result.IsSuccess) return Ok(result.Value);

        return result.Error switch
        {
            IdentityErrors.Forbidden => StatusCode(403, new {code = result.Error}),
            IdentityErrors.CrossTenantTarget => NotFound(new { code = result.Error }),
            IdentityErrors.EmailTaken => Conflict(new {code = result.Error}),
            _ => BadRequest(new {code = result.Error}),
        };
        
    }
}
