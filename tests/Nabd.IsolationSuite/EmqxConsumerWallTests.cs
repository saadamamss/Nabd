namespace Nabd.IsolationSuite;

using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Nabd.Ingestion;
using Xunit;

// P3 spec 02 — the broker's THIRD door: the MQTT consumer's identity and ACL,
// over REAL HTTP (the EmqxDoorWallTests lane). The consumer is the one server-side
// subscriber: it may authenticate, it may subscribe to the ONE lawful wildcard
// shape, it may never publish — and its deny answers are byte-identical to the
// device deny body (the uniformity law survived the new identity).
// Facts 5/6 judge ConsumerAccess directly (config is pure string logic — no DB);
// facts 1–4 ride the same doors the broker calls.

public sealed class EmqxConsumerWallTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string SuiteSecret = "suite-local-internal-secret";
    private const string ConsumerUser = "nabd-consumer";
    private const string ConsumerSecret = "suite-local-consumer-secret";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public EmqxConsumerWallTests(PostgresFixture pg)
    {
        // The doors' consumer branch reads its identity from env at DI build —
        // set BEFORE the factory materializes (PostgresFixture owns the internal
        // secret; this class owns the consumer identity, restored in Dispose).
        Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_USER", ConsumerUser);
        Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET", ConsumerSecret);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));
        _client = _factory.CreateClient();
        Pg = pg;
    }

    private PostgresFixture Pg { get; }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_USER", null);
        Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET", null);
    }

    private async Task<string> PostAsync(string pathAndQuery, string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(pathAndQuery, body);
        return $"{(int)response.StatusCode}|{await response.Content.ReadAsStringAsync()}";
    }

    // ---- fact 1: the consumer identity authenticates at the auth door ---------

    [Fact]
    public async Task auth_allows_the_consumer_identity()
    {
        var raw = await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","password":"{{ConsumerSecret}}"}""");
        Assert.Equal("200|{\"result\":\"allow\"}", raw);
    }

    // ---- fact 2: a consumer secret miss denies UNIFORMLY -----------------------
    //      byte-identical to the device deny body — no "this was a consumer" hint,
    //      no existence hint. The uniformity law binds every identity at the door.

    [Fact]
    public async Task auth_denies_the_consumer_uniformly_on_wrong_secret()
    {
        var wrongSecret = await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","password":"not-the-consumer-secret"}""");
        var unknownUser = await PostAsync($"/internal/emqx/auth?k={SuiteSecret}",
            $$"""{"username":"nabd-stranger","password":"{{ConsumerSecret}}"}""");

        Assert.Equal("200|{\"result\":\"deny\"}", wrongSecret);
        Assert.Equal(wrongSecret, unknownUser);   // byte-identical: anti-enumeration
    }

    // ---- fact 3: the consumer's subscribe allow-set is EXACTLY one shape -------
    //      t / (+|literal) / dev / (+|literal) / v1 — wildcards ONLY at segments
    //      1 and 3; every other wildcard placement, segment count, or last
    //      segment dies. (The consumer reads device telemetry, nothing else.)

    [Fact]
    public async Task acl_allows_the_consumer_only_the_lawful_wildcard_subscribe()
    {
        var lawful = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"t/+/dev/+/v1","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"allow\"}", lawful);

        var literalShape = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"t/{{Guid.NewGuid()}}/dev/{{Guid.NewGuid()}}/v1","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"allow\"}", literalShape);

        var wrongLastSegment = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"t/+/dev/+/v2","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", wrongLastSegment);

        var wildcardInNameSegment = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"t/+/+/+/v1","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", wildcardInNameSegment);

        var hashTail = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"t/+/dev/+/#","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", hashTail);

        var wrongRoot = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"x/+/dev/+/v1","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", wrongRoot);

        var tooFewSegments = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"t/+/dev/+/v1/extra","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", tooFewSegments);

        var tooManySegments = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"t/+/dev/v1","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", tooManySegments);
    }

    // ---- fact 4: the consumer never publishes; devices never subscribe ---------
    //      (the P2 law re-asserted beside the new identity — one door, two roles,
    //      zero crossings)

    [Fact]
    public async Task acl_denies_consumer_publish_and_device_subscribe()
    {
        var consumerPublish = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{ConsumerUser}}","topic":"t/+/dev/+/v1","action":"publish"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", consumerPublish);

        var deviceSubscribe = await PostAsync($"/internal/emqx/acl?k={SuiteSecret}",
            $$"""{"username":"{{Guid.NewGuid()}}","topic":"t/+/dev/+/v1","action":"subscribe"}""");
        Assert.Equal("200|{\"result\":\"deny\"}", deviceSubscribe);
    }

    // ---- fact 5: the consumer username may never be a Guid ---------------------
    //      Device usernames ARE Guids — a Guid-shaped consumer name would shadow
    //      a real device at the auth door's dispatch. Configuration that tries
    //      refuses to exist.

    [Fact]
    public void consumer_access_refuses_a_guid_shaped_username()
    {
        var originalUser = Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_USER");
        var originalSecret = Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_USER", $"{Guid.NewGuid()}");
            var ex = Record.Exception(() => new ConsumerAccess());
            Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("Guid", ex!.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_USER", originalUser);
            Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET", originalSecret);
        }
    }

    // ---- fact 6: the consumer identity is fail-shut — unconfigured = it refuses
    //      to exist (the InternalSecrets pattern, same law, new door)

    [Fact]
    public void consumer_access_fail_shut_without_env()
    {
        var originalUser = Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_USER");
        var originalSecret = Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_USER", null);
            var ex = Record.Exception(() => new ConsumerAccess());
            Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("NABD_MQTT_CONSUMER_USER", ex!.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_USER", originalUser);
            Environment.SetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET", originalSecret);
        }
    }
}
