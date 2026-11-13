namespace Nabd.Ingestion;

using System.Security.Cryptography;
using System.Text;
using Nabd.Ingestion.Contracts;

/// <summary>
/// The MQTT consumer's identity and access decisions (P3 spec 02) — the broker's
/// THIRD door. Pure configuration/string logic: NO database, NO connection pool.
/// The consumer is not a device and must never look like one: devices authenticate
/// against hashed tokens in Postgres; the consumer authenticates against one env
/// secret read once at startup (the InternalSecrets pattern, same law, new door).
/// </summary>
public sealed class ConsumerAccess : IConsumerAccess
{
    public string Username { get; }
    public byte[] Secret { get; }

    public ConsumerAccess()
    {
        // Fail-shut (NFR-08): unconfigured = the identity refuses to exist —
        // the doors must never half-boot with a half-defined consumer branch.
        Username = Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_USER")
            ?? throw new InvalidOperationException(
                "NABD_MQTT_CONSUMER_USER must be set (fail-shut: the MQTT consumer identity refuses to exist without it, NFR-08).");
        var secret = Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET")
            ?? throw new InvalidOperationException(
                "NABD_MQTT_CONSUMER_SECRET must be set (fail-shut: the MQTT consumer identity refuses to exist without it, NFR-08).");

        // The Guid guard: device usernames ARE Guids — the auth door dispatches on
        // Guid.TryParse. A Guid-shaped consumer name would shadow a real device at
        // the door forever. Configuration that tries refuses to exist.
        if (Guid.TryParse(Username, out _))
            throw new InvalidOperationException(
                "NABD_MQTT_CONSUMER_USER must NOT parse as a Guid (device usernames are Guids; a Guid-shaped consumer name would shadow a real device at the auth door).");

        Secret = Encoding.UTF8.GetBytes(secret);
    }

    public bool IsConsumerUsername(string? username)
        => username is not null && string.Equals(username, Username, StringComparison.Ordinal);

    public bool Authenticate(string username, string? presentedSecret)
    {
        if (!IsConsumerUsername(username)) return false;
        var presented = Encoding.UTF8.GetBytes(presentedSecret ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(presented, Secret);
    }

    public bool AuthorizeSubscribe(string username, string topic)
        => IsConsumerUsername(username) && SubscribeShapeLawful(topic);

    public bool AuthorizePublish(string username, string topic)
        => false;   // the consumer READS. It never speaks on the broker.

    // The consumer's subscribe allow-set is EXACTLY one topic-filter shape:
    //   t / (+|literal) / dev / (+|literal) / v1
    // wildcards permitted ONLY at segments 1 (tenant) and 3 (device) — never in
    // the structural segments, never as '#' (multi-level), never a different
    // segment count. The consumer reads device telemetry, nothing else.
    private static bool SubscribeShapeLawful(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return false;
        var segments = topic.Split('/');
        if (segments.Length != 5) return false;
        if (segments[0] != "t" || segments[2] != "dev" || segments[4] != "v1") return false;
        return SegmentLawful(segments[1]) && SegmentLawful(segments[3]);
    }

    private static bool SegmentLawful(string segment)
        => segment == "+" || (segment.Length > 0 && !segment.Contains('+') && !segment.Contains('#'));
}
