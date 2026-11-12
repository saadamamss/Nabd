using Nabd.SharedKernel;
using Nabd.Identity.Contracts;   // قانوني: Contracts→Contracts (نفس شكل Admin — RoomBoundaryTests يسمح)

namespace Nabd.Assets.Contracts;

// P2 spec 02 — FR-10: create / list / get / edit / archive an asset.
// The wall pattern inherited from P1: CallerContext from verified JWT (never
// client-invented), layer-2 asserts in the service (07 §5), fail-shut RLS under everything.

/// <summary>The asset as doors see it. IsActive=false = archived (soft — devices hang off it by FK).</summary>
public sealed record AssetSnapshot(
    Guid AssetId, Guid TenantId, Guid TemplateId,
    string Name, string? Location, string? Notes, bool IsActive);

public sealed record CreateAssetRequest(Guid TargetTenantId, Guid TemplateId, string Name, string? Location, string? Notes);

/// <summary>PATCH semantics: null = "leave as is". The template is NOT editable after
/// creation — channel history would lie (spec decision, plan/05 §5 unit policy).</summary>
public sealed record UpdateAssetRequest(Guid TargetTenantId, Guid AssetId, string? Name, string? Location, string? Notes);

public interface IAssetCatalog
{
    Task<Result<AssetSnapshot>> CreateAssetAsync(CallerContext acting, CreateAssetRequest request, CancellationToken ct = default);

    /// <summary>Cross-tenant ids answer AssetNotFound (RLS hides them — the P1 404 law).</summary>
    Task<Result<AssetSnapshot>> GetAssetAsync(CallerContext acting, Guid assetId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<AssetSnapshot>>> ListAssetsAsync(CallerContext acting, Guid tenantId, bool includeArchived = false, CancellationToken ct = default);

    Task<Result<AssetSnapshot>> UpdateAssetAsync(CallerContext acting, UpdateAssetRequest request, CancellationToken ct = default);

    /// <summary>FR-10's "archive": SOFT — is_active=false, never a row delete.</summary>
    Task<Result<Unit>> ArchiveAssetAsync(CallerContext acting, Guid targetTenantId, Guid assetId, CancellationToken ct = default);
}

/// <summary>Stable error codes the API Host translates to HTTP + catalog text.</summary>
public static class AssetErrors
{
    public const string AssetNotFound = "assets:asset_not_found";
    public const string UnknownTemplate = "assets:unknown_template";
    public const string InvalidName = "assets:invalid_name";
}