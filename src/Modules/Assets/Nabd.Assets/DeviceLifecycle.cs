using Npgsql;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Assets;

/// <summary>
/// The real device lifecycle (FR-11/12) — SQL-backed, behind the 0012 wall
/// (assets.devices + assets.device_credentials_log are RLS-scoped; the log is
/// append-only by GRANT). Two-layer enforcement: matrix asserts HERE, before
/// any data access; attributes at the doors are layer 1.
/// </summary>
public sealed class DeviceLifecycle(AssetsDb db, DeviceTokenHasher hasher) : IDeviceLifecycle
{
    public async Task<Result<DeviceMintResult>> CreateDeviceAsync(
        CallerContext acting, DeviceCreateRequest request, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.DeviceRegister, acting.TenantId);
        if (forbidden is not null) return Result<DeviceMintResult>.Failure(forbidden);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Result<DeviceMintResult>.Failure(DeviceErrors.InvalidName);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            // the device hangs off an OWN-TENANT asset; its template mirrors the asset's
            Guid assetTemplate;
            await using (var check = new NpgsqlCommand("""
                SELECT template_id FROM assets.assets WHERE id = @a;
                """, conn, tx))
            {
                check.Parameters.AddWithValue("a", request.AssetId);
                var found = await check.ExecuteScalarAsync(ct);
                if (found is null)
                    return Result<DeviceMintResult>.Failure(DeviceErrors.AssetNotFound); // RLS hid it: 404 semantics
                assetTemplate = (Guid)found;
            }

            var deviceId = Guid.NewGuid();
            var token = DeviceTokenHasher.Mint();

            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO assets.devices (id, tenant_id, asset_id, template_id, name, token_hash)
                VALUES (@id, @tenant, @asset, @template, @name, @hash)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", deviceId);
                cmd.Parameters.AddWithValue("tenant", acting.TenantId);
                cmd.Parameters.AddWithValue("asset", request.AssetId);
                cmd.Parameters.AddWithValue("template", assetTemplate);
                cmd.Parameters.AddWithValue("name", request.Name.Trim());
                cmd.Parameters.AddWithValue("hash", hasher.Hash(token));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await LogAsync(conn, tx, deviceId, acting.TenantId, "issued", acting.UserId.ToString(), ct);

            await tx.CommitAsync(ct);
            return Result<DeviceMintResult>.Success(new DeviceMintResult(
                new DeviceSnapshot2(deviceId, acting.TenantId, request.AssetId, assetTemplate,
                    request.Name.Trim(), IsActive: true, TokenRevokedAtUtc: null),
                token));
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

