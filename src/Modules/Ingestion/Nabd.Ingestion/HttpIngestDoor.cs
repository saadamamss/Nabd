namespace Nabd.Ingestion;

using Nabd.Assets.Contracts;
using Nabd.Ingestion.Contracts;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// RED STUB (P3 spec 05 scaffolding): the HTTP ingest door's orchestration —
/// the second transport over the SAME pipeline (plan/05 §4, VETO ruling).
/// The real one runs, in order: (1) token → device via IDeviceLifecycle
/// (401 E401_AUTH + security-event log on null); (2) per-device quota →
/// 429 E429_QUOTA + Retry-After; (3) envelope → the SAME ValidationPipeline
/// the MQTT path uses; (4) rejection → the catalog code mapped to a status;
/// (5) acceptance → IReadingsStore batch → commit → device presence mark →
/// 200 with the stored count and any E422_TS warnings. A bounded SemaphoreSlim
/// (the spike's 16-worker shape) sheds overload as 429 BEFORE the store.
/// 200 means committed — never before.
/// </summary>
public sealed class HttpIngestDoor(
    IDeviceLifecycle devices,
    DeviceRateLimiter quota,
    ValidationPipeline pipeline,
    IReadingsStore store,
    IDeviceStatusTracker deviceStatus,
    Microsoft.Extensions.Logging.ILogger<HttpIngestDoor>? logger = null,
    int concurrencyBound = 16)
{
    public Task<HttpIngestVerdict> IngestAsync(string presentedToken, byte[] payload, CancellationToken ct = default)
    {
        _ = devices; _ = quota; _ = pipeline; _ = store; _ = deviceStatus; _ = logger; _ = concurrencyBound; _ = presentedToken; _ = payload;
        throw new NotImplementedException("P3 spec 05: owner implements from study/implementation-code.md");
    }
}

/// <summary>
/// The door's verdict: the HTTP status is decided HERE (the door is the HTTP
/// transport's caller — "dispositions live with the caller", plan/05 §6), so
/// the controller stays a thin adapter. Code is the catalog constant verbatim
/// (E401_AUTH / DLQ_INVALID / E422_* / E429_QUOTA); Accepted carries the stored
/// count and any clock warnings (E422_TS).
/// </summary>
public sealed record HttpIngestVerdict(
    int Status,
    string? Code,
    string? Detail,
    int Stored,
    IReadOnlyList<string> Warnings,
    int? RetryAfterSeconds);
