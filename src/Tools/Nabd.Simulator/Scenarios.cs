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

public static class ScenarioRegistry
{
    public static IScenario Create(
        string name,
        IReadOnlyList<(double Value, string Channel)> channels,
        double? spikeTo = null, int? spikeEvery = null, int? spikeHoldFor = null) =>
        name switch
        {
            "steady" => new SteadyScenario(channels),
            "spike" => new SpikeScenario(
                spikeTo ?? throw new ArgumentException("spike requires --spike-to"),
                spikeEvery ?? throw new ArgumentException("spike requires --every"),
                spikeHoldFor ?? throw new ArgumentException("spike requires --hold-for"),
                channels),
            "dropout" => new SteadyScenario(channels),   // same values; the clean stop is --run-for's job
            _ => throw new ArgumentException($"unknown scenario '{name}' (v0 knows: steady, spike, dropout)"),
        };
}