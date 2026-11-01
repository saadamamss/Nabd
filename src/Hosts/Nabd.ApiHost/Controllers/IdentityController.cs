namespace Nabd.ApiHost.Controllers;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Identity.Contracts;


public sealed record DevCallerJson(Guid UserId, Guid TenantId, string[] RoleCodes);
public sealed record RoleChangeJson(string Role);
public sealed record LocaleUpdateJson(string Locale);

[ApiController]
[Route("api/v1/tenants")]
public sealed class IdentityController(ITenantRegistration registration, IUserManagement users) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterTenantRequest request, CancellationToken ct)
    {
        var result = await registration.RegisterTenantAsync(request, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);

    }

    [TenantAdmin]
    [HttpPost("{tenantId:guid}/users")]
    public async Task<IActionResult> CreateUser(Guid tenantId, [FromBody] NewUserRequest body, CancellationToken ct)
    {
        var caller = ReadDevCaller();
        if (caller is null) return Unauthorized();

        var request = body with { TargetTenantId = tenantId };

        var result = await users.CreateUserAsync(caller, request, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [Viewer]
    [HttpGet("{tenantId:guid}")]
    public async Task<IActionResult> GetTenant(Guid tenantId, CancellationToken ct)
    {
        var caller = ReadDevCaller();

        if (caller is null) return Unauthorized();

        var result = await registration.GetTenantAsync(caller, tenantId, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [Viewer]
    [HttpGet("{tenantId:guid}/users")]
    public async Task<IActionResult> ListUsers(Guid tenantId, CancellationToken ct)
    {
        var caller = ReadDevCaller();

        if (caller is null) return Unauthorized();

        var result = await users.ListUsersAsync(caller, tenantId, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [TenantAdmin]
    [HttpDelete("{tenantId:guid}/users/{userId:guid}")]
    public async Task<IActionResult> DeleteUser(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var caller = ReadDevCaller();

        if (caller is null) return Unauthorized();

        var result = await users.DeleteUserAsync(caller, tenantId, userId, ct);

        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    [TenantAdmin]
    [HttpPatch("{tenantId:guid}/users/{userId:guid}/roles")]
    public async Task<IActionResult> SetUserRole(Guid tenantId, Guid userId, [FromBody] RoleChangeJson body, CancellationToken ct)
    {
        var caller = ReadDevCaller();

        if (caller is null) return Unauthorized();

        var result = await users.SetUserRoleAsync(caller, new UserRoleChangeRequest(tenantId, userId, body.Role), ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [Viewer]
    [HttpPatch("{tenantId:guid}/users/{userId:guid}/locale")]
    public async Task<IActionResult> SetUserLocale(Guid tenantId, Guid userId, [FromBody] LocaleUpdateJson body, CancellationToken ct)
    {
        var caller = ReadDevCaller();

        if (caller is null) return Unauthorized();

        var result = await users.SetUserLocaleAsync(caller, new UserLocaleUpdateRequest(tenantId, userId, body.Locale), ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
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

    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.Forbidden => StatusCode(403, new { code = error }),
        IdentityErrors.CrossTenantTarget or IdentityErrors.UserNotFound or IdentityErrors.TenantNotFound => NotFound(new { code = error }),
        IdentityErrors.EmailTaken or IdentityErrors.SlugTaken or IdentityErrors.LastAdmin or IdentityErrors.SelfDelete => Conflict(new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}
