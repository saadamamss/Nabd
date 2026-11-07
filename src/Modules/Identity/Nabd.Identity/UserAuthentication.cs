using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;
using Npgsql;

namespace Nabd.Identity;

public sealed class UserAuthentication : IUserAuthentication
{
    private readonly IdentityDb _db;
    private readonly LoginThrottle _throttle;

    public UserAuthentication(IdentityDb db, LoginThrottle throttle)
    {
        _db = db;
        _throttle = throttle;
    }

    public async Task<Result<LoginResult>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password)) return Refuse(request);

        LoginRow? user = null;

        await using (var conn = await _db.OpenPreAuthConnectionAsync(ct))
        {
            await using var cmd = new NpgsqlCommand("SELECT user_id, tenant_id, password_hash FROM identity.find_login_user(@email)", conn);

            cmd.Parameters.AddWithValue("email", request.Email);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                user = new LoginRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2));
            }
        }

        if (user is null) return Refuse(request);

        // The verify is the wrong-password burn itself: negated, it pays argon2id's
        // full cost before refusing (plan/07 §3 row 7 — no shortcuts in either lane).
        if (!PasswordHasher.Verify(request.Password, user.Value.Hash)) return Refuse(request); // wrong-password IS a failure: the streak counts it (row 5)

        _throttle.RecordSuccess(ThrottleKey(request));

        return await OpenSessionAsync(user.Value.UserId, user.Value.TenantId, ct);
    }

    public async Task<Result<LoginResult>> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Result<LoginResult>.Failure(IdentityErrors.InvalidCredentials);
        }

        var hash = RefreshTokenMint.Hash(request.RefreshToken);

        RefreshRow? row = null;
        await using (var conn = await _db.OpenPreAuthConnectionAsync(ct))
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT user_id, tenant_id, revoked_at, expires_at FROM identity.find_refresh_session(@h)", conn
            );
            cmd.Parameters.AddWithValue("h", hash);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                row = new RefreshRow(
                    reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)
                );
            }
        }

        // Unknown and expired answer the SAME error: no oracle for guessing which one.
        // Replay detection MUST come below this line — an expired token is just cold.
        if (row is null || row.Value.ExpiresAt <= DateTime.UtcNow)
        {
            return Result<LoginResult>.Failure(IdentityErrors.InvalidCredentials);
        }

        if (row.Value.RevokedAt is not null)
        {
            await RevokeAllSessionsAsync(row.Value.UserId, row.Value.TenantId, ct);

            return Result<LoginResult>.Failure(IdentityErrors.InvalidCredentials);
        }

        {
            var (conn, tx) = await _db.OpenTenantScopeAsync(row.Value.TenantId, ct);

            try
            {
                await using var cmd = new NpgsqlCommand(
                    "UPDATE identity.refresh_tokens SET revoked_at = now() WHERE token_hash = @h;", conn, tx
                );

                cmd.Parameters.AddWithValue("h", hash);
                var n = await cmd.ExecuteNonQueryAsync(ct);

                if (n != 1)
                {
                    await tx.RollbackAsync(ct);
                    await RevokeAllSessionsAsync(row.Value.UserId, row.Value.TenantId, ct);

                    return Result<LoginResult>.Failure(IdentityErrors.InvalidCredentials);
                }

                await tx.CommitAsync(ct);
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

        return await OpenSessionAsync(row.Value.UserId, row.Value.TenantId, ct);
    }

    public async Task<Result<Unit>> LogoutAsync(RefreshRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return Result<Unit>.Success(Unit.Value); // idempotent: nothing to revoke is already revoked

        var hash = RefreshTokenMint.Hash(request.RefreshToken);

        RotatedRow? row = null;
        await using (var conn = await _db.OpenPreAuthConnectionAsync(ct))
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT user_id, tenant_id, revoked_at FROM identity.find_refresh_session(@h)", conn);
            cmd.Parameters.AddWithValue("h", hash);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                row = new RotatedRow(reader.GetGuid(0), reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
        }

        if (row is null || row.Value.RevokedAt is not null)
            return Result<Unit>.Success(Unit.Value); // idempotent: dead/unknown = nothing to do

        // Logout revokes EXACTLY the presented session — the user's other devices stay alive.
        var (private_conn, private_tx) = await _db.OpenTenantScopeAsync(row.Value.TenantId, ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE identity.refresh_tokens SET revoked_at = now() WHERE token_hash = @h;", private_conn, private_tx);
            cmd.Parameters.AddWithValue("h", hash);
            await cmd.ExecuteNonQueryAsync(ct);
            await private_tx.CommitAsync(ct);
            return Result<Unit>.Success(Unit.Value);
        }
        catch { await private_tx.RollbackAsync(ct); throw; }
        finally { await private_conn.DisposeAsync(); }
    }

    public async Task<Result<Unit>> ChangePasswordAsync(CallerContext acting, Guid tenantId, Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {

        if (acting.UserId != userId)
        {
            return Result<Unit>.Failure(IdentityErrors.Forbidden);
        }
        if (acting.TenantId != tenantId)
        {
            return Result<Unit>.Failure(IdentityErrors.CrossTenantTarget);
        }

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || request.NewPassword is null || request.NewPassword.Length < 8)
        {
            return Result<Unit>.Failure(IdentityErrors.InvalidCredentials);
        }

        var (conn, tx) = await _db.OpenTenantScopeAsync(tenantId, ct);

        try
        {
            string? stored = null;

            await using (var cmd = new NpgsqlCommand(
                "SELECT password_hash FROM identity.users WHERE id = @u AND tenant_id = @t;", conn, tx
            ))
            {
                cmd.Parameters.AddWithValue("u", userId);
                cmd.Parameters.AddWithValue("t", tenantId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct)) stored = reader.GetString(0);
            }

            if (stored is null || !PasswordHasher.Verify(request.CurrentPassword, stored))
            {
                return Result<Unit>.Failure(IdentityErrors.InvalidCredentials); // wrong current password gets the login answer, never a change
            }

            await using (var cmd = new NpgsqlCommand(
                "UPDATE identity.users SET password_hash = @h WHERE id = @u;", conn, tx
            ))
            {
                cmd.Parameters.AddWithValue("u", userId);
                cmd.Parameters.AddWithValue("h", PasswordHasher.Hash(request.NewPassword));

                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(
                "UPDATE identity.refresh_tokens SET revoked_at = now() WHERE user_id = @u AND revoked_at IS NULL;", conn, tx
            ))
            {
                cmd.Parameters.AddWithValue("u", userId);
                await cmd.ExecuteNonQueryAsync(ct);

            }

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
    // Helpers

    private async Task RevokeAllSessionsAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var (conn, tx) = await _db.OpenTenantScopeAsync(tenantId, ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE identity.refresh_tokens SET revoked_at = now() WHERE user_id = @u AND revoked_at IS NULL;", conn, tx);
            cmd.Parameters.AddWithValue("u", userId);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
        finally { await conn.DisposeAsync(); }
    }

    private static string MintAccessToken(Guid userId, Guid tenantId, string role)
    {
        var hex = Environment.GetEnvironmentVariable("NABD_JWT_SECRET") ?? throw new InvalidOperationException("NABD_JWT_SECRET must be set (NFR-08: 12-factor).");

        var key = new SymmetricSecurityKey(Convert.FromHexString(hex)) { KeyId = "nabd-p1" };

        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "nabd-identity",
            audience: "nabd-api",
            claims: [new Claim("sub", userId.ToString()), new Claim("tid", tenantId.ToString()), new Claim("roles", role)],
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<Result<LoginResult>> OpenSessionAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var (conn, tx) = await _db.OpenTenantScopeAsync(tenantId, ct);

        try
        {
            string? email = null, locale = null, role = null;

            await using (var cmd = new NpgsqlCommand("""
                SELECT u.email::text, u.locale, COALESCE(r.role_code,'viewer') FROM identity.users u
                LEFT JOIN identity.user_roles r ON r.user_id = u.id AND r.tenant_id = u.tenant_id
                WHERE u.id = @u AND u.tenant_id = @t
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("u", userId);
                cmd.Parameters.AddWithValue("t", tenantId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    email = reader.GetString(0);
                    locale = reader.GetString(1);
                    role = reader.GetString(2);
                }
            }

            if (email is null)
            {
                return Result<LoginResult>.Failure(IdentityErrors.InvalidCredentials);
            }

            var presented = RefreshTokenMint.New();
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO identity.refresh_tokens (user_id, token_hash, expires_at) VALUES (@u, @h, now()+ interval '7 days');
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("u", userId);
                cmd.Parameters.AddWithValue("h", RefreshTokenMint.Hash(presented));

                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return Result<LoginResult>.Success(new LoginResult(

                User: new UserSnapshot(userId, tenantId, email, role!, locale!),
                AccessToken: MintAccessToken(userId, tenantId, role!),
                RefreshToken: presented
            ));
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

    private Result<LoginResult> Refuse(LoginRequest request)
    {
        // row 5 discipline: EVERY denial is counted (banded attempts included, so a
        // hammering bot escalates its own band), then the band is computed from the
        // NEW streak — the 5th recorded failure is the first to answer 1s, doubling on.
        // row 7 discipline stays: success is never banded (verify runs first and the
        // reset-on-success at LoginAsync 51 keeps the honest door reachable forever).
        var key = ThrottleKey(request);
        _throttle.RecordFailure(key);
        var band = _throttle.DelaySeconds(key);

        if (band > 0)
            return Result<LoginResult>.Failure($"{IdentityErrors.LoginBackoff}:{band}"); // 429-ish, band in body

        EnumerationArmor.Burn(request.Password);   // row 7: pay argon2id's full cost before refusing
        return Result<LoginResult>.Failure(IdentityErrors.InvalidCredentials);
    }

    private static string ThrottleKey(LoginRequest request)
    {
        return $"ip:{request.ClientIp ?? "unknown"}|account:{request.Email?.Trim().ToLowerInvariant()}";
    }

    private readonly record struct LoginRow(Guid UserId, Guid TenantId, string Hash);
    private readonly record struct RefreshRow(Guid UserId, Guid TenantId, DateTimeOffset? RevokedAt, DateTimeOffset ExpiresAt);
    private readonly record struct RotatedRow(Guid UserId, Guid TenantId, DateTimeOffset? RevokedAt);
}
