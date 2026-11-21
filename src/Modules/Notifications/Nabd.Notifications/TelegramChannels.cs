namespace Nabd.Notifications;

using System.Text.Json;
using Npgsql;
using Nabd.Alerting.Contracts;
using Nabd.Identity.Contracts;
using Nabd.Notifications.Contracts;
using Nabd.SharedKernel;

/// <summary>One verified chat and its owner — what a fan-out addresses.</summary>
public sealed record TelegramChat(Guid UserId, string ChatId);

/// <summary>
/// The Telegram channel's row owner (FR-38, plan/04 §9). Two surfaces, one class:
/// the user doors behind ITelegramChannels (link/state/severities/unlink), and the
/// internal reads the fan-out uses (recipients/verify). Everything user-scoped runs
/// under app.current_tenant; verification alone rides the audited SECURITY DEFINER
/// function because the bot has no tenant.
/// </summary>
public sealed class TelegramChannels(NotificationsDb db) : ITelegramChannels
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- the user doors ---------------------------------------------------------

    public async Task<Result<TelegramLinkTicket>> IssueLinkCodeAsync(
        CallerContext acting, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(
            acting, Operations.TelegramManage, acting.TenantId);
        if (forbidden is not null) return Result<TelegramLinkTicket>.Failure(forbidden);

        var code = TelegramLinkCodes.Mint();
        var hash = TelegramLinkCodes.Hash(code);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            DateTimeOffset expiresAt;
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO notifications.user_channels
                    (user_id, tenant_id, channel, link_code_hash, link_code_expires_at)
                VALUES (@u, @t, 'telegram', @h, now() + interval '10 minutes')
                ON CONFLICT (user_id, channel) DO UPDATE SET
                    link_code_hash = EXCLUDED.link_code_hash,
                    link_code_expires_at = EXCLUDED.link_code_expires_at,
                    updated_at = now()
                RETURNING link_code_expires_at;
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("u", acting.UserId);
                cmd.Parameters.AddWithValue("t", acting.TenantId);
                cmd.Parameters.AddWithValue("h", hash);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                expiresAt = reader.GetFieldValue<DateTimeOffset>(0);   // the DB's clock, one truth
            }
            await tx.CommitAsync(ct);

            return Result<TelegramLinkTicket>.Success(
                new TelegramLinkTicket(code, expiresAt, await UsernameAsync(ct)));
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

    public async Task<Result<TelegramChannelState>> GetAsync(
        CallerContext acting, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(
            acting, Operations.TelegramManage, acting.TenantId);
        if (forbidden is not null) return Result<TelegramChannelState>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            var state = await ReadStateAsync(conn, tx, acting.UserId, ct);
            await tx.CommitAsync(ct);
            return Result<TelegramChannelState>.Success(state);
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

    public async Task<Result<TelegramChannelState>> SetSeveritiesAsync(
        CallerContext acting, IReadOnlyList<string> severities, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(
            acting, Operations.TelegramManage, acting.TenantId);
        if (forbidden is not null) return Result<TelegramChannelState>.Failure(forbidden);

        var chosen = severities.Distinct(StringComparer.Ordinal).ToArray();
        // plan/06 §6: info is dashboard-only; an unknown string is a bug, never a silent drop.
        if (chosen.Any(s => s != AlertSeverities.Warn && s != AlertSeverities.Critical))
            return Result<TelegramChannelState>.Failure(NotificationErrors.InvalidSeverity);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO notifications.user_channels (user_id, tenant_id, channel, severities)
                VALUES (@u, @t, 'telegram', @s::jsonb)
                ON CONFLICT (user_id, channel) DO UPDATE SET
                    severities = EXCLUDED.severities,
                    updated_at = now();
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("u", acting.UserId);
                cmd.Parameters.AddWithValue("t", acting.TenantId);
                cmd.Parameters.AddWithValue("s", JsonSerializer.Serialize(chosen));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var state = await ReadStateAsync(conn, tx, acting.UserId, ct);
            await tx.CommitAsync(ct);
            return Result<TelegramChannelState>.Success(state);
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

    public async Task<Result<TelegramChannelState>> UnlinkAsync(
        CallerContext acting, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(
            acting, Operations.TelegramManage, acting.TenantId);
        if (forbidden is not null) return Result<TelegramChannelState>.Failure(forbidden);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            await using (var cmd = new NpgsqlCommand("""
                UPDATE notifications.user_channels
                   SET external_id = NULL,
                       verified_at = NULL,
                       link_code_hash = NULL,
                       link_code_expires_at = NULL,
                       updated_at = now()
                 WHERE user_id = @u AND channel = 'telegram';
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("u", acting.UserId);
                await cmd.ExecuteNonQueryAsync(ct);      // idempotent: no row = no-op
            }

            var state = await ReadStateAsync(conn, tx, acting.UserId, ct);
            await tx.CommitAsync(ct);
            return Result<TelegramChannelState>.Success(state);
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

    // ---- the fan-out reads ------------------------------------------------------

    /// <summary>Verified chats of one tenant whose owner picked this severity (D9e).</summary>
    public async Task<IReadOnlyList<TelegramChat>> RecipientsAsync(
        Guid tenantId, string severity, CancellationToken ct = default)
    {
        var list = new List<TelegramChat>();
        var (conn, tx) = await db.OpenTenantScopeAsync(tenantId, ct);
        try
        {
            await using (var cmd = new NpgsqlCommand("""
                SELECT user_id, external_id
                  FROM notifications.user_channels
                 WHERE tenant_id = @t
                   AND channel = 'telegram'
                   AND verified_at IS NOT NULL
                   AND severities ? @s
                 ORDER BY user_id;
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("s", severity);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    list.Add(new TelegramChat(reader.GetGuid(0), reader.GetString(1)));
            }
            await tx.CommitAsync(ct);
            return list;
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
    /// The bot's only write (D9c): hash → function. NULL = wrong, expired, or already used —
    /// the three are deliberately indistinguishable to the chat.
    /// </summary>
    public async Task<Guid?> VerifyAsync(
        string code, string chatId, CancellationToken ct = default)
    {
        var hash = TelegramLinkCodes.Hash(code);
        await using var conn = await db.OpenPlatformCommandAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT user_id FROM notifications.verify_telegram_link(@h, @c);", conn);
        cmd.Parameters.AddWithValue("h", hash);
        cmd.Parameters.AddWithValue("c", chatId);
        return await cmd.ExecuteScalarAsync(ct) is Guid userId ? userId : null;
    }

    // ---- helpers ----------------------------------------------------------------

    private static async Task<TelegramChannelState> ReadStateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT verified_at, severities
              FROM notifications.user_channels
             WHERE user_id = @u AND channel = 'telegram';
            """, conn, tx);
        cmd.Parameters.AddWithValue("u", userId);

        // reader inside its own block: commit must never happen with it open (lesson #15)
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return new TelegramChannelState(
                    false, null, [AlertSeverities.Warn, AlertSeverities.Critical]);

            var severities = JsonSerializer.Deserialize<string[]>(reader.GetString(1), Json) ?? [];
            return new TelegramChannelState(
                !reader.IsDBNull(0),
                reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
                severities);
        }
    }

    private async Task<string?> UsernameAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenPlatformReadAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT config->>'username'
              FROM notifications.channel_config
             WHERE channel = 'telegram' AND enabled;
            """, conn);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
