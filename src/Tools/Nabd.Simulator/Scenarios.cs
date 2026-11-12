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

public static class ScenarioRegistry
{
    public static IScenario Create(string name, IReadOnlyList<(double Value, string Channel)> channels) =>
        name switch
        {
            "steady" => new SteadyScenario(channels),
            _ => throw new ArgumentException($"unknown scenario '{name}' (v0 knows: steady)"),
        };
}