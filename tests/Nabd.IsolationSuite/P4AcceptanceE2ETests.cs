namespace Nabd.IsolationSuite;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nabd.Alerting;
using Nabd.Assets;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Nabd.Notifications;
using Nabd.ProcessingHost;
using Nabd.Rules;
using Nabd.SharedKernel;
using Nabd.Simulator;
using Nabd.TelemetryStore;
using Npgsql;
using Xunit;

// P4 spec 10 — acceptance #2 and #3 as AUTOMATED E2E facts (plan/10 §P4).
//
// THE TOPOLOGY (owner-ruled D10a): real ApiHost doors boot in-process through
// `WebApplicationFactory<Program>`; a second in-process `IHost` runs the REAL
// P4 vertical (`AddP4Vertical`, D10g) against the fixture's real Timescale
// container; the feed is the REAL `FlushPump` → validation → store → bus →
// RulesEngine → AlertLifecycle → email queue/sender/log. The ONE fake is
// `IEmailTransport` (records sends, always succeeds, D10e). No broker, no
// Telegram bot, no child processes.
//
// RED-FIRST: `WarmupDemo`, `WarmupDemoScenario`, `P4Vertical.AddP4Vertical`
// and the test project's ProcessingHost reference do not exist yet — the build
// fails on purpose until the owner implants the code from
// study/implementation-code.md (RULES §9.1). The agent owns this net.
//
// THE LAWS THIS NET PINS:
//   #2  the mint-seeded fridge rule (temperature 2–8 °C, 60 s, warn) stays
//       silent through the 6 s door beat; the firing reading (t=157 in the
//       story) makes the alert visible through GET /api/v1/alerts within 5 s
//       of its pump flush (D10b: the designed duration is NOT latency), and
//       the email log row says `sent` within 60 s; 20 more violating samples
//       still leave ONE alert row and ONE email row (D10i).
//   #3  a device patched to a 1-minute window fires `offline` within
//       60 s + 7 s (tick 2 s, D10f), with the true last-seen stamp; its email
//       uses `device_offline`; the return resolves the SAME row with the TRUE
//       gap and `device_online_restored`, no `suppressed` event, no duplicate.

