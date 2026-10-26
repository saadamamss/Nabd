using Nabd.SharedKernel;
namespace Nabd.Admin.Contracts;

/// <summary>Platform Admin room's outbound event: a tenant got disabled (FR-61) — all its devices should be rejected.</summary>
public sealed record TenantDisabled(Guid TenantId, DateTimeOffset AtUtc) : IntegrationEvent;
