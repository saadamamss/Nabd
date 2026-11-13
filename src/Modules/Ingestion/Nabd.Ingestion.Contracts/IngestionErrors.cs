namespace Nabd.Ingestion.Contracts;

/// <summary>
/// The error catalog's ONE home (P3 spec 03, lesson #16): every rejection reason
/// both ingest paths can produce, as constants. Dispositions live with the caller
/// (HTTP maps to status codes; the MQTT consumer routes to raw-rejects/logging) —
/// the pipeline only ever REPORTS a code, it never decides transport fate.
/// Codes are stable API: logs, metrics counters, and the raw_rejects reason
/// column all carry them verbatim.
/// </summary>
public static class IngestionErrors
{
    /// <summary>Stage 1: credential invalid/revoked. MQTT: never arrives (the broker refused). HTTP: 401.</summary>
    public const string E401_AUTH = "E401_AUTH";

    /// <summary>Stage 2: identity mismatch (topic's device != credential's device). MQTT: ACL deny-200 (P2). HTTP: n/a.</summary>
    public const string E403_TOPIC = "E403_TOPIC";

    /// <summary>Stage 4: channel not in the device's template. HTTP: 422; MQTT: raw-reject (spec 06) — never silently dropped.</summary>
    public const string E422_UNKNOWN_CHANNEL = "E422_UNKNOWN_CHANNEL";

    /// <summary>Stage 4: value not finite / out of any lawful range. HTTP: 422; MQTT: raw-reject (spec 06).</summary>
    public const string E422_RANGE = "E422_RANGE";

    /// <summary>Stage 4/5 clock discipline: NOT a rejection — the timestamp is sanitized (clamped or accepted-and-flagged) per plan/05 §8.</summary>
    public const string E422_TS = "E422_TS";

    /// <summary>HTTP only: per-device rate limit (spec 05). The MQTT path has no quota stage.</summary>
    public const string E429_QUOTA = "E429_QUOTA";

    /// <summary>Consumer: structurally unparseable/unsanely-shaped batch. Raw-reject with reason (spec 06) — never silently dropped.</summary>
    public const string DLQ_INVALID = "DLQ_INVALID";
}
