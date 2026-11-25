using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nabd.Assets.Contracts;
using Nabd.Realtime.Contracts;

namespace Nabd.ApiHost.Realtime;

/// <summary>
/// P5 spec 03 (B4/B5) — the ApiHost's ear on `nabd_events`.
///
/// Every change to the alert row publishes a hint (0028's AFTER trigger, delivered at
/// COMMIT). This worker LISTENs once per process, RE-READS the asset's derived status
/// through <see cref="IAssetStatusReader"/> (the snapshot stays the truth; a dropped
/// hint only means the next one brings the screen up to date), then broadcasts on the
/// two channel constants. A malformed payload is logged and dropped — never thrown
/// into the loop, because one bad row must not silence every tenant.
/// </summary>
public sealed class NabdEventsListener : BackgroundService
{
    private readonly IAssetStatusReader _statuses;
    private readonly IRealtimeBroadcaster _broadcaster;
    private readonly ILogger<NabdEventsListener> _logger;
    private readonly NpgsqlDataSource _dataSource;

    public NabdEventsListener(
        IAssetStatusReader statuses,
        IRealtimeBroadcaster broadcaster,
        ILogger<NabdEventsListener> logger)
    {
        _statuses = statuses;
        _broadcaster = broadcaster;
        _logger = logger;

        var host = Environment.GetEnvironmentVariable("NABD_DB_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("NABD_DB_PORT") ?? "5432";
        var db = Environment.GetEnvironmentVariable("NABD_DB_NAME") ?? "nabd";
        var user = Environment.GetEnvironmentVariable("NABD_APP_DB_USER")
                   ?? throw new InvalidOperationException("NABD_APP_DB_USER must be set (0012).");
        var pass = Environment.GetEnvironmentVariable("NABD_APP_DB_PASSWORD")
                   ?? throw new InvalidOperationException("NABD_APP_DB_PASSWORD must be set (NFR-08).");

        _dataSource = new NpgsqlDataSourceBuilder(
            $"Host={host};Port={port};Database={db};Username={user};Password={pass}").Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenOnceAsync(stoppingToken);
                return;            // only a cancelled WaitAsync returns here
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e,
                    "nabd_events listener lost the database; retrying in {Delay}", backoff);
                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task ListenOnceAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        conn.Notification += (_, args) => queue.Writer.TryWrite(args.Payload);

        await using (var listen = new NpgsqlCommand("LISTEN nabd_events", conn))
            await listen.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("LISTEN nabd_events — realtime hints are live");

        // One dedicated reader for the wire; the handler drains it in order.
        var wire = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutdown
            }
            finally
            {
                queue.Writer.TryComplete();
            }
        }, ct);

        try
        {
            await foreach (var payload in queue.Reader.ReadAllAsync(ct))
                await HandlePayloadAsync(payload, ct);
            await wire;
        }
        finally
        {
            queue.Writer.TryComplete();
        }
    }

    /// <summary>
    /// One hint → one re-read → at most two broadcasts. Public so the suite can pin
    /// the contract without a socket: malformed input never throws.
    /// </summary>
    public async Task HandlePayloadAsync(string? payload, CancellationToken ct = default)
    {
        var hint = Parse(payload);
        if (hint is null)
        {
            _logger.LogWarning("dropping a malformed nabd_events payload");
            return;
        }

        try
        {
            var status = await _statuses.GetForTenantAsync(hint.TenantId, hint.AssetId, ct);
            if (status is null)
            {
                // The asset vanished (or the hint lied): there is nothing honest to show.
                _logger.LogDebug("no status for asset {AssetId}; hint dropped", hint.AssetId);
                return;
            }

            await _broadcaster.BroadcastToTenantAsync(
                hint.TenantId, RealtimeChannels.AssetStatus, status, ct);

            await _broadcaster.BroadcastToTenantAsync(
                hint.TenantId, RealtimeChannels.Alerts,
                new
                {
                    kind = hint.Kind,
                    alert = new AlertChangeHint(
                        hint.AlertId, hint.AssetId, hint.Severity, hint.AlertStatus, hint.Since),
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The durable outcome already happened; a push failure is a lost hint, not a lost alert.
            _logger.LogError(e, "status re-read/broadcast failed for asset {AssetId}", hint.AssetId);
        }
    }

    private static EventHint? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!TryGuid(root, "tenantId", out var tenantId)
                || !TryGuid(root, "assetId", out var assetId)
                || !TryGuid(root, "alertId", out var alertId))
                return null;

            if (!root.TryGetProperty("kind", out var kindNode)
                || kindNode.ValueKind != JsonValueKind.String)
                return null;
            var kind = kindNode.GetString();
            if (kind is not ("raised" or "changed")) return null;

            // The trigger always writes severity/status/since. A hint without them
            // is malformed — enriching it from defaults would put invented facts on
            // the wire (`warn`/`open`/now), which the UI must never receive.
            var severity = StringOr(root, "severity");
            var alertStatus = StringOr(root, "status");
            if (severity is null || alertStatus is null) return null;

            if (!root.TryGetProperty("since", out var sinceNode)
                || sinceNode.ValueKind != JsonValueKind.String
                || !sinceNode.TryGetDateTimeOffset(out var since))
                return null;

            return new EventHint(tenantId, assetId, alertId, kind, severity, alertStatus, since);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var node)
               && node.ValueKind == JsonValueKind.String
               && Guid.TryParse(node.GetString(), out value);
    }

    private static string? StringOr(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private sealed record EventHint(
        Guid TenantId, Guid AssetId, Guid AlertId,
        string Kind, string Severity, string AlertStatus, DateTimeOffset Since);

    public override void Dispose()
    {
        _dataSource.Dispose();
        base.Dispose();
    }
}
