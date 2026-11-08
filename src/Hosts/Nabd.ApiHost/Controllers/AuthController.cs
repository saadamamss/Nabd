namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Identity.Contracts;

public sealed record JsonLogin(string Email, string Password);
public sealed record JsonRefresh(string RefreshToken);
public sealed record JsonPasswordChange(string CurrentPassword, string NewPassword);
/// <summary>
/// The pre-auth doors (P1 spec 03): login / refresh / logout. No caller context,
/// no dev-caller lane, no role attributes — proof here is credentials only.
/// Both RFC walls live: row 7 anti-enumeration (one body for every denial) and
/// row 5 brute-force backoff (429 + the delay band the service composed).
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(IUserAuthentication auth) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] JsonLogin body, CancellationToken ct)
    {
        var result = await auth.LoginAsync(new LoginRequest(body.Email, body.Password, Request.HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] JsonRefresh body, CancellationToken ct)
    {
        var result = await auth.RefreshAsync(new RefreshRequest(body.RefreshToken), ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] JsonRefresh body, CancellationToken ct)
    {
        var result = await auth.LogoutAsync(new RefreshRequest(body.RefreshToken), ct);
        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    [Viewer(Operations.PasswordChange)]
    [HttpPatch("{tenantId:guid}/users/{userId:guid}/password")]
    public async Task<IActionResult> ChangePassword(Guid tenantId, Guid userId, [FromBody] JsonPasswordChange body, CancellationToken ct){
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await auth.ChangePasswordAsync(caller, tenantId, userId, new ChangePasswordRequest(body.CurrentPassword, body.NewPassword), ct);

        return result.IsSuccess ? NoContent() : Translate(result.Error!); // Error! is the established controller-side contract (mirrors IdentityController)
    }

    // plan/07 §3 row 7: ONE legal auth-denial body (identical on wrong email & wrong password).
    // plan/07 §3 row 5: backoff answers 429 — the service composes "code:<band>"; parse the band.
    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.InvalidCredentials => Unauthorized(new { code = error }),
        _ when error.StartsWith(IdentityErrors.LoginBackoff) => StatusCode(429, new
        {
            code = IdentityErrors.LoginBackoff,
            delaySeconds = int.TryParse(error.Split(':')[^1], out var band) ? band : 0,
        }),
        _ => BadRequest(new { code = error }),
    };
}