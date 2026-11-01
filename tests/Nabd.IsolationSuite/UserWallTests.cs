namespace Nabd.IsolationSuite;

using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// Isolation-suite piece 3 (P1 spec 02): the service-layer denials already witnessed by
// the Postman ladder, now automated and permanent. These call UserManagement /
// TenantRegistration DIRECTLY (no HTTP) — they prove layer 2 (07 §5) survives with
// layer 1 completely absent, which is the removal-drill promise of the DoD.
// Registration is REAL (TenantRegistration, app lane) so every wall stands against real rows.

public sealed class UserWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Wall {label}",
            Slug: $"wall-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@wall.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    [Fact]
    public async Task cross_tenant_reads_are_refused_before_any_data_touch()
    {
        var a = await RegisterAsync("a");
        var b = await RegisterAsync("b");
        var admin = new CallerContext(a.AdminUser.UserId, a.Tenant.TenantId, ["tenant_admin"]);

        var readTenant = await new TenantRegistration(pg.Db).GetTenantAsync(admin, b.Tenant.TenantId);
        Assert.False(readTenant.IsSuccess);
        Assert.Equal(IdentityErrors.CrossTenantTarget, readTenant.Error);

        var readUsers = await new UserManagement(pg.Db).ListUsersAsync(admin, b.Tenant.TenantId);
        Assert.False(readUsers.IsSuccess);
        Assert.Equal(IdentityErrors.CrossTenantTarget, readUsers.Error);
    }

    [Fact]
    public async Task cross_tenant_writes_are_refused_before_any_data_touch()
    {
        var a = await RegisterAsync("c");
        var b = await RegisterAsync("d");
        var admin = new CallerContext(a.AdminUser.UserId, a.Tenant.TenantId, ["tenant_admin"]);
        var users = new UserManagement(pg.Db);

        var roleChange = await users.SetUserRoleAsync(
            admin, new UserRoleChangeRequest(b.Tenant.TenantId, b.AdminUser.UserId, "viewer"));
        Assert.False(roleChange.IsSuccess);
        Assert.Equal(IdentityErrors.CrossTenantTarget, roleChange.Error);

        var delete = await users.DeleteUserAsync(admin, b.Tenant.TenantId, b.AdminUser.UserId);
        Assert.False(delete.IsSuccess);
        Assert.Equal(IdentityErrors.CrossTenantTarget, delete.Error);

        var create = await users.CreateUserAsync(admin, new NewUserRequest(
            TargetTenantId: b.Tenant.TenantId,
            Email: $"{Uid()}@b.test", DisplayName: "B New", Locale: "en",
            Password: "CorrectHorse1", RoleCode: "viewer"));
        Assert.False(create.IsSuccess);
        Assert.Equal(IdentityErrors.CrossTenantTarget, create.Error);
    }

    [Fact]
    public async Task viewer_verb_is_refused_even_for_own_tenant()
    {
        var a = await RegisterAsync("e");
        var viewer = new CallerContext(Guid.NewGuid(), a.Tenant.TenantId, ["viewer"]);
        var users = new UserManagement(pg.Db);

        var create = await users.CreateUserAsync(viewer, new NewUserRequest(
            TargetTenantId: a.Tenant.TenantId,
            Email: $"{Uid()}@a.test", DisplayName: "Should Not Exist", Locale: "en",
            Password: "CorrectHorse1", RoleCode: "viewer"));

        Assert.False(create.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, create.Error);
    }

    [Fact]
    public async Task last_admin_cannot_demote_themselves()
    {
        var a = await RegisterAsync("f");
        var admin = new CallerContext(a.AdminUser.UserId, a.Tenant.TenantId, ["tenant_admin"]);
        var users = new UserManagement(pg.Db);

        var demote = await users.SetUserRoleAsync(
            admin, new UserRoleChangeRequest(a.Tenant.TenantId, a.AdminUser.UserId, "viewer"));

        Assert.False(demote.IsSuccess);
        Assert.Equal(IdentityErrors.LastAdmin, demote.Error);
    }

    [Fact]
    public async Task admin_cannot_delete_themselves()
    {
        var a = await RegisterAsync("g");
        var admin = new CallerContext(a.AdminUser.UserId, a.Tenant.TenantId, ["tenant_admin"]);
        var users = new UserManagement(pg.Db);

        var delete = await users.DeleteUserAsync(admin, a.Tenant.TenantId, a.AdminUser.UserId);

        Assert.False(delete.IsSuccess);
        Assert.Equal(IdentityErrors.SelfDelete, delete.Error);
    }
}
