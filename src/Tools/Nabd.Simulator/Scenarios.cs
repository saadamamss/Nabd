namespace Nabd.Simulator;

public interface IScenario
{
    /// <summary>One tick = the readings for one message (a value per channel).</summary>
    IReadOnlyList<(double Value, string Channel)> Tick(long seq);
}

public sealed class SteadyScenario : IScenario
{
    private readonly IReadOnlyList<(double Value, string Channel)> _channels;

    public SteadyScenario(IReadOnlyList<(double Value, string Channel)> channels)
        => _channels = channels;

    public IReadOnlyList<(double Value, string Channel)> Tick(long seq) => _channels;
}

public sealed class SpikeScenario(
    double peak, int every, int holdFor,
    IReadOnlyList<(double Value, string Channel)> channels) : IScenario
{
    public IReadOnlyList<(double Value, string Channel)> Tick(long seq)
    {
        var rem = (seq - 1) % every;
        if (rem < holdFor)
        {
            return channels.Select(c => (peak, c.Channel)).ToList();
        }
        return channels;
    }
}

/// <summary>
/// P4 spec 10 (D10h): the fridge-warming launch story. One channel follows the
/// pure <see cref="WarmupDemo"/> profile; every other channel rides constant.
/// The profile is rate-aware, so the same 306 scripted seconds exist at 1/s
/// and at 5/s — only the sample count changes.
/// </summary>
public sealed class WarmupDemoScenario : IScenario
{
    private readonly IReadOnlyList<(double Value, string Channel)> _channels;
    private readonly string _channel;

    public WarmupDemo Profile { get; }

    public WarmupDemoScenario(
        string channel, double peak, double tickRate,
        IReadOnlyList<(double Value, string Channel)> channels)
    {
        var baseline = channels.Where(c => c.Channel == channel).ToList();
        if (baseline.Count == 0)
            throw new ArgumentException($"--warmup-channel '{channel}' is not one of the --channel keys");

        _channel = channel;
        _channels = channels;
        Profile = new WarmupDemo(baseline[0].Value, peak, tickRate);
    }

    public IReadOnlyList<(double Value, string Channel)> Tick(long seq)
    {
        var value = Profile.ValueAt(seq);
        var readings = new List<(double Value, string Channel)>(_channels.Count);
        foreach (var channel in _channels)
            readings.Add((channel.Channel == _channel ? value : channel.Value, channel.Channel));
        return readings;
    }
}

public static class ScenarioRegistry
{
    public static IScenario Create(
        string name,
        IReadOnlyList<(double Value, string Channel)> channels,
        double? spikeTo = null, int? spikeEvery = null, int? spikeHoldFor = null,
        double tickRate = 1, string warmupChannel = "temperature", double warmupPeak = 12.0) =>
        name switch
        {
            "steady" => new SteadyScenario(channels),
            "spike" => new SpikeScenario(
                spikeTo ?? throw new ArgumentException("spike requires --spike-to"),
                spikeEvery ?? throw new ArgumentException("spike requires --every"),
                spikeHoldFor ?? throw new ArgumentException("spike requires --hold-for"),
                channels),
            "dropout" => new SteadyScenario(channels),   // same values; the clean stop is --run-for's job
            "warmup-demo" => new WarmupDemoScenario(warmupChannel, warmupPeak, tickRate, channels),
            _ => throw new ArgumentException(
                $"unknown scenario '{name}' (v0 knows: steady, spike, dropout, warmup-demo)"),
        };
}