using System.Globalization;
using System.Text.Json;
using Npgsql;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Assets;

/// <summary>
/// P5 spec 04: the history read model over `assets.asset_history` (0029).
/// The SQL function is the one place source/width/channel validation lives;
/// this class carries the caller's tenant and the two failure mappings.
/// </summary>
public sealed class AssetHistoryReader(AssetsDb db) : IAssetHistoryReader
{
    /// <summary>Door lane: permission asserted before any data access (07 §5).</summary>
    public async Task<Result<AssetHistoryResult>> ReadAsync(
        CallerContext acting, Guid assetId, string? channel,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.AssetRead, acting.TenantId);
        if (forbidden is not null)
            return Result<AssetHistoryResult>.Failure(forbidden);

        // The function owns the channel truth (REV 4 #2). The one rule that cannot
        // reach it is "there is no channel to ask for at all".
        if (string.IsNullOrWhiteSpace(channel))
            return Result<AssetHistoryResult>.Failure(HistoryErrors.UnknownChannel);

        var (conn, tx) = await db.OpenTenantScopeAsync(acting.TenantId, ct);
        try
        {
            Result<AssetHistoryResult>? answer = null;

            await using (var cmd = new NpgsqlCommand("""
                SELECT state, source, bucket_seconds, points
                  FROM assets.asset_history(@tenant, @asset, @channel, @from, @to)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("tenant", acting.TenantId);
                cmd.Parameters.AddWithValue("asset", assetId);
                cmd.Parameters.AddWithValue("channel", channel.Trim());
                cmd.Parameters.AddWithValue("from", fromUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("to", toUtc.UtcDateTime);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    answer = Translate(reader);
            }

            await tx.CommitAsync(ct);
            return answer ?? Result<AssetHistoryResult>.Failure(AssetErrors.AssetNotFound);
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

    /// <summary>One row, four states: the function's vocabulary is the door's.</summary>
    private static Result<AssetHistoryResult> Translate(NpgsqlDataReader r) => r.GetString(0) switch
    {
        "ok" => Result<AssetHistoryResult>.Success(new AssetHistoryResult(
            r.GetString(1),
            r.GetInt32(2),
            ParsePoints(r.GetString(3)))),
        "invalid_range" => Result<AssetHistoryResult>.Failure(HistoryErrors.InvalidRange),
        "unknown_channel" => Result<AssetHistoryResult>.Failure(HistoryErrors.UnknownChannel),
        // A tenant-mismatched asset is exactly "not found" (the P1 404 law, #8).
        _ => Result<AssetHistoryResult>.Failure(AssetErrors.AssetNotFound),
    };

    /// <summary>
    /// `points` is the function's jsonb array. Timestamps arrive as an ISO instant
    /// (offset included); we normalize to UTC so the wire has one spelling.
    /// </summary>
    private static IReadOnlyList<HistoryPoint> ParsePoints(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var points = new List<HistoryPoint>();

        foreach (var p in doc.RootElement.EnumerateArray())
        {
            points.Add(new HistoryPoint(
                DateTimeOffset.Parse(
                        p.GetProperty("t").GetString()!,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)
                    .ToUniversalTime(),
                p.GetProperty("min").GetDouble(),
                p.GetProperty("max").GetDouble(),
                p.GetProperty("avg").GetDouble(),
                p.GetProperty("n").GetInt64()));
        }

        return points;
    }
}
