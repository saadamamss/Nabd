namespace Nabd.Ingestion;

/// <summary>
/// The flush bounds (P4 spec 01, R1 — owner-ruled defaults, env-tunable):
///   FlushSize        readings per flush trigger (default 200)
///   FlushIntervalMs  the timer bound (default 100 ms — whichever first, A or T)
///   ReceiveMaximum   PER-CONNECTION MQTT5 window (default 200 — owner ruling
///                    2026-11-16: the window is NO LONGER FlushSize; a jammed
///                    2,000-message backlog made the broker's connection
///                    process saturate and EMQX killed the session)
///   ConsumerShards   how many MQTT connections share the subscription
///                    (default 1; the pressure ruling set 4 — the aggregate
///                    window is Shards × ReceiveMaximum)
/// Fail-shut (NFR-08 pattern): a nonsensical bound refuses to boot — the same
/// law as ConsumerAccess's missing env. ReceiveMaximum stays inside MQTT5's
/// wire cap (65535), validated HERE so the consumer's options can never carry
/// an illegal value.
/// The HOST reads IConfiguration and calls From() (the composition root's
/// job) — this room never references Microsoft.Extensions.Configuration.
/// </summary>
public sealed record FlushOptions(int FlushSize, int FlushIntervalMs, int ReceiveMaximum = 200, int ConsumerShards = 1)
{
    /// <summary>MQTT5's wire cap on Receive Maximum — it must fit inside it.</summary>
    public const int MaxReceiveMaximum = 65535;

    /// <summary>A farm cap: sharding multiplies sessions and windows — nonsense at boot refuses to boot.</summary>
    public const int MaxConsumerShards = 32;

    /// <summary>
    /// R7's structural bound, AMENDED (sharding ruling 2026-11-16): pending ≡
    /// unacked is bounded by the AGGREGATE window of all consumer connections
    /// (effective per connection = min(client RM, EMQX max_inflight)).
    /// </summary>
    public int AggregateWindow => ConsumerShards * ReceiveMaximum;

    public static FlushOptions From(int flushSize, int flushIntervalMs, int receiveMaximum = 200, int consumerShards = 1)
    {
        if (flushSize < 1 || flushSize > MaxReceiveMaximum)
            throw new InvalidOperationException(
                $"Ingestion:FlushSize must be in [1, {MaxReceiveMaximum}] (fail-shut, NFR-08) — got {flushSize}.");
        if (flushIntervalMs < 1)
            throw new InvalidOperationException(
                $"Ingestion:FlushIntervalMs must be >= 1 (fail-shut, NFR-08) — got {flushIntervalMs}.");
        if (receiveMaximum < 1 || receiveMaximum > MaxReceiveMaximum)
            throw new InvalidOperationException(
                $"Ingestion:ReceiveMaximum must be in [1, {MaxReceiveMaximum}] (fail-shut, NFR-08) — got {receiveMaximum}.");
        if (consumerShards < 1 || consumerShards > MaxConsumerShards)
            throw new InvalidOperationException(
                $"Ingestion:ConsumerShards must be in [1, {MaxConsumerShards}] (fail-shut, NFR-08) — got {consumerShards}.");
        return new FlushOptions(flushSize, flushIntervalMs, receiveMaximum, consumerShards);
    }
}
