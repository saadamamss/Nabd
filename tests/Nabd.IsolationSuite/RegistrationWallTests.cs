namespace Nabd.IsolationSuite;

using Npgsql;
using Xunit;
using Nabd.Identity;
using Nabd.Identity.Contracts;


public sealed class RegistrationWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task registration_persists_tenant_admin_role_and_argon2id_hash()
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: "Suite Acme",
            Slug: "Suite-acme",
            AdminEmail: "suite-admin@acme.test",
            AdminDisplayName: "Suite Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"
        ));

        Assert.True(result.IsSuccess, $"registration failed with: {result.Error}");
        Assert.NotEqual(Guid.Empty, result.Value.AdminUser.UserId);

        await using var ownerConn = await pg.Owner.OpenConnectionAsync();

        await using (var cmd = new NpgsqlCommand("SELECT name FROM identity.tenants WHERE id = @id", ownerConn))
        {
            cmd.Parameters.AddWithValue("id", result.Value.Tenant.TenantId);
            Assert.Equal("Suite Acme", await cmd.ExecuteScalarAsync());
        }


        await using (var cmd = new NpgsqlCommand("""
            SELECT password_hash FROM identity.users
            WHERE id = @id AND email = @email
            """, ownerConn))
        {
            cmd.Parameters.AddWithValue("id", result.Value.AdminUser.UserId);
            cmd.Parameters.AddWithValue("email", "suite-admin@acme.test");

            var hash = await cmd.ExecuteScalarAsync() as string;
            Assert.NotNull(hash);
            Assert.StartsWith("argon2id$", hash ?? "");

        }

        await using (var cmd = new NpgsqlCommand(
                    """
                    SELECT role_code FROM identity.user_roles
                    WHERE user_id = @id AND role_code = 'tenant_admin'
                    """, ownerConn))
        {
            cmd.Parameters.AddWithValue("id", result.Value.AdminUser.UserId);
            Assert.Equal("tenant_admin", await cmd.ExecuteScalarAsync());
        }
    }
}
