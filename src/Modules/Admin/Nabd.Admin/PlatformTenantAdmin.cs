using Microsoft.Extensions.Logging;
using Nabd.Admin.Contracts;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;
using Npgsql;

namespace Nabd.Admin;

public sealed class PlatformTenantAdmin : IPlatformTenantAdmin
{
    private readonly AdminDb _db;
    private readonly ILogger<PlatformTenantAdmin> _log;

    public PlatformTenantAdmin(AdminDb db, ILogger<PlatformTenantAdmin> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<Result<IReadOnlyList<TenantFleetSnapshot>>> ListTenantsAsync(CallerContext acting, CancellationToken ct = default)
    {
        var deny = PermissionMatrix.Assert(acting, Operations.TenantList, acting.TenantId);
        if (deny is not null)
        {
            Audit(acting, "tenant.list", null, "denied");
            return Result<IReadOnlyList<TenantFleetSnapshot>>.Failure(deny);
        }


        var rows = new List<TenantFleetSnapshot>();

        await using var conn = await _db.OpenAdminConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand("""
            SELECT id, name, locale, is_active, created_at
            FROM identity.tenants
            ORDER BY created_at
        """, conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TenantFleetSnapshot(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetDateTime(4)
            ));
        }

        Audit(acting, "tenant.list", null, $"listed={rows.Count}");

        return Result<IReadOnlyList<TenantFleetSnapshot>>.Success(rows);
    }

    public async Task<Result<Unit>> DisableTenantAsync(CallerContext acting, Guid targetTenantId, CancellationToken ct = default)
    {
        return await SetTenantActiveAsync(acting, targetTenantId, isActive: false, action: "tenant.disable", ct);
    }
    public async Task<Result<Unit>> EnableTenantAsync(CallerContext acting, Guid targetTenantId, CancellationToken ct = default)
    {
        return await SetTenantActiveAsync(acting, targetTenantId, isActive: true, action: "tenant.enable", ct);
    }
    public async Task<Result<Unit>> SetTenantActiveAsync(CallerContext acting, Guid targetTenantId, Boolean isActive, string action, CancellationToken ct)
    {
        var deny = PermissionMatrix.Assert(acting, OperationOf(action), targetTenantId);
        if (deny is not null)
        {
            Audit(acting, action, targetTenantId, "denied");
            return Result<Unit>.Failure(deny);
        }

        await using var conn = await _db.OpenAdminConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE identity.tenants SET is_active = @active WHERE id = @id", conn
        );
        cmd.Parameters.AddWithValue("active", isActive);
        cmd.Parameters.AddWithValue("id", targetTenantId);

        var touched = await cmd.ExecuteNonQueryAsync(ct);
        if (touched == 0)
        {
            Audit(acting, action, targetTenantId, "target_absent");
            return Result<Unit>.Failure(IdentityErrors.TenantNotFound);
        }

        Audit(acting, action, targetTenantId, isActive ? "enabled" : "disabled");
        return Result<Unit>.Success(Unit.Value);
    }

    // -- audit ---------------------------------------------------------------
    //
    // (plan/07 §9: Admin actions = tenant enable/disable + EVERY admin-module
    // query; retention 400 days — that is P6 ops config, not code.)
    // Fields exactly per the spec sentence: who (actor sub), crossing-from
    // (actor tenant + roles), target-tenant, what (action), when (log ts).
    // Tokens/secrets NEVER enter these lines (§9 rule: no secrets in logs).
    private void Audit(CallerContext acting, string action, Guid? targetTenantId, string outcome) =>
        _log.LogInformation(
            "admin.audit actor={Actor} actor_tenant={ActorTenant} roles={Roles} action={Action} target_tenant={Target} outcome={Outcome}",
            acting.UserId, acting.TenantId,
            string.Join('+', acting.RoleCodes),
            action, targetTenantId, outcome);

    private static string OperationOf(string action) => action switch
    {
        "tenant.disable" => Operations.TenantDisable,
        "tenant.enable" => Operations.TenantEnable,
        _ => Operations.TenantList,
    };
}