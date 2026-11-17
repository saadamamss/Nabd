namespace Nabd.Ingestion;

using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Nabd.Ingestion.Contracts;

/// <summary>
/// The MQTT consumer (P3 spec 02, REWORKED by P4 spec 01, SHARDED by the owner's
/// pressure ruling 2026-11-16): N independent MQTT 5.0 connections join ONE
/// shared subscription group ($share/consumers/…) and all feed the SAME
/// FlushBuffer. P4's law: the delivery handler is O(1) — parse-free, DB-free,
/// ack-FREE. It fills an IngestEnvelope and OFFERS it to the FlushBuffer with
/// the deferred PUBACK as a plain delegate; the FlushPump owns every durable
/// outcome and invokes the ack after the message's outcome commits. The handler
/// never sees a store, an edge, or a reject table — the wall is structural
/// (its constructor has no port to any of them, fact 10).
///
/// Flow control (R5, AMENDED): Receive Maximum is PER CONNECTION (its own knob,
/// default 200) — the effective per-connection window is
/// min(client RM, EMQX mqtt.max_inflight) and the AGGREGATE window is
/// Shards × that. The pressure diagnosis showed why the window must stay
/// SMALL per connection: a jammed 2,000-message backlog saturates the broker's
/// connection process (EMQX kills it on mailbox_overflow or max_heap_size)
/// and the backlog dies with the session. Sharding spreads the fleet so each
/// connection runs far inside its proven zero-drop zone.
///
/// Durability contract (NFR-04 at the MQTT hop): every shard's session is
/// DURABLE and resumable (shard-unique stable client id, CleanStart=false
/// ALWAYS, SessionExpiryInterval 1h): a RESTARTED process resumes each shard's
/// SAME session and the broker redelivers everything queued or in-flight —
/// drill 1's mechanism (spec 07). MQTTnet 5.x carries no reconnect strategy —
/// the jittered backoff ladder is ours per shard (the simulator's shape:
/// double from 1 s, cap 60 s, +jitter); a dead shard reconnects alone while
/// the others keep feeding the buffer.
///
/// Graceful shutdown (R11): on stoppingToken every shard's inflow stops FIRST —
/// unsubscribe (no new deliveries enter the buffer while the PUBACK path stays
/// alive) then disconnect. The pump's ONE bounded final flush runs AFTER this
/// returns (the host orchestrates: consumer stop → FinalFlushAsync).
/// </summary>
public sealed class EmqxIngestConsumer(IConsumerAccess access, FlushBuffer buffer, FlushOptions flush, FlushPump? ackDrain, ILogger logger)
{
    private static readonly TimeSpan BackoffMin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(60);

    /// <summary>The ONE lawful telemetry shape (P2 spec 05) — the raw filter the group wraps.</summary>
    public const string TopicFilter = "t/+/dev/+/v1";

    /// <summary>The shared-subscription group (sharding ruling 2026-11-16): one name, all shards join it.</summary>
    public const string SharedGroup = "consumers";

