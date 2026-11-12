using Npgsql;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Assets;

/// <summary>
/// FR-10 through the wall. Two-layer enforcement, layer 2 lives HERE: the matrix
/// assert (07 §5) fires BEFORE any data access, against the TARGET tenant —
/// the spec-06 review lesson (targetTenantId, never acting.TenantId as the target).
/// Even if the API attributes vanish, these asserts hold (the removal-drill promise).
/// </summary>
public sealed class AssetCatalog(AssetsDb db) : IAssetCatalog
{
    public async Task<Result<AssetSnapshot>> CreateAssetAsync(
        CallerContext acting, CreateAssetRequest request, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AssetCreate, request.TargetTenantId);
        if (forbidden is not null) return Result<AssetSnapshot>.Failure(forbidden);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Result<AssetSnapshot>.Failure(AssetErrors.InvalidName);

        var (conn, tx) = await db.OpenTenantScopeAsync(request.TargetTenantId, ct);
        try
        {
            // the template must exist AND be alive — channels are config, not schema (FR-13)
            await using (var check = new NpgsqlCommand("""
                SELECT 1 FROM assets.asset_type_templates WHERE id = @t AND alive = true;
                """, conn, tx))
            {
                check.Parameters.AddWithValue("t", request.TemplateId);
                if (await check.ExecuteScalarAsync(ct) is null)
                    return Result<AssetSnapshot>.Failure(AssetErrors.UnknownTemplate);
            }

            await using var cmd = new NpgsqlCommand("""
                INSERT INTO assets.assets (id, tenant_id, template_id, name, location, notes)
                VALUES (@id, @tenant, @template, @name, @location, @notes)
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenant", request.TargetTenantId);
            cmd.Parameters.AddWithValue("template", request.TemplateId);
            cmd.Parameters.AddWithValue("name", request.Name.Trim());
            cmd.Parameters.AddWithValue("location", (object?)request.Location ?? DBNull.Value);
            cmd.Parameters.AddWithValue("notes", (object?)request.Notes ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);

            var created = await FindRowAsync(conn, tx, (Guid)cmd.Parameters["id"].Value!, ct)
                          ?? throw new InvalidOperationException("INSERT reported success but the row is invisible — RLS scope bug");
            await tx.CommitAsync(ct);
            return Result<AssetSnapshot>.Success(created);
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

    public async Task<Result<AssetSnapshot>> GetAssetAsync(
        CallerContext acting, Guid assetId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AssetRead, acting.TenantId);
        if (forbidden is not null) return Result<AssetSnapshot>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var row = await FindRowAsync(conn, tx, assetId, ct);
            if (row is null)
                return Result<AssetSnapshot>.Failure(AssetErrors.AssetNotFound); // RLS hid it: not-found, not forbidden

            await tx.CommitAsync(ct);
            return Result<AssetSnapshot>.Success(row);
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

    public async Task<Result<IReadOnlyList<AssetSnapshot>>> ListAssetsAsync(
        CallerContext acting, Guid tenantId, bool includeArchived = false, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AssetRead, tenantId);
        if (forbidden is not null) return Result<IReadOnlyList<AssetSnapshot>>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(tenantId, ct);
        try
        {
            var list = new List<AssetSnapshot>();

            await using (var cmd = new NpgsqlCommand("""
                SELECT id, tenant_id, template_id, name, location, notes, is_active
                FROM assets.assets
                WHERE (@includeArchived OR is_active)
                ORDER BY name
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("includeArchived", includeArchived);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    list.Add(ReadRow(reader));
            }

            await tx.CommitAsync(ct);
            return Result<IReadOnlyList<AssetSnapshot>>.Success(list);
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

    public async Task<Result<AssetSnapshot>> UpdateAssetAsync(
        CallerContext acting, UpdateAssetRequest request, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AssetUpdate, request.TargetTenantId);
        if (forbidden is not null) return Result<AssetSnapshot>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(request.TargetTenantId, ct);
        try
        {
            var current = await FindRowAsync(conn, tx, request.AssetId, ct);
            if (current is null)
                return Result<AssetSnapshot>.Failure(AssetErrors.AssetNotFound);

            // PATCH semantics: null = leave as is. Name (if given) must stay non-empty.
            var name = request.Name is null ? current.Name : request.Name.Trim();
            if (name.Length == 0)
                return Result<AssetSnapshot>.Failure(AssetErrors.InvalidName);

            await using var cmd = new NpgsqlCommand("""
                UPDATE assets.assets
                SET name = @n, location = @l, notes = @notes
                WHERE id = @a
                """, conn, tx);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("l", (object?)(request.Location ?? current.Location) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("notes", (object?)(request.Notes ?? current.Notes) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("a", request.AssetId);
            await cmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            return Result<AssetSnapshot>.Success(current with
            {
                Name = name,
                Location = request.Location ?? current.Location,
                Notes = request.Notes ?? current.Notes,
            });
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

    public async Task<Result<Unit>> ArchiveAssetAsync(
        CallerContext acting, Guid targetTenantId, Guid assetId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AssetArchive, targetTenantId);
        if (forbidden is not null) return Result<Unit>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(targetTenantId, ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE assets.assets SET is_active = false WHERE id = @a;", conn, tx);
            cmd.Parameters.AddWithValue("a", assetId);
            var touched = await cmd.ExecuteNonQueryAsync(ct);

            if (touched == 0)
                return Result<Unit>.Failure(AssetErrors.AssetNotFound);

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

    /// <summary>The one row-reader. The reader closes HERE (shop law #15: never touch
    /// the transaction while a reader is open — P1's NpgsqlOperationInProgressException).</summary>
    private static async Task<AssetSnapshot?> FindRowAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, tenant_id, template_id, name, location, notes, is_active
            FROM assets.assets WHERE id = @a
            """, conn, tx);
        cmd.Parameters.AddWithValue("a", assetId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRow(reader);
    }

    private static AssetSnapshot ReadRow(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetBoolean(6));
}