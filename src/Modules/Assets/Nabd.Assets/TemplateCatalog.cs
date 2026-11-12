using Npgsql;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Assets;

/// <summary>
/// The template catalog's read side (FR-13/FR-15) — global data, one SELECT join,
/// assembled in memory. Layer 2 asserts the matrix BEFORE any data access, same
/// line as every other room service; layer 1 (attributes) sits at the door.
/// </summary>
public sealed class TemplateCatalog(AssetsDb db) : ITemplateCatalog
{
    public async Task<Result<IReadOnlyList<TemplateSnapshot>>> ListTemplatesAsync(
        CallerContext acting, CancellationToken ct = default)
    {
        var forbidden = PermissionMatrix.Assert(acting, Operations.TemplateRead, acting.TenantId);
        if (forbidden is not null) return Result<IReadOnlyList<TemplateSnapshot>>.Failure(forbidden);

        var conn = await db.OpenPreAuthConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT t.id, t.key, t.name_ar, t.name_en,
                       c.key, c.name_ar, c.name_en, c.unit, c.data_type,
                       c.lower_limit, c.upper_limit
                FROM assets.asset_type_templates t
                LEFT JOIN assets.channel_definitions c ON c.template_id = t.id
                WHERE t.alive
                ORDER BY t.key, c.key
                """, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var byId = new Dictionary<Guid, TemplateSnapshot>();
            while (await reader.ReadAsync(ct))
            {
                var templateId = reader.GetGuid(0);
                if (!byId.TryGetValue(templateId, out var template))
                {
                    template = new TemplateSnapshot(templateId,
                        reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        Channels: []);
                    byId[templateId] = template;
                }

                // LEFT JOIN: a template may legitimately have zero channels (a new
                // vertical starts as one template row) — a NULL channel row is skipped.
                if (!reader.IsDBNull(4))
                {
                    var channel = new ChannelDefinition(templateId,
                        reader.GetString(4), reader.GetString(5), reader.GetString(6),
                        reader.GetString(7), reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetDouble(9),
                        reader.IsDBNull(10) ? null : reader.GetDouble(10));
                    byId[templateId] = template with
                    {
                        Channels = [.. template.Channels, channel],
                    };
                }
            }

            return Result<IReadOnlyList<TemplateSnapshot>>.Success([.. byId.Values]);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
}