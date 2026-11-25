namespace Nabd.IsolationSuite;

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nabd.Alerting.Contracts;
using Nabd.ApiHost.Realtime;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Realtime.Contracts;
using Xunit;

// P5 spec 03 — the status truth (FR-40, plan/03 §3, plan/06 §5.1).
//
// This net pins the SQL derivation (0028) and the two lanes of the read model.
// The rules are B1/B2 (derived, never a stored column), B7 (offline > critical >
// warn/info, acknowledged still counts) and B12 (`muted` is a notification fact,
// so a quiet-hours crossing still shows its real severity).
//
// Seeding: tenants come through the REAL registration door; assets/devices/alerts
// are inserted through the OWNER lane (templates and alert rows are fixtures —
// test bookkeeping, the same class of act as the fixture's ALTER ROLE passwords).
// The app role is exercised by the reader itself, so its grant is proven too.

public sealed class AssetStatusTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private Guid? _templateId;

    private async Task<Guid> TemplateAsync()
    {
        if (_templateId is { } known) return known;

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
            VALUES (@k, 'قالب حالة', 'Status Template')
            RETURNING id
            """, conn);
        cmd.Parameters.AddWithValue("k", $"status-{Uid()}");
        _templateId = (Guid)(await cmd.ExecuteScalarAsync())!;
        return _templateId.Value;
    }

    private async Task<Guid> TenantAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Status {label}",
            Slug: $"st-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@st.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value.Tenant.TenantId;
    }

    private async Task<Guid> AssetAsync(Guid tenantId, string name, bool active = true)
    {
        var template = await TemplateAsync();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO assets.assets (id, tenant_id, template_id, name, is_active)
            VALUES (@id, @tenant, @template, @name, @active)
            RETURNING id
            """, conn);
        var id = Guid.NewGuid();
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("template", template);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("active", active);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> DeviceAsync(Guid tenantId, Guid assetId, DateTimeOffset? lastSeen)
    {
        var template = await TemplateAsync();
        var id = Guid.NewGuid();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO assets.devices (id, tenant_id, asset_id, template_id, name, token_hash)
            VALUES (@id, @tenant, @asset, @template, 'Probe', @hash);
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("asset", assetId);
        cmd.Parameters.AddWithValue("template", template);
        cmd.Parameters.AddWithValue("hash", $"hash-{id:N}");
        await cmd.ExecuteNonQueryAsync();

        if (lastSeen is { } seen)
        {
            await using var status = new NpgsqlCommand("""
                INSERT INTO telemetry.device_status (device_id, tenant_id, last_seen_at, last_event_time)
                VALUES (@id, @tenant, @seen, @seen)
                """, conn);
            status.Parameters.AddWithValue("id", id);
            status.Parameters.AddWithValue("tenant", tenantId);
            status.Parameters.AddWithValue("seen", seen.UtcDateTime);
            await status.ExecuteNonQueryAsync();
        }

        return id;
    }

    private async Task<Guid> AlertAsync(
        Guid tenantId, Guid assetId, string type, string severity, string status,
        DateTimeOffset openedAt, Guid? ruleId = null)
    {
        var id = Guid.NewGuid();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO alerting.alerts
                (id, tenant_id, asset_id, rule_id, type, severity, status,
                 opened_at, acknowledged_at, acknowledged_by, resolved_at, title, detail)
            VALUES
                (@id, @tenant, @asset, @rule, @type, @severity, @status,
                 @opened, CASE WHEN @status = 'acknowledged' THEN @opened END,
                 CASE WHEN @status = 'acknowledged' THEN @user END,
                 CASE WHEN @status = 'resolved' THEN @opened END,
                 '{"en":"Status","ar":"حالة"}'::jsonb,
                 '{"value":10,"channelKey":"temp","ruleType":"threshold","deviceId":"00000000-0000-0000-0000-000000000000"}'::jsonb)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("asset", assetId);
        cmd.Parameters.AddWithValue("rule", ruleId ?? Guid.NewGuid());
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("severity", severity);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("opened", openedAt.UtcDateTime);
        cmd.Parameters.AddWithValue("user", Guid.NewGuid());
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static CallerContext AdminOf(Guid tenantId) =>
        new(Guid.NewGuid(), tenantId, ["tenant_admin"]);

    private static async Task<NpgsqlConnection> OpenRawAsync(PostgresFixture pg)
    {
        var conn = (NpgsqlConnection)await pg.Owner.OpenConnectionAsync();
        return conn;
    }

    private async Task<AssetStatusSnapshot?> OneAsync(Guid tenantId, Guid assetId) =>
        await new AssetStatusReader(pg.Assets).GetForTenantAsync(tenantId, assetId);

    // ---- fact 1: the derivation matrix — four states, nothing else -------------

    [Fact]
    public async Task the_derivation_matrix_is_exactly_four_states()
    {
        var tenant = await TenantAsync("matrix");
        var healthy = await AssetAsync(tenant, "healthy");
        var offline = await AssetAsync(tenant, "offline");
        var alert = await AssetAsync(tenant, "alert");
        var warning = await AssetAsync(tenant, "warning");
        var info = await AssetAsync(tenant, "info");
        var acknowledged = await AssetAsync(tenant, "ack");
        var resolvedOnly = await AssetAsync(tenant, "resolved");

        await AlertAsync(tenant, offline, AlertTypes.Offline, AlertSeverities.Warn, AlertStatuses.Open, T0);
        await AlertAsync(tenant, alert, AlertTypes.Threshold, AlertSeverities.Critical, AlertStatuses.Open, T0);
        await AlertAsync(tenant, warning, AlertTypes.Threshold, AlertSeverities.Warn, AlertStatuses.Open, T0);
        await AlertAsync(tenant, info, AlertTypes.Threshold, AlertSeverities.Info, AlertStatuses.Open, T0);
        await AlertAsync(tenant, acknowledged, AlertTypes.Threshold, AlertSeverities.Critical, AlertStatuses.Acknowledged, T0);
        await AlertAsync(tenant, resolvedOnly, AlertTypes.Threshold, AlertSeverities.Critical, AlertStatuses.Resolved, T0);

        Assert.Equal(AssetStatuses.Healthy, (await OneAsync(tenant, healthy))!.Status);
        Assert.Equal(AssetStatuses.Offline, (await OneAsync(tenant, offline))!.Status);
        Assert.Equal(AssetStatuses.Alert, (await OneAsync(tenant, alert))!.Status);
        Assert.Equal(AssetStatuses.Warning, (await OneAsync(tenant, warning))!.Status);
        Assert.Equal(AssetStatuses.Warning, (await OneAsync(tenant, info))!.Status);
        Assert.Equal(AssetStatuses.Alert, (await OneAsync(tenant, acknowledged))!.Status);
        Assert.Equal(AssetStatuses.Healthy, (await OneAsync(tenant, resolvedOnly))!.Status);

        // healthy means: no winning alert, so `since`/`alertId`/`severity` are null —
        // the UI must not show a "since" it cannot explain.
        var clean = (await OneAsync(tenant, healthy))!;
        Assert.Null(clean.Since);
        Assert.Null(clean.AlertId);
        Assert.Null(clean.Severity);

        var live = (await OneAsync(tenant, alert))!;
        Assert.Equal(T0, live.Since);
        Assert.Equal(AlertSeverities.Critical, live.Severity);
        Assert.NotNull(live.AlertId);

        // and the four words are the whole vocabulary (B12: no `muted`)
        var list = await new AssetStatusReader(pg.Assets)
            .ListAsync(AdminOf(tenant));
        Assert.True(list.IsSuccess);
        Assert.All(list.Value, row => Assert.Contains(row.Status, AssetStatuses.All));
        Assert.DoesNotContain("muted", AssetStatuses.All);
    }

    // ---- fact 2: precedence is deterministic ----------------------------------

    [Fact]
    public async Task offline_beats_critical_and_critical_beats_warn_even_when_older()
    {
        var tenant = await TenantAsync("prec");

        var both = await AssetAsync(tenant, "offline+critical");
        await AlertAsync(tenant, both, AlertTypes.Threshold, AlertSeverities.Critical, AlertStatuses.Open, T0.AddMinutes(5));
        await AlertAsync(tenant, both, AlertTypes.Offline, AlertSeverities.Warn, AlertStatuses.Open, T0);

        var criticalAndWarn = await AssetAsync(tenant, "critical+warn");
        await AlertAsync(tenant, criticalAndWarn, AlertTypes.Threshold, AlertSeverities.Warn, AlertStatuses.Open, T0.AddMinutes(9));
        await AlertAsync(tenant, criticalAndWarn, AlertTypes.Threshold, AlertSeverities.Critical, AlertStatuses.Open, T0);

        // newest within a class wins (two open warns: the later one is `since`)
        var twoWarns = await AssetAsync(tenant, "two warns");
        await AlertAsync(tenant, twoWarns, AlertTypes.Threshold, AlertSeverities.Warn, AlertStatuses.Open, T0);
        await AlertAsync(tenant, twoWarns, AlertTypes.Threshold, AlertSeverities.Warn, AlertStatuses.Open, T0.AddMinutes(2));

        var a = (await OneAsync(tenant, both))!;
        Assert.Equal(AssetStatuses.Offline, a.Status);
        Assert.Equal(AlertSeverities.Warn, a.Severity);   // the winning row's own severity

        var b = (await OneAsync(tenant, criticalAndWarn))!;
        Assert.Equal(AssetStatuses.Alert, b.Status);
        Assert.Equal(T0, b.Since);

        var c = (await OneAsync(tenant, twoWarns))!;
        Assert.Equal(AssetStatuses.Warning, c.Status);
        Assert.Equal(T0.AddMinutes(2), c.Since);
    }

    // ---- fact 3: acknowledged is still a live problem --------------------------

    [Fact]
    public async Task acknowledging_an_alert_does_not_turn_the_asset_healthy()
    {
        var tenant = await TenantAsync("ack");
        var asset = await AssetAsync(tenant, "ack");

        var alertId = await AlertAsync(
            tenant, asset, AlertTypes.Threshold, AlertSeverities.Critical,
            AlertStatuses.Acknowledged, T0);

        var row = (await OneAsync(tenant, asset))!;
        Assert.Equal(AssetStatuses.Alert, row.Status);
        Assert.Equal(alertId, row.AlertId);
        Assert.Equal(T0, row.Since);
    }

    // ---- fact 3b: quiet hours are a notification fact, never a status ----------

    [Fact]
    public async Task a_crossing_inside_a_quiet_window_still_shows_its_real_status()
    {
        var tenant = await TenantAsync("mute");
        var asset = await AssetAsync(tenant, "muted crossing");
        var alertId = await AlertAsync(
            tenant, asset, AlertTypes.Threshold, AlertSeverities.Warn, AlertStatuses.Open, T0);

        await using (var conn = await OpenRawAsync(pg))
        {
            await using var evt = new NpgsqlCommand("""
                INSERT INTO alerting.alert_events (tenant_id, alert_id, event, reason, at)
                VALUES (@tenant, @alert, 'suppressed', 'mute_window', @at)
                """, conn);
            evt.Parameters.AddWithValue("tenant", tenant);
            evt.Parameters.AddWithValue("alert", alertId);
            evt.Parameters.AddWithValue("at", T0.UtcDateTime);
            await evt.ExecuteNonQueryAsync();
        }

        var row = (await OneAsync(tenant, asset))!;
        Assert.Equal(AssetStatuses.Warning, row.Status);
        Assert.Equal(alertId, row.AlertId);
    }

    // ---- fact 4/5: the function is tenant-scoped by its own argument ------------

    [Fact]
    public async Task the_function_returns_zero_rows_for_another_tenant()
    {
        var a = await TenantAsync("func-a");
        var b = await TenantAsync("func-b");
        await AssetAsync(a, "A's asset");

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM assets.asset_status_by_tenant(@t)", conn);
        cmd.Parameters.AddWithValue("t", b);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    // ---- fact: the app grant is real, PUBLIC's is not ---------------------------

    [Fact]
    public async Task the_function_is_executable_by_the_app_role_only()
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        // `proacl IS NULL` means the DEFAULT acl applies — and a function's default
        // is EXECUTE to PUBLIC. Coalescing with acldefault keeps the assertion
        // unable to false-pass if the REVOKE is ever deleted.
        await using var cmd = new NpgsqlCommand("""
            SELECT has_function_privilege(
                       'nabd_app', 'assets.asset_status_by_tenant(uuid)', 'EXECUTE'),
                   EXISTS (
                       SELECT 1
                         FROM pg_proc p,
                              aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                        WHERE p.oid = 'assets.asset_status_by_tenant(uuid)'::regprocedure
                          AND a.grantee = 0)
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    // ---- fact 6: archived filtering belongs to the door lane --------------------

    [Fact]
    public async Task archived_assets_are_excluded_by_default_and_keep_their_status()
    {
        var tenant = await TenantAsync("arch");
        var alive = await AssetAsync(tenant, "alive");
        var archived = await AssetAsync(tenant, "archived", active: false);
        await AlertAsync(tenant, archived, AlertTypes.Threshold, AlertSeverities.Warn, AlertStatuses.Open, T0);

        var reader = new AssetStatusReader(pg.Assets);
        var admin = AdminOf(tenant);

        var defaultList = await reader.ListAsync(admin);
        Assert.True(defaultList.IsSuccess);
        Assert.Contains(defaultList.Value, r => r.AssetId == alive);
        Assert.DoesNotContain(defaultList.Value, r => r.AssetId == archived);

        var withArchived = await reader.ListAsync(admin, includeArchived: true);
        Assert.True(withArchived.IsSuccess);
        var row = Assert.Single(withArchived.Value, r => r.AssetId == archived);
        Assert.False(row.IsActive);
        Assert.Equal(AssetStatuses.Warning, row.Status);
    }

    // ---- fact 7: last seen is the newest device, and null is honest ------------

    [Fact]
    public async Task last_seen_is_the_newest_device_status_over_the_assets_devices()
    {
        var tenant = await TenantAsync("seen");
        var asset = await AssetAsync(tenant, "two probes");
        await DeviceAsync(tenant, asset, T0);
        await DeviceAsync(tenant, asset, T0.AddMinutes(7));

        var silent = await AssetAsync(tenant, "no probes");

        var row = (await OneAsync(tenant, asset))!;
        Assert.Equal(T0.AddMinutes(7), row.LastSeenAt);

        // no reading has ever arrived: null, never "now" (the UI says "no readings yet")
        Assert.Null((await OneAsync(tenant, silent))!.LastSeenAt);
    }

    // ---- fact 8: the door lane asserts the matrix (layer 2, no HTTP) -----------

    [Fact]
    public async Task the_reader_refuses_a_role_without_asset_read()
    {
        var tenant = await TenantAsync("assert");
        var nobody = new CallerContext(Guid.NewGuid(), tenant, ["no_such_role"]);

        var refused = await new AssetStatusReader(pg.Assets).ListAsync(nobody);
        Assert.False(refused.IsSuccess);
        Assert.Equal(IdentityErrors.Forbidden, refused.Error);
    }

    // ---- fact 9: the trigger notifies at COMMIT, not at INSERT ----------------

    [Fact]
    public async Task the_alert_trigger_publishes_once_at_commit()
    {
        var tenant = await TenantAsync("notify");
        var asset = await AssetAsync(tenant, "notified");

        await using var listener = await pg.Owner.OpenConnectionAsync();
        await using (var listen = new NpgsqlCommand("LISTEN nabd_events", listener))
            await listen.ExecuteNonQueryAsync();

        var payloads = new List<string>();
        listener.Notification += (_, args) => payloads.Add(args.Payload);

        await using var writer = await pg.Owner.OpenConnectionAsync();
        await using var tx = await writer.BeginTransactionAsync();

        var alertId = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO alerting.alerts
                (id, tenant_id, asset_id, rule_id, type, severity, status, opened_at, title, detail)
            VALUES (@id, @tenant, @asset, @rule, 'threshold', 'critical', 'open', @at,
                    '{"en":"T","ar":"ت"}'::jsonb, '{}'::jsonb)
            """, writer, tx))
        {
            ins.Parameters.AddWithValue("id", alertId);
            ins.Parameters.AddWithValue("tenant", tenant);
            ins.Parameters.AddWithValue("asset", asset);
            ins.Parameters.AddWithValue("rule", Guid.NewGuid());
            ins.Parameters.AddWithValue("at", T0.UtcDateTime);
            await ins.ExecuteNonQueryAsync();
        }

        // BEFORE commit the LISTEN my process receives nothing (a hint must never
        // describe a write that could still roll back)
        var beforeCommit = await listener.WaitAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None);
        Assert.False(beforeCommit);

        await tx.CommitAsync();

        var received = await listener.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(received, "no NOTIFY arrived after commit");
        var payload = Assert.Single(payloads);
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal(tenant, doc.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(asset, doc.RootElement.GetProperty("assetId").GetGuid());
        Assert.Equal(alertId, doc.RootElement.GetProperty("alertId").GetGuid());
        Assert.Equal("raised", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("critical", doc.RootElement.GetProperty("severity").GetString());
    }

    // ---- fact 10: NOTIFY → one re-read → the two channel constants -------------

    [Fact]
    public async Task a_hint_rereads_the_asset_once_and_broadcasts_both_channels()
    {
        var tenant = await TenantAsync("hint");
        var asset = await AssetAsync(tenant, "hinted");
        var reader = new CountingReader();
        var broadcaster = new RecordingBroadcaster();
        using var listener = new NabdEventsListener(
            reader, broadcaster, NullLogger<NabdEventsListener>.Instance);

        var alertId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            tenantId = tenant,
            assetId = asset,
            alertId,
            kind = "changed",
            severity = "warn",
            status = "acknowledged",
            since = T0,
        });

        await listener.HandlePayloadAsync(payload);

        Assert.Equal(1, reader.Calls);
        Assert.Equal((tenant, asset), reader.LastRequest);
        Assert.Equal(2, broadcaster.Sent.Count);
        Assert.Equal(RealtimeChannels.AssetStatus, broadcaster.Sent[0].Channel);
        Assert.Equal(RealtimeChannels.Alerts, broadcaster.Sent[1].Channel);
        Assert.All(broadcaster.Sent, s => Assert.Equal(tenant, s.TenantId));

        // the wire vocabulary is one set of literals (B10) — a typo here is a dead channel
        Assert.Equal("asset-status", RealtimeChannels.AssetStatus);
        Assert.Equal("alerts", RealtimeChannels.Alerts);

        // a malformed payload is dropped: no read, no broadcast, no throw
        await listener.HandlePayloadAsync("{not json");
        await listener.HandlePayloadAsync(null);
        await listener.HandlePayloadAsync("""{"tenantId":"x","assetId":"y"}""");

        // …and so is a hint missing a fact: inventing `warn`/`open`/now would put
        // a fabricated alert on the wire. The trigger always sends all three.
        await listener.HandlePayloadAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            tenantId = tenant, assetId = asset, alertId, kind = "raised", status = "open", since = T0,
        }));
        await listener.HandlePayloadAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            tenantId = tenant, assetId = asset, alertId, kind = "raised", severity = "warn", status = "open",
        }));

        Assert.Equal(1, reader.Calls);
        Assert.Equal(2, broadcaster.Sent.Count);

        // and an asset the function cannot find broadcasts nothing at all
        reader.Answer = null;
        await listener.HandlePayloadAsync(payload);
        Assert.Equal(2, broadcaster.Sent.Count);
    }

    private sealed class CountingReader : IAssetStatusReader
    {
        public int Calls { get; private set; }
        public (Guid Tenant, Guid Asset)? LastRequest { get; private set; }
        public AssetStatusSnapshot? Answer { get; set; } = new(
            Guid.NewGuid(), AssetStatuses.Alert, T0, Guid.NewGuid(), AlertSeverities.Warn,
            T0, true);

        public Task<Nabd.SharedKernel.Result<IReadOnlyList<AssetStatusSnapshot>>> ListAsync(
            CallerContext acting, bool includeArchived = false, CancellationToken ct = default) =>
            throw new InvalidOperationException("the listener never uses the door lane");

        public Task<AssetStatusSnapshot?> GetForTenantAsync(
            Guid tenantId, Guid assetId, CancellationToken ct = default)
        {
            Calls++;
            LastRequest = (tenantId, assetId);
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingBroadcaster : IRealtimeBroadcaster
    {
        public List<(Guid TenantId, string Channel, object Payload)> Sent { get; } = [];

        public Task BroadcastToTenantAsync(
            Guid tenantId, string channel, object payload, CancellationToken ct = default)
        {
            Sent.Add((tenantId, channel, payload));
            return Task.CompletedTask;
        }
    }
}
