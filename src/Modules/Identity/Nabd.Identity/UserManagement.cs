using System.Data;
using Npgsql;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Identity;

/// <summary>
/// FR-04: user management inside the caller's tenant — add / set role / set locale / delete.
/// Two-layer enforcement, layer 2 lives HERE: permission asserts from the caller context
/// (07 §5) happen BEFORE any data access; the API layer's attributes are layer 1 (spec 04 makes
/// them real — this service must be safe even if layer 1 goes missing, the removal-drill promise).
/// Caller context comes from verified JWT (never client-invented); tenant comes from the token.
/// </summary>
public sealed class UserManagement : IUserManagement
{
    private static readonly string[] Locales = ["ar", "en"];  // FR-06's stored preference (UI at P5)
    private static readonly string[] RoleCodes = ["viewer", "tenant_admin", "platform_admin"]; // FR-03: 3 exactly

    private readonly IdentityDb _db;

    public UserManagement(IdentityDb db) => _db = db;

    public async Task<Result<UserSnapshot>> CreateUserAsync(
        CallerContext acting, NewUserRequest request, CancellationToken ct = default)
    {
        // ---- layer 2 asserts (07 §5): management is tenant_admin work, OWN tenant only ----
        var forbidden = AssertCanManage(acting, request.TargetTenantId);
        if (forbidden is not null) return Result<UserSnapshot>.Failure(forbidden);

        if (request.RoleCode is not ("viewer" or "tenant_admin"))
            return Result<UserSnapshot>.Failure(IdentityErrors.RoleUnknown); // only platform_admin exists as a code, but no caller may ASSIGN it here
        if (request.Locale is not ("ar" or "en"))
            return Result<UserSnapshot>.Failure(IdentityErrors.InvalidLocale);
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return Result<UserSnapshot>.Failure("identity:invalid_email");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return Result<UserSnapshot>.Failure("identity:weak_password");

        var userId = Guid.NewGuid();
        var passwordHash = PasswordHasher.Hash(request.Password);
        var (conn, tx) = await _db.OpenTenantScopeAsync(request.TargetTenantId, ct);
        try
        {
            if (await ExistsAsync(conn, tx, "SELECT EXISTS(SELECT 1 FROM identity.users WHERE email = @v);", request.Email, ct))
                return Result<UserSnapshot>.Failure(IdentityErrors.EmailTaken);

            await using var cmd = new NpgsqlCommand("""
                INSERT INTO identity.users (id, tenant_id, email, display_name, locale, password_hash)
                VALUES (@id, @tenant, @email, @name, @locale, @hash)
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", userId);
            cmd.Parameters.AddWithValue("tenant", request.TargetTenantId);
            cmd.Parameters.AddWithValue("email", request.Email);
            cmd.Parameters.AddWithValue("name", request.DisplayName);
            cmd.Parameters.AddWithValue("locale", request.Locale);
            cmd.Parameters.AddWithValue("hash", passwordHash);
            await cmd.ExecuteNonQueryAsync(ct);

            await using var roleCmd = new NpgsqlCommand("""
                INSERT INTO identity.user_roles (user_id, role_code, tenant_id)
                VALUES (@user, @role, @tenant)
                """, conn, tx);
            roleCmd.Parameters.AddWithValue("user", userId);
            roleCmd.Parameters.AddWithValue("role", request.RoleCode);
            roleCmd.Parameters.AddWithValue("tenant", request.TargetTenantId);
            await roleCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            return Result<UserSnapshot>.Success(
                new UserSnapshot(userId, request.TargetTenantId, request.Email, request.RoleCode, request.Locale));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            return Result<UserSnapshot>.Failure(IdentityErrors.EmailTaken);
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

    public async Task<Result<UserSnapshot>> SetUserRoleAsync(
        CallerContext acting, UserRoleChangeRequest request, CancellationToken ct = default)
    {
        var forbidden = AssertCanManage(acting, request.TargetTenantId);
        if (forbidden is not null) return Result<UserSnapshot>.Failure(forbidden);

        if (request.RoleCode is not ("viewer" or "tenant_admin"))
            return Result<UserSnapshot>.Failure(IdentityErrors.RoleUnknown);

        var (conn, tx) = await _db.OpenTenantScopeAsync(request.TargetTenantId, ct);
        try
        {
            var user = await FindUserRowAsync(conn, tx, request.TargetUserId, ct);
            if (user is null)
                return Result<UserSnapshot>.Failure(IdentityErrors.UserNotFound);

            // the LAST-admin guard: tenant_admin must always remain reachable for someone
            if (user.Value.currentRole == "tenant_admin" && request.RoleCode != "tenant_admin")
            {
                var adminCount = await CountTenantAdminsAsync(conn, tx, request.TargetTenantId, ct);
                if (adminCount <= 1)
                    return Result<UserSnapshot>.Failure("identity:last_admin");
            }

            await using (var del = new NpgsqlCommand("""
                DELETE FROM identity.user_roles WHERE user_id = @u AND tenant_id = @t;
                """, conn, tx))
            {
                del.Parameters.AddWithValue("u", request.TargetUserId);
                del.Parameters.AddWithValue("t", request.TargetTenantId);
                await del.ExecuteNonQueryAsync(ct);
            }

            await using (var ins = new NpgsqlCommand("""
                INSERT INTO identity.user_roles (user_id, role_code, tenant_id)
                VALUES (@u, @r, @t);
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("u", request.TargetUserId);
                ins.Parameters.AddWithValue("r", request.RoleCode);
                ins.Parameters.AddWithValue("t", request.TargetTenantId);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return Result<UserSnapshot>.Success(
                user.Value.snapshot with { Role = request.RoleCode });
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

    public async Task<Result<UserSnapshot>> SetUserLocaleAsync(
        CallerContext acting, UserLocaleUpdateRequest request, CancellationToken ct = default)
    {
        // locale is self-service: a user changes THEIR OWN preference; tenant_admin changes
        // anyone's INSIDE the tenant. Anything else is redesign tomorrow, not permissive code today.
        var isSelf = acting.UserId == request.TargetUserId;
        var isAdmin = acting.RoleCodes.Contains("tenant_admin") && acting.TenantId == request.TargetTenantId;
        if (!isSelf && !isAdmin)
            return Result<UserSnapshot>.Failure(IdentityErrors.Forbidden);
        if (request.Locale is not ("ar" or "en"))
            return Result<UserSnapshot>.Failure(IdentityErrors.InvalidLocale);

        var (conn, tx) = await _db.OpenTenantScopeAsync(request.TargetTenantId, ct);
        try
        {
            var user = await FindUserRowAsync(conn, tx, request.TargetUserId, ct);
            if (user is null)
                return Result<UserSnapshot>.Failure(IdentityErrors.UserNotFound);

            await using var cmd = new NpgsqlCommand(
                "UPDATE identity.users SET locale = @l WHERE id = @u;", conn, tx);
            cmd.Parameters.AddWithValue("l", request.Locale);
            cmd.Parameters.AddWithValue("u", request.TargetUserId);
            await cmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            return Result<UserSnapshot>.Success(user.Value.snapshot with { Locale = request.Locale });
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

    public async Task<Result<Unit>> DeleteUserAsync(
        CallerContext acting, Guid targetTenantId, Guid targetUserId, CancellationToken ct = default)
    {
        var forbidden = AssertCanManage(acting, targetTenantId);
        if (forbidden is not null) return Result<Unit>.Failure(forbidden);
        if (targetUserId == acting.UserId)
            return Result<Unit>.Failure("identity:self_delete"); // no admin removes themselves into a locked office

        var (conn, tx) = await _db.OpenTenantScopeAsync(targetTenantId, ct);
        try
        {
            var user = await FindUserRowAsync(conn, tx, targetUserId, ct);
            if (user is null)
                return Result<Unit>.Failure(IdentityErrors.UserNotFound);

            if (user.Value.currentRole == "tenant_admin")
            {
                var adminCount = await CountTenantAdminsAsync(conn, tx, targetTenantId, ct);
                if (adminCount <= 1)
                    return Result<Unit>.Failure("identity:last_admin");
            }

            // FKs CASCADE for user_roles + refresh_tokens (0004), so the row delete is complete
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM identity.users WHERE id = @u;", conn, tx);
            cmd.Parameters.AddWithValue("u", targetUserId);
            await cmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            return Result<Unit>.Success(Unit.Value);
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

    // ---- helpers ----

    private static string? AssertCanManage(CallerContext acting, Guid targetTenantId)
    {
        if (!acting.RoleCodes.Contains("tenant_admin"))
            return IdentityErrors.Forbidden;               // verb check BEFORE anything else
        if (acting.TenantId != targetTenantId)
            return IdentityErrors.CrossTenantTarget;       // target in another tenant = never visible
        return null;
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, string value, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("v", value);
        return await cmd.ExecuteScalarAsync(ct) is bool b && b;
    }

    private static async Task<(UserSnapshot snapshot, string currentRole)?> FindUserRowAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT u.id, u.tenant_id, u.email::text AS email, u.display_name, u.locale,
                   COALESCE(r.role_code, 'viewer') AS role_code
            FROM identity.users u
            LEFT JOIN identity.user_roles r ON r.user_id = u.id AND r.tenant_id = u.tenant_id
            WHERE u.id = @u
            """, conn, tx);
        cmd.Parameters.AddWithValue("u", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var snap = new UserSnapshot(
            reader.GetGuid(0), reader.GetGuid(1),
            reader.GetString(2), reader.GetString(5), reader.GetString(4));
        return (snap, reader.GetString(5));
    }

    private static async Task<int> CountTenantAdminsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM identity.user_roles
            WHERE tenant_id = @t AND role_code = 'tenant_admin';
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is long n ? (int)n : 0;
    }
}
