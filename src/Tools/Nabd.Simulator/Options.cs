namespace Nabd.Simulator;

public sealed class SimulatorOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string? DeviceId { get; init; }
    public required string? Token { get; init; }
    public required string TenantId { get; init; }
    /// <summary>
    /// The fleet mode (owner ruling 2026-11-17, after the instrumented gate
    /// caught the 120-process fleet itself burning 4.3 of 8 cores on scheduler
    /// overhead while EMQX idled at 71 % of one core): one PROCESS carries many
    /// devices — same doors, same tokens, same topics, one connection each.
    /// </summary>
    public IReadOnlyList<(string DeviceId, string Token)>? Devices { get; init; }
    public string Scenario { get; init; } = "steady";
    public double Rate { get; init; } = 1;
    public double? RunFor { get; init; }        // seconds — the clean-stop bound (null = unbounded)
    public int Dup { get; init; } = 1;          // publish each message k times — the duplicate-flood rider (spec 09)
    public double? SpikeTo { get; init; }
    public int? SpikeEvery { get; init; }
    public int? SpikeHoldFor { get; init; }
    /// <summary>
    /// The shared grid anchor (owner ruling 2026-11-17, after the official
    /// referee failed at 1,998.5/s): the deadline grid initializes at THIS
    /// instant instead of each device's connect moment — the fleet publishes
    /// the profile EXACTLY; each device bursts its owed ticks at connect
    /// (the catch-up law), then runs on the grid. Null = connect-anchored
    /// (the single-device law, unchanged).
    /// </summary>
    public double? StartEpoch { get; init; }
    /// <summary>
    /// P4 spec 10 (D10h): the channel the warmup story warms, and its peak.
    /// Both are warmup-demo's contract; other scenarios ignore them.
    /// </summary>
    public string WarmupChannel { get; init; } = "temperature";
    public double WarmupPeak { get; init; } = 12.0;
    public required IReadOnlyList<(double Value, string Channel)> Channels { get; init; }

    private const string Usage =
        """
        usage: dotnet run --project src/Tools/Nabd.Simulator --
          --broker   host:port      (default localhost:1883)
          --device   <deviceId>     (required — the broker username)
          --token    <plaintext>    (required — the broker password, shown once at mint)
          --tenant   <tenantId>     (required — composes the topic t/{tenant}/dev/{device}/v1)
          --devices-file <path>     (FLEET mode: one process carries many devices;
                                     lines are dev<TAB>token; NOT combinable with --device/--token)
          --scenario steady         (v0 knows: steady, spike, dropout, warmup-demo)
          --rate     <msgs/s>       (default 1)
          --channel  <key>=<value>  (repeatable, at least one required)
          --spike-to <peak>         (spike: the peak value)
          --every    <n>            (spike: a plateau opens every n-th cycle)
          --hold-for <s>            (spike: the plateau lasts s cycles)
          --run-for  <s>            (dropout | warmup-demo: publish for s seconds, then stop CLEANLY)
          --dup      <k>            (publish each message k times — the duplicate-flood rider; default 1)
          --start-epoch <s>         (FLEET: anchor the deadline grid at this unix epoch —
                                     the fleet publishes the profile exactly; default = connect)
        """;

    /// <summary>One fleet line: <c>device<TAB>token</c>, uuid-checked at boot.</summary>
    private static (string DeviceId, string Token) ParseDeviceLine(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length != 2 || parts[1].Length == 0)
            throw new ArgumentException($"--devices-file line must be dev<TAB>token: '{line}'");
        if (!Guid.TryParse(parts[0], out _))
            throw new ArgumentException($"--devices-file device must be the minted deviceId (uuid): '{parts[0]}'");
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// P4 spec 10: warmup-demo's flags are ITS contract (the spec-08 law) —
    /// refuse, never guess. It needs a scripted end, a channel that exists, and
    /// a peak above that channel's baseline; otherwise the story is a lie.
    /// </summary>
    private static void ValidateWarmup(
        string scenario, double? runFor, string warmupChannel, double warmupPeak,
        IReadOnlyList<(double Value, string Channel)> channels)
    {
        if (scenario != "warmup-demo") return;

        if (runFor is null)
            throw new ArgumentException(
                "--scenario warmup-demo requires --run-for (a demo has a scripted end)\n" + Usage);

        var baseline = channels.Where(c => c.Channel == warmupChannel).ToList();
        if (baseline.Count == 0)
            throw new ArgumentException(
                $"--warmup-channel '{warmupChannel}' is not one of the --channel keys\n" + Usage);

        if (warmupPeak <= baseline[0].Value)
            throw new ArgumentException(
                $"--warmup-peak {warmupPeak} must be above the baseline {baseline[0].Value} of '{warmupChannel}'\n" + Usage);
    }

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
        string? devicesFile = null;
        double? startEpoch = null;
        var warmupChannel = "temperature";
        var warmupPeak = 12.0;

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
                case "--devices-file":
                    devicesFile = args[i + 1];
                    break;
                case "--start-epoch":
                    if (!double.TryParse(args[i + 1], out var epochValue) || epochValue < 0)
                        throw new ArgumentException($"--start-epoch must be a non-negative unix epoch in seconds: '{args[i + 1]}'\n{Usage}");
                    startEpoch = epochValue;
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
                case "--warmup-channel":
                    warmupChannel = args[i + 1];
                    break;
                case "--warmup-peak":
                    if (!double.TryParse(args[i + 1], out var warmupPeakValue))
                        throw new ArgumentException($"--warmup-peak must be a number: '{args[i + 1]}'\n{Usage}");
                    warmupPeak = warmupPeakValue;
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}'\n{Usage}");
                }
            }

        if (devicesFile is not null)
        {
            // THE FLEET LAWS: the two cores are mutually exclusive — one mode,
            // never a mix (a mixed boot would silently carry a phantom device).
            if (device is not null || token is not null)
                throw new ArgumentException(
                    "--devices-file cannot combine with --device/--token — pick ONE mode\n" + Usage);
            if (tenant is null)
                throw new ArgumentException("--tenant is required in fleet mode\n" + Usage);

            // The warmup contract holds in fleet mode too (a fleet can warm N fridges).
            ValidateWarmup(scenario, runFor, warmupChannel, warmupPeak, channels);

            // THE FILE: every line dev<TAB>token, uuid-checked at boot — the
            // live-ladder lesson (2026-11-12) applied to the whole fleet.
            List<(string, string)> devices;
            try { devices = [.. File.ReadAllLines(devicesFile)
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .Select(ParseDeviceLine)]; }
            catch (Exception e) when (e is not ArgumentException)
            {
                throw new ArgumentException($"--devices-file is unreadable: {devicesFile} ({e.Message})\n{Usage}");
            }
            if (devices.Count == 0)
                throw new ArgumentException($"--devices-file carries no devices: '{devicesFile}'\n" + Usage);

            return new SimulatorOptions
            {
                Host = host, Port = port, DeviceId = null, Token = null,
                TenantId = tenant, Devices = devices, Scenario = scenario, Rate = rate,
                RunFor = runFor, Dup = dup, SpikeTo = spikeTo,
                SpikeEvery = spikeEvery, SpikeHoldFor = spikeHoldFor,
                WarmupChannel = warmupChannel, WarmupPeak = warmupPeak,
                Channels = channels, StartEpoch = startEpoch,
            };
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
        ValidateWarmup(scenario, runFor, warmupChannel, warmupPeak, channels);

        return new SimulatorOptions
        {
            Host = host, Port = port, DeviceId = device, Token = token,
            TenantId = tenant, Scenario = scenario, Rate = rate,
            RunFor = runFor, Dup = dup, SpikeTo = spikeTo,
            SpikeEvery = spikeEvery, SpikeHoldFor = spikeHoldFor,
            WarmupChannel = warmupChannel, WarmupPeak = warmupPeak,
            Channels = channels, StartEpoch = startEpoch,
        };
    }
}