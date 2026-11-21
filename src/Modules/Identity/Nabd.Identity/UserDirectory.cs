namespace Nabd.Identity;

using Npgsql;
using Nabd.Identity.Contracts;

/// <summary>
/// The tenant's people, read through the Identity room's own door (P4 spec 08). Notifications
/// asks this port; it never reads `identity.users` (room wall, 03 §4 rule 1). The tenant
/// scope is the standard law: BEGIN then app.current_tenant, so the users RLS policy bites.
/// </summary>
public sealed class UserDirectory(IdentityDb db) : IUserDirectory
{
    public async Task<IReadOnlyList<AlertRecipient>> RecipientsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var (conn, tx) = await db.OpenTenantScopeAsync(tenantId, ct);
        try
        {
            var list = new List<AlertRecipient>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT id, email::text, locale
                  FROM identity.users
                 WHERE tenant_id = @t
                 ORDER BY created_at, id;
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    list.Add(new AlertRecipient(
                        reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
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
}