public sealed class P4AcceptanceE2ETests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan AlertBar = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EmailBar = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OfflineBudget = TimeSpan.FromSeconds(67);   // 60 s window + 7 s margin
    private static readonly TimeSpan ResolveBar = TimeSpan.FromSeconds(7);

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    // ---- setup: everything through the real doors, one fresh tenant per fact ---

    private sealed record RuleRow(
        Guid RuleId, string RuleType, string? ChannelKey,
        double? Low, double? High, int DurationSeconds, string Severity, int OfflineAfterMinutes);

    private sealed record Setup(
        Guid TenantId, Guid AssetId, Guid DeviceId, string DeviceToken,
        string Token, RuleRow Threshold, RuleRow Offline);

    private static RuleRow ToRule(JsonElement r) => new(
        r.GetProperty("ruleId").GetGuid(),
        r.GetProperty("ruleType").GetString()!,
        r.TryGetProperty("channelKey", out var c) && c.ValueKind != JsonValueKind.Null ? c.GetString() : null,
        r.TryGetProperty("thresholdLow", out var lo) && lo.ValueKind != JsonValueKind.Null ? lo.GetDouble() : null,
        r.TryGetProperty("thresholdHigh", out var hi) && hi.ValueKind != JsonValueKind.Null ? hi.GetDouble() : null,
        r.GetProperty("durationSeconds").GetInt32(),
        r.GetProperty("severity").GetString()!,
        r.GetProperty("offlineAfterMinutes").GetInt32());

    private async Task<Setup> SetupAsync(P4VerticalRig rig, string label)
    {
        var email = $"{Uid()}@e2e.test";
        var registration = await rig.SendJsonAsync(HttpMethod.Post, "/api/v1/tenants", new
        {
            name = $"E2E {label}",
            slug = $"e2e-{label}-{Uid()}",
            adminEmail = email,
            adminDisplayName = "E2E Admin",
            adminPassword = "CorrectHorse1",
            adminLocale = "en",
        }, bearer: null);
        var tenantId = registration.GetProperty("tenant").GetProperty("tenantId").GetGuid();

        var login = await rig.SendJsonAsync(HttpMethod.Post, "/api/v1/auth/login",
            new { email, password = "CorrectHorse1" }, bearer: null);
        var token = login.GetProperty("accessToken").GetString()!;

        var templates = await rig.SendJsonAsync(HttpMethod.Get, "/api/v1/templates", null, token);
        var fridge = templates.EnumerateArray().Single(t => t.GetProperty("key").GetString() == "fridge");
        var templateId = fridge.GetProperty("templateId").GetGuid();

        var asset = await rig.SendJsonAsync(HttpMethod.Post, "/api/v1/assets",
            new { templateId, name = $"Fridge {Uid()}" }, token);
        var assetId = asset.GetProperty("assetId").GetGuid();

        var minted = await rig.SendJsonAsync(HttpMethod.Post, $"/api/v1/assets/{assetId}/devices",
            new { name = $"Dev {Uid()}" }, token);
        var deviceId = minted.GetProperty("device").GetProperty("deviceId").GetGuid();
        var deviceToken = minted.GetProperty("token").GetString()!;

        var rules = await rig.SendJsonAsync(HttpMethod.Get, $"/api/v1/devices/{deviceId}/rules", null, token);
        var rows = rules.EnumerateArray().Select(ToRule).ToList();
        return new Setup(
            tenantId, assetId, deviceId, deviceToken, token,
            rows.Single(r => r.RuleType == "outside_range" && r.ChannelKey == "temperature"),
            rows.Single(r => r.RuleType == "offline"));
    }

    /// <summary>The channel row the sender reads; sealed exactly as the owner tool seals it.</summary>
    private async Task SeedEmailChannelAsync()
    {
        var (cipher, nonce, tag) = SecretBox.Seal("suite-smtp-password", "email");
        var config = JsonSerializer.Serialize(new
        {
            host = "smtp.suite.test",
            port = 587,
            user = "suite-user",
            from = "from@suite.test",
            senderName = "Nabd",
        });

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO notifications.channel_config
                (channel, enabled, config, secret_enc, secret_nonce, secret_tag, updated_at)
            VALUES ('email', true, @c::jsonb, @e, @n, @t, now())
            ON CONFLICT (channel) DO UPDATE SET
                enabled = true,
                config = EXCLUDED.config,
                secret_enc = EXCLUDED.secret_enc,
                secret_nonce = EXCLUDED.secret_nonce,
                secret_tag = EXCLUDED.secret_tag,
                updated_at = now();
            """, conn);
        cmd.Parameters.AddWithValue("c", config);
        cmd.Parameters.AddWithValue("e", cipher);
        cmd.Parameters.AddWithValue("n", nonce);
        cmd.Parameters.AddWithValue("t", tag);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- the E2E rig: two in-process hosts, one real vertical, one real DB -----

    private sealed class P4VerticalRig : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public IHost Host { get; private set; } = null!;
        public DeviceStatusTracker Tracker { get; private set; } = null!;
        public FlushPump Pump { get; private set; } = null!;
        public FlushBuffer Buffer { get; private set; } = null!;
        public ReconcileCounters Reconcile { get; private set; } = null!;
        public FlushCounters FlushCounters { get; private set; } = null!;
        public RejectionCounters Rejections { get; private set; } = null!;
        public RecordingEmailTransport Transport { get; } = new();

        /// <summary>The SAME pure profile the simulator publishes (D10h).</summary>
        public WarmupDemoScenario Scenario { get; } =
            new("temperature", 12.0, 1, [(4.0, "temperature")]);

        public static async Task<P4VerticalRig> StartAsync()
        {
            var rig = new P4VerticalRig();

            rig.Factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseEnvironment("Development"));
            rig.Client = rig.Factory.CreateClient();

            // D10f: test cadences live in MEMORY only — production defaults 30/20 untouched.
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Watchdog:OfflineIntervalSeconds"] = "2",
                ["Ingestion:FlushSeconds"] = "1",
            });
            builder.Services.AddP4Vertical();
            // D10e: the ONE fake, registered AFTER the extension (last descriptor wins).
            builder.Services.AddSingleton<IEmailTransport>(rig.Transport);

            rig.Host = builder.Build();
            await rig.Host.StartAsync();

            // The boot-order law: subscribers/state exist BEFORE the first flush.
            rig.Host.Services.GetRequiredService<RulesEngine>();
            rig.Host.Services.GetRequiredService<AlertLifecycle>();
            rig.Host.Services.GetRequiredService<EmailNotifier>();

            var telemetry = rig.Host.Services.GetRequiredService<TelemetryStoreDb>();
            rig.Tracker = rig.Host.Services.GetRequiredService<DeviceStatusTracker>();
            var bus = rig.Host.Services.GetRequiredService<IEventBus>();

            // The REAL pump, exactly as ProcessingWorker composes it (minus MQTT).
            var edges = new IngestionEdges(new AssetsDb());
            rig.Reconcile = new ReconcileCounters();
            rig.Buffer = new FlushBuffer(10, rig.Reconcile);
            rig.Rejections = new RejectionCounters(rig.Reconcile);
            rig.FlushCounters = new FlushCounters(rig.Reconcile);
            rig.Pump = new FlushPump(
                rig.Buffer,
                FlushOptions.From(10, 1000, 200, 1),
                edges,
                new ValidationPipeline(edges, edges, maxReadingsPerBatch: 32),
                new PostgresReadingsStore(telemetry, 32),
                new RawRejectsStore(telemetry),
                rig.Tracker,
                rig.Rejections,
                rig.FlushCounters,
                rig.Reconcile,
                bus,
                NullLogger<FlushPump>.Instance);

            return rig;
        }

        /// <summary>One sample = one offer = one flush = one commit (the story's clock is explicit).</summary>
        public async Task FeedAsync(
            Guid deviceId, long seq, DateTimeOffset eventTime, DateTimeOffset? receivedAt = null)
        {
            var topic = $"t/00000000-0000-0000-0000-000000000000/dev/{deviceId}/v1";
            var payload = Encoding.UTF8.GetBytes(PayloadV1.Compose(seq, eventTime, Scenario.Tick(seq)));
            var envelope = new IngestEnvelope(deviceId, topic, payload, receivedAt ?? eventTime);

            var acked = false;
            Buffer.Offer(envelope, _ => { acked = true; return Task.CompletedTask; });

            var outcome = await Pump.FlushOnceAsync(CancellationToken.None);
            Assert.Equal(FlushOutcome.Completed, outcome);
            await Pump.DrainAcksAsync(CancellationToken.None);
            Assert.True(acked, "the pump must ack only after a committed outcome");
        }

        public async Task<JsonElement> SendJsonAsync(
            HttpMethod method, string path, object? body, string? bearer)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body, JsonWeb), Encoding.UTF8, "application/json");
            if (bearer is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            using var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode,
                $"{method} {path} → {(int)response.StatusCode}: {text}");

            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            await Host.StopAsync(TimeSpan.FromSeconds(10));
            Host.Dispose();
        }
    }

    private sealed class RecordingEmailTransport : IEmailTransport
    {
        public List<(string To, string Subject)> Sent { get; } = [];

        public Task SendAsync(
            EmailSettings settings, string to, string subject, string body,
            CancellationToken ct = default)
        {
            lock (Sent) Sent.Add((to, subject));
            return Task.CompletedTask;
        }
    }

    // ---- polling helpers (real time is the budget; no sleeps as assertions) ----

    private async Task<JsonElement?> WaitAlertAsync(
        P4VerticalRig rig, Setup s, Func<JsonElement, bool> match, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow <= deadline)
        {
            var alerts = await rig.SendJsonAsync(
                HttpMethod.Get, $"/api/v1/alerts?assetId={s.AssetId}", null, s.Token);
            foreach (var alert in alerts.EnumerateArray())
                if (match(alert)) return alert;
            await Task.Delay(50);
        }
        return null;
    }

    private async Task<int> AlertCountAsync(P4VerticalRig rig, Setup s)
    {
        var alerts = await rig.SendJsonAsync(
            HttpMethod.Get, $"/api/v1/alerts?assetId={s.AssetId}", null, s.Token);
        return alerts.EnumerateArray().Count();
    }

    private async Task<JsonElement?> WaitStatusAsync(
        P4VerticalRig rig, Setup s, Guid alertId, string status, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow <= deadline)
        {
            var alert = await rig.SendJsonAsync(
                HttpMethod.Get, $"/api/v1/alerts/{alertId}", null, s.Token);
            if (alert.GetProperty("status").GetString() == status) return alert;
            await Task.Delay(50);
        }
        return null;
    }

    private sealed record EmailRow(string Status, int Attempts, string? TemplateKey, DateTimeOffset? SentAt);

    private async Task<List<EmailRow>> EmailRowsAsync(Guid alertId)
    {
        var rows = new List<EmailRow>();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT status, attempts, template_key, sent_at
              FROM alerting.notification_log
             WHERE alert_id = @a AND channel = 'email'
             ORDER BY id;
            """, conn);
        cmd.Parameters.AddWithValue("a", alertId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new EmailRow(
                reader.GetString(0), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        return rows;
    }

    private async Task<EmailRow> WaitEmailAsync(Guid alertId, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow <= deadline)
        {
            var sent = (await EmailRowsAsync(alertId)).FirstOrDefault(r => r.Status == "sent");
            if (sent is not null) return sent;
            await Task.Delay(50);
        }
        Assert.Fail($"no `sent` email row for alert {alertId} by {deadline:O}");
        return null!;
    }

    private sealed record AlertEvent(string Event, string? Reason);

    private async Task<List<AlertEvent>> EventsAsync(Guid alertId)
    {
        var events = new List<AlertEvent>();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT event, reason
              FROM alerting.alert_events
             WHERE alert_id = @a
             ORDER BY at, event;
            """, conn);
        cmd.Parameters.AddWithValue("a", alertId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            events.Add(new AlertEvent(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return events;
    }

    private async Task<bool> DeviceStatusExistsAsync(Guid deviceId)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM telemetry.device_status WHERE device_id = @d;", conn);
        cmd.Parameters.AddWithValue("d", deviceId);
        return (long)(await cmd.ExecuteScalarAsync())! == 1L;
    }

    private async Task<long> RawRejectsAsync(Guid deviceId)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM telemetry.raw_rejects WHERE device_id = @d;", conn);
        cmd.Parameters.AddWithValue("d", deviceId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static DateTimeOffset NowAtMilliseconds() =>
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    // ---- acceptance #2 — threshold + email, with the real 5 s / 60 s bars -----

    [Fact]
    public async Task acceptance_2_fridge_warming_alert_is_visible_within_5s_and_email_is_sent_within_60s()
    {
        await using var rig = await P4VerticalRig.StartAsync();
        var setup = await SetupAsync(rig, "acc2");
        await SeedEmailChannelAsync();

        // the mint's seed IS the story — the E2E creates no rule
        Assert.Equal("outside_range", setup.Threshold.RuleType);
        Assert.Equal("temperature", setup.Threshold.ChannelKey);
        Assert.Equal(2.0, setup.Threshold.Low);
        Assert.Equal(8.0, setup.Threshold.High);
        Assert.Equal(60, setup.Threshold.DurationSeconds);
        Assert.Equal("warn", setup.Threshold.Severity);

        // The story's clock: 306 s of script, 1 Hz. Values come from the SAME
        // WarmupDemo the simulator publishes; the timestamps are story time only.
        const long firstAbove = 98;    // t=97
        const long firingSample = 158; // t=157
        var t0 = NowAtMilliseconds().AddSeconds(-306);

        // 1..97 includes the 6 s door beat: it must stay silent
        for (var seq = 1L; seq < firstAbove; seq++)
            await rig.FeedAsync(setup.DeviceId, seq, t0.AddSeconds(seq - 1));

        await Task.Delay(1000);
        Assert.Equal(0, await AlertCountAsync(rig, setup));

        // 98..157: violating, but the 60 s window is one sample short
        for (var seq = firstAbove; seq < firingSample; seq++)
            await rig.FeedAsync(setup.DeviceId, seq, t0.AddSeconds(seq - 1));

        await Task.Delay(300);
        Assert.Equal(0, await AlertCountAsync(rig, setup));

        // THE FIRING SAMPLE — latency starts at this pump flush (D10b)
        var tFire = DateTimeOffset.UtcNow;
        await rig.FeedAsync(setup.DeviceId, firingSample, t0.AddSeconds(firingSample - 1));

        var alert = await WaitAlertAsync(
            rig, setup, a => a.GetProperty("type").GetString() == "threshold", tFire + AlertBar);
        Assert.NotNull(alert);
        var latency = DateTimeOffset.UtcNow - tFire;
        Assert.True(latency <= AlertBar,
            $"the alert must be visible within {AlertBar.TotalSeconds:F0} s, measured {latency.TotalMilliseconds:F0} ms");

        var alertId = alert!.Value.GetProperty("alertId").GetGuid();
        Assert.Equal("open", alert.Value.GetProperty("status").GetString());
        Assert.Equal("warn", alert.Value.GetProperty("severity").GetString());
        Assert.Equal(t0.AddSeconds(firingSample - 1),
            alert.Value.GetProperty("openedAt").GetDateTimeOffset());
        Assert.Equal(1, await AlertCountAsync(rig, setup));

        var sent = await WaitEmailAsync(alertId, tFire + EmailBar);
        Assert.Equal("sent", sent.Status);
        Assert.Equal(1, sent.Attempts);
        Assert.Equal("alert_raised", sent.TemplateKey);
        Assert.NotNull(sent.SentAt);
        var emailLatency = sent.SentAt!.Value - tFire;
        Assert.True(emailLatency <= EmailBar,
            $"the email must be sent within {EmailBar.TotalSeconds:F0} s, measured {emailLatency.TotalSeconds:F2} s");

        // D10i: 20 more violating samples change NOTHING (one episode, one story)
        for (var extra = 1; extra <= 20; extra++)
            await rig.FeedAsync(setup.DeviceId, firingSample + extra,
                t0.AddSeconds(firingSample + extra - 1));
        await Task.Delay(1000);

        Assert.Equal(1, await AlertCountAsync(rig, setup));
        Assert.Single(await EmailRowsAsync(alertId));
        Assert.Equal(firingSample + 20, rig.FlushCounters.RowsStored);
        Assert.Equal(0, rig.Buffer.PendingMessages);
        Assert.Equal(0, rig.Reconcile.Deficit);
        Assert.Equal(0, rig.Reconcile.Alarms);
        Assert.Empty(rig.Rejections.Snapshot());
        Assert.Equal(0L, await RawRejectsAsync(setup.DeviceId));

        Console.WriteLine(
            $"E2E #2: alert visible {latency.TotalMilliseconds:F0} ms after the firing reading (bar ≤ 5 s) · "
            + $"email sent {emailLatency.TotalSeconds:F2} s after the fire (bar ≤ 60 s) · "
            + $"1 alert · 1 email · dedup held (20 extra samples)");
    }

    // ---- acceptance #3 — offline + return, with the true gap ------------------

    [Fact]
    public async Task acceptance_3_a_silent_device_fires_offline_inside_window_plus_margin_and_return_resolves_with_the_gap()
    {
        await using var rig = await P4VerticalRig.StartAsync();
        var setup = await SetupAsync(rig, "acc3");
        await SeedEmailChannelAsync();

        // D10f: the test's own cadence — 1 minute instead of the mint's 30
        var patched = await rig.SendJsonAsync(HttpMethod.Patch,
            $"/api/v1/devices/{setup.DeviceId}/rules/{setup.Offline.RuleId}",
            new { offlineAfterMinutes = 1 }, setup.Token);
        Assert.Equal(1, patched.GetProperty("offlineAfterMinutes").GetInt32());

        var tLast = NowAtMilliseconds();
        await rig.FeedAsync(setup.DeviceId, 1, tLast, tLast);
        await rig.Tracker.FlushNowAsync();   // the candidate query inner-joins device_status
        Assert.True(await DeviceStatusExistsAsync(setup.DeviceId));

        var alert = await WaitAlertAsync(
            rig, setup, a => a.GetProperty("type").GetString() == "offline", tLast + OfflineBudget);
        Assert.NotNull(alert);

        var alertId = alert!.Value.GetProperty("alertId").GetGuid();
        var openedAt = alert.Value.GetProperty("openedAt").GetDateTimeOffset();
        var detection = openedAt - tLast;
        Assert.True(detection >= TimeSpan.FromMinutes(1),
            $"offline must not fire before the window: measured {detection.TotalSeconds:F1} s");

        var sent = await WaitEmailAsync(alertId, tLast + OfflineBudget);
        Assert.Equal("sent", sent.Status);
        Assert.Equal("device_offline", sent.TemplateKey);

        // THE RETURN — the same row must resolve, with the TRUE gap
        var tReturn = NowAtMilliseconds();
        await rig.FeedAsync(setup.DeviceId, 2, tReturn, tReturn);

        var resolved = await WaitStatusAsync(rig, setup, alertId, "resolved", tReturn + ResolveBar);
        Assert.NotNull(resolved);

        var resolvedAt = resolved!.Value.GetProperty("resolvedAt").GetDateTimeOffset();
        Assert.True(resolvedAt > openedAt, "resolved_at must come after opened_at");

        var gapMinutes = resolved.Value.GetProperty("detail").GetProperty("offlineGapMinutes").GetDouble();
        var expectedMinutes = (tReturn - tLast).TotalMinutes;
        Assert.True(Math.Abs(gapMinutes - expectedMinutes) <= 2.0 / 60.0,
            $"the row must carry the TRUE gap: {gapMinutes:F3} min vs {expectedMinutes:F3} min (2 s tolerance)");

        var events = await EventsAsync(alertId);
        Assert.Equal(["opened", "resolved"], events.Select(e => e.Event).ToArray());
        Assert.Equal("device_online_restored", events[1].Reason);
        Assert.DoesNotContain(events, e => e.Event == "suppressed");

        // the episode sends TWO emails by design: the raise and the resolve.
        // Each template once — "one story" never means "one row forever".
        var emails = await EmailRowsAsync(alertId);
        Assert.Single(emails, r => r.TemplateKey == "device_offline");
        Assert.Single(emails, r => r.TemplateKey == "alert_resolved");

        // re-armed, dedup held: one more in-range sample creates nothing new
        await rig.FeedAsync(setup.DeviceId, 3, NowAtMilliseconds().AddSeconds(1));
        await Task.Delay(500);
        Assert.Equal(1, await AlertCountAsync(rig, setup));

        Console.WriteLine(
            $"E2E #3: offline after {detection.TotalSeconds:F1} s on a 60 s window (tick 2 s, margin 7 s) · "
            + $"resolved at +{(resolvedAt - tReturn).TotalSeconds:F1} s with gap {gapMinutes:F2} min · 0 duplicates");
    }
}
