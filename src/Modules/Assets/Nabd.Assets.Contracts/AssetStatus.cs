using Nabd.Identity.Contracts;   // قانوني: Contracts→Contracts (نمط IAssetCatalog)
using Nabd.SharedKernel;

namespace Nabd.Assets.Contracts;

// P5 spec 03 (FR-40) — the live dashboard's truth.
// The status is DERIVED (B1/B2): open/acknowledged alert rows + last seen, through
// one SQL function. There is no status column to drift, and no `muted` value (B12).

/// <summary>
/// The four states of FR-14 as the DOMAIN actually has them. `muted` is a
/// notification fact, not an asset state: quiet hours silence the message, the
/// alert row still opens (0023 + AlertService), so the pill keeps the real severity.
/// </summary>
public static class AssetStatuses
{
    public const string Healthy = "healthy";
    public const string Warning = "warning";
    public const string Alert = "alert";
    public const string Offline = "offline";

    public static readonly IReadOnlyList<string> All = [Healthy, Warning, Alert, Offline];
}

/// <summary>
/// One asset's derived state (0028). `Since`/`AlertId`/`Severity` are the winning
/// live alert's own facts; all three are null exactly when the asset is healthy.
/// `LastSeenAt` is the newest `telemetry.device_status` row over the asset's devices.
/// </summary>
public sealed record AssetStatusSnapshot(
    Guid AssetId,
    string Status,
    DateTimeOffset? Since,
    Guid? AlertId,
    string? Severity,
    DateTimeOffset? LastSeenAt,
    bool IsActive);

/// <summary>
/// The status read model. Two lanes on purpose:
/// <list type="bullet">
///   <item>the DOOR lane takes the caller and asserts `asset.read` (RULES §2.6 —
///   layer 2 survives the removal drill);</item>
///   <item>the SYSTEM lane takes the tenant from a committed fact (the hub's
///   post-NOTIFY re-read), never from a client (B6).</item>
/// </list>
/// </summary>
public interface IAssetStatusReader
{
    Task<Result<IReadOnlyList<AssetStatusSnapshot>>> ListAsync(
        CallerContext acting, bool includeArchived = false, CancellationToken ct = default);

    /// <summary>The hub's re-read after a NOTIFY. No caller exists there; the
    /// tenant came from the committed alert row, not from a request.</summary>
    Task<AssetStatusSnapshot?> GetForTenantAsync(
        Guid tenantId, Guid assetId, CancellationToken ct = default);
}
