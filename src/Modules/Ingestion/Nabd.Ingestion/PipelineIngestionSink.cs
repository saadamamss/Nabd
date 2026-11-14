namespace Nabd.Ingestion;

using Microsoft.Extensions.Logging;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// The MQTT path's sink (P3 spec 03): runs the pipeline FIRST, and hands the
/// store port a batch ONLY when the pipeline accepted. THE LAW (test-pinned):
/// a rejected delivery returns here without ever touching the store — nothing
/// malformed can reach an INSERT (the MQTT twin of the ADR-005 VETO's
/// 200-after-commit: durability only after the data is proven real).
/// Rejections are LOUD: logged with their catalog code; spec 06 adds the
/// raw_rejects table beside this log line — never a silent drop.
/// </summary>
public sealed class PipelineIngestionSink(
    ValidationPipeline pipeline,
    IReadingsStore store,
    ILogger<PipelineIngestionSink> logger) : IIngestionSink
{
    public async Task AcceptAsync(IngestEnvelope envelope, CancellationToken ct = default)
    {
        var result = await pipeline.RunAsync(envelope, ct);

        if (!result.IsAccepted)
        {
            var rejected = result.Rejected!;
            logger.LogWarning(
                "ingest REJECTED code={Code} detail={Detail} device={DeviceId} topic={Topic}",
                rejected.Code, rejected.Detail, envelope.DeviceId, envelope.Topic);
            // spec 06: E422_UNKNOWN_CHANNEL / E422_RANGE / DLQ_INVALID land in
            // telemetry.raw_rejects WITH the raw bytes + a per-code counter.
            // Until that migration exists, this log line IS the contract.
            return;
        }

        var accepted = result.Accepted!;
        var batch = new ReadingBatchDto(
            accepted[0].TenantId,
            envelope.DeviceId,
            accepted.Select(r => new ReadingDto(r.ChannelKey, r.TimeUtc, r.Value, r.Seq)).ToList());

        await store.StoreBatchAsync(batch, ct);

        logger.LogInformation(
            "ingest STORED device={DeviceId} rows={Count} seq={Seq} warnings={Warnings}",
            envelope.DeviceId, accepted.Count, accepted[0].Seq,
            result.Warnings.Count == 0 ? "none" : string.Join(",", result.Warnings));
    }
}