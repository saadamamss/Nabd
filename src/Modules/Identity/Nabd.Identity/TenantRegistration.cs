using Npgsql;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Identity;

/// <summary>
/// FR-01: tenant registration = tenant + its FIRST user (tenant_admin) + role row,
/// ONE transaction, behind the same wall the rest of the house uses.
/// Law chain: plan/04 §4 (tenants/users composition) · plan/07 §3 row 1 (argon2id at room level)
/// · anti-enumeration discipline: duplicate slug/email produce PURPOSED error codes,
/// checked before touching rows (the constraint violations remain as enforcement, not UX).
/// </summary>
public sealed class TenantRegistration : ITenantRegistration
{
    private readonly IdentityDb _db;

    public TenantRegistration(IdentityDb db) => _db = db;

    public async Task<Result<TenantRegistrationResult>> RegisterTenantAsync(
        RegisterTenantRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Result<TenantRegistrationResult>.Failure("identity:invalid_name");
        if (string.IsNullOrWhiteSpace(request.Slug) || request.Slug.Length < 3)
            return Result<TenantRegistrationResult>.Failure("identity:invalid_slug");
        if (string.IsNullOrWhiteSpace(request.AdminEmail) || !request.AdminEmail.Contains('@'))
            return Result<TenantRegistrationResult>.Failure("identity:invalid_email");
        if (string.IsNullOrWhiteSpace(request.AdminPassword) || request.AdminPassword.Length < 8)
            return Result<TenantRegistrationResult>.Failure("identity:weak_password");
        if (request.AdminLocale is not ("ar" or "en"))
            return Result<TenantRegistrationResult>.Failure(IdentityErrors.InvalidLocale);

        // wall mechanics: the tenants policy checks id == app.current_tenant on the NEW row,
        // so we drive the transaction with the tenant id we are about to insert.
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var passwordHash = PasswordHasher.Hash(request.AdminPassword);

        var (conn, tx) = await _db.OpenRegistrationScopeAsync(tenantId, ct);
        try
        {
            if (await ExistsAsync(conn, tx, "SELECT EXISTS(SELECT 1 FROM identity.tenants WHERE slug = @v);", request.Slug, ct))
                return Result<TenantRegistrationResult>.Failure(IdentityErrors.SlugTaken);
            if (await ExistsAsync(conn, tx, "SELECT EXISTS(SELECT 1 FROM identity.users WHERE email = @v);", request.AdminEmail, ct))
                return Result<TenantRegistrationResult>.Failure(IdentityErrors.EmailTaken);

            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO identity.tenants (id, name, locale, is_active, plan_ref, slug)
                VALUES (@id, @name, @locale, true, 'starter', @slug)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", tenantId);
                cmd.Parameters.AddWithValue("name", request.Name);
                cmd.Parameters.AddWithValue("locale", request.AdminLocale);
                cmd.Parameters.AddWithValue("slug", request.Slug);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO identity.users (id, tenant_id, email, display_name, locale, password_hash)
                VALUES (@id, @tenant, @email, @name, @locale, @hash)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", userId);
                cmd.Parameters.AddWithValue("tenant", tenantId);
                cmd.Parameters.AddWithValue("email", request.AdminEmail);
                cmd.Parameters.AddWithValue("name", request.AdminDisplayName);
                cmd.Parameters.AddWithValue("locale", request.AdminLocale);
                cmd.Parameters.AddWithValue("hash", passwordHash);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO identity.user_roles (user_id, role_code, tenant_id)
                VALUES (@user, 'tenant_admin', @tenant)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("user", userId);
                cmd.Parameters.AddWithValue("tenant", tenantId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            var tenant = new TenantSnapshot(tenantId, request.Name, true);
            var admin = new UserSnapshot(userId, tenantId, request.AdminEmail, "tenant_admin", request.AdminLocale);
            return Result<TenantRegistrationResult>.Success(new TenantRegistrationResult(tenant, admin));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            return Result<TenantRegistrationResult>.Failure(
                (ex.ConstraintName ?? "").Contains("slug")
                    ? IdentityErrors.SlugTaken
                    : IdentityErrors.EmailTaken);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, string value, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("v", value);
        return await cmd.ExecuteScalarAsync(ct) is true or "t" or "true";
    }
}
