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
}