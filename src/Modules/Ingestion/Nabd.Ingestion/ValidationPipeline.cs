namespace Nabd.Ingestion;

using Nabd.Ingestion.Contracts;

/// <summary>
/// RED STUB (P3 spec 03 scaffolding): the 7-stage validation pipeline's pure
/// core — the owner's real implementation replaces this from
/// study/implementation-code.md. The real one: parse (stage 3, structural) →
/// channel validation (stage 4, via IDeviceChannels) → normalize (stage 5:
/// tenant fill, clock sanitize, received_at stamp). Stages 1–2 (credential,
/// identity) live at the doors; stage 6 (store) and stage 7 (emit) belong to
/// the callers (spec 04/05) — the pipeline never touches the store.
/// </summary>
public sealed class ValidationPipeline(IDeviceChannels channels, IIngestionIdentity identity)
{
    public Task<PipelineResult> RunAsync(IngestEnvelope envelope, CancellationToken ct = default)
    {
        // RED stub: the edges are unread until the owner's implementation lands —
        // discarded here so the constructor shape survives unchanged.
        _ = channels;
        _ = identity;
        return Task.FromResult(PipelineResult.Fail("NOT_IMPLEMENTED", "RED stub — owner's implementation lands from the study file"));
    }
}
