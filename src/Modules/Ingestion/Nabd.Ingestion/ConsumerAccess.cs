namespace Nabd.Ingestion;

using System.Text;
using Nabd.Ingestion.Contracts;

/// <summary>
/// RED STUB (P3 spec 02 scaffolding): fail-shut env reads only — every access
/// verdict is false until the owner's real implementation replaces it from
/// study/implementation-code.md. What survives into the real code unchanged:
/// the ctor's fail-shut shape (unconfigured = refuse to exist, NFR-08) and
/// the "read config once at startup" law (the same pattern as ApiHost's
/// InternalSecrets). The owner's implementation adds the Guid-username guard
/// and the real verdicts on top of exactly this skeleton.
/// </summary>
public sealed class ConsumerAccess : IConsumerAccess
{
    public string Username { get; }
    public byte[] Secret { get; }

    public ConsumerAccess()
    {
        // Fail-shut: the consumer identity refuses to exist unconfigured —
        // the doors must never half-boot with a half-defined consumer branch.
        Username = Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_USER")
            ?? throw new InvalidOperationException(
                "NABD_MQTT_CONSUMER_USER must be set (fail-shut: the MQTT consumer identity refuses to exist without it, NFR-08).");
        var secret = Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_SECRET")
            ?? throw new InvalidOperationException(
                "NABD_MQTT_CONSUMER_SECRET must be set (fail-shut: the MQTT consumer identity refuses to exist without it, NFR-08).");
        Secret = Encoding.UTF8.GetBytes(secret);
    }

    public bool IsConsumerUsername(string? username) => false;

    public bool Authenticate(string username, string? presentedSecret) => false;

    public bool AuthorizeSubscribe(string username, string topic) => false;

    public bool AuthorizePublish(string username, string topic) => false;
}
