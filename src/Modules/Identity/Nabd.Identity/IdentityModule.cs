namespace Nabd.Identity;

/// <summary>
/// Identity — Tenancy module (03 §3): tenants, users, roles, sessions.
/// P0: empty on purpose. P1 fills it: tenant/user CRUD (04 §4), argon2id login,
/// JWT + refresh rotation, 3 roles, RLS policies, isolation suite.
/// Entries from the room map: offers user/tenant lookups + permission checks; listens to nothing yet.
/// </summary>
public static class IdentityModule
{
    public const string RoomName = "Identity & Tenancy";
}
