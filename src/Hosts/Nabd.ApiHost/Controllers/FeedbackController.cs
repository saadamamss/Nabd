namespace Nabd.ApiHost.Controllers;

using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Feedback.Contracts;
using Nabd.Identity.Contracts;

[ApiController]
public sealed class FeedbackController(IFeedback feedback) : ControllerBase
{
    /// <summary>
    /// P5 spec 06 (F6b): every authenticated role may send feedback — the viewer
    /// wall admits all three, so `feedback.submit` sits in all three matrix rows
    /// (RouteWallTests fact 3 is the drift detector).
    ///
    /// The body carries message/page/locale/contactEmail and nothing else: the
    /// tenant, the user and the stamped author email come from the token and the
    /// directory. The answer echoes the receipt only (F9/9: no PII back).
    /// </summary>
    [Viewer(Operations.FeedbackSubmit)]
    [HttpPost("api/v1/feedback")]
    public async Task<IActionResult> Submit(
        [FromBody] FeedbackSubmitRequest body, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await feedback.SubmitAsync(caller, body, ct);
        if (!result.IsSuccess) return Translate(result.Error!);

        var submission = result.Value;

        if (submission.Accepted)
            return StatusCode(201, new { id = submission.Id, createdAt = submission.CreatedAt });

        // inside the 30 s window: the wait is the whole answer. The body speaks
        // `code` like every door here; the header speaks HTTP to any client that
        // only reads headers (the ingest door's precedent, REV 4 C4).
        if (submission.RetryAfterSeconds is { } wait)
        {
            Response.Headers.RetryAfter = wait.ToString();
            return StatusCode(429, new
            {
                code = FeedbackErrors.RateLimited,
                retryAfterSeconds = wait,
            });
        }

        // the function found no directory row for (token user, token tenant):
        // never a forged stamp — the token is stale, the door says so.
        return Unauthorized(new { code = FeedbackErrors.UserNotFound });
    }

    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.Forbidden => StatusCode(403, new { code = error }),
        // today the gone-author answer is the direct 401 below (a refusal, not a
        // Result failure — the wait and the reason must survive); this arm keeps
        // the code honest if a future path returns it as an error.
        FeedbackErrors.UserNotFound => Unauthorized(new { code = error }),
        FeedbackErrors.InvalidMessage or FeedbackErrors.InvalidPage
            or FeedbackErrors.InvalidEmail or FeedbackErrors.InvalidLocale
            => UnprocessableEntity(new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}
