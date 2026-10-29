using Nabd.SharedKernel;

namespace Nabd.Identity.Contracts;

// P1 spec 02 — tenant + user management doors (FR-01, FR-04; law: plan/04 §4, plan/10 P1 row 1).
// Style follows the room's existing contracts file: records + small interfaces; the service
// layer inside the Identity room implements them; no other room may pass this line.

/// <summary>
/// Who is acting, as verified by the auth layer — JWT-derived, NEVER client-invented.
/// Rooms receive this context and re-assert permissions themselves (07 §5 layer 2).
/// </summary>
public sealed record CallerContext(Guid UserId, Guid TenantId, IReadOnlyList<string> RoleCodes);

/// <summary>FR-01: register a tenant + its FIRST user (the tenant_admin) in one transaction.
/// Plaintext password is accepted ONLY at this room boundary — argon2id hashing happens
/// inside the room (plan/07 §3 row 1); no other room ever handles raw passwords.</summary>
public sealed record RegisterTenantRequest(string Name, string Slug, string AdminEmail, string AdminDisplayName, string AdminPassword, string AdminLocale = "en");

public sealed record TenantRegistrationResult(TenantSnapshot Tenant, UserSnapshot AdminUser);

/// <summary>FR-04: add a user inside the caller's tenant (hashing inside the room, same rule).</summary>
public sealed record NewUserRequest(Guid TargetTenantId, string Email, string DisplayName, string Locale, string Password, string RoleCode);

public sealed record UserRoleChangeRequest(Guid TargetTenantId, Guid TargetUserId, string RoleCode);

public sealed record UserLocaleUpdateRequest(Guid TargetTenantId, Guid TargetUserId, string Locale);

public interface ITenantRegistration
{
    Task<Result<TenantRegistrationResult>> RegisterTenantAsync(RegisterTenantRequest request, CancellationToken ct = default);
}

public interface IUserManagement
{
    Task<Result<UserSnapshot>> CreateUserAsync(CallerContext acting, NewUserRequest request, CancellationToken ct = default);

    /// <summary>Replaces the user's role rows for the target tenant (P1 model: one primary role).</summary>
    Task<Result<UserSnapshot>> SetUserRoleAsync(CallerContext acting, UserRoleChangeRequest request, CancellationToken ct = default);

    /// <summary>FR-06's identity-side half: persist per-user ar/en preference (UI lives in P5).</summary>
    Task<Result<UserSnapshot>> SetUserLocaleAsync(CallerContext acting, UserLocaleUpdateRequest request, CancellationToken ct = default);

    /// <summary>FR-04's "remove": hard delete at P1 (FKs cascade roles/sessions; user status column deferred — NOTES).</summary>
    Task<Result<Unit>> DeleteUserAsync(CallerContext acting, Guid targetTenantId, Guid targetUserId, CancellationToken ct = default);
}

/// <summary>Stable error codes the API Host translates to HTTP + catalog text (05 §6 family).</summary>
public static class IdentityErrors
{
    public const string EmailTaken        = "identity:email_taken";
    public const string SlugTaken         = "identity:slug_taken";
    public const string TenantNotFound    = "identity:tenant_not_found";
    public const string UserNotFound      = "identity:user_not_found";
    public const string RoleUnknown       = "identity:role_unknown";
    public const string Forbidden         = "identity:forbidden";          // caller lacks the role for this op (07 §5)
    public const string CrossTenantTarget = "identity:cross_tenant_target"; // target alive in another tenant
    public const string InvalidLocale     = "identity:invalid_locale";     // ar|en only at P1 (FR-06)
}
