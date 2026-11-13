namespace Nabd.Ingestion;

using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using Nabd.Ingestion.Contracts;

/// <summary>
/// The MQTT consumer (P3 spec 02): a server-side MQTT 5.0 client that
/// subscribes to the ONE lawful wildcard shape and hands every delivery to
/// the sink as an IngestEnvelope. Durability contract (NFR-04 at the MQTT
/// hop): ack ONLY after the sink returns; a sink exception leaves the
/// message unacknowledged — the broker redelivers, and the identity key
/// makes the eventual re-store idempotent. The session is DURABLE
/// (CleanStart on first connect only, SessionExpiryInterval 1h): messages
/// published while the consumer is down are queued by the broker, not lost.
/// MQTTnet 5.x carries no reconnect strategy — the jittered backoff ladder
/// is ours (the simulator's shape: double from 1 s, cap 60 s, +jitter).
/// </summary>
public sealed class EmqxIngestConsumer(IConsumerAccess access, IIngestionSink sink, ILogger logger)
{
    private static readonly TimeSpan BackoffMin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(60);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var (host, port) = ParseBroker(
            Environment.GetEnvironmentVariable("NABD_MQTT_CONSUMER_BROKER") ?? "127.0.0.1:1883");
        var secret = Encoding.UTF8.GetString(access.Secret);

        var factory = new MqttClientFactory();
        var backoff = BackoffMin;
        var firstConnect = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // IMqttClient is IDisposable (not IAsyncDisposable) in MQTTnet 5.x —
                // sync using, and dispose closes the connection.
                using var client = factory.CreateMqttClient();
                client.ApplicationMessageReceivedAsync += args => OnDeliveryAsync(args);

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(host, port)
                    .WithCredentials(access.Username, secret)
                    .WithProtocolVersion(MqttProtocolVersion.V500)
                    .WithCleanSession(firstConnect)       // resume the session on reconnects
                    .WithSessionExpiryInterval(3600)      // broker queues QoS1 for 1 h while we're down
                    .Build();

                await client.ConnectAsync(options, stoppingToken);
                firstConnect = false;
                backoff = BackoffMin;                     // connected: reset the ladder

                var subscribe = await client.SubscribeAsync(
                    new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter("t/+/dev/+/v1", MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build(), stoppingToken);
                var grant = subscribe.Items.First();
                if (grant.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0
                                             or MqttClientSubscribeResultCode.GrantedQoS1
                                             or MqttClientSubscribeResultCode.GrantedQoS2))
                {
                    logger.LogWarning(
                        "MQTT consumer subscribe DENIED ({Code}) — check the ACL door; will retry",
                        grant.ResultCode);
                }
                else
                {
                    logger.LogInformation("MQTT consumer subscribed to t/+/dev/+/v1 (granted {Code})",
                        grant.ResultCode);
                }

                // Hold the connection open; deliveries arrive through the handler.
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // shutdown: the client disposes; unacked messages redeliver next boot
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

    private async Task OnDeliveryAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        // Manual ack — the broker's redelivery guarantee is OURS to control:
        // ack ONLY after the sink accepted the envelope (NFR-04 at the MQTT hop).
        args.AutoAcknowledge = false;
        try
        {
            var message = args.ApplicationMessage;
            var deviceId = ParseDeviceId(message.Topic);
            if (deviceId is null)
            {
                // Unreachable through our own subscription filter; if the broker
                // ever delivers off-shape anyway, log loudly and DROP — a poison
                // topic must not freeze the stream. Counted in the log, never silent.
                logger.LogError("MQTT consumer received off-shape topic {Topic} — dropped",
                    message.Topic);
                await args.AcknowledgeAsync(CancellationToken.None);
                return;
            }

            var envelope = new IngestEnvelope(
                deviceId.Value,
                message.Topic,
                message.Payload.ToArray(),   // PayloadSegment is setter-only in 5.2 — the ReadOnlySequence is the read path
                DateTimeOffset.UtcNow);
            await sink.AcceptAsync(envelope, CancellationToken.None);
            await args.AcknowledgeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // NO ack: the broker redelivers (QoS1). The identity key makes the
            // eventual re-store idempotent (plan/05 §6) — redelivery is safe by design.
            logger.LogError(ex,
                "MQTT consumer sink FAILED for topic {Topic} — left unacknowledged for redelivery",
                args.ApplicationMessage.Topic);
        }
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