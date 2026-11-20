using Npgsql;
using Nabd.Identity.Contracts;
using Nabd.Rules.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Rules;

/// <summary>
/// The rules CRUD service (P4 spec 03, FR-30/D1) — behind the 0021 wall.
/// Two-layer enforcement: the matrix asserts HERE before any data access;
/// the RLS policy is layer 3, the DB's own refusal. `offline` is refused by
/// law (the device-offline flow owns those rows, spec 06).
/// </summary>
public sealed class DeviceRules(RulesDb db) : IDeviceRules
{
    public async Task<Result<RuleSnapshot>> CreateRuleAsync(
        CallerContext acting, RuleCreateRequest request, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.RuleCreate, acting.TenantId);
        if (forbidden is not null) return Result<RuleSnapshot>.Failure(forbidden);

        if (request.RuleType == "offline")
            return Result<RuleSnapshot>.Failure(RuleErrors.OfflineRefused);
        if (!ThresholdsValid(request.RuleType, request.ThresholdLow, request.ThresholdHigh))
            return Result<RuleSnapshot>.Failure(RuleErrors.InvalidRule);
        // the §8 defaults are the floor, not a free-for-all: the DB CHECKs refuse
        // these too, but a 23514 must never surface as a 500 (door law: 422).
        if (request.Severity is not ("info" or "warn" or "critical")
            || request.DurationSeconds < 1 || request.CooldownMinutes < 0)
            return Result<RuleSnapshot>.Failure(RuleErrors.InvalidRule);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            // the device must exist IN THIS TENANT — RLS hides any other (fact 2)
            var deviceId = await FindDeviceAsync(conn, tx, request.DeviceId, ct);
            if (deviceId is null)
                return Result<RuleSnapshot>.Failure(RuleErrors.DeviceNotFound);

            // the dedup law, explicit (the UNIQUE constraint is the backstop)
            await using (var dup = new NpgsqlCommand("""
                SELECT 1 FROM rules.rules
                WHERE device_id = @d AND rule_type = @t
                  AND channel_key IS NOT DISTINCT FROM @ch;
                """, conn, tx))
            {
                dup.Parameters.AddWithValue("d", request.DeviceId);
                dup.Parameters.AddWithValue("t", request.RuleType);
                dup.Parameters.AddWithValue("ch", (object?)request.ChannelKey ?? DBNull.Value);
                if (await dup.ExecuteScalarAsync(ct) is not null)
                    return Result<RuleSnapshot>.Failure(RuleErrors.DuplicateRule);
            }

