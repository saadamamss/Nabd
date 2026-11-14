namespace Nabd.Ingestion.Contracts;

/// <summary>
/// One reading as it travels INSIDE payload v1 (plan/05 §5 — byte-exact with the
/// simulator's composer): a channel key and a numeric value. Parsing from bytes
/// is the pipeline's stage-3 job (Nabd.Ingestion impl); this record is the shape
/// the parser yields.
/// </summary>
/// <summary>
/// One reading as it travels INSIDE payload v1 (plan/05 §5 — byte-exact with the
/// simulator's composer): a channel key and a numeric value. Parsing from bytes
/// is the pipeline's stage-3 job (Nabd.Ingestion impl); this record is the shape
/// the parser yields. TimeUtc is the per-reading ts OVERRIDE (plan/05 §5 batch
/// form: "each reading may override with own ts") — null means the batch-level
/// ts governs, which is what the MQTT simulator payloads always do.
/// </summary>
public sealed record PipelineReading(string ChannelKey, double Value, DateTimeOffset? TimeUtc = null);

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
