namespace Nabd.Ingestion;

using System.Text.Json;
using Nabd.Ingestion.Contracts;

/// <summary>
/// Stage 3 — parses payload v1 (plan/05 §5, byte-exact with the simulator's
/// composer) and enforces every STRUCTURAL law. Pure function: no DB, no clock,
/// no edges — bytes in, shape out, or a DLQ_INVALID-class failure. Content
/// validation (channels, finiteness) is the pipeline's stage 4, not this parser's.
/// </summary>
public static class PayloadV1Parser
{
    /// <summary>≤ 32 readings per MQTT message (plan/05 §6 stage 3's size law — the
    /// MQTT transport's default; the HTTP door passes its own 500 cap, plan/05 §4).</summary>
    public const int MaxReadingsPerMessage = 32;

    /// <summary>Seq sanity (owner ruling 2026-11-13): an integer in [0, 2^62) — and NOTHING more.</summary>
    public static readonly long MaxSeq = 1L << 62;

    /// <summary>The MQTT transport's cap. The HTTP door calls the overload below with 500.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, out ReadingBatch? batch, out string? error)
        => TryParse(bytes, MaxReadingsPerMessage, out batch, out error);

    /// <summary>Transport-scoped parse: each door passes ITS batch cap (MQTT 32, HTTP 500 — plan/05 §4).</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, int maxReadings, out ReadingBatch? batch, out string? error)
    {
        batch = null;
        error = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            error = "payload is not valid JSON";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root is not an object";
                return false;
            }

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt64() != 1)
            {
                error = "v must be the number 1";
                return false;
            }

            if (!root.TryGetProperty("ts", out var tsEl) || tsEl.ValueKind != JsonValueKind.String)
            {
                error = "ts missing or not a string";
                return false;
            }

            // Verified behavior: JsonDocument.Parse does NOT validate the ts string's
            // CONTENT — the throw lives here (FormatException on garbage).
            DateTimeOffset ts;
            try
            {
                ts = tsEl.GetDateTimeOffset().ToUniversalTime();
            }
            catch (FormatException)
            {
                error = "ts is not an ISO-8601 timestamp";
                return false;
            }

            if (!root.TryGetProperty("seq", out var seqEl) || seqEl.ValueKind != JsonValueKind.Number)
            {
                error = "seq missing or not a number";
                return false;
            }

            long seq;
            try
            {
                seq = seqEl.GetInt64();
            }
            catch (FormatException)
            {
                error = "seq is not an integer";
                return false;
            }

            if (seq < 0 || seq >= MaxSeq)
            {
                error = $"seq must be in [0, 2^62), got {seq} (no monotonic enforcement — owner ruling 2026-11-13)";
                return false;
            }

            if (!root.TryGetProperty("rr", out var rr) || rr.ValueKind != JsonValueKind.Array)
            {
                error = "rr missing or not an array";
                return false;
            }

            var count = rr.GetArrayLength();
            if (count == 0 || count > maxReadings)
            {
                error = $"rr must hold 1..{maxReadings} readings, got {count}";
                return false;
            }

            var readings = new List<PipelineReading>(count);
            var index = 0;
            foreach (var item in rr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = $"rr[{index}] is not an object";
                    return false;
                }

                if (!item.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number)
                {
                    error = $"rr[{index}].t missing or not numeric";
                    return false;
                }

                if (!item.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.String
                    || c.GetString() is not { Length: > 0 } channel)
                {
                    error = $"rr[{index}].c missing or empty";
                    return false;
                }

                // Verified: "1e400" parses to ±Infinity WITHOUT throwing — the finite
                // check is the pipeline's stage 4 (E422_RANGE), not a parse failure.
                double value;
                try
                {
                    value = t.GetDouble();
                }
                catch (FormatException)
                {
                    error = $"rr[{index}].t is not a double";
                    return false;
                }

                // Per-reading ts override (plan/05 §5 batch form): OPTIONAL — absent
                // means the batch-level ts governs. Same content law as the batch ts
                // (ISO-8601 or FormatException → DLQ_INVALID).
                DateTimeOffset? readingTs = null;
                if (item.TryGetProperty("ts", out var rts))
                {
                    if (rts.ValueKind != JsonValueKind.String)
                    {
                        error = $"rr[{index}].ts is not a string";
                        return false;
                    }
                    try
                    {
                        readingTs = rts.GetDateTimeOffset().ToUniversalTime();
                    }
                    catch (FormatException)
                    {
                        error = $"rr[{index}].ts is not an ISO-8601 timestamp";
                        return false;
                    }
                }

                readings.Add(new PipelineReading(channel, value, readingTs));
                index++;
            }

            batch = new ReadingBatch(1, ts, seq, readings);
            return true;
        }
    }
}