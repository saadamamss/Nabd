namespace Nabd.Ingestion.Contracts;

/// <summary>
/// The broker's THIRD internal door (P3 spec 02): the server-side MQTT consumer's
/// identity and access decisions. Pure configuration/string logic — NO database.
///
/// Why a separate identity from devices (plan/05 §5): the consumer SUBSCRIBES to
/// every device's topic and must never PUBLISH. Devices are the opposite. One
/// identity space would make "can subscribe" mean "anyone" — so the consumer is
/// its own username, its own secret, its own ACL verdicts, judged by the same
/// always-200 doors the broker already calls (P2 spec 05 contract).
///
/// Fail-shut (NFR-08): the implementation refuses to exist without its env
/// configuration — unconfigured means the doors have no consumer branch at all,
/// never "an empty consumer that denies nothing specially".
///
/// Uniformity law (P2, still binding): deny answers are byte-identical to the
/// device deny body — no existence hints, no "consumer vs device" distinction
/// visible to the broker.
/// </summary>
public interface IConsumerAccess
{
    /// <summary>
    /// True only for the ONE configured consumer username. The configured name
    /// must never parse as a Guid — device usernames ARE Guids, and a Guid-shaped
    /// consumer name would shadow a real device at the auth door's dispatch.
    /// </summary>
    bool IsConsumerUsername(string? username);

    /// <summary>
    /// The consumer's auth verdict: the presented secret must byte-match the
    /// configured secret (constant-time compare — the same law as
    /// ApiHost's InternalSecrets ?k= handler).
    /// </summary>
    bool Authenticate(string username, string? presentedSecret);

    /// <summary>
    /// The consumer's subscribe verdict: lawful shape is EXACTLY the topic-filter
    /// segments <c>t / (+|literal) / dev / (+|literal) / v1</c> — wildcards
    /// permitted ONLY at segments 1 and 3 (tenant and device), never elsewhere,
    /// and no multi-level <c>#</c> anywhere. Anything else is denied: the
    /// consumer reads device telemetry, nothing else in the namespace.
    /// </summary>
    bool AuthorizeSubscribe(string username, string topic);

    /// <summary>
    /// The consumer NEVER publishes (plan/05 §5: it reads). Always false —
    /// the method exists so the ACL door can answer the broker's publish
    /// question for a consumer username without falling through to the
    /// device dispatch.
    /// </summary>
    bool AuthorizePublish(string username, string topic);
}
