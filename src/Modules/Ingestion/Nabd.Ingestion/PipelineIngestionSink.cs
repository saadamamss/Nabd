namespace Nabd.Ingestion;

using Microsoft.Extensions.Logging;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// The MQTT path's sink (P3 specs 03+04). THE LAWS, test-pinned:
/// 1. pipeline verdict FIRST; the store port is touched ONLY on an accepted
///    result — nothing malformed reaches an INSERT (spec 03's ordering law).
/// 2. every ACCEPTED batch marks the device seen — buffered, one bulk flush
///    per window (plan/05 §9), never inside the hot path's DB traffic.
/// 3. a store FAILURE PROPAGATES OUT — never swallowed — so the consumer's
///    catch leaves the message un-acked and the broker redelivers (NFR-04's
///    ack-after-commit; crash between commit and ack = idempotent re-insert).
/// Rejections stay LOUD: logged with their catalog code (spec 06 adds the
/// raw_rejects table beside this log line — never a silent drop).
/// </summary>
public sealed class PipelineIngestionSink(
    ValidationPipeline pipeline,
    IReadingsStore store,
    IDeviceStatusTracker deviceStatus,
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
            // spec 06 (migration 0019): rejections land in telemetry.raw_rejects
            // WITH the raw bytes. Until then this log line IS the contract.
            return;
        }

        var accepted = result.Accepted!;
        var batch = new ReadingBatchDto(
            accepted[0].TenantId,
            envelope.DeviceId,
            accepted.Select(r => new ReadingDto(
                r.ChannelKey, r.TimeUtc, r.Value, r.Seq, r.ReceivedAtUtc)).ToList());

        // NO try/catch here — on purpose (fact 3). If the store throws, this
        // method throws, the consumer's catch fires, and the message is NOT
        // acked: the broker redelivers, and the store's ON CONFLICT makes the
        // replay a no-op. The ack is downstream of the commit, always.
        await store.StoreBatchAsync(batch, ct);

        // Presence rides AFTER the store succeeded — unseen data is never "seen".
        await deviceStatus.MarkSeenAsync(
            envelope.DeviceId, accepted[0].TenantId,
            envelope.ReceivedAtUtc, accepted[0].TimeUtc, ct);

        logger.LogInformation(
            "ingest STORED device={DeviceId} rows={Count} seq={Seq} warnings={Warnings}",
            envelope.DeviceId, accepted.Count, accepted[0].Seq,
            result.Warnings.Count == 0 ? "none" : string.Join(",", result.Warnings));
    }
}