    public async Task<Result<IReadOnlyList<DeviceSnapshot2>>> ListByAssetAsync(
        CallerContext acting, Guid assetId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.DeviceRead, acting.TenantId);
        if (forbidden is not null) return Result<IReadOnlyList<DeviceSnapshot2>>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var list = new List<DeviceSnapshot2>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT id, tenant_id, asset_id, template_id, name, is_active, token_revoked_at
                FROM assets.devices WHERE asset_id = @a ORDER BY name
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("a", assetId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) list.Add(ReadRow(reader));
            }
            await tx.CommitAsync(ct);
            return Result<IReadOnlyList<DeviceSnapshot2>>.Success(list);
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

    public async Task<Result<DeviceMintResult>> ReissueTokenAsync(
        CallerContext acting, Guid deviceId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.DeviceTokenReissue, acting.TenantId);
        if (forbidden is not null) return Result<DeviceMintResult>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var device = await FindRowAsync(conn, tx, deviceId, ct);
            if (device is null)
                return Result<DeviceMintResult>.Failure(DeviceErrors.DeviceNotFound);

            var token = DeviceTokenHasher.Mint();
            await using (var cmd = new NpgsqlCommand("""
                UPDATE assets.devices
                SET token_hash = @hash, token_issued_at = now(), token_revoked_at = NULL
                WHERE id = @d
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("hash", hasher.Hash(token));
                cmd.Parameters.AddWithValue("d", deviceId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // TWO audit lines: the old credential's revocation, then the new issuance
            await LogAsync(conn, tx, deviceId, acting.TenantId, "revoked", acting.UserId.ToString(), ct);
            await LogAsync(conn, tx, deviceId, acting.TenantId, "issued", acting.UserId.ToString(), ct);

            await tx.CommitAsync(ct);
            return Result<DeviceMintResult>.Success(new DeviceMintResult(device with { TokenRevokedAtUtc = null }, token));
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

    public async Task<Result<Unit>> RevokeDeviceAsync(
        CallerContext acting, Guid deviceId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.DeviceRevoke, acting.TenantId);
        if (forbidden is not null) return Result<Unit>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var device = await FindRowAsync(conn, tx, deviceId, ct);
            if (device is null)
                return Result<Unit>.Failure(DeviceErrors.DeviceNotFound);

            await using (var cmd = new NpgsqlCommand("""
                UPDATE assets.devices
                SET token_revoked_at = now(), is_active = false
                WHERE id = @d
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("d", deviceId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await LogAsync(conn, tx, deviceId, acting.TenantId, "revoked", acting.UserId.ToString(), ct);

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

    public async Task<Guid?> VerifyTokenAsync(string presentedToken, CancellationToken ct = default)
    {
        // Pre-auth by design (the broker's auth door calls this BEFORE any caller exists)
        // — so NO tenant scope: one keyed hash lookup, app role, own connection.
        var hash = hasher.Hash(presentedToken);
        var conn = await db.OpenPreAuthConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT assets.device_id_by_token_hash(@h)", conn);
            cmd.Parameters.AddWithValue("h", hash);
            var raw = await cmd.ExecuteScalarAsync(ct);
            return raw is Guid id ? id : null;
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---- the broker path (P2 spec 05) ---------------------------------------

    public async Task<bool> BrokerAuthenticateAsync(
        Guid deviceId, string presentedToken, CancellationToken ct = default)
    {
        var verified = await VerifyTokenAsync(presentedToken, ct);
        return verified == deviceId;   // valid AND THIS device — not "valid for anyone"
    }

    public async Task<bool> AuthorizePublishAsync(
            Guid deviceId, string topic, string action, CancellationToken ct = default)
    {
        if (action != "publish") return false;      // subscribe denied at MVP (commands are V1+)

        // the topic must be EXACTLY t/{tid}/dev/{deviceId}/v1 — wildcards and
        // anything else die in the split (a topic is never a pattern here)
        var seg = topic.Split('/');
        if (seg.Length != 5 || seg[0] != "t" || seg[2] != "dev" || seg[4] != "v1")
            return false;
        if (seg[3] != deviceId.ToString()) return false;   // own id only — no DB needed yet

        // tid must match the CREDENTIAL's tenant: 0016 answers null for
        // revoked/inactive devices → deny (revocation enforced at every publish)
        var conn = await db.OpenPreAuthConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT assets.device_tenant_by_id(@d)", conn);
            cmd.Parameters.AddWithValue("d", deviceId);
            var raw = await cmd.ExecuteScalarAsync(ct);
            if (raw is not Guid tenantId) return false;
            return seg[1] == tenantId.ToString();
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    // ---- helpers ----

    /// <summary>The audit line — INSERT only (the log is append-only by GRANT, 0012).</summary>
    private static async Task LogAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid deviceId, Guid tenantId, string action, string actor, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO assets.device_credentials_log (device_id, tenant_id, action, actor)
            VALUES (@d, @t, @a, @who)
            """, conn, tx);
        cmd.Parameters.AddWithValue("d", deviceId);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("who", actor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DeviceSnapshot2?> FindRowAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid deviceId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, tenant_id, asset_id, template_id, name, is_active, token_revoked_at
            FROM assets.devices WHERE id = @d
            """, conn, tx);
        cmd.Parameters.AddWithValue("d", deviceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRow(reader);
    }

    private static DeviceSnapshot2 ReadRow(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
        reader.GetString(4),
        reader.GetBoolean(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
}