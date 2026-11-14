namespace Nabd.Ingestion;

using Microsoft.Extensions.Logging;
using Nabd.Assets.Contracts;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// The HTTP ingest door's orchestration (P3 spec 05, plan/05 §4 + the VETO
/// ruling): the SECOND transport over the SAME pipeline. Order is a contract:
/// credential → quota → body size → bounded concurrency → pipeline → store
/// commit → presence → 200. NOTHING is 200 before the commit — a client that
/// saw 200 never lost rows (the spike's in-flight exposure = 0).
/// Overload SHEDS (429 + Retry-After) instead of ballooning latency — the
/// VETO's whole point (plan/05 §7: overflow → backpressure, not memory).
/// </summary>
public sealed class HttpIngestDoor(
    IDeviceLifecycle devices,
    DeviceRateLimiter quota,
    ValidationPipeline pipeline,
    IReadingsStore store,
    IDeviceStatusTracker deviceStatus,
    RejectionCounters counters,
    ILogger<HttpIngestDoor>? logger = null,
    int concurrencyBound = 16)   // the spike's 16-worker shape (ADR-005 verdict)
{
    private readonly SemaphoreSlim _slots = new(concurrencyBound, concurrencyBound);

    /// <summary>plan/05 §4: max body 1 MB. The controller's [RequestSizeLimit] is
    /// the outer net; the door's own check is the law inside the room.</summary>
    private const int MaxBodyBytes = 1_000_000;

    public async Task<HttpIngestVerdict> IngestAsync(string presentedToken, byte[] payload, CancellationToken ct = default)
    {
        _ = counters;   // spec 06's port — the owner's increments (RED until then)

        // ---- stage 1: the device-token lane (NOT the JWT wall) ----------------
        var deviceId = presentedToken is { Length: > 0 }
            ? await devices.VerifyTokenAsync(presentedToken, ct)
            : null;
        if (deviceId is null)
        {
            logger?.LogWarning("ingest DENIED code={Code} reason=invalid-device-token",
                IngestionErrors.E401_AUTH);
            return new(401, IngestionErrors.E401_AUTH, "invalid device credential", 0, [], null);
        }

        // ---- stage 2: the per-device quota (30/min) ---------------------------
        if (!quota.Allow(deviceId.Value, out var retryAfter))
        {
            logger?.LogWarning("ingest DENIED code={Code} device={DeviceId}", IngestionErrors.E429_QUOTA, deviceId);
            return new(429, IngestionErrors.E429_QUOTA, "per-device rate exceeded", 0, [], retryAfter);
        }

        // ---- stage 3: the body-size law ----------------------------------------
        if (payload.Length > MaxBodyBytes)
        {
            return new(413, IngestionErrors.DLQ_INVALID, "body exceeds the 1 MB limit (plan/05 §4)", 0, [], null);
        }

        // ---- stage 4: bounded concurrency — shed BEFORE the store -------------
        if (!await _slots.WaitAsync(0, ct))
        {
            return new(429, IngestionErrors.E429_QUOTA, "door at capacity — retry shortly", 0, [], 1);
        }

        try
        {
            // ---- stage 5: the SAME pipeline the MQTT path uses (spec 03) -------
            var envelope = new IngestEnvelope(deviceId.Value, "http", payload, DateTimeOffset.UtcNow);
            var result = await pipeline.RunAsync(envelope, ct);

            if (!result.IsAccepted)
            {
                var rejected = result.Rejected!;
                logger?.LogWarning("ingest REJECTED code={Code} detail={Detail} device={DeviceId}",
                    rejected.Code, rejected.Detail, deviceId);
                return new(StatusOf(rejected.Code), rejected.Code, rejected.Detail, 0, [], null);
            }

            // ---- stage 6: the store — THE COMMIT IS THE CONTRACT ---------------
            var accepted = result.Accepted!;
            var batch = new ReadingBatchDto(
                accepted[0].TenantId,
                deviceId.Value,
                accepted.Select(r => new ReadingDto(
                    r.ChannelKey, r.TimeUtc, r.Value, r.Seq, r.ReceivedAtUtc)).ToList());

            await store.StoreBatchAsync(batch, ct);   // الالتزام أولاً وقبل كل شيء

            // presence rides after the commit
            await deviceStatus.MarkSeenAsync(
                deviceId.Value, accepted[0].TenantId,
                envelope.ReceivedAtUtc, accepted[0].TimeUtc, ct);

            logger?.LogInformation("ingest STORED device={DeviceId} rows={Count} warnings={Warnings}",
                deviceId, accepted.Count,
                result.Warnings.Count == 0 ? "none" : string.Join(",", result.Warnings));

            return new(200, null, null, accepted.Count, result.Warnings, null);
        }
        finally
        {
            _slots.Release();
        }
    }

    private static int StatusOf(string code) => code switch
    {
        IngestionErrors.E401_AUTH => 401,
        IngestionErrors.DLQ_INVALID => 400,
        IngestionErrors.E422_UNKNOWN_CHANNEL => 422,
        IngestionErrors.E422_RANGE => 422,
        IngestionErrors.E429_QUOTA => 429,
        _ => 400,
    };
}

public sealed record HttpIngestVerdict(
    int Status,
    string? Code,
    string? Detail,
    int Stored,
    IReadOnlyList<string> Warnings,
    int? RetryAfterSeconds);