namespace Nabd.Ingestion.Contracts;

/// <summary>
/// One reading as it travels INSIDE payload v1 (plan/05 §5 — byte-exact with the
/// simulator's composer): a channel key and a numeric value. Parsing from bytes
/// is the pipeline's stage-3 job (Nabd.Ingestion impl); this record is the shape
/// the parser yields.
/// </summary>
public sealed record PipelineReading(string ChannelKey, double Value);

/// <summary>
/// A parsed payload-v1 batch: the message-level fields plus its readings.
/// Stage 3 validates the SHAPE of this (version, seq sanity, reading-count
/// bound); stage 4 validates the CONTENT (channels, finiteness).
/// </summary>
public sealed record ReadingBatch(
    long Version,
    DateTimeOffset TimeUtc,
    long Seq,
    IReadOnlyList<PipelineReading> Readings);
