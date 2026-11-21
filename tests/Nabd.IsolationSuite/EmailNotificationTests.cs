namespace Nabd.IsolationSuite;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Alerting.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Notifications;
using Nabd.Notifications.Contracts;
using Nabd.SharedKernel;
using Xunit;

// P4 spec 08 — email notifications (FR-34, acceptance #2's ≤ 60 s leg; plan/04 §9, plan/06 §6).
//
// RED-FIRST: `EmailNotifier`, `EmailNotificationSender`, `EmailQueue`, `EmailDispatchWorker`,
// `EmailChannelConfig`, `AlertEmailRenderer`, `SecretBox`, `NotificationsDb`, `IUserDirectory`,
// `ChannelRouting` and the widened `NotificationMessage`/`AlertResolved` do not exist yet.
// The build fails on purpose until the code is implanted from study/implementation-code.md.
//
// The laws this net pins:
//   1. warn/critical → one email per tenant user, in THAT user's locale; info → nothing.
//   2. the template follows the alert type: threshold → alert_raised, offline →
//      device_offline, any resolve → alert_resolved (never traded).
//   3. a successful send is exactly one log row: sent, attempts=1, template_key,
//      locale, recipient, sent_at.
//   4. an unconfigured/disabled channel is a `failed` row with a loud reason, and the
//      alert path is untouched (the alert row is still there, open).
//   5. two failures then success → sent with attempts=3; permanent failure → failed
//      with attempts=3 (the row is the evidence, not an exception).
//   6. a missing or short NABD_SECRET_KEY fails shut: nothing is sealed or sent, and
//      no secret material reaches the log.
//   7. a missing template is an error that is logged — never an empty email.
//   8. the wall: notification_log rows are invisible to another tenant, and nabd_app
//      cannot write channel_config.
//
// Delivery is asynchronous (D8j): every fact waits for the log row instead of sleeping.

