namespace Nabd.Simulator;

public sealed class SimulatorOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string DeviceId { get; init; }
    public required string Token { get; init; }
    public required string TenantId { get; init; }
    public string Scenario { get; init; } = "steady";
    public double Rate { get; init; } = 1;
    public double? RunFor { get; init; }        // seconds — the clean-stop bound (null = unbounded)
    public int Dup { get; init; } = 1;          // publish each message k times — the duplicate-flood rider (spec 09)
    public double? SpikeTo { get; init; }
    public int? SpikeEvery { get; init; }
    public int? SpikeHoldFor { get; init; }
    public required IReadOnlyList<(double Value, string Channel)> Channels { get; init; }

    private const string Usage =
        """
        usage: dotnet run --project src/Tools/Nabd.Simulator --
          --broker   host:port      (default localhost:1883)
          --device   <deviceId>     (required — the broker username)
          --token    <plaintext>    (required — the broker password, shown once at mint)
          --tenant   <tenantId>     (required — composes the topic t/{tenant}/dev/{device}/v1)
          --scenario steady         (v0 knows: steady, spike, dropout)
          --rate     <msgs/s>       (default 1)
          --channel  <key>=<value>  (repeatable, at least one required)
          --spike-to <peak>         (spike: the peak value)
          --every    <n>            (spike: a plateau opens every n-th cycle)
          --hold-for <s>            (spike: the plateau lasts s cycles)
          --run-for  <s>            (dropout: publish for s seconds, then stop CLEANLY)
          --dup      <k>            (publish each message k times — the duplicate-flood rider; default 1)
        """;

    public static SimulatorOptions Parse(string[] args)
    {
        string host = "localhost";
        var port = 1883;
        string? device = null, token = null, tenant = null, scenario = "steady";
        double rate = 1;
        double? runFor = null, spikeTo = null;
        int? spikeEvery = null, spikeHoldFor = null;
        var dup = 1;
        var channels = new List<(double Value, string Channel)>();

        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"missing value for {args[i]}\n{Usage}");

            switch (args[i])
            {
                case "--broker":
                    var parts = args[i + 1].Split(':', 2);
                    host = parts[0];
                    if (parts.Length == 2 && !int.TryParse(parts[1], out port))
                        throw new ArgumentException($"--broker port is not a number: '{args[i + 1]}'\n{Usage}");
                    break;
                case "--device": device = args[i + 1]; break;
                case "--token": token = args[i + 1]; break;
                case "--tenant": tenant = args[i + 1]; break;
                case "--scenario": scenario = args[i + 1]; break;
                case "--rate":
                    if (!double.TryParse(args[i + 1], out rate) || rate <= 0)
                        throw new ArgumentException($"--rate must be a positive number: '{args[i + 1]}'\n{Usage}");
                    break;
                case "--channel":
                    var kv = args[i + 1].Split('=', 2);
                    if (kv.Length != 2 || !double.TryParse(kv[1], out var value))
                        throw new ArgumentException($"--channel must be key=number: '{args[i + 1]}'\n{Usage}");
                    channels.Add((value, kv[0]));
                    break;
                case "--run-for":
                    if (!double.TryParse(args[i + 1], out var runForValue) || runForValue <= 0)
                        throw new ArgumentException($"--run-for must be a positive number of seconds: '{args[i + 1]}'\n{Usage}");
                    runFor = runForValue;
                    break;
                case "--dup":
                    if (!int.TryParse(args[i + 1], out var dupValue) || dupValue <= 0)
                        throw new ArgumentException($"--dup must be a positive integer: '{args[i + 1]}'\n{Usage}");
                    dup = dupValue;
                    break;
                case "--spike-to":
                    if (!double.TryParse(args[i + 1], out var spikeToValue))
                        throw new ArgumentException($"--spike-to must be a number: '{args[i + 1]}'\n{Usage}");
                    spikeTo = spikeToValue;
                    break;
                case "--every":
                    if (!int.TryParse(args[i + 1], out var everyValue) || everyValue <= 0)
                        throw new ArgumentException($"--every must be a positive integer: '{args[i + 1]}'\n{Usage}");
                    spikeEvery = everyValue;
                    break;
                case "--hold-for":
                    if (!int.TryParse(args[i + 1], out var holdForValue) || holdForValue <= 0)
                        throw new ArgumentException($"--hold-for must be a positive integer: '{args[i + 1]}'\n{Usage}");
                    spikeHoldFor = holdForValue;
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}'\n{Usage}");
                }
            }

        if (device is null || token is null || tenant is null)
            throw new ArgumentException("--device, --token and --tenant are required\n" + Usage);
        if (!Guid.TryParse(device, out _))
            throw new ArgumentException($"--device must be the minted deviceId (uuid): '{device}'\n{Usage}");
        if (!Guid.TryParse(tenant, out _))
            throw new ArgumentException($"--tenant must be the tenantId (uuid): '{tenant}'\n{Usage}");
        if (channels.Count == 0)
            throw new ArgumentException("at least one --channel is required\n" + Usage);
        // P3 spec 08: a scenario's flags are ITS contract — refuse, never guess.
        if (scenario == "spike" && (spikeTo is null || spikeEvery is null || spikeHoldFor is null))
            throw new ArgumentException(
                "--scenario spike requires --spike-to, --every and --hold-for\n" + Usage);
        if (scenario == "dropout" && runFor is null)
            throw new ArgumentException("--scenario dropout requires --run-for\n" + Usage);

        return new SimulatorOptions
        {
            Host = host, Port = port, DeviceId = device, Token = token,
            TenantId = tenant, Scenario = scenario, Rate = rate,
            RunFor = runFor, Dup = dup, SpikeTo = spikeTo,
            SpikeEvery = spikeEvery, SpikeHoldFor = spikeHoldFor,
            Channels = channels,
        };
    }
}