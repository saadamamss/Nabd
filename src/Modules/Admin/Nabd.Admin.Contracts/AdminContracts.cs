using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Admin.Contracts;

/// <summary>
/// FR-61 as seen by the fleet page: one row per tenant, status included.
/// Snapshot naming matches Identity's TenantSnapshot on purpose — the P5
/// console renders this record without room-crossing reads.
/// </summary>
public sealed record TenantFleetSnapshot(
    Guid TenantId, string Name, string Locale, bool IsActive, DateTimeOffset CreatedAtUtc);

/// <summary>
/// P5 spec 06 (F9): one stored feedback message as the owner's read returns it.
/// `AuthorEmail`/`AuthorDisplayName` are the STAMPED columns (0030), never a
/// join to `identity.users` — the admin role has no grant there, and a deleted
/// author's row must still show who wrote it (F10).
/// </summary>
public sealed record FeedbackFleetRow(
    Guid Id, string TenantName, string AuthorEmail, string? AuthorDisplayName,
    string Page, string Locale, string Message, string? ContactEmail,
    DateTimeOffset CreatedAt);

/// <summary>
/// One page of the owner's read plus the table's TRUE total (C6): a page size
/// must never be mistaken for how many submissions exist.
/// </summary>
public sealed record FeedbackFleetPage(IReadOnlyList<FeedbackFleetRow> Items, long Total);

/// <summary>P5 spec 06 S3: the read's own bounds (C6) — one source for door + service.</summary>
public static class FeedbackReadLimits
{
    public const int Default = 50;
    public const int Max = 200;
}

/// <summary>Codes the Admin room answers that are not identity errors.</summary>
public static class AdminErrors
{
    /// <summary>`limit` outside 1–200 — the read refuses to guess (C6).</summary>
    public const string InvalidFeedbackLimit = "feedback:invalid_limit";
}

/// <summary>
/// The Admin room's askable door. Guard layers in order: route policy
/// [PlatformAdmin] (host side, spec 04's wall) + service-side PermissionMatrix
/// assert (07 §5 layer 2 — the attribute alone checks nothing) + the audit
/// line on EVERY call (plan/04 §10; plan/07 §9 Admin-actions row).
/// </summary>
public interface IPlatformTenantAdmin
{
    Task<Result<IReadOnlyList<TenantFleetSnapshot>>> ListTenantsAsync(CallerContext acting, CancellationToken ct = default);

    Task<Result<Unit>> DisableTenantAsync(CallerContext acting, Guid targetTenantId, CancellationToken ct = default);

    Task<Result<Unit>> EnableTenantAsync(CallerContext acting, Guid targetTenantId, CancellationToken ct = default);

    /// <summary>
    /// P5 spec 06 S3: the owner's fleet-wide read, newest first. Runs on the
    /// audited BYPASSRLS lane and reads `feedback.messages` directly — the Admin
    /// room takes no project reference to `Nabd.Feedback` (C9).
    /// </summary>
    Task<Result<FeedbackFleetPage>> ListFeedbackAsync(CallerContext acting, int limit, CancellationToken ct = default);
}