public sealed class EmailNotificationTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static CallerContext Admin(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    // ---- rigs and fakes ---------------------------------------------------------

    private sealed record Captured(string To, string Subject, string Body);

    private sealed class FakeTransport : IEmailTransport
    {
        public int FailuresBeforeSuccess { get; set; }
        public bool AlwaysFail { get; set; }
        public List<Captured> Sent { get; } = [];

        private int _calls;

        public Task SendAsync(
            EmailSettings settings, string to, string subject, string body,
            CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (AlwaysFail || call <= FailuresBeforeSuccess)
                throw new InvalidOperationException("smtp down (suite)");

            lock (Sent) Sent.Add(new Captured(to, subject, body));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
            lock (Exceptions) Exceptions.Add(exception);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeTransport Transport { get; init; } = null!;
        public InProcessEventBus Bus { get; private set; } = null!;
        public EmailQueue Queue { get; private set; } = null!;
        public EmailNotificationSender Sender { get; private set; } = null!;
        public EmailNotifier Notifier { get; private set; } = null!;
        public EmailDispatchWorker Worker { get; private set; } = null!;
        public CapturingLogger<EmailNotifier> NotifierLog { get; } = new();
        public CapturingLogger<EmailNotificationSender> SenderLog { get; } = new();

        public static async Task<Rig> StartAsync(PostgresFixture pg, FakeTransport transport)
        {
            var rig = new Rig { Transport = transport };

            var db = pg.Notifications;
            var config = new EmailChannelConfig(db);
            // spec 09: the renderer is channel-aware and the log insert is shared
            var renderer = new AlertMessageRenderer(db);
            var policy = new EmailRetryPolicy([TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]);

            rig.Queue = new EmailQueue();
            rig.Sender = new EmailNotificationSender(
                config, transport, policy, new NotificationLog(db), rig.SenderLog);
            rig.Bus = new InProcessEventBus();
            rig.Notifier = new EmailNotifier(
                rig.Bus, new UserDirectory(pg.Db), renderer, rig.Queue, rig.NotifierLog);

            // the host's worker, driven here on the same queue
            rig.Worker = new EmailDispatchWorker(rig.Queue, rig.Sender, Microsoft.Extensions.Logging.Abstractions.NullLogger<EmailDispatchWorker>.Instance);
            await rig.Worker.StartAsync(CancellationToken.None);
            return rig;
        }

        public Task PublishRaisedAsync(AlertSnapshot alert) =>
            Bus.PublishAsync(new AlertRaised(alert));

        public Task PublishResolvedAsync(AlertSnapshot alert) =>
            Bus.PublishAsync(new AlertResolved(
                alert.TenantId, alert.AlertId, alert.ResolvedAt ?? T0, AlertReasons.ConditionRestored, alert));

        public async ValueTask DisposeAsync()
        {
            Notifier.Dispose();
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

    private sealed record LogRow(
        Guid TenantId, Guid AlertId, string Channel, string? Recipient, string Status,
        int Attempts, string? Error, string? TemplateKey, string? Locale, DateTimeOffset? SentAt);

    // ---- setup helpers ----------------------------------------------------------

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Mail {label}",
            Slug: $"mail-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@mail.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    /// <summary>A second person in the tenant, with their own language (owner lane: setup only).</summary>
    private async Task AddUserAsync(TenantRegistrationResult tenant, string locale, string label)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO identity.users (tenant_id, email, display_name, locale)
            VALUES (@t, @e, @n, @l);
            """, conn);
        cmd.Parameters.AddWithValue("t", tenant.Tenant.TenantId);
        cmd.Parameters.AddWithValue("e", $"{Uid()}-{label}@mail.test");
        cmd.Parameters.AddWithValue("n", $"{label} Person");
        cmd.Parameters.AddWithValue("l", locale);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> EnsureTemplateAsync(string label)
    {
        var key = $"mail-{label}-{Uid()}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
                VALUES (@k, 'قالب', 'Mail Template');
                INSERT INTO assets.channel_definitions
                    (template_id, key, name_ar, name_en, unit, data_type, lower_limit, upper_limit)
                SELECT t.id, 'temperature', 'حرارة', 'Temperature', '°C', 'number', 2.0, 8.0
                FROM assets.asset_type_templates t WHERE t.key = @k;
                """, conn);
            ins.Parameters.AddWithValue("k", key);
            await ins.ExecuteNonQueryAsync();
        }
        await using var sel = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM assets.asset_type_templates WHERE key = @k;", sel);
        cmd.Parameters.AddWithValue("k", key);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> CreateAssetAsync(TenantRegistrationResult tenant, string label)
    {
        var template = await EnsureTemplateAsync(label);
        var catalog = new AssetCatalog(pg.Assets);
        var created = await catalog.CreateAssetAsync(
            Admin(tenant),
            new CreateAssetRequest(tenant.Tenant.TenantId, template, $"Asset {Uid()}", null, null));
        Assert.True(created.IsSuccess, $"SETUP failed (asset): {created.Error}");
        return created.Value.AssetId;
    }

    /// <summary>The alert row the log's FK needs; the snapshot is the event's payload.</summary>
    private async Task<AlertSnapshot> InsertAlertAsync(
        TenantRegistrationResult tenant, Guid assetId, string type, string severity,
        string status = AlertStatuses.Open, DateTimeOffset? at = null)
    {
        var alertId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var openedAt = at ?? T0;
        var resolvedAt = status == AlertStatuses.Resolved ? openedAt.AddMinutes(5) : (DateTimeOffset?)null;

        var detail = new AlertDetail(
            Value: type == AlertTypes.Threshold ? 9.5 : null,
            ChannelKey: type == AlertTypes.Threshold ? "temperature" : null,
            RuleType: type,
            ThresholdLow: type == AlertTypes.Threshold ? 2.0 : null,
            ThresholdHigh: type == AlertTypes.Threshold ? 8.0 : null,
            DeviceId: Guid.NewGuid(),
            OfflineGapMinutes: type == AlertTypes.Offline ? 31.0 : null,
            LastSeenAtUtc: type == AlertTypes.Offline ? openedAt.AddMinutes(-31) : null);

        var titleEn = type == AlertTypes.Offline
            ? $"device offline since {openedAt:u} — Fridge A"
            : $"temperature 9.5 above 8 — Fridge A";
        var titleAr = type == AlertTypes.Offline
            ? $"الجهاز متوقف عن الإرسال منذ {openedAt:u} — ثلاجة أ"
            : $"temperature 9.5 أعلى من 8 — ثلاجة أ";

        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO alerting.alerts
                    (id, tenant_id, asset_id, rule_id, type, severity, status, opened_at,
                     resolved_at, title, detail)
                VALUES (@id, @t, @a, @r, @type, @sev, @status, @opened, @resolved,
                        @title::jsonb, @detail::jsonb);
                """, conn);
            cmd.Parameters.AddWithValue("id", alertId);
            cmd.Parameters.AddWithValue("t", tenant.Tenant.TenantId);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.AddWithValue("r", ruleId);
            cmd.Parameters.AddWithValue("type", type);
            cmd.Parameters.AddWithValue("sev", severity);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("opened", openedAt);
            cmd.Parameters.AddWithValue("resolved", (object?)resolvedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("title", System.Text.Json.JsonSerializer.Serialize(
                new { En = titleEn, Ar = titleAr }));
            cmd.Parameters.AddWithValue("detail", System.Text.Json.JsonSerializer.Serialize(detail));
            await cmd.ExecuteNonQueryAsync();
        }

        return new AlertSnapshot(
            alertId, tenant.Tenant.TenantId, assetId, ruleId, type, severity, status,
            openedAt, resolvedAt, null, null, titleEn, titleAr, detail);
    }

    /// <summary>The channel row the sender reads; secret sealed exactly as the tool seals it.</summary>
    private async Task ConfigureChannelAsync(bool enabled = true)
    {
        var (cipher, nonce, tag) = SecretBox.Seal("suite-smtp-password", "email");
        var config = System.Text.Json.JsonSerializer.Serialize(new
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
            VALUES ('email', @on, @c::jsonb, @e, @n, @t, now())
            ON CONFLICT (channel) DO UPDATE SET
                enabled = @on,
                config = EXCLUDED.config,
                secret_enc = EXCLUDED.secret_enc,
                secret_nonce = EXCLUDED.secret_nonce,
                secret_tag = EXCLUDED.secret_tag,
                updated_at = now();
            """, conn);
        cmd.Parameters.AddWithValue("on", enabled);
        cmd.Parameters.AddWithValue("c", config);
        cmd.Parameters.AddWithValue("e", cipher);
        cmd.Parameters.AddWithValue("n", nonce);
        cmd.Parameters.AddWithValue("t", tag);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<LogRow>> RowsAsync(Guid? alertId = null)
    {
        var rows = new List<LogRow>();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT tenant_id, alert_id, channel, recipient, status, attempts, error,
                   template_key, locale, sent_at
              FROM alerting.notification_log
             WHERE (@a::uuid IS NULL OR alert_id = @a)
             ORDER BY id;
            """, conn);
        cmd.Parameters.AddWithValue("a", (object?)alertId ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new LogRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)));
        return rows;
    }

    private async Task<List<LogRow>> WaitRowsAsync(Guid alertId, int expected)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var rows = await RowsAsync(alertId);
            if (rows.Count >= expected) return rows;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"expected {expected} notification_log row(s) for {alertId}, saw {rows.Count}");
            await Task.Delay(25);
        }
    }

    private async Task<DateTimeOffset?> WaitForNoNewRowsAsync(Guid alertId, TimeSpan quiet)
    {
        // The email leg has no reply; give the worker a bounded quiet window, then count.
        await Task.Delay(quiet);
        var rows = await RowsAsync(alertId);
        Assert.Empty(rows);
        return null;
    }

    // ---- fact 1 — severity routing, per-recipient language ----------------------

    [Fact]
    public async Task warn_reaches_every_user_in_their_own_language_and_info_never_leaves()
    {
        var tenant = await RegisterAsync("routing");
        var asset = await CreateAssetAsync(tenant, "routing");
        await AddUserAsync(tenant, "ar", "arabic");
        await ConfigureChannelAsync();

        var transport = new FakeTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        var warn = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
        await rig.PublishRaisedAsync(warn);

        var rows = await WaitRowsAsync(warn.AlertId, 2);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("sent", r.Status));

        // one email per user, each in that user's locale
        Assert.Equal(2, transport.Sent.Count);
        Assert.Contains(transport.Sent, m => m.Subject.Contains("[Nabd]") && m.Body.Contains("warn"));
        Assert.Contains(transport.Sent, m => m.Subject.Contains("[نبض]") && m.Body.Contains("تحذير"));
        Assert.DoesNotContain(transport.Sent, m => m.Subject.Contains("[Nabd]") && m.Body.Contains("تحذير"));

        // info never leaves the dashboard (plan/06 §6)
        var info = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "info");
        await rig.PublishRaisedAsync(info);
        await WaitForNoNewRowsAsync(info.AlertId, TimeSpan.FromMilliseconds(300));
        Assert.Empty(await RowsAsync(info.AlertId));
    }

    // ---- fact 2 — the template follows the type, resolves included --------------

    [Fact]
    public async Task the_template_follows_the_alert_type_and_a_resolve_uses_its_own()
    {
        var tenant = await RegisterAsync("templates");
        var asset = await CreateAssetAsync(tenant, "templates");
        await ConfigureChannelAsync();

        var transport = new FakeTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        var raised = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
        var offline = await InsertAlertAsync(tenant, asset, AlertTypes.Offline, "warn");
        var resolved = await InsertAlertAsync(
            tenant, asset, AlertTypes.Threshold, "warn", AlertStatuses.Resolved);

        await rig.PublishRaisedAsync(raised);
        await rig.PublishRaisedAsync(offline);
        await rig.PublishResolvedAsync(resolved);

        var rows = await WaitRowsAsync(raised.AlertId, 1);
        rows.AddRange(await WaitRowsAsync(offline.AlertId, 1));
        rows.AddRange(await WaitRowsAsync(resolved.AlertId, 1));

        Assert.Equal("alert_raised", rows.Single(r => r.AlertId == raised.AlertId).TemplateKey);
        Assert.Equal("device_offline", rows.Single(r => r.AlertId == offline.AlertId).TemplateKey);
        Assert.Equal("alert_resolved", rows.Single(r => r.AlertId == resolved.AlertId).TemplateKey);

        // the offline body carries the true gap, the threshold body the reading
        Assert.Contains(transport.Sent, m => m.Body.Contains("Silent for: 31 minutes"));
        Assert.Contains(transport.Sent, m => m.Body.Contains("temperature 9.5 above 8"));
    }

    // ---- fact 3 — a successful send is one honest row ---------------------------

    [Fact]
    public async Task a_sent_email_is_exactly_one_row_with_its_template_and_locale()
    {
        var tenant = await RegisterAsync("row");
        var asset = await CreateAssetAsync(tenant, "row");
        await ConfigureChannelAsync();

        var transport = new FakeTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        var alert = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "critical");
        await rig.PublishRaisedAsync(alert);

        var row = Assert.Single(await WaitRowsAsync(alert.AlertId, 1));
        Assert.Equal("email", row.Channel);
        Assert.Equal("sent", row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.Error);
        Assert.Equal("alert_raised", row.TemplateKey);
        Assert.Equal("en", row.Locale);                       // the admin's locale
        Assert.Equal(transport.Sent.Single().To, row.Recipient);
        Assert.NotNull(row.SentAt);
        Assert.True(row.SentAt > DateTimeOffset.UtcNow.AddMinutes(-5),
            "sent_at must be a real stamp, not the fixture clock");
    }

    // ---- fact 4 — an unconfigured channel cannot hurt the alert path ------------

    [Fact]
    public async Task an_unconfigured_channel_is_a_failed_row_and_the_alert_survives()
    {
        var tenant = await RegisterAsync("noconfig");
        var asset = await CreateAssetAsync(tenant, "noconfig");
        await ConfigureChannelAsync(enabled: false);

        var transport = new FakeTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        var alert = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
        await rig.PublishRaisedAsync(alert);                  // must not throw

        var row = Assert.Single(await WaitRowsAsync(alert.AlertId, 1));
        Assert.Equal("failed", row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Contains("not configured", row.Error);
        Assert.Empty(transport.Sent);

        // the alert itself is untouched and still open
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM alerting.alerts WHERE id = @a;", conn);
        cmd.Parameters.AddWithValue("a", alert.AlertId);
        Assert.Equal("open", (string)(await cmd.ExecuteScalarAsync())!);
    }

    // ---- fact 5 — retries end in the row, never in the caller -------------------

    [Fact]
    public async Task two_failures_then_success_is_sent_with_three_attempts()
    {
        var tenant = await RegisterAsync("retry-ok");
        var asset = await CreateAssetAsync(tenant, "retry-ok");
        await ConfigureChannelAsync();

        var transport = new FakeTransport { FailuresBeforeSuccess = 2 };
        await using var rig = await Rig.StartAsync(pg, transport);

        var alert = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
        await rig.PublishRaisedAsync(alert);

        var row = Assert.Single(await WaitRowsAsync(alert.AlertId, 1));
        Assert.Equal("sent", row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task a_permanent_failure_is_failed_with_three_attempts_and_no_throw()
    {
        var tenant = await RegisterAsync("retry-bad");
        var asset = await CreateAssetAsync(tenant, "retry-bad");
        await ConfigureChannelAsync();

        var transport = new FakeTransport { AlwaysFail = true };
        await using var rig = await Rig.StartAsync(pg, transport);

        var alert = await InsertAlertAsync(tenant, asset, AlertTypes.Offline, "warn");
        await rig.PublishRaisedAsync(alert);

        var row = Assert.Single(await WaitRowsAsync(alert.AlertId, 1));
        Assert.Equal("failed", row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Contains("smtp down", row.Error);
        Assert.Empty(transport.Sent);
    }

    // ---- fact 6 — the key is a wall, not a suggestion ---------------------------

    [Fact]
    public async Task a_missing_or_short_key_fails_shut_and_never_logs_a_secret()
    {
        var key = Environment.GetEnvironmentVariable("NABD_SECRET_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "the suite must set NABD_SECRET_KEY");

        try
        {
            Environment.SetEnvironmentVariable("NABD_SECRET_KEY", null);
            Assert.Throws<InvalidOperationException>(() => SecretBox.Seal("x", "email"));

            // a 16-byte key is not AES-256 — refuse, never silently derive
            Environment.SetEnvironmentVariable(
                "NABD_SECRET_KEY", Convert.ToBase64String(new byte[16]));
            Assert.Throws<InvalidOperationException>(() => SecretBox.Seal("x", "email"));

            // 33 bytes is just as wrong
            Environment.SetEnvironmentVariable(
                "NABD_SECRET_KEY", Convert.ToBase64String(new byte[33]));
            Assert.Throws<InvalidOperationException>(() => SecretBox.Seal("x", "email"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NABD_SECRET_KEY", key);
        }

        // and the whole channel fails shut without touching the log
        var tenant = await RegisterAsync("nokey");
        var asset = await CreateAssetAsync(tenant, "nokey");
        await ConfigureChannelAsync();                        // sealed while the key exists

        var transport = new FakeTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        try
        {
            Environment.SetEnvironmentVariable("NABD_SECRET_KEY", null);
            var alert = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
            await rig.PublishRaisedAsync(alert);

            await Task.Delay(300);
            Assert.Empty(await RowsAsync(alert.AlertId));
            Assert.Empty(transport.Sent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NABD_SECRET_KEY", key);
        }

        var logged = string.Join("\n", rig.SenderLog.Messages.Concat(rig.NotifierLog.Messages));
        Assert.DoesNotContain("suite-smtp-password", logged);
        Assert.DoesNotContain(key!, logged);
    }

    // ---- fact 7 — a missing template is an error, not an empty email ------------

    [Fact]
    public async Task a_missing_template_is_logged_and_nothing_is_sent()
    {
        var tenant = await RegisterAsync("notemplate");
        var asset = await CreateAssetAsync(tenant, "notemplate");
        await ConfigureChannelAsync();

        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var del = new NpgsqlCommand(
                "DELETE FROM notifications.message_templates WHERE key = 'alert_raised';", conn);
            await del.ExecuteNonQueryAsync();
        }

        try
        {
            var transport = new FakeTransport();
            await using var rig = await Rig.StartAsync(pg, transport);

            var alert = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
            await rig.PublishRaisedAsync(alert);

            await Task.Delay(300);
            Assert.Empty(await RowsAsync(alert.AlertId));
            Assert.Empty(transport.Sent);
            Assert.Contains(rig.NotifierLog.Messages, m => m.Contains("email fan-out failed"));
        }
        finally
        {
            // restore the two seeds the rest of the class relies on
            await using var conn = await pg.Owner.OpenConnectionAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO notifications.message_templates (key, channel, locale, subject, body)
                VALUES
                ('alert_raised', 'email', 'en', '[Nabd] {{severity}} — {{title}}',
                 E'Nabd alert ({{severity}})\n\n{{title}}\n\nOpened: {{time}} UTC\nAlert: {{alert_id}}\n'),
                ('alert_raised', 'email', 'ar', '[نبض] {{severity}} — {{title}}',
                 E'تنبيه من نبض ({{severity}})\n\n{{title}}\n\nوقت الفتح: {{time}} UTC\nرقم التنبيه: {{alert_id}}\n')
                ON CONFLICT (key, channel, locale) DO NOTHING;
                """, conn);
            await seed.ExecuteNonQueryAsync();
        }
    }

    // ---- fact 8 — the wall ------------------------------------------------------

    [Fact]
    public async Task notification_log_is_tenant_walled_and_the_app_cannot_write_channel_config()
    {
        var a = await RegisterAsync("wall-a");
        var b = await RegisterAsync("wall-b");
        var asset = await CreateAssetAsync(a, "wall-a");
        await ConfigureChannelAsync();

        var transport = new FakeTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        var alert = await InsertAlertAsync(a, asset, AlertTypes.Threshold, "warn");
        await rig.PublishRaisedAsync(alert);
        await WaitRowsAsync(alert.AlertId, 1);

        // tenant A sees its own row through the app lane
        var (connA, txA) = await pg.Notifications.OpenTenantScopeAsync(a.Tenant.TenantId);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM alerting.notification_log;", connA, txA);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            await txA.CommitAsync();
        }
        finally
        {
            await connA.DisposeAsync();
        }

        // tenant B sees nothing — the log is not a shared feed
        var (connB, txB) = await pg.Notifications.OpenTenantScopeAsync(b.Tenant.TenantId);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM alerting.notification_log;", connB, txB);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
            await txB.CommitAsync();
        }
        finally
        {
            await connB.DisposeAsync();
        }

        // the app may read the channel row but never write it (secret door is owner-only)
        await using var connC = await pg.Notifications.OpenPlatformReadAsync();
        await using var write = new NpgsqlCommand("""
            UPDATE notifications.channel_config SET enabled = false WHERE channel = 'email';
            """, connC);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState);               // insufficient_privilege
    }
}
