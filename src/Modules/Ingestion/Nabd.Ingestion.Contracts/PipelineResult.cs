namespace Nabd.Ingestion.Contracts;

/// <summary>
/// The pipeline's verdict on one delivery (P3 spec 03): EITHER a normalized batch
/// ready for the store, OR a rejection with its catalog code — never both, never
/// half. Clock discipline never rejects: clamped/flagged timestamps ride inside
/// an ACCEPTED result as warnings (E422_TS) — plan/05 §6's law.
/// </summary>
public sealed record PipelineResult(
    IReadOnlyList<NormalizedReading>? Accepted,
    IReadOnlyList<string> Warnings,
    Rejection? Rejected)
{
    public bool IsAccepted => Rejected is null;

    public static PipelineResult Ok(IReadOnlyList<NormalizedReading> readings, IReadOnlyList<string>? warnings = null)
        => new(readings, warnings ?? [], null);

    public static PipelineResult Fail(string code, string detail)
        => new(null, [], new Rejection(code, detail));
}

/// <summary>A rejection: the catalog code plus a human-readable detail for logs and raw-reject forensics.</summary>
public sealed record Rejection(string Code, string Detail);