    /// <summary>
    /// The wire filter EVERY shard subscribes with: MQTT5's shared subscription
    /// — the broker round-robins deliveries across the group's connections, so
    /// no single broker connection process carries the whole fleet (the
    /// pressure diagnosis: one session at full fleet rate saturated its broker
    /// process — mailbox_overflow and max_heap_size kills).
    /// </summary>
    public static readonly string SubscribedFilter = $"$share/{SharedGroup}/{TopicFilter}";

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        // ONE task per shard, each an INDEPENDENT connection with its own
        // client id, its own durable session and its own backoff ladder —
        // a dead shard reconnects alone while the others keep feeding the
        // buffer (the buffer is the shared singleton; the wall is unchanged).
        var shards = Enumerable.Range(1, flush.ConsumerShards)
            .Select(shard => RunShardAsync(shard, stoppingToken));
        await Task.WhenAll(shards);
    }

    private async Task RunShardAsync(int shard, CancellationToken stoppingToken)
    {
        var (host, port) = ParseBroker(
            Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_BROKER") ?? "127.0.0.1:1883");
        var secret = Encoding.UTF8.GetString(access.Secret);
        var clientId = $"{access.Username}-{shard}";   // shard-unique AND stable: each shard resumes ITS OWN session

        var factory = new MqttClientFactory();
        var backoff = BackoffMin;
        while (!stoppingToken.IsCancellationRequested)
        {
            // IMqttClient is IDisposable (not IAsyncDisposable) in MQTTnet 5.x —
            // sync using, and dispose closes the connection. Declared OUTSIDE the
            // try so the shutdown catch can still reach it for the graceful stop.
            using var client = factory.CreateMqttClient();
            client.ApplicationMessageReceivedAsync += OnDeliveryAsync;

            try
            {
                var options = BuildOptions(access, flush.ReceiveMaximum, clientId, host, port, secret);

                await client.ConnectAsync(options, stoppingToken);
                backoff = BackoffMin;                     // connected: reset the ladder

                // Spec 09 official-run finding (third sibling of the spec-07 fixes):
                // a subscribe DENIAL does NOT drop the connection, so the IsConnected
                // poll cannot see it — the old code logged "will retry" and then held
                // a connected-but-UNSUBSCRIBED client while the broker dropped every
                // message as unroutable. The denial is transient by design (the authz
                // door points at the API host, which may boot after EMQX): re-ask the
                // grant until it is given — or until the connection itself dies, and
                // the backoff ladder reconnects.
                var granted = false;
                while (!stoppingToken.IsCancellationRequested && client.IsConnected && !granted)
                {
                    var subscribe = await client.SubscribeAsync(
                        new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(SubscribedFilter, MqttQualityOfServiceLevel.AtLeastOnce)
                            .Build(), stoppingToken);
                    var grant = subscribe.Items.First();
                    granted = grant.ResultCode is (MqttClientSubscribeResultCode.GrantedQoS0
                                                   or MqttClientSubscribeResultCode.GrantedQoS1
                                                   or MqttClientSubscribeResultCode.GrantedQoS2);
                    if (!granted)
                    {
                        logger.LogWarning(
                            "MQTT consumer shard {Shard} subscribe DENIED ({Code}) — the door isn't up yet; re-asking in {Delay}",
                            shard, grant.ResultCode, BackoffMin);
                        await Task.Delay(BackoffMin, stoppingToken);
                    }
                }
                logger.LogInformation(
                    "MQTT consumer shard {Shard} subscribed to {TopicFilter} (Receive Maximum = {ReceiveMaximum})",
                    shard, SubscribedFilter, flush.ReceiveMaximum);

                // Hold the connection open; deliveries arrive through the handler.
                // AFTER (the fix): poll IsConnected — a keepalive death during a broker
                // blackout (drill 2) must wake the backoff ladder.
                while (!stoppingToken.IsCancellationRequested && client.IsConnected)
                    await Task.Delay(500, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    await GracefulStopAsync(client);   // R11: inflow stops FIRST
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // R11 shutdown: stop the inflow BEFORE the pump's final flush —
                // unsubscribe stops new deliveries while the ack path stays alive
                // until disconnect; then the broker re-queues whatever is unacked.
                await GracefulStopAsync(client);
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MQTT consumer connection dropped; reconnecting in {Backoff}",
                    backoff);
            }

            try
            {
                await Task.Delay(backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromMilliseconds(
                Math.Min(backoff.TotalMilliseconds * 2, BackoffMax.TotalMilliseconds)
                + Random.Shared.Next(0, 500));            // jitter: a farm of consumers must not sync
        }
    }

    /// <summary>
    /// The built options, as its own seam (fact 15), AMENDED by the sharding
    /// ruling: Receive Maximum is PER CONNECTION (its own knob now — a jammed
    /// large window made the broker's connection process saturate; 200 per
    /// shard is inside the proven zero-drop zone), and the client id is
    /// shard-unique but STABLE ("nabd-consumer-N") so a restarted process
    /// resumes every shard's own durable session. MQTT5's wire cap is 65535
    /// and FlushOptions already refuses anything outside [1, 65535] (fact 16),
    /// so the cast below can never truncate a lawful value.
    /// </summary>
    public static MqttClientOptions BuildOptions(
        IConsumerAccess access, int receiveMaximum, string clientId, string host, int port, string secret)
    {
        return new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithCredentials(access.Username, secret)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId(clientId)               // the shard's stable identity — a RESTARTED process resumes the SAME session
            .WithCleanSession(false)              // clean start FALSE always — resume-or-create, never discard
            .WithSessionExpiryInterval(3600)      // broker queues QoS1 for 1 h while we're down
            .WithReceiveMaximum((ushort)receiveMaximum)   // R5 (amended): the per-connection window
            .Build();
    }

    /// <summary>
    /// R11's graceful stop, AMENDED by the ack-overlap ruling (2026-11-17):
    /// unsubscribe FIRST (no new deliveries enter the buffer), then DRAIN the
    /// pump's launched PUBACK chunks while EVERY shard's connection is still
    /// open (bounded 3 s — an ack that cannot complete in time fails on the
    /// disconnect and rides the broker's redelivery: idempotent replay, never
    /// a loss), THEN disconnect. Best-effort — a dying connection has nothing
    /// left to unsubscribe from.
    /// </summary>
    private async Task GracefulStopAsync(IMqttClient client)
    {
        try { await client.UnsubscribeAsync(SubscribedFilter); }
        catch { /* the connection may already be gone — disconnect is next */ }
        if (ackDrain is not null)
        {
            try
            {
                using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ackDrain.DrainAcksAsync(bound.Token);
            }
            catch { /* best-effort — the disconnect follows */ }
        }
        try { await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
                .WithReasonString("graceful shutdown — final flush follows").Build()); }
        catch { /* already gone */ }
    }

    /// <summary>
    /// The O(1) delivery handler (fact 10): NO DB, NO edges, NO sink — fill the
    /// envelope, offer it with the deferred PUBACK, return. The pump does the
    /// rest; every durable outcome (store, receipts, acks) is the pump's.
    /// </summary>
    private Task OnDeliveryAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        // Manual ack — the broker's redelivery guarantee is OURS to control:
        // the ack delegate rides INTO the buffer and the PUMP invokes it only
        // after the message's durable outcome commits (the ack law).
        args.AutoAcknowledge = false;
        var message = args.ApplicationMessage;
        var deviceId = ParseDeviceId(message.Topic);
        if (deviceId is null)
        {
            // Unreachable through our own subscription filter; if the broker
            // ever delivers off-shape anyway, log loudly and DROP — a poison
            // topic must not freeze the stream. Counted in the log, never silent.
            logger.LogError("MQTT consumer received off-shape topic {Topic} — dropped", message.Topic);
            _ = args.AcknowledgeAsync(CancellationToken.None);   // dropped, but acked: off-shape has no downstream law
            return Task.CompletedTask;
        }

        var envelope = new IngestEnvelope(
            deviceId.Value,
            message.Topic,
            message.Payload.ToArray(),   // PayloadSegment is setter-only in 5.2 — the ReadOnlySequence is the read path
            DateTimeOffset.UtcNow);
        buffer.Offer(envelope, token => args.AcknowledgeAsync(token));   // the deferred PUBACK rides into the buffer
        return Task.CompletedTask;
    }

    internal static Guid? ParseDeviceId(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return null;
        var segments = topic.Split('/');
        return segments.Length == 5
               && segments[0] == "t" && segments[2] == "dev" && segments[4] == "v1"
               && Guid.TryParse(segments[3], out var id)
            ? id
            : null;
    }

    private static (string Host, int Port) ParseBroker(string address)
    {
        var colon = address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(address[(colon + 1)..], out var port))
            throw new InvalidOperationException(
                $"NABD_MQTT_CONSUMER_BROKER must be host:port — got '{address}'.");
        return (address[..colon], port);
    }
}
