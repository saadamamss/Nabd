using Nabd.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace Nabd.Ingestion;

public sealed class LoggingIngestionSink(ILogger<LoggingIngestionSink> logger) : IIngestionSink
{
    public Task AcceptAsync(IngestEnvelope envelope, CancellationToken ct = default)
    {
        logger.LogInformation("ingest device={DeviceId} topic={Topic} bytes={Bytes} receivedAt={ReceivedAtUtc:o}",
            envelope.DeviceId, envelope.Topic, envelope.Payload.Length, envelope.ReceivedAtUtc);

        return Task.CompletedTask;
    }
}