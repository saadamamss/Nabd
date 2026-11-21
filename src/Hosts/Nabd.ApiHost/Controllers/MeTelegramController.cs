namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Identity.Contracts;
using Nabd.Notifications.Contracts;

/// <summary>
/// The user's own Telegram channel (FR-38, D9j). Four doors, one wall: every role may
/// link its own chat — the service refuses any user id that is not the caller's.
/// </summary>
[ApiController]
public sealed class MeTelegramController(ITelegramChannels channels) : ControllerBase
{
    public sealed record SetSeveritiesBody(IReadOnlyList<string>? Severities);

    [Viewer(Operations.TelegramManage)]
    [HttpGet("api/v1/me/telegram")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await channels.GetAsync(caller, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    // plan/04 §9: the code is shown ONCE; the row keeps only its hash.
    [Viewer(Operations.TelegramManage)]
    [HttpPost("api/v1/me/telegram/link")]
    public async Task<IActionResult> Link(CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await channels.IssueLinkCodeAsync(caller, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [Viewer(Operations.TelegramManage)]
    [HttpDelete("api/v1/me/telegram/link")]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await channels.UnlinkAsync(caller, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    // plan/06 §6: warn/critical only; info is refused (dashboard-only).
    [Viewer(Operations.TelegramManage)]
    [HttpPatch("api/v1/me/telegram/severities")]
    public async Task<IActionResult> SetSeverities(
        [FromBody] SetSeveritiesBody body, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        // A missing field is a MALFORMED request, not "mute everything": only an
        // explicit [] silences the channel (D9e, review 2026-11-22).
        if (body.Severities is null)
            return UnprocessableEntity(new { code = NotificationErrors.InvalidSeverity });

        var result = await channels.SetSeveritiesAsync(caller, body.Severities, ct);
        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.Forbidden => StatusCode(403, new { code = error }),
        NotificationErrors.InvalidSeverity or NotificationErrors.InvalidCode
            => UnprocessableEntity(new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}
