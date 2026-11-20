using System.Globalization;
using System.Text.Json;
using Npgsql;
using Nabd.Alerting.Contracts;
using Nabd.Identity.Contracts;
using Nabd.Rules.Contracts;
using Nabd.SharedKernel;
using Nabd.TelemetryStore.Contracts;

namespace Nabd.Alerting;

/// <summary>What the raise path did — the lifecycle publishes only for the first two.</summary>
public enum AlertRaiseOutcome { Opened, Reraised, Deduplicated, Suppressed, Muted }

public sealed record AlertRaiseResult(AlertRaiseOutcome Outcome, AlertSnapshot Alert);

/// <summary>
/// The Alerting room's one DB owner (P4 spec 05): the door contract (list/get/ack/
/// resolve) AND the lifecycle machine (RaiseAsync/RestoreAsync) that spec 05's
/// consumer calls. Two layers, one room: the matrix asserts before any data access,
/// the RLS policy is the DB's own refusal (07 §5). One alert row per
/// (tenant, asset, rule) for its whole life — the story thread (plan/06 §5.1).
/// </summary>
public sealed class AlertService(AlertingDb db) : IAlerting
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One column list, one shape — every read goes through ReadRow.</summary>
    private const string Columns = """
        id, tenant_id, asset_id, rule_id, type, severity, status,
        opened_at, resolved_at, acknowledged_at, acknowledged_by,
        title ->> 'en' AS title_en, title ->> 'ar' AS title_ar, detail
        """;

    // ===== the doors (IAlerting) ============================================

    public async Task<Result<IReadOnlyList<AlertSnapshot>>> ListAsync(
        CallerContext acting, AlertFilter filter, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AlertRead, acting.TenantId);
        if (forbidden is not null) return Result<IReadOnlyList<AlertSnapshot>>.Failure(forbidden);

        // 422-not-500: the door validates its own inputs (a DB CHECK must never surface)
        if (filter.Status is not null && !AlertStatuses.All.Contains(filter.Status))
            return Result<IReadOnlyList<AlertSnapshot>>.Failure(AlertErrors.InvalidFilter);
        if (filter.Severity is not null && !AlertSeverities.All.Contains(filter.Severity))
            return Result<IReadOnlyList<AlertSnapshot>>.Failure(AlertErrors.InvalidFilter);
        if (filter.Limit is < 1 or > 500)
            return Result<IReadOnlyList<AlertSnapshot>>.Failure(AlertErrors.InvalidFilter);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var rows = new List<AlertSnapshot>();
            await using (var cmd = new NpgsqlCommand($"""
                SELECT {Columns} FROM alerting.alerts
                WHERE (@status::text IS NULL OR status = @status)
                  AND (@asset::uuid IS NULL OR asset_id = @asset)
                  AND (@severity::text IS NULL OR severity = @severity)
                ORDER BY opened_at DESC
                LIMIT @limit
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("status", (object?)filter.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("asset", (object?)filter.AssetId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("severity", (object?)filter.Severity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("limit", filter.Limit);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) rows.Add(ReadRow(reader));
            }
            await tx.CommitAsync(ct);   // reader already closed with its command block (lesson #15)
            return Result<IReadOnlyList<AlertSnapshot>>.Success(rows);
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

    public async Task<Result<AlertSnapshot>> GetAsync(
        CallerContext acting, Guid alertId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AlertRead, acting.TenantId);
        if (forbidden is not null) return Result<AlertSnapshot>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            AlertSnapshot? row = null;
            await using (var cmd = new NpgsqlCommand(
                $"SELECT {Columns} FROM alerting.alerts WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", alertId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct)) row = ReadRow(reader);
            }
            await tx.CommitAsync(ct);

            return row is null
                ? Result<AlertSnapshot>.Failure(AlertErrors.AlertNotFound)   // 404-hide: no existence hints
                : Result<AlertSnapshot>.Success(row);
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

    public async Task<Result<AlertSnapshot>> AcknowledgeAsync(
        CallerContext acting, Guid alertId, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AlertAck, acting.TenantId);
        if (forbidden is not null) return Result<AlertSnapshot>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var row = await ReadForUpdateAsync(conn, tx, alertId, ct);
            if (row is null) return Result<AlertSnapshot>.Failure(AlertErrors.AlertNotFound);
            if (row.Status == AlertStatuses.Resolved)
                return Result<AlertSnapshot>.Failure(AlertErrors.InvalidStatus);

            if (row.Status == AlertStatuses.Acknowledged)
            {
                await tx.CommitAsync(ct);
                return Result<AlertSnapshot>.Success(row);   // idempotent: no new audit row
            }

            var at = DateTimeOffset.UtcNow;
            await using (var up = new NpgsqlCommand("""
                UPDATE alerting.alerts
                   SET status = 'acknowledged', acknowledged_at = @at, acknowledged_by = @by
                 WHERE id = @id
                """, conn, tx))
            {
                up.Parameters.AddWithValue("at", at.UtcDateTime);
                up.Parameters.AddWithValue("by", acting.UserId);
                up.Parameters.AddWithValue("id", alertId);
                await up.ExecuteNonQueryAsync(ct);
            }
            await InsertEventAsync(conn, tx, row.TenantId, alertId,
                "acknowledged", acting.UserId, null, null, at, ct);
            await tx.CommitAsync(ct);

            return Result<AlertSnapshot>.Success(row with
            {
                Status = AlertStatuses.Acknowledged,
                AcknowledgedAt = at,
                AcknowledgedBy = acting.UserId,
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

    public async Task<Result<AlertSnapshot>> ResolveAsync(
        CallerContext acting, Guid alertId, string? reason, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AlertResolve, acting.TenantId);
        if (forbidden is not null) return Result<AlertSnapshot>.Failure(forbidden);

        var why = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim();
        if (why.Length > 200) return Result<AlertSnapshot>.Failure(AlertErrors.InvalidReason);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var row = await ReadForUpdateAsync(conn, tx, alertId, ct);
            if (row is null) return Result<AlertSnapshot>.Failure(AlertErrors.AlertNotFound);
            if (row.Status == AlertStatuses.Resolved)
                return Result<AlertSnapshot>.Failure(AlertErrors.InvalidStatus);

            var at = DateTimeOffset.UtcNow;
            await using (var up = new NpgsqlCommand("""
                UPDATE alerting.alerts SET status = 'resolved', resolved_at = @at WHERE id = @id
                """, conn, tx))
            {
                up.Parameters.AddWithValue("at", at.UtcDateTime);
                up.Parameters.AddWithValue("id", alertId);
                await up.ExecuteNonQueryAsync(ct);
            }
            await InsertEventAsync(conn, tx, row.TenantId, alertId,
                "resolved", acting.UserId, why, null, at, ct);
            await tx.CommitAsync(ct);

            return Result<AlertSnapshot>.Success(row with
            {
                Status = AlertStatuses.Resolved,
                ResolvedAt = at,
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

    // ===== the lifecycle machine (called by AlertLifecycle, never by a door) ==

    /// <summary>
    /// plan/06 §5.2 verbatim. Returns WHAT happened so the consumer knows whether
    /// to notify; the row is committed before this method returns (no receipt
    /// before the durable outcome — the publish happens in the consumer).
    /// </summary>
    public async Task<Result<AlertRaiseResult>> RaiseAsync(
        RuleSnapshot rule, CommittedReading reading, CancellationToken ct = default)
    {
        var (conn, tx) = await db.OpenTenantScopeAsync(rule.TenantId, ct);
        try
        {
            // FR-33: the alert belongs to the ASSET. Resolution is tenant-scoped, so a
            // foreign/deleted device simply has no asset (fail-shut, nothing invented).
            var asset = await ResolveAssetAsync(conn, tx, rule.DeviceId, ct);
            if (asset is null) return Result<AlertRaiseResult>.Failure(AlertErrors.AssetNotFound);

            var detail = new AlertDetail(
                reading.Value, reading.ChannelKey, rule.RuleType,
                rule.ThresholdLow, rule.ThresholdHigh, rule.DeviceId);
            var detailJson = JsonSerializer.Serialize(detail, Json);

            var (titleEn, titleAr) = BuildTitle(rule, reading, asset.Value.Name);
            var titleJson = JsonSerializer.Serialize(new { En = titleEn, Ar = titleAr }, Json);

            var at = reading.TimeUtc;   // event time: the moment the condition was observed
            var existing = await ReadForKeyForUpdateAsync(conn, tx, rule, asset.Value.AssetId, ct);

            // ---- none: a new alert opens and notifies ----------------------------
            if (existing is null)
            {
                var id = Guid.NewGuid();
                await using (var ins = new NpgsqlCommand("""
                    INSERT INTO alerting.alerts
                        (id, tenant_id, asset_id, rule_id, type, severity, status,
                         opened_at, title, detail)
                    VALUES (@id, @t, @a, @r, 'threshold', @sev, 'open', @at, @title::jsonb, @detail::jsonb)
                    """, conn, tx))
                {
                    ins.Parameters.AddWithValue("id", id);
                    ins.Parameters.AddWithValue("t", rule.TenantId);
                    ins.Parameters.AddWithValue("a", asset.Value.AssetId);
                    ins.Parameters.AddWithValue("r", rule.RuleId);
                    ins.Parameters.AddWithValue("sev", rule.Severity);
                    ins.Parameters.AddWithValue("at", at.UtcDateTime);
                    ins.Parameters.AddWithValue("title", titleJson);
                    ins.Parameters.AddWithValue("detail", detailJson);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await InsertEventAsync(conn, tx, rule.TenantId, id,
                    "opened", null, null, detailJson, at, ct);

                var snapshot = new AlertSnapshot(
                    id, rule.TenantId, asset.Value.AssetId, rule.RuleId,
                    AlertTypes.Threshold, rule.Severity, AlertStatuses.Open,
                    at, null, null, null, titleEn, titleAr, detail);

                // plan/06 §5.4 (D6d): the row STILL opens — the story stays visible in
                // history — but a crossing inside a mute window notifies nobody. The
                // decision uses the rule's windows AT FIRE TIME, on event time.
                if (MuteDecision.ShouldMute(AlertTypes.Threshold, rule.MuteWindows, at))
                {
                    await InsertEventAsync(conn, tx, rule.TenantId, id,
                        "suppressed", null, "mute_window", detailJson, at, ct);
                    await tx.CommitAsync(ct);
                    return Result<AlertRaiseResult>.Success(
                        new AlertRaiseResult(AlertRaiseOutcome.Muted, snapshot));
                }

                await tx.CommitAsync(ct);
                return Result<AlertRaiseResult>.Success(
                    new AlertRaiseResult(AlertRaiseOutcome.Opened, snapshot));
            }

            // ---- open/acknowledged: update the detail, NEVER re-notify -----------
            if (existing.Status != AlertStatuses.Resolved)
            {
                await using (var up = new NpgsqlCommand("""
                    UPDATE alerting.alerts
                       SET title = @title::jsonb, detail = @detail::jsonb
                     WHERE id = @id
                    """, conn, tx))
                {
                    up.Parameters.AddWithValue("title", titleJson);
                    up.Parameters.AddWithValue("detail", detailJson);
                    up.Parameters.AddWithValue("id", existing.AlertId);
                    await up.ExecuteNonQueryAsync(ct);
                }
                await InsertEventAsync(conn, tx, rule.TenantId, existing.AlertId,
                    "deduplicated", null, "open", detailJson, at, ct);
                await tx.CommitAsync(ct);

                return Result<AlertRaiseResult>.Success(new AlertRaiseResult(
                    AlertRaiseOutcome.Deduplicated,
                    existing with { TitleEn = titleEn, TitleAr = titleAr, Detail = detail }));
            }

            // ---- resolved: inside cooldown = suppress-but-log; spent = re-raise --
            var elapsed = reading.TimeUtc - existing.ResolvedAt!.Value;
            if (elapsed < CooldownOf(rule))
            {
                await InsertEventAsync(conn, tx, rule.TenantId, existing.AlertId,
                    "suppressed", null, "cooldown", detailJson, at, ct);
                await tx.CommitAsync(ct);

                return Result<AlertRaiseResult>.Success(
                    new AlertRaiseResult(AlertRaiseOutcome.Suppressed, existing));
            }

            await using (var up = new NpgsqlCommand("""
                UPDATE alerting.alerts
                   SET status = 'open', opened_at = @at, resolved_at = NULL,
                       acknowledged_at = NULL, acknowledged_by = NULL,
                       title = @title::jsonb, detail = @detail::jsonb
                 WHERE id = @id
                """, conn, tx))
            {
                up.Parameters.AddWithValue("at", at.UtcDateTime);
                up.Parameters.AddWithValue("title", titleJson);
                up.Parameters.AddWithValue("detail", detailJson);
                up.Parameters.AddWithValue("id", existing.AlertId);
                await up.ExecuteNonQueryAsync(ct);
            }
            await InsertEventAsync(conn, tx, rule.TenantId, existing.AlertId,
                "reopened", null, null, detailJson, at, ct);

            var reopened = existing with
            {
                    Status = AlertStatuses.Open,
                    OpenedAt = at,
                    ResolvedAt = null,
                    AcknowledgedAt = null,
                    AcknowledgedBy = null,
                    TitleEn = titleEn,
                    TitleAr = titleAr,
                    Detail = detail,
                };

            // spec 06 (D6e): a window silences the fan-out — never the state machine.
            if (MuteDecision.ShouldMute(AlertTypes.Threshold, rule.MuteWindows, at))
            {
                await InsertEventAsync(conn, tx, rule.TenantId, existing.AlertId,
                    "suppressed", null, "mute_window", detailJson, at, ct);
                await tx.CommitAsync(ct);
                return Result<AlertRaiseResult>.Success(
                    new AlertRaiseResult(AlertRaiseOutcome.Muted, reopened));
            }

            await tx.CommitAsync(ct);
            return Result<AlertRaiseResult>.Success(
                new AlertRaiseResult(AlertRaiseOutcome.Reraised, reopened));
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

    /// <summary>
    /// plan/06 §5.3: a threshold restore resolves the OPEN/ACK alert automatically.
    /// No row → null (a restore for something that never opened is a no-op, never
    /// an invented alert). The caller publishes AlertResolved AFTER this commit.
    /// </summary>
    public async Task<AlertSnapshot?> RestoreAsync(
        RuleSnapshot rule, CommittedReading reading, CancellationToken ct = default)
    {
        var (conn, tx) = await db.OpenTenantScopeAsync(rule.TenantId, ct);
        try
        {
            AlertSnapshot? open = null;
            await using (var cmd = new NpgsqlCommand($"""
                SELECT {Columns} FROM alerting.alerts
                 WHERE rule_id = @r AND status <> 'resolved'
                 FOR UPDATE
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("r", rule.RuleId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct)) open = ReadRow(reader);
            }

            if (open is null)
            {
                await tx.CommitAsync(ct);
                return null;
            }

            var at = reading.TimeUtc;
            await using (var up = new NpgsqlCommand("""
                UPDATE alerting.alerts SET status = 'resolved', resolved_at = @at WHERE id = @id
                """, conn, tx))
            {
                up.Parameters.AddWithValue("at", at.UtcDateTime);
                up.Parameters.AddWithValue("id", open.AlertId);
                await up.ExecuteNonQueryAsync(ct);
            }

            var detailJson = JsonSerializer.Serialize(new AlertDetail(
                reading.Value, reading.ChannelKey, rule.RuleType,
                rule.ThresholdLow, rule.ThresholdHigh, rule.DeviceId), Json);
            await InsertEventAsync(conn, tx, open.TenantId, open.AlertId,
                "resolved", null, "condition_restored", detailJson, at, ct);
            await tx.CommitAsync(ct);

            return open with { Status = AlertStatuses.Resolved, ResolvedAt = at };
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

    // ===== private plumbing ==================================================

    private static async Task<AlertSnapshot?> ReadForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid alertId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM alerting.alerts WHERE id = @id FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("id", alertId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
    }

    private static async Task<AlertSnapshot?> ReadForKeyForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        RuleSnapshot rule, Guid assetId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Columns} FROM alerting.alerts
             WHERE tenant_id = @t AND asset_id = @a AND rule_id = @r
             FOR UPDATE
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", rule.TenantId);
        cmd.Parameters.AddWithValue("a", assetId);
        cmd.Parameters.AddWithValue("r", rule.RuleId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
    }

    /// <summary>The asset behind the device (FR-33). Tenant-scoped read of the assets
    /// schema — the same shape DeviceRules uses to validate its device_id, and the
    /// 0016/0017 lookup doors' precedent: the CONTRACT stays narrow, the wall stays.</summary>
    private static async Task<(Guid AssetId, string Name)?> ResolveAssetAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid deviceId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT d.asset_id, a.name
              FROM assets.devices d
              JOIN assets.assets a ON a.id = d.asset_id
             WHERE d.id = @d
            """, conn, tx);
        cmd.Parameters.AddWithValue("d", deviceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetGuid(0), reader.GetString(1))
            : null;
    }

    private static async Task InsertEventAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tenantId, Guid alertId, string evt, Guid? actor, string? reason,
        string? detailJson, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO alerting.alert_events
                (tenant_id, alert_id, event, actor_user_id, reason, detail, at)
            VALUES (@t, @a, @e, @actor, @reason, @detail::jsonb, @at)
            """, conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", alertId);
        cmd.Parameters.AddWithValue("e", evt);
        cmd.Parameters.AddWithValue("actor", (object?)actor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("detail", (object?)detailJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", at.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static AlertSnapshot ReadRow(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3),
        r.GetString(4), r.GetString(5), r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
        r.IsDBNull(10) ? null : r.GetGuid(10),
        r.GetString(11), r.GetString(12),
        JsonSerializer.Deserialize<AlertDetail>(r.GetString(13), Json)
            ?? throw new InvalidOperationException("alert detail JSONB is empty — the row is corrupt"));

    /// <summary>plan/06 §5.2: cooldown_minutes, and critical cools at half (§6/§8).</summary>
    private static TimeSpan CooldownOf(RuleSnapshot rule) =>
        TimeSpan.FromMinutes(rule.Severity == AlertSeverities.Critical
            ? rule.CooldownMinutes / 2.0
            : rule.CooldownMinutes);

    /// <summary>Titles are per-locale strings (plan/04 §8, 00 D3). Short resource strings —
    /// the final product voice is the owner's (spec 08 reviews the wording).</summary>
    private static (string En, string Ar) BuildTitle(
        RuleSnapshot rule, CommittedReading reading, string assetName)
    {
        var channel = rule.ChannelKey ?? reading.ChannelKey;
        var value = reading.Value.ToString("0.###", CultureInfo.InvariantCulture);
        var low = Number(rule.ThresholdLow);
        var high = Number(rule.ThresholdHigh);

        return rule.RuleType switch
        {
            "above" => ($"{channel} {value} above {high} — {assetName}",
                        $"{channel} {value} أعلى من {high} — {assetName}"),
            "below" => ($"{channel} {value} below {low} — {assetName}",
                        $"{channel} {value} أدنى من {low} — {assetName}"),
            "outside_range" => ($"{channel} {value} outside {low}–{high} — {assetName}",
                                $"{channel} {value} خارج النطاق {low}–{high} — {assetName}"),
            _ => ($"{channel} {value} ({rule.RuleType}) — {assetName}",
                  $"{channel} {value} ({rule.RuleType}) — {assetName}"),
        };
    }

    private static string Number(double? v) =>
        v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";
}
