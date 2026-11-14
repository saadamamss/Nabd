namespace Nabd.Ingestion;

using Microsoft.Extensions.Logging;
using Nabd.TelemetryStore.Contracts;

/// <summary>
/// PLACEHOLDER store port (P3 spec 03): logs every batch the pipeline hands it.
/// Spec 04's batch writer (the real IReadingsStore against telemetry.readings)
/// replaces this class at the wiring site ONLY — the sink, pipeline, and tests
/// never change. Its job today: prove the ordering live (accepted deliveries
/// arrive here, rejected ones never do).
/// </summary>
public sealed class LoggingReadingsStore(ILogger<LoggingReadingsStore> logger) : IReadingsStore
{
    public Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default)
    {
        logger.LogInformation(
            "store PLACEHOLDER (spec 04 replaces) device={DeviceId} tenant={TenantId} rows={Count}",
            batch.DeviceId, batch.TenantId, batch.Readings.Count);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReadingValueDto>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ReadingValueDto>>([]);
}