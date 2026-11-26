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

    /// <summary>
    /// P5 spec 06 S3 (F6/F9/F10, C6/C9): the owner's read. Same audited lane as
    /// ListTenants — `nabd_admin` holds SELECT on `feedback.messages` (0030), so
    /// the room queries the table directly and never references `Nabd.Feedback`.
    /// The author columns are the stamped ones; `user_id` is NOT joined back to
    /// `identity.users` (no grant there, and a deleted author must survive).
    /// </summary>
    public async Task<Result<FeedbackFleetPage>> ListFeedbackAsync(
        CallerContext acting, int limit, CancellationToken ct = default)
    {
        var deny = PermissionMatrix.Assert(acting, Operations.FeedbackRead, acting.TenantId);
        if (deny is not null)
        {
            Audit(acting, "feedback.list", null, "denied");
            return Result<FeedbackFleetPage>.Failure(deny);
        }

        // C6: refuse the page size BEFORE any SQL — the door answers 422, not a
        // silently clamped page that would make `total` look like a lie.
        if (limit is < 1 or > FeedbackReadLimits.Max)
        {
            Audit(acting, "feedback.list", null, "invalid_limit");
            return Result<FeedbackFleetPage>.Failure(AdminErrors.InvalidFeedbackLimit);
        }

        await using var conn = await _db.OpenAdminConnectionAsync(ct);

        long total;
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM feedback.messages", conn))
            total = (long)(await count.ExecuteScalarAsync(ct))!;

        var rows = new List<FeedbackFleetRow>();

        await using (var cmd = new NpgsqlCommand("""
            SELECT f.id, t.name, f.author_email, f.author_display_name,
                   f.page, f.locale, f.message, f.contact_email, f.created_at
              FROM feedback.messages f
              JOIN identity.tenants t ON t.id = f.tenant_id
             ORDER BY f.created_at DESC, f.id
             LIMIT @limit
            """, conn))
        {
            cmd.Parameters.AddWithValue("limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new FeedbackFleetRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetDateTime(8)));
            }
        }

        Audit(acting, "feedback.list", null, $"listed={rows.Count}");

        return Result<FeedbackFleetPage>.Success(new FeedbackFleetPage(rows, total));
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

    // C8: the default arm THROWS. It used to fall back to TenantList, which made
    // a forgotten case pass silently (platform_admin holds both). Only the two
    // write actions reach this map; a third name is a programming error, loudly.
    private static string OperationOf(string action) => action switch
    {
        "tenant.disable" => Operations.TenantDisable,
        "tenant.enable" => Operations.TenantEnable,
        "feedback.list" => Operations.FeedbackRead,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "unmapped admin action"),
    };
}