            var ruleId = Guid.NewGuid();
            await using (var ins = new NpgsqlCommand("""
                INSERT INTO rules.rules
                    (id, tenant_id, device_id, channel_key, rule_type,
                     threshold_low, threshold_high, duration_seconds, cooldown_minutes,
                     severity, enabled)
                VALUES (@id, @tenant, @device, @ch, @type,
                        @lo, @hi, @dur, @cool, @sev, true)
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", ruleId);
                ins.Parameters.AddWithValue("tenant", acting.TenantId);
                ins.Parameters.AddWithValue("device", request.DeviceId);
                ins.Parameters.AddWithValue("ch", (object?)request.ChannelKey ?? DBNull.Value);
                ins.Parameters.AddWithValue("type", request.RuleType);
                ins.Parameters.AddWithValue("lo", (object?)request.ThresholdLow ?? DBNull.Value);
                ins.Parameters.AddWithValue("hi", (object?)request.ThresholdHigh ?? DBNull.Value);
                ins.Parameters.AddWithValue("dur", request.DurationSeconds);
                ins.Parameters.AddWithValue("cool", request.CooldownMinutes);
                ins.Parameters.AddWithValue("sev", request.Severity);
                await ins.ExecuteNonQueryAsync(ct);
            }

            // READ INSIDE THE TX: app.current_tenant is SET LOCAL, so it dies at
            // COMMIT — a post-commit read would be fail-shut invisible (zero rows).
            var snapshot = await ReadOneAsync(conn, tx, ruleId, ct);
            await tx.CommitAsync(ct);
            return Result<RuleSnapshot>.Success(snapshot);
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

    public async Task<Result<IReadOnlyList<RuleSnapshot>>> ListByDeviceAsync(
        CallerContext acting, Guid deviceId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.RuleRead, acting.TenantId);
        if (forbidden is not null) return Result<IReadOnlyList<RuleSnapshot>>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            if (await FindDeviceAsync(conn, tx, deviceId, ct) is null)
                return Result<IReadOnlyList<RuleSnapshot>>.Failure(RuleErrors.DeviceNotFound);

            var list = new List<RuleSnapshot>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT id, tenant_id, device_id, channel_key, rule_type,
                       threshold_low, threshold_high, duration_seconds,
                       cooldown_minutes, severity, enabled
                FROM rules.rules WHERE device_id = @d
                ORDER BY channel_key NULLS LAST, rule_type
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("d", deviceId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) list.Add(ReadRow(reader));
            }

            // D6c: one query, then attach — a device has a handful of rules at most.
            var windows = new Dictionary<Guid, List<MuteWindow>>();
            await using (var w = new NpgsqlCommand("""
                SELECT w.rule_id, w.day_of_week, w.start_minute, w.end_minute
                FROM rules.mute_windows w
                JOIN rules.rules r ON r.id = w.rule_id
                WHERE r.device_id = @d
                ORDER BY w.day_of_week, w.start_minute
                """, conn, tx))
            {
                w.Parameters.AddWithValue("d", deviceId);
                await using var reader = await w.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetGuid(0);
                    if (!windows.TryGetValue(id, out var set)) windows[id] = set = [];
                    set.Add(new MuteWindow(reader.GetInt16(1), reader.GetInt16(2), reader.GetInt16(3)));
                }
            }

            list = list
                .Select(r => r with
                {
                    MuteWindows = windows.TryGetValue(r.RuleId, out var set) ? set : [],
                })
                .ToList();

            await tx.CommitAsync(ct);
            return Result<IReadOnlyList<RuleSnapshot>>.Success(list);
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

    public async Task<Result<RuleSnapshot>> UpdateRuleAsync(
        CallerContext acting, Guid deviceId, Guid ruleId, RuleUpdateRequest request,
        CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.RuleUpdate, acting.TenantId);
        if (forbidden is not null) return Result<RuleSnapshot>.Failure(forbidden);

        if (request.Severity is { } sev && sev is not ("info" or "warn" or "critical"))
            return Result<RuleSnapshot>.Failure(RuleErrors.InvalidRule);
        if (request.DurationSeconds is < 1 || request.CooldownMinutes is < 0)
            return Result<RuleSnapshot>.Failure(RuleErrors.InvalidRule);
        if (request.MuteWindows is { } windows && ValidateWindows(windows) is { } windowError)
            return Result<RuleSnapshot>.Failure(windowError);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            // COALESCE: only the PRESENT legs move; identity legs never do (PATCH)
            await using (var upd = new NpgsqlCommand("""
                UPDATE rules.rules SET
                    threshold_low    = COALESCE(@lo, threshold_low),
                    threshold_high   = COALESCE(@hi, threshold_high),
                    duration_seconds = COALESCE(@dur, duration_seconds),
                    cooldown_minutes = COALESCE(@cool, cooldown_minutes),
                    severity         = COALESCE(@sev, severity),
                    enabled          = COALESCE(@en, enabled)
                WHERE id = @r AND device_id = @d
                """, conn, tx))
            {
                upd.Parameters.AddWithValue("r", ruleId);
                upd.Parameters.AddWithValue("d", deviceId);
                upd.Parameters.AddWithValue("lo", (object?)request.ThresholdLow ?? DBNull.Value);
                upd.Parameters.AddWithValue("hi", (object?)request.ThresholdHigh ?? DBNull.Value);
                upd.Parameters.AddWithValue("dur", (object?)request.DurationSeconds ?? DBNull.Value);
                upd.Parameters.AddWithValue("cool", (object?)request.CooldownMinutes ?? DBNull.Value);
                upd.Parameters.AddWithValue("sev", (object?)request.Severity ?? DBNull.Value);
                upd.Parameters.AddWithValue("en", (object?)request.Enabled ?? DBNull.Value);
                if (await upd.ExecuteNonQueryAsync(ct) == 0)
                    return Result<RuleSnapshot>.Failure(RuleErrors.RuleNotFound);   // RLS hid it or absent
            }

            // D6c: full replacement — null leaves the set alone, [] clears it.
            if (request.MuteWindows is { } newWindows)
            {
                await using (var del = new NpgsqlCommand(
                    "DELETE FROM rules.mute_windows WHERE rule_id = @r", conn, tx))
                {
                    del.Parameters.AddWithValue("r", ruleId);
                    await del.ExecuteNonQueryAsync(ct);
                }
                foreach (var w in newWindows)
                {
                    await using var ins = new NpgsqlCommand("""
                        INSERT INTO rules.mute_windows
                            (tenant_id, rule_id, day_of_week, start_minute, end_minute)
                        VALUES (@t, @r, @d, @s, @e)
                        """, conn, tx);
                    ins.Parameters.AddWithValue("t", acting.TenantId);
                    ins.Parameters.AddWithValue("r", ruleId);
                    ins.Parameters.AddWithValue("d", (short)w.DayOfWeek);
                    ins.Parameters.AddWithValue("s", (short)w.StartMinute);
                    ins.Parameters.AddWithValue("e", (short)w.EndMinute);
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            // READ INSIDE THE TX (app.current_tenant is SET LOCAL — it dies at COMMIT)
            var snapshot = await ReadOneAsync(conn, tx, ruleId, ct);
            await tx.CommitAsync(ct);
            return Result<RuleSnapshot>.Success(snapshot);
        }
        catch (PostgresException pg) when (pg.SqlState is "23514" or "23505")
        {
            // the DB's own laws fired (threshold shape / severity / unique):
            // the door answers 422, never a 500 (the CHECKs are the backstop).
            await tx.RollbackAsync(ct);
            return Result<RuleSnapshot>.Failure(
                pg.SqlState == "23505" ? RuleErrors.DuplicateRule : RuleErrors.InvalidRule);
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

    public async Task<Result<bool>> DeleteRuleAsync(
        CallerContext acting, Guid deviceId, Guid ruleId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.RuleDelete, acting.TenantId);
        if (forbidden is not null) return Result<bool>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            await using var del = new NpgsqlCommand(
                "DELETE FROM rules.rules WHERE id = @r AND device_id = @d", conn, tx);
            del.Parameters.AddWithValue("r", ruleId);
            del.Parameters.AddWithValue("d", deviceId);
            var rows = await del.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return rows == 0
                ? Result<bool>.Failure(RuleErrors.RuleNotFound)   // RLS hid it or absent
                : Result<bool>.Success(true);
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

    // ---- the private anchors -------------------------------------------------

    private static bool ThresholdsValid(string type, double? lo, double? hi) => type switch
    {
        "above" => hi is not null && lo is null,
        "below" => lo is not null && hi is null,
        "outside_range" => lo is not null && hi is not null,
        _ => false,   // 'offline' never reaches here (refused first); unknown = invalid
    };

    private static async Task<Guid?> FindDeviceAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid deviceId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM assets.devices WHERE id = @d;", conn, tx);
        cmd.Parameters.AddWithValue("d", deviceId);
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }

    /// <summary>D6c: day 0–6 · 0 ≤ start &lt; end ≤ 1440 · ≤ 20 windows · no same-day overlap.
    /// Returns the door error, or null when the set is lawful.</summary>
    private static string? ValidateWindows(IReadOnlyList<MuteWindow> windows)
    {
        if (windows.Count > 20) return RuleErrors.InvalidMuteWindow;

        foreach (var w in windows)
            if (w.DayOfWeek is < 0 or > 6
                || w.StartMinute < 0 || w.EndMinute > 1440
                || w.StartMinute >= w.EndMinute)
                return RuleErrors.InvalidMuteWindow;

        foreach (var day in windows.GroupBy(w => w.DayOfWeek))
        {
            var ordered = day.OrderBy(w => w.StartMinute).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].StartMinute < ordered[i - 1].EndMinute)
                    return RuleErrors.MuteWindowOverlap;
        }

        return null;
    }

    private static async Task<RuleSnapshot> ReadOneAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid ruleId, CancellationToken ct)
    {
        RuleSnapshot row;
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, tenant_id, device_id, channel_key, rule_type,
                   threshold_low, threshold_high, duration_seconds,
                   cooldown_minutes, severity, enabled
            FROM rules.rules WHERE id = @r;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("r", ruleId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            row = ReadRow(reader);
        }

        var windows = new List<MuteWindow>();
        await using (var w = new NpgsqlCommand("""
            SELECT day_of_week, start_minute, end_minute
            FROM rules.mute_windows WHERE rule_id = @r
            ORDER BY day_of_week, start_minute
            """, conn, tx))
        {
            w.Parameters.AddWithValue("r", ruleId);
            await using var reader = await w.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                windows.Add(new MuteWindow(reader.GetInt16(0), reader.GetInt16(1), reader.GetInt16(2)));
        }

        return row with { MuteWindows = windows };
    }

    private static RuleSnapshot ReadRow(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetDouble(5),
        reader.IsDBNull(6) ? null : reader.GetDouble(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetString(9),
        reader.GetBoolean(10));
}
