namespace Nabd.SharedKernel;

/// <summary>
/// Base marker for integration events crossing room boundaries (03 §3).
/// In-process rooms exchange these as in-memory events (ADR-006);
/// later, the same payload crosses host boundaries via RabbitMQ.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Unique per event instance — used for tracing/correlation IDs.</summary>
    Guid EventId { get; }

    /// <summary>UTC timestamp when the event was produced (03 §8: everything UTC).</summary>
    DateTimeOffset OccurredAtUtc { get; }
}

public abstract record IntegrationEvent : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
