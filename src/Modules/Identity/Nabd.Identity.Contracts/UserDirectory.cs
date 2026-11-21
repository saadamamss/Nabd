namespace Nabd.Identity.Contracts;

/// <summary>
/// The minimum a notifier needs to address a person (P4 spec 08): an address and the
/// language that person reads. Nothing else about a user belongs to a notifier.
/// </summary>
public sealed record AlertRecipient(Guid UserId, string Email, string Locale);

/// <summary>
/// Read port over the tenant's people, owned by Identity (the wall: Notifications never
/// reads `identity.users`, it asks this port). Ordered for a stable send order.
/// </summary>
public interface IUserDirectory
{
    Task<IReadOnlyList<AlertRecipient>> RecipientsAsync(Guid tenantId, CancellationToken ct = default);
}
