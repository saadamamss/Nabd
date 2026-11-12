using Nabd.Identity.Contracts;
using Nabd.SharedKernel;

namespace Nabd.Assets.Contracts;

// P2 spec 04 — FR-13 (channels are CONFIG from the seed tables, not schema) +
// FR-15 (the cold-chain preset exists as DATA). READ-ONLY by design: template
// writes are migrations only (0015; app-role INSERT is denied by 0012's grants —
// the suite keeps that probe as a permanent fact).
// HONESTY (FR-15): lower_limit / upper_limit are PRESET defaults for the P4
// rules engine to seed per-rule from. Nothing in P2 evaluates them and no rule
// rows are created here — saying it in the contract so no phantom feature is born.

/// <summary>One channel as the doors expose it — the unit TRAVELS with the channel
/// (plan/05 §5): devices send raw values in this declared unit, no conversion anywhere.</summary>
public sealed record ChannelDefinition(
    Guid TemplateId, string Key, string NameAr, string NameEn,
    string Unit, string DataType, double? LowerLimit, double? UpperLimit);

/// <summary>A global template — same catalog for every tenant (0002: no tenant_id).</summary>
public sealed record TemplateSnapshot(
    Guid TemplateId, string Key, string NameAr, string NameEn,
    IReadOnlyList<ChannelDefinition> Channels);

public interface ITemplateCatalog
{
    /// <summary>All alive templates with their channels, ordered by template key.
    /// Global read — no tenant argument, no RLS: 0012 grants SELECT with no policy.</summary>
    Task<Result<IReadOnlyList<TemplateSnapshot>>> ListTemplatesAsync(
        CallerContext acting, CancellationToken ct = default);
}