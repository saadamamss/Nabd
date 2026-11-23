namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Identity.Contracts;

public sealed record JsonLogin(string Email, string Password);
public sealed record JsonPasswordChange(string CurrentPassword, string NewPassword);
/// <summary>
/// What the login/refresh doors return: the caller's snapshot and a short-lived
/// access token. The refresh token is NOT here — it rides an HttpOnly cookie, so
/// no script (and no XSS) can read it.
/// </summary>
public sealed record SessionResponse(UserSnapshot User, string AccessToken);
public sealed record JsonResetRequest(string Email);
public sealed record JsonResetConfirm(string token, string NewPassword);
/// <summary>
/// The pre-auth doors (P1 spec 03): login / refresh / logout. No caller context,
/// no dev-caller lane, no role attributes — proof here is credentials only.
/// Both RFC walls live: row 7 anti-enumeration (one body for every denial) and
/// row 5 brute-force backoff (429 + the delay band the service composed).
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(IUserAuthentication auth, IWebHostEnvironment env) : ControllerBase
{
    // scoped to the auth doors: the token is never sent to ordinary API routes,
    // so a cookie-authenticated request cannot be replayed against them.
    private const string RefreshCookie = "nabd_refresh";
    private const string RefreshCookiePath = "/api/v1/auth";

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] JsonLogin body, CancellationToken ct)
    {
        var result = await auth.LoginAsync(new LoginRequest(body.Email, body.Password, Request.HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        return result.IsSuccess ? Issue(result.Value) : Translate(result.Error!);
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var token = Request.Cookies[RefreshCookie];
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { code = IdentityErrors.InvalidCredentials });

        var result = await auth.RefreshAsync(new RefreshRequest(token), ct);
        if (!result.IsSuccess)
        {
            ClearRefreshCookie(); // dead or replayed: do not keep presenting it
            return Translate(result.Error!);
        }

        return Issue(result.Value);
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Cookies[RefreshCookie];
        ClearRefreshCookie(); // idempotent: the browser forgets the session first
        if (string.IsNullOrWhiteSpace(token)) return NoContent();

        var result = await auth.LogoutAsync(new RefreshRequest(token), ct);
        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    /// <summary>
    /// The only place the plaintext refresh token is written: HttpOnly (no
    /// script access), SameSite=Strict (no cross-site sends), Path scoped to the
    /// auth doors, session-scoped (closing the browser ends it), and Secure
    /// outside Development so a local http rig still works.
    /// </summary>
    private CookieOptions RefreshCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = !env.IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = RefreshCookiePath,
        IsEssential = true,
    };

    private IActionResult Issue(LoginResult session)
    {
        Response.Cookies.Append(RefreshCookie, session.RefreshToken, RefreshCookieOptions());
        return Ok(new SessionResponse(session.User, session.AccessToken));
    }

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookie, RefreshCookieOptions());

    [Viewer(Operations.PasswordChange)]
    [HttpPatch("{tenantId:guid}/users/{userId:guid}/password")]
    public async Task<IActionResult> ChangePassword(Guid tenantId, Guid userId, [FromBody] JsonPasswordChange body, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await auth.ChangePasswordAsync(caller, tenantId, userId, new ChangePasswordRequest(body.CurrentPassword, body.NewPassword), ct);

        return result.IsSuccess ? NoContent() : Translate(result.Error!); // Error! is the established controller-side contract (mirrors IdentityController)
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetRequest([FromBody] JsonResetRequest body, CancellationToken ct = default)
    {
        var result = await auth.RequestPasswordResetAsync(new ResetPasswordRequest(body.Email, Request.HttpContext.Connection.RemoteIpAddress?.ToString()));

        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    [AllowAnonymous]
    [HttpPost("reset-confirm")]
    public async Task<IActionResult> ResetConfirm([FromBody] JsonResetConfirm body, CancellationToken ct = default)
    {
        var result = await auth.ConfirmPasswordResetAsync(new ResetConfirmRequest(body.token, body.NewPassword), ct);

        return result.IsSuccess ? NoContent() : Translate(result.Error!);
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
        IdentityErrors.ResetTokenDead => StatusCode(410, new { code = error }),
        IdentityErrors.WeakPassword => BadRequest(new { code = error }),
        IdentityErrors.TenantDisabled => StatusCode(403, new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}