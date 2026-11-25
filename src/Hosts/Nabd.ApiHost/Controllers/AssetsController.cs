namespace Nabd.ApiHost.Controllers;

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Nabd.ApiHost.Auth;
using Nabd.Assets.Contracts;
using Nabd.Identity.Contracts;

public sealed record CreateAssetJson(Guid TemplateId, string Name, string? Location, string? Notes);
public sealed record UpdateAssetJson(string? Name, string? Location, string? Notes);

/// <summary>The history wire shape: UTC instants, the server's resolution, no tenant.</summary>
public sealed record HistoryResponseJson(
    string Source, int BucketSeconds, string Channel,
    DateTimeOffset From, DateTimeOffset To, IReadOnlyList<HistoryPoint> Points);

[ApiController]
[Route("api/v1/assets")]
public sealed class AssetsController(
    IAssetCatalog assets, IAssetStatusReader statuses, IAssetHistoryReader history)
    : ControllerBase
{
    [TenantAdmin(Operations.AssetCreate)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAssetJson body, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        // NO tenant segment in the URL (spec decision): self-service for the CALLER's
        // tenant — the tid from the verified token is the only tenant in play here.
        var request = new CreateAssetRequest(caller.TenantId, body.TemplateId, body.Name, body.Location, body.Notes);

        var result = await assets.CreateAssetAsync(caller, request, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [Viewer(Operations.AssetRead)]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeArchived = false, CancellationToken ct = default)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await assets.ListAssetsAsync(caller, caller.TenantId, includeArchived, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [Viewer(Operations.AssetRead)]
    [HttpGet("{assetId:guid}")]
    public async Task<IActionResult> Get(Guid assetId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await assets.GetAssetAsync(caller, assetId, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    [TenantAdmin(Operations.AssetUpdate)]
    [HttpPatch("{assetId:guid}")]
    public async Task<IActionResult> Update(Guid assetId, [FromBody] UpdateAssetJson body, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var request = new UpdateAssetRequest(caller.TenantId, assetId, body.Name, body.Location, body.Notes);

        var result = await assets.UpdateAssetAsync(caller, request, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    /// <summary>
    /// P5 spec 03 (FR-40): one snapshot for the whole dashboard grid. The tenant is
    /// ALWAYS the caller's token tenant — the door accepts no tenant argument at all
    /// (a guessed id cannot even be expressed). The `{assetId:guid}` route below
    /// cannot shadow `status` (the guid constraint), so the two coexist.
    /// </summary>
    [Viewer(Operations.AssetRead)]
    [HttpGet("status")]
    public async Task<IActionResult> Status(
        [FromQuery] bool includeArchived = false, CancellationToken ct = default)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await statuses.ListAsync(caller, includeArchived, ct);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.Error!);
    }

    /// <summary>
    /// P5 spec 04 (FR-41/FR-43): one asset's history for one range. Bounds are
    /// parsed HERE (not model-bound) so an unparseable bound answers 422
    /// `history:invalid_range` instead of MVC's generic 400. The tenant is the
    /// caller's own; no tenant parameter exists (the P1 404 law).
    /// </summary>
    [Viewer(Operations.AssetRead)]
    [HttpGet("{assetId:guid}/history")]
    public async Task<IActionResult> History(
        Guid assetId,
        [FromQuery] string? channel,
        [FromQuery(Name = "from")] string? fromText,
        [FromQuery(Name = "to")] string? toText,
        CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        if (!TryParseInstant(fromText, out var fromUtc) || !TryParseInstant(toText, out var toUtc))
            return StatusCode(422, new { code = HistoryErrors.InvalidRange });

        var requestedChannel = channel?.Trim() ?? string.Empty;   // echoed, not validated twice
        var result = await history.ReadAsync(caller, assetId, requestedChannel, fromUtc, toUtc, ct);

        if (!result.IsSuccess) return Translate(result.Error!);

        return Ok(new HistoryResponseJson(
            result.Value.Source,
            result.Value.BucketSeconds,
            requestedChannel,
            fromUtc,
            toUtc,
            result.Value.Points));
    }

    [TenantAdmin(Operations.AssetArchive)]
    [HttpDelete("{assetId:guid}")]
    public async Task<IActionResult> Archive(Guid assetId, CancellationToken ct)
    {
        var caller = Caller.FromUser(User);
        if (caller is null) return Unauthorized();

        var result = await assets.ArchiveAssetAsync(caller, caller.TenantId, assetId, ct);

        return result.IsSuccess ? NoContent() : Translate(result.Error!);
    }

    /// <summary>UTC only on the wire; a missing/blank/unparseable bound is a range fault.</summary>
    private static bool TryParseInstant(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private IActionResult Translate(string error) => error switch
    {
        IdentityErrors.Forbidden => StatusCode(403, new { code = error }),
        IdentityErrors.CrossTenantTarget or AssetErrors.AssetNotFound => NotFound(new { code = error }),
        AssetErrors.UnknownTemplate => StatusCode(422, new { code = error }),
        HistoryErrors.InvalidRange or HistoryErrors.UnknownChannel => StatusCode(422, new { code = error }),
        _ => BadRequest(new { code = error }),
    };
}
