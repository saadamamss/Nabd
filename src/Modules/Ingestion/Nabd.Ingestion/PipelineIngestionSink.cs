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
/// 4. (SPEC 06) a rejected delivery is NEVER silent: raw bytes + reason land
///    in telemetry.raw_rejects (0019) and the reason increments its counter.
/// Rejections stay LOUD: logged with their catalog code (spec 06 adds the
/// raw_rejects table beside this log line — never a silent drop).
/// </summary>
public sealed class PipelineIngestionSink(
    ValidationPipeline pipeline,
    IReadingsStore store,
    IDeviceStatusTracker deviceStatus,
    IRawRejectStore rawRejects,
    RejectionCounters counters,
    ILogger<PipelineIngestionSink> logger) : IIngestionSink
{
    public async Task AcceptAsync(IngestEnvelope envelope, CancellationToken ct = default)
    {

        var result = await pipeline.RunAsync(envelope, ct);

        if (!result.IsAccepted)
        {
            var rejected = result.Rejected!;

            // SPEC 06 — the DESTINATION law: a rejection is never silent.
            // Raw bytes verbatim + catalog reason land in telemetry.raw_rejects
            // (0019, the VETO-shaped destination), and the reason counts.
            // (HTTP-path rejections do NOT land here — the 4xx response IS
            // that client's receipt; this table is the MQTT path's ledger.)
            await rawRejects.StoreAsync(new RawRejectDto(
                envelope.ReceivedAtUtc,          // time = received at MVP (forensics, not a time series)
                envelope.ReceivedAtUtc,
                TenantId: null,                  // identity may precede/fail resolution — nullable by design
                DeviceId: envelope.DeviceId,
                rejected.Code,
                envelope.Payload));

            counters.Increment(rejected.Code);

            logger.LogWarning(
                "ingest REJECTED code={Code} detail={Detail} device={DeviceId} topic={Topic} → raw_rejects",
                rejected.Code, rejected.Detail, envelope.DeviceId, envelope.Topic);
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