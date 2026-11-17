namespace Nabd.Ingestion;

using Nabd.Ingestion.Contracts;

/// <summary>
/// The validation pipeline's pure core (P3 spec 03): stage 3 (parse — handed to
/// PayloadV1Parser) → stage 4 (channel + finiteness) → stage 5 (tenant fill +
/// clock discipline). Stages 1–2 live at the doors (P2/spec 05); stage 6 (store)
/// and stage 7 (emit) belong to the callers — the pipeline never touches a store.
/// Clock discipline NEVER rejects (plan/05 §6): a far-future stamp is CLAMPED to
/// received_at, a stale stamp is accepted-and-flagged — both as E422_TS warnings.
/// P4 spec 01 (Move D): the post-resolution core is extracted into
/// ValidateResolved — ONE verdict text, TWO identity answerers: the HTTP path
/// resolves per device via the edges (unchanged rates), the flush pump passes
/// the set-resolver's answer (ONE resolution per flush). Verdicts are
/// byte-identical: the core was MOVED, not rewritten.
/// </summary>
public sealed class ValidationPipeline(IDeviceChannels channels, IIngestionIdentity identity, int maxReadingsPerBatch = PayloadV1Parser.MaxReadingsPerMessage)
{
    /// <summary>Clock rules (plan/05 §8) — the ONLY home of these constants.</summary>
    private static readonly TimeSpan FutureClamp = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleFlag = TimeSpan.FromDays(30);

    public async Task<PipelineResult> RunAsync(IngestEnvelope envelope, CancellationToken ct = default)
    {
        // Stage 4/5's edges first — cheap, and an unknown device must never see a
        // parse path. The rejection code is the CHANNEL-class code ON PURPOSE:
        // "unknown device" and "unknown channel" deny UNIFORMLY (anti-enumeration,
        // the same law as P2's doors — nothing tells a stranger what exists).
        var tenant = await identity.ResolveTenantIdAsync(envelope.DeviceId, ct);
        var keys = await channels.GetChannelKeysAsync(envelope.DeviceId, ct);
        if (tenant is null || keys is null)
        {
            return PipelineResult.Fail(IngestionErrors.E422_UNKNOWN_CHANNEL,
                "unknown device or channel catalog (uniform rejection — no existence hints)");
        }

        // The extracted core — byte-identical verdicts (stages 3–5 moved verbatim;
        // the keys' null-safety is already proven by the uniform check above).
        return ValidateResolved(envelope, tenant.Value, keys!);
    }

    /// <summary>
    /// The POST-resolution core (P4 spec 01, Move D): stages 3–5, MOVED verbatim
    /// from the pre-extraction RunAsync. Takes the ALREADY-RESOLVED identity —
    /// the HTTP path answers it per device above; the flush pump answers it
    /// from 0020's set resolver ONCE per flush. ABSENCE (unknown/revoked/
    /// deactivated) must NEVER reach here — the uniform rejection happens at
    /// resolution, before this method runs. Synchronous BY LAW: no awaits live
    /// inside — parse, content, and clock discipline are pure CPU.
    /// </summary>
    public PipelineResult ValidateResolved(IngestEnvelope envelope, Guid tenantId, IReadOnlySet<string> channelKeys)
    {
        // Stage 3: structure (payload v1, seq bound, count bound — THIS transport's cap).
        if (!PayloadV1Parser.TryParse(envelope.Payload, maxReadingsPerBatch, out var batch, out var error) || batch is null)
        {
            return PipelineResult.Fail(IngestionErrors.DLQ_INVALID, error ?? "unparseable payload-v1");
        }

        // Stage 4: content — every channel lawful, every value finite.
        var index = 0;
        foreach (var reading in batch.Readings)
        {
            if (!channelKeys.Contains(reading.ChannelKey))
            {
                return PipelineResult.Fail(IngestionErrors.E422_UNKNOWN_CHANNEL,
                    $"reading[{index}] channel '{reading.ChannelKey}' is not in the device's template");
            }

            // Verified: JSON "1e400" parses to ±Infinity silently — this check is
            // what stops it. A NaN literal never got here (the parser threw).
            if (!double.IsFinite(reading.Value))
            {
                return PipelineResult.Fail(IngestionErrors.E422_RANGE,
                    $"reading[{index}] value for '{reading.ChannelKey}' is not finite");
            }

            index++;
        }

        // Stage 5: clock discipline (plan/05 §8) — sanitize, never reject. Per
        // READING now: the batch ts governs unless a reading carries its own ts
        // override (plan/05 §5 batch form); each effective event time is checked
        // against the same future-clamp / stale-flag window.
        var warnings = new List<string>();
        var readings = new List<NormalizedReading>(batch.Readings.Count);
        foreach (var reading in batch.Readings)
        {
            var eventTime = reading.TimeUtc ?? batch.TimeUtc;
            if (eventTime - envelope.ReceivedAtUtc > FutureClamp)
            {
                // Power-loss clock reset: the device thinks it lives in the future.
                // Clamp to the server's receive time — event time is never trusted alone.
                eventTime = envelope.ReceivedAtUtc;
                if (!warnings.Contains(IngestionErrors.E422_TS)) warnings.Add(IngestionErrors.E422_TS);
            }
            else if (envelope.ReceivedAtUtc - eventTime > StaleFlag)
            {
                // Store-and-forward flood: old but honest. Accept, flag, move on
                // (dashboards on event time will bucket it where it belongs).
                if (!warnings.Contains(IngestionErrors.E422_TS)) warnings.Add(IngestionErrors.E422_TS);
            }

            readings.Add(new NormalizedReading(
                tenantId, envelope.DeviceId, reading.ChannelKey,
                eventTime, reading.Value, batch.Seq, envelope.ReceivedAtUtc));
        }

        return PipelineResult.Ok(readings, warnings);
    }
}
