namespace Nabd.Feedback.Contracts;

using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

/// <summary>P5 spec 06: the two locales the table accepts (CHECK `feedback_locale_known`).</summary>
public static class FeedbackLocales
{
    public const string Arabic = "ar";
    public const string English = "en";
}

/// <summary>
/// F3: the bounds the door enforces BEFORE the table's CHECKs see anything —
/// a 23514 must never surface as a 500 (door law: 422).
/// </summary>
public static class FeedbackBounds
{
    public const int MessageMax = 2000;
    public const int PageMax = 200;
    public const int EmailMax = 254;
}

/// <summary>
/// The submit body (F2/F7). Deliberately carries NO tenant/user/author email:
/// the stamp comes from the token and the directory, and an unknown JSON member
/// is ignored by the serializer — so a forged `tenantId` cannot even bind.
/// </summary>
public sealed record FeedbackSubmitRequest(
    string? Message,
    string? Page,
    string? Locale,
    string? ContactEmail = null);

/// <summary>
/// What the door answers. `Accepted=false` is NOT a failure of the call — it is
/// the function's honest refusal: either the 30 s window (with a wait) or an
/// author the directory no longer has (no id, no wait). A `Result` failure
/// could not carry `RetryAfterSeconds`, and losing it would leave the client
/// guessing.
/// </summary>
public sealed record FeedbackSubmission(
    bool Accepted,
    Guid? Id,
    DateTimeOffset? CreatedAt,
    int? RetryAfterSeconds);

/// <summary>Door error codes — the controller's only translation table (lesson #16).</summary>
public static class FeedbackErrors
{
    public const string InvalidMessage = "feedback:invalid_message";
    public const string InvalidPage = "feedback:invalid_page";     // the spec's list forgot it; test 3 demands it
    public const string InvalidEmail = "feedback:invalid_email";
    public const string InvalidLocale = "feedback:invalid_locale";
    public const string RateLimited = "feedback:rate_limited";
    public const string UserNotFound = "feedback:user_not_found";   // the token outlived its directory row (REV 4 C3)
}

/// <summary>
/// The Feedback room's public door (P5 spec 06 S2). It writes and answers a
/// receipt — never reads. The owner's read is the admin lane (S3), so this
/// interface has exactly one method on purpose.
/// </summary>
public interface IFeedback
{
    Task<Result<FeedbackSubmission>> SubmitAsync(
        CallerContext acting, FeedbackSubmitRequest request, CancellationToken ct = default);
}
