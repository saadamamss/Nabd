using Nabd.SharedKernel;
namespace Nabd.Identity.Contracts;

/// <summary>Identity room's outbound event (03 §3): a user joined a tenant.</summary>
public sealed record UserCreated(Guid TenantId, Guid UserId, string Email, string Locale) : IntegrationEvent;

/// <summary>Identity room's outbound event: permissions/roles changed — listening rooms must refresh their cache.</summary>
public sealed record RoleChanged(Guid TenantId, Guid UserId, string Role) : IntegrationEvent;

public sealed record UserSnapshot(Guid UserId, Guid TenantId, string Email, string Role, string Locale);

public sealed record TenantSnapshot(Guid TenantId, string Name, bool IsActive);

/// <summary>
/// What other rooms may ASK of Identity (03 §3 "asks other modules for").
/// Role/portrait checks ride on this — never on the users table (room rule 1).
/// </summary>
public interface IIdentityDirectory
{
    Task<UserSnapshot?> FindUserAsync(Guid userId, CancellationToken ct = default);

    Task<TenantSnapshot?> FindTenantAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>Permission checks asserted in the service layer (07 §5, 12 §5 review point 3).</summary>
public interface IPermissionChecker
{
    /// <summary>Permission names come from 02 §5 (e.g. "assets:manage", "alerts:acknowledge").</summary>
    bool HasPermission(Guid userId, string permission);
}
