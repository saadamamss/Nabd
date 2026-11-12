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
    public required IReadOnlyList<(double Value, string Channel)> Channels { get; init; }

    private const string Usage =
        """
        usage: dotnet run --project src/Tools/Nabd.Simulator --
          --broker   host:port      (default localhost:1883)
          --device   <deviceId>     (required — the broker username)
          --token    <plaintext>    (required — the broker password, shown once at mint)
          --tenant   <tenantId>     (required — composes the topic t/{tenant}/dev/{device}/v1)
          --scenario steady         (v0 knows: steady)
          --rate     <msgs/s>       (default 1)
          --channel  <key>=<value>  (repeatable, at least one required)
        """;

    public static SimulatorOptions Parse(string[] args)
    {
        string host = "localhost";
        var port = 1883;
        string? device = null, token = null, tenant = null, scenario = "steady";
        double rate = 1;
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

        return new SimulatorOptions
        {
            Host = host, Port = port, DeviceId = device, Token = token,
            TenantId = tenant, Scenario = scenario, Rate = rate,
            Channels = channels,
        };
    }
}