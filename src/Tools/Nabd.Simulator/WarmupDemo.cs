namespace Nabd.Simulator;

/// <summary>
/// The `warmup-demo` profile (05 §10, 02 §6 #7): a cold-storage fridge warming
/// from its operating baseline. A pure function of (seq, tick rate) — never the
/// wall clock — so the 1/s simulator and the E2E feed tell the SAME story in
/// event time, and a slow PUBACK can never bend the profile.
///
/// Scripted seconds (the contract, D10h):
///   0–60    stable at the channel baseline (4.0 °C for the seeded fridge rule)
///   60–66   door beat at 9.5 °C — 6 s above the 8 °C limit, and it MUST stay silent
///   66–126  ramp baseline → peak (12.0 °C default)
///   126+    hold at peak
/// </summary>
public sealed class WarmupDemo
{
    /// <summary>The seeded 0015 fridge ceiling — the profile's story limit, not a rule read.</summary>
    public const double LimitCelsius = 8.0;

    /// <summary>The seeded rule's duration; the marker names the story's firing moment.</summary>
    public const double SignalWindowSeconds = 60;

    public const double StableSeconds = 60;
    public const double DoorSeconds = 6;
    public const double DoorValue = 9.5;
    public const double RampSeconds = 60;
    public const double HoldSeconds = 180;

    public double Baseline { get; }
    public double Peak { get; }
    public double TickRate { get; }

    public WarmupDemo(double baseline, double peak, double tickRate)
    {
        if (tickRate <= 0)
            throw new ArgumentException("warmup-demo: tick rate must be positive");
        if (peak <= baseline)
            throw new ArgumentException("warmup-demo: peak must be above the baseline");

        Baseline = baseline;
        Peak = peak;
        TickRate = tickRate;
    }

    /// <summary>Scripted seconds of a 1-based seq (seq 1 = t 0).</summary>
    public double SecondsAt(long seq) => (seq - 1) / TickRate;

    public string PhaseAt(long seq)
    {
        var t = SecondsAt(seq);
        if (t < StableSeconds) return "stable";
        if (t < StableSeconds + DoorSeconds) return "door";
        if (t < StableSeconds + DoorSeconds + RampSeconds) return "ramp";
        return "hold";
    }

    public double ValueAt(long seq)
    {
        var t = SecondsAt(seq);
        if (t < StableSeconds) return Baseline;
        if (t < StableSeconds + DoorSeconds) return DoorValue;
        if (t < StableSeconds + DoorSeconds + RampSeconds)
        {
            var progress = (t - StableSeconds - DoorSeconds) / RampSeconds;
            return Baseline + (Peak - Baseline) * progress;
        }
        return Peak;
    }

    /// <summary>
    /// The first RAMP sample strictly above <paramref name="threshold"/> — the
    /// crossing the alert story is about. The door beat is deliberately above
    /// the limit and deliberately NOT a crossing (the rule's 60 s window is what
    /// keeps it silent), so the search starts at the ramp and never counts it.
    /// Null when the peak never exceeds the threshold.
    /// </summary>
    public long? FirstAbove(double threshold)
    {
        var start = SeqAtSeconds(StableSeconds + DoorSeconds);
        var end = SeqAtSeconds(StableSeconds + DoorSeconds + RampSeconds);
        for (var seq = start; seq <= end; seq++)
            if (ValueAt(seq) > threshold) return seq;
        return null;
    }

    /// <summary>
    /// The firing sample (D10b): the first seq whose event time is at least
    /// <paramref name="durationSeconds"/> after the crossing sample — what the
    /// rule engine raises on. Null when the profile never crosses.
    /// </summary>
    public long? FirstWindowMet(double threshold, double durationSeconds)
    {
        var first = FirstAbove(threshold);
        if (first is null) return null;

        var required = SecondsAt(first.Value) + durationSeconds;
        var seq = first.Value;
        while (SecondsAt(seq) + 1e-9 < required) seq++;
        while (ValueAt(seq) <= threshold) seq++;
        return seq;
    }

    /// <summary>The final line (05 §10) — the scenario's own label, not dropout's.</summary>
    public static string StopLine(double runForSeconds, long lastSeq) =>
        $"warmup-demo: stopped after {runForSeconds:F0} s, last seq={lastSeq}";

    /// <summary>First 1-based seq at or after the given scripted second.</summary>
    private long SeqAtSeconds(double seconds) => (long)Math.Floor(seconds * TickRate) + 1;
}