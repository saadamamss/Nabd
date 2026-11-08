namespace Nabd.IsolationSuite;

using Nabd.Identity.Contracts;
using Xunit;

// Isolation-suite piece 5 (P1 spec 04): the authorization matrix — ONE source
// of truth for role→operation (plan/02 §5, plan/07 §5; overview box 5).
// Failing-first: written before Nabd.Identity.Contracts.Authorization exists.
// These are the VALVE seats: each row pins a verdict the removal drills will
// deliberately knock out later.

public sealed class AuthorizationMatrixTests
{
    // ---- viewer = read-only everywhere, plus the two honest self-service ops --

    [Theory]
    [InlineData(Operations.TenantRead)]
    [InlineData(Operations.UserRead)]
    public void viewer_may_read(string op) =>
        Assert.True(AuthorizationMatrix.Allows("viewer", op), $"viewer must read: {op}");

    [Fact]
    public void viewer_may_set_own_locale() =>
        // the API door stays Viewer (self-service; the SERVICE enforces own-user)
        Assert.True(AuthorizationMatrix.Allows("viewer", Operations.UserLocaleSet));

    [Theory]
    [InlineData(Operations.UserCreate)]
    [InlineData(Operations.UserDelete)]
    [InlineData(Operations.UserRoleSet)]
    public void viewer_may_not_write_others(string op) =>
        Assert.False(AuthorizationMatrix.Allows("viewer", op), $"viewers are read-only: {op}");

    // ---- tenant_admin = the tenant's management role -------------------------

    [Theory]
    [InlineData(Operations.TenantRead)]
    [InlineData(Operations.UserRead)]
    [InlineData(Operations.UserCreate)]
    [InlineData(Operations.UserDelete)]
    [InlineData(Operations.UserRoleSet)]
    [InlineData(Operations.UserLocaleSet)]
    public void tenant_admin_reaches_the_management_ops(string op) =>
        Assert.True(AuthorizationMatrix.Allows("tenant_admin", op), op);

    // ---- platform_admin = full row; the CROSSING is audited, not forbidden ---
    // (§5 row 3: the admin exception is audited, not silent — services log it)

    [Theory]
    [InlineData(Operations.TenantRead)]
    [InlineData(Operations.UserRead)]
    [InlineData(Operations.UserCreate)]
    [InlineData(Operations.UserDelete)]
    [InlineData(Operations.UserRoleSet)]
    [InlineData(Operations.UserLocaleSet)]
    public void platform_admin_superset(string op) =>
        Assert.True(AuthorizationMatrix.Allows("platform_admin", op), op);

    // ---- unknown roles deny hard (fail-shut by default, like the walls) ------

    [Theory]
    [InlineData("guest")]
    [InlineData("")]
    [InlineData("admin")]           // not a real role code; the right code is impersonator bait
    public void unknown_roles_deny_everything(string role) =>
        Assert.False(AuthorizationMatrix.Allows(role, Operations.TenantRead));

    // ---- the ONE helper: PermissionMatrix.Assert (07 §5 layer 2) -------------

    private static CallerContext Acting(string role, Guid tenantId) =>
        new(Guid.NewGuid(), tenantId, [role]);

    [Fact]
    public void assert_passes_when_role_and_tenant_align()
    {
        var tenantId = Guid.NewGuid();
        string? error = PermissionMatrix.Assert(Acting("tenant_admin", tenantId),
            Operations.UserDelete, targetTenantId: tenantId);
        Assert.Null(error);
    }

    [Fact]
    public void assert_denies_the_role_gap_before_the_tenant_gap()
    {
        var tenantId = Guid.NewGuid();
        // verb check BEFORE anything else (spec-02 law): wrong role → Forbidden
        string? error = PermissionMatrix.Assert(Acting("viewer", tenantId),
            Operations.UserDelete, targetTenantId: tenantId);
        Assert.Equal(IdentityErrors.Forbidden, error);
    }

    [Fact]
    public void assert_denies_cross_tenant_target()
    {
        string? error = PermissionMatrix.Assert(Acting("tenant_admin", Guid.NewGuid()),
            Operations.UserDelete, targetTenantId: Guid.NewGuid());
        Assert.Equal(IdentityErrors.CrossTenantTarget, error);
    }

    [Fact]
    public void assert_lets_platform_admin_cross_tenants()
    {
        // the audited exception — matrix-mathematical yes; services own the LOG
        string? error = PermissionMatrix.Assert(Acting("platform_admin", Guid.NewGuid()),
            Operations.UserDelete, targetTenantId: Guid.NewGuid());
        Assert.Null(error);
    }
}
