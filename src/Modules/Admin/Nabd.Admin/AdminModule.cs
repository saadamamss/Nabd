namespace Nabd.Admin;

/// <summary>
/// Platform Admin module (03 §3): tenant list/usage APIs, enable/disable tenant (FR-61).
/// Owns no tables — reads through cross-module contract views only.
/// P0: empty on purpose. P1 fills the BYPASSRLS audited path (04 §10).
/// Emits: TenantDisabled.
/// </summary>
public static class AdminModule
{
    public const string RoomName = "Admin (Platform)";
}
