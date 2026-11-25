// RED SCAFFOLDING — P5 spec 04 (4a). The shapes are FINAL (study §2); the reader
// next door throws until the owner applies study §2/§3. Nothing here is wired.
using Nabd.Identity.Contracts;   // قانوني: Contracts→Contracts (نفس نمط AssetStatus)
using Nabd.SharedKernel;

namespace Nabd.Assets.Contracts;

// P5 spec 04 (FR-41/FR-43) — سجل الأصل فوق 0029.
// الخادم يختار المصدر والعرض؛ العميل لا يملك أي معامل تحكم في الدقة (C3).
// الفجوة غياب دلو، لا صفر (C7) — والشكل يحملها كما هي.

/// <summary>The three server-chosen resolutions (0029).</summary>
public static class HistorySources
{
    public const string Raw = "raw";
    public const string Hourly = "hourly";
    public const string Daily = "daily";
}

/// <summary>
/// One bucket as the function emitted it: `t` is the bucket start (UTC instant),
/// `n` is the real reading count behind it. Edges keep their true `n`.
/// </summary>
public sealed record HistoryPoint(
    DateTimeOffset T, double Min, double Max, double Avg, long N);

/// <summary>
/// The answer for one asset+channel+range. `Source`/`BucketSeconds` are always
/// present — including on an empty range (REV 4 correction 1): the label must be
/// able to say "hourly · no readings" without guessing.
/// </summary>
public sealed record AssetHistoryResult(
    string Source, int BucketSeconds, IReadOnlyList<HistoryPoint> Points);

/// <summary>
/// Door lane only: the caller comes from the verified JWT, the tenant is the
/// caller's own (C2). There is no system lane for history.
/// </summary>
public interface IAssetHistoryReader
{
    Task<Result<AssetHistoryResult>> ReadAsync(
        CallerContext acting, Guid assetId, string? channel,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);
}

/// <summary>Stable codes the door translates to 422 (one code, one path).</summary>
public static class HistoryErrors
{
    public const string InvalidRange = "history:invalid_range";
    public const string UnknownChannel = "history:unknown_channel";
}
