using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Assets.Contracts;

// P2 spec 03 — FR-11 (register devices) + FR-12 (revocable credential, blocked at
// ingestion when revoked) + NFR-07 (hash-only storage). The token is shown ONCE at
// mint/reissue — after that only its HMAC hash exists anywhere (NFR-07, plan/05 §11).

/// <summary>The device as doors see it — NEVER carries the token or its hash.</summary>
public sealed record DeviceSnapshot2(
    Guid DeviceId, Guid TenantId, Guid AssetId, Guid TemplateId,
    string Name, bool IsActive, DateTimeOffset? TokenRevokedAtUtc);

/// <summary>Mint result: the ONLY moment the plaintext token exists on a wire.</summary>
public sealed record DeviceMintResult(DeviceSnapshot2 Device, string Token);

public sealed record DeviceCreateRequest(Guid TargetTenantId, Guid AssetId, string Name);

public interface IDeviceLifecycle
{
    /// <summary>Creates the device under an OWN-TENANT asset, mints its token, logs "issued".</summary>
    Task<Result<DeviceMintResult>> CreateDeviceAsync(CallerContext acting, DeviceCreateRequest request, CancellationToken ct = default);

    Task<Result<IReadOnlyList<DeviceSnapshot2>>> ListByAssetAsync(CallerContext acting, Guid assetId, CancellationToken ct = default);

    /// <summary>05 §2: re-issue = NEW credential shown once + OLD one revoked + TWO log lines.</summary>
    Task<Result<DeviceMintResult>> ReissueTokenAsync(CallerContext acting, Guid deviceId, CancellationToken ct = default);

    /// <summary>FR-12's off switch: revoke token + is_active=false + log line. Row survives.</summary>
    Task<Result<Unit>> RevokeDeviceAsync(CallerContext acting, Guid deviceId, CancellationToken ct = default);

    /// <summary>Token → device id, or null. Uniform shape (unknown vs revoked vs wrong).
    /// The broker-auth door (spec 05) is this method's caller.</summary>
    Task<Guid?> VerifyTokenAsync(string presentedToken, CancellationToken ct = default);

    // ---- the broker path (P2 spec 05) — pre-auth by design: no CallerContext
    // exists; the authority is the shared secret at the internal door, and the
    // token hash / device id in the query itself. No matrix assert here — same
    // class as VerifyTokenAsync. Both answers are bools: uniform deny, the door
    // never distinguishes unknown/bad/revoked (anti-enumeration).

    /// <summary>CONNECT gate: the token must be VALID and belong to THIS device.
    /// Rides 0014 (already filters revoked/inactive/unknown → null).</summary>
    Task<bool> BrokerAuthenticateAsync(Guid deviceId, string presentedToken, CancellationToken ct = default);

    /// <summary>PUBLISH gate: allow ONLY for the device's own topic
    /// t/{tid}/dev/{deviceId}/v1, action=publish, credential alive. Rides 0016.</summary>
    Task<bool> AuthorizePublishAsync(Guid deviceId, string topic, string action, CancellationToken ct = default);
}

/// <summary>Stable error codes for the devices doors.</summary>
public static class DeviceErrors
{
    public const string DeviceNotFound = "assets:device_not_found";
    public const string AssetNotFound = "assets:asset_not_found";
    public const string InvalidName = "assets:invalid_name";
}