using System.Text;
using Npgsql;
using Nabd.Feedback.Contracts;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Feedback;

/// <summary>
/// The submit door's service (P5 spec 06 S2, F2/F3/F4/F5).
///
/// Two jobs and nothing else: validate the body into the table's own bounds
/// (so a CHECK violation never becomes a 500), then call the ONE function that
/// counts the 30 s window, resolves the author and inserts — because nabd_app
/// is INSERT-only on the table and has no SELECT with which to count.
/// The caller's tenant/user come from the token; the body can never set them.
/// </summary>
public sealed class FeedbackService(FeedbackDb db) : IFeedback
{
    public async Task<Result<FeedbackSubmission>> SubmitAsync(
        CallerContext acting, FeedbackSubmitRequest request, CancellationToken ct = default)
    {
        // layer 2 (the door attribute is layer 1, the function + RLS are layer 3)
        var forbidden = PermissionMatrix.Assert(acting, Operations.FeedbackSubmit, acting.TenantId);
        if (forbidden is not null) return Result<FeedbackSubmission>.Failure(forbidden);

        // trim FIRST — the table measures after trim too (0030 inserts btrim).
        // Postgres counts CHARACTERS (code points); .NET counts UTF-16 units, so
        // 2,000 emoji are 4,000 here and 2,000 in the table. Count runes, or the
        // door would refuse text the CHECK accepts and call it "too long".
        var message = request.Message?.Trim() ?? "";
        if (message.EnumerateRunes().Count() is < 1 or > FeedbackBounds.MessageMax)
            return Result<FeedbackSubmission>.Failure(FeedbackErrors.InvalidMessage);

        var page = request.Page?.Trim() ?? "";
        if (page.EnumerateRunes().Count() is < 1 or > FeedbackBounds.PageMax)
            return Result<FeedbackSubmission>.Failure(FeedbackErrors.InvalidPage);

        var locale = request.Locale?.Trim();
        if (locale is not (FeedbackLocales.Arabic or FeedbackLocales.English))
            return Result<FeedbackSubmission>.Failure(FeedbackErrors.InvalidLocale);

        var email = request.ContactEmail?.Trim();
        if (string.IsNullOrEmpty(email)) email = null;
        else if (!LooksLikeEmail(email))
            return Result<FeedbackSubmission>.Failure(FeedbackErrors.InvalidEmail);

        Guid? id;
        DateTimeOffset? createdAt;
        bool accepted;
        int? retry;

        try
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT id, created_at, accepted, retry_after_seconds
                  FROM feedback.submit_message(@tenant, @user, @page, @locale, @message, @email)
                """, conn);
            cmd.Parameters.AddWithValue("tenant", acting.TenantId);
            cmd.Parameters.AddWithValue("user", acting.UserId);
            cmd.Parameters.AddWithValue("page", page);
            cmd.Parameters.AddWithValue("locale", locale!);
            cmd.Parameters.AddWithValue("message", message);
            cmd.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("feedback.submit_message always answers one row (0030)");

            id = reader.IsDBNull(0) ? null : reader.GetGuid(0);
            createdAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            accepted = reader.GetBoolean(2);
            retry = reader.IsDBNull(3) ? null : reader.GetInt32(3);
        }
        catch (PostgresException pg) when (pg.SqlState == "23514")
        {
            // the CHECKs are the backstop, never the first line: if one fires the
            // door still answers 422, not a 500 — mapped by the constraint that spoke.
            // `feedback_author_len` can only fire on a directory email > 254 (a data
            // fault, not the caller's); it stays on the default arm deliberately —
            // an imprecise 422 beats a 500 on a branch that should be unreachable.
            return Result<FeedbackSubmission>.Failure(pg.ConstraintName switch
            {
                "feedback_locale_known" => FeedbackErrors.InvalidLocale,
                "feedback_page_len" => FeedbackErrors.InvalidPage,
                "feedback_contact_len" => FeedbackErrors.InvalidEmail,
                _ => FeedbackErrors.InvalidMessage,
            });
        }

        return Result<FeedbackSubmission>.Success(
            new FeedbackSubmission(accepted, id, createdAt, retry));
    }

    /// <summary>
    /// Shape only, deliberately cheap: no MX lookup, no RFC 5322 theatre. One
    /// '@' with something on each side and no whitespace — enough to refuse a
    /// typo'd reply address without pretending to be a mail verifier.
    /// </summary>
    private static bool LooksLikeEmail(string value)
    {
        if (value.Length > FeedbackBounds.EmailMax || value.Any(char.IsWhiteSpace)) return false;
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1 && value.IndexOf('@', at + 1) < 0;
    }
}
