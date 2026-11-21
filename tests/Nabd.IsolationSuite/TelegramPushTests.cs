namespace Nabd.IsolationSuite;

using Microsoft.Extensions.Logging;
using Npgsql;
using Nabd.Alerting.Contracts;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Notifications;
using Nabd.Notifications.Contracts;
using Nabd.SharedKernel;
using Xunit;

// P4 spec 09 — Telegram push (FR-34, FR-38; plan/04 §9, plan/06 §6).
//
// RED-FIRST: `TelegramChannels`, `TelegramChannelConfig`, `TelegramLinkCodes`,
// `TelegramQueue`, `TelegramRetryPolicy`, `TelegramNotificationSender`,
// `TelegramDispatchWorker`, `TelegramNotifier`, `ITelegramTransport`, `TelegramSettings`,
// `TelegramUpdate`, `TelegramChat`, `NotificationLog`, `AlertMessageRenderer`,
// `ITelegramChannels`, `TelegramLinkTicket`, `TelegramChannelState`, `NotificationErrors`
// and the `0027` migration do not exist yet. The build fails on purpose until the code
// is implanted from study/implementation-code.md (steps 0–8).
//
// Plus two MECHANICAL spec-08 rig edits this file's sibling already carries:
//   `new AlertEmailRenderer(db)`        → `new AlertMessageRenderer(db)`
//   `new EmailNotificationSender(db, …)` → `new EmailNotificationSender(config, transport,
//                                          policy, new NotificationLog(db), log)`
//
// The laws this net pins:
//   1. one 16-char Crockford code, stored as an HMAC (never plaintext), single use,
//      10-minute DB-clock expiry; a valid code moves the chat between accounts.
//   2. the chat id is the message's address, never a log line.
//   3. severities filter BOTH ways (warn-only / critical-only), info never pushes,
//      and the door refuses info/unknown with `NotificationErrors.InvalidSeverity`.
//   4. unlink stops the next push immediately and keeps the user's severity choice.
//   5. the template follows the type (raised/offline/resolved) in the recipient's
//      language, with no subject (Telegram has none).
//   6. a success is one `sent` row with attempts=1; retries end in the row
//      (sent/attempts=3), never in the caller; the alert row is untouchable.
//   7. the wall: user_channels is tenant-scoped, channel_config is write-forbidden
//      to the app, and the bot's only write path is the SECURITY DEFINER function.
//   8. the bot parses only its own payload (`/start CODE`, `/start@bot CODE`, bare code).
//
// Delivery is asynchronous (D9g): every fact waits for the log row instead of sleeping.

public sealed class TelegramPushTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static CallerContext Admin(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private static CallerContext Viewer(Guid userId, Guid tenantId) =>
        new(userId, tenantId, ["viewer"]);

    // ---- rigs and fakes ---------------------------------------------------------

    private sealed record Push(string ChatId, string Text);

    private sealed class FakeTelegramTransport : ITelegramTransport
    {
        public int FailuresBeforeSuccess { get; set; }
        public bool AlwaysFail { get; set; }
        public List<Push> Sent { get; } = [];

        private int _calls;

        public Task SendMessageAsync(
            string botToken, string chatId, string text, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (AlwaysFail || call <= FailuresBeforeSuccess)
                throw new InvalidOperationException("suite telegram down");

            lock (Sent) Sent.Add(new Push(chatId, text));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
            string botToken, long offset, int timeoutSeconds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TelegramUpdate>>([]);
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
        public FakeTelegramTransport Transport { get; init; } = null!;
        public InProcessEventBus Bus { get; private set; } = null!;
        public TelegramQueue Queue { get; private set; } = null!;
        public TelegramNotificationSender Sender { get; private set; } = null!;
        public TelegramChannels Channels { get; private set; } = null!;
        public TelegramNotifier Notifier { get; private set; } = null!;
        public TelegramDispatchWorker Worker { get; private set; } = null!;
        public CapturingLogger<TelegramNotifier> NotifierLog { get; } = new();
        public CapturingLogger<TelegramNotificationSender> SenderLog { get; } = new();

        public static async Task<Rig> StartAsync(PostgresFixture pg, FakeTelegramTransport transport)
        {
            var rig = new Rig { Transport = transport };

            var db = pg.Notifications;
            var config = new TelegramChannelConfig(db);
            var renderer = new AlertMessageRenderer(db);
            var policy = new TelegramRetryPolicy(
                [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]);

            rig.Channels = new TelegramChannels(db);
            rig.Queue = new TelegramQueue();
            rig.Sender = new TelegramNotificationSender(
                config, transport, policy, new NotificationLog(db), rig.SenderLog);
            rig.Bus = new InProcessEventBus();
            rig.Notifier = new TelegramNotifier(
                rig.Bus, new UserDirectory(pg.Db), rig.Channels, renderer, rig.Queue, rig.NotifierLog);

            // the host's worker, driven here on the same queue
            rig.Worker = new TelegramDispatchWorker(
                rig.Queue, rig.Sender,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramDispatchWorker>.Instance);
            await rig.Worker.StartAsync(CancellationToken.None);
            return rig;
        }

        public Task PublishRaisedAsync(AlertSnapshot alert) =>
            Bus.PublishAsync(new AlertRaised(alert));

        public Task PublishResolvedAsync(AlertSnapshot alert) =>
            Bus.PublishAsync(new AlertResolved(
                alert.TenantId, alert.AlertId, alert.ResolvedAt ?? T0,
                AlertReasons.ConditionRestored, alert));

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
            Name: $"Push {label}",
            Slug: $"push-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@push.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    /// <summary>A second person in the tenant, with their own language (owner lane: setup only).</summary>
    private async Task<Guid> AddUserAsync(TenantRegistrationResult tenant, string locale, string label)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO identity.users (tenant_id, email, display_name, locale)
            VALUES (@t, @e, @n, @l)
            RETURNING id;
            """, conn);
        cmd.Parameters.AddWithValue("t", tenant.Tenant.TenantId);
        cmd.Parameters.AddWithValue("e", $"{Uid()}-{label}@push.test");
        cmd.Parameters.AddWithValue("n", $"{label} Person");
        cmd.Parameters.AddWithValue("l", locale);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> EnsureTemplateAsync(string label)
    {
        var key = $"push-{label}-{Uid()}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
                VALUES (@k, 'قالب', 'Push Template');
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

    /// <summary>The channel row the bot/sender read; the token is sealed exactly as the tool seals it.</summary>
    private async Task ConfigureTelegramAsync(bool enabled = true)
    {
        var (cipher, nonce, tag) = SecretBox.Seal("suite-telegram-token", "telegram");
        var config = System.Text.Json.JsonSerializer.Serialize(new { username = "nabd_suite_bot" });

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO notifications.channel_config
                (channel, enabled, config, secret_enc, secret_nonce, secret_tag, updated_at)
            VALUES ('telegram', @on, @c::jsonb, @e, @n, @t, now())
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
             WHERE channel = 'telegram'
               AND (@a::uuid IS NULL OR alert_id = @a)
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
                Assert.Fail($"expected {expected} telegram log row(s) for {alertId}, saw {rows.Count}");
            await Task.Delay(25);
        }
    }

    /// <summary>The silence must be proven by a bounded wait: nothing arrives, and no reply exists to ask.</summary>
    private async Task AssertQuietAsync(Guid alertId, TimeSpan? quiet = null)
    {
        await Task.Delay(quiet ?? TimeSpan.FromMilliseconds(300));
        Assert.Empty(await RowsAsync(alertId));
    }

    // ---- fact 1 — the one-time code, its hash, and the chat that changes hands -----

    [Fact]
    public async Task a_one_time_code_links_its_chat_once_and_another_account_can_take_it_over()
    {
        var tenant = await RegisterAsync("link");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);
        var admin = Admin(tenant);

        // not linked yet: the state is the default, not an error
        var before = await channels.GetAsync(admin);
        Assert.True(before.IsSuccess, $"state failed: {before.Error}");
        Assert.False(before.Value.Linked);
        Assert.Equal(new[] { "warn", "critical" }, before.Value.Severities);

        var issued = await channels.IssueLinkCodeAsync(admin);
        Assert.True(issued.IsSuccess, $"issue failed: {issued.Error}");

        var ticket = issued.Value;
        Assert.Equal(16, ticket.Code.Length);
        Assert.All(ticket.Code, c => Assert.Contains(c, Crockford));   // no I/L/O/U
        Assert.Equal("nabd_suite_bot", ticket.BotUsername);             // non-secret config
        Assert.InRange(ticket.ExpiresAt,
            DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));

        // the row keeps a 32-byte HMAC; the plaintext code exists only in this reply
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT length(link_code_hash),
                       position(upper(@code) in upper(encode(link_code_hash, 'hex')))
                  FROM notifications.user_channels
                 WHERE user_id = @u AND channel = 'telegram';
                """, conn);
            cmd.Parameters.AddWithValue("u", admin.UserId);
            cmd.Parameters.AddWithValue("code", ticket.Code);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(32, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
        }

        // one use: the second verify of the same code learns nothing
        Assert.Equal(admin.UserId, await channels.VerifyAsync(ticket.Code, "100100"));
        Assert.Null(await channels.VerifyAsync(ticket.Code, "999999"));

        var state = (await channels.GetAsync(admin)).Value;
        Assert.True(state.Linked);
        Assert.NotNull(state.VerifiedAt);
        Assert.Equal(new[] { "warn", "critical" }, state.Severities);

        // a second account (viewer — the chat is the user's own) takes the same chat over
        var second = await AddUserAsync(tenant, "en", "second");
        var viewer = Viewer(second, tenant.Tenant.TenantId);
        var secondTicket = (await channels.IssueLinkCodeAsync(viewer)).Value;
        Assert.Equal(second, await channels.VerifyAsync(secondTicket.Code, "100100"));
        Assert.True((await channels.GetAsync(viewer)).Value.Linked);

        var detached = (await channels.GetAsync(admin)).Value;
        Assert.False(detached.Linked);                                  // the chat moved, the row stayed
        Assert.Null(detached.VerifiedAt);
        Assert.Equal(new[] { "warn", "critical" }, detached.Severities); // the choice is the user's
    }

    // ---- fact 2 — 10-minute expiry, on the database's clock -----------------------

    [Fact]
    public async Task an_expired_code_is_refused_on_the_real_clock()
    {
        var tenant = await RegisterAsync("expiry");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);
        var admin = Admin(tenant);

        var ticket = (await channels.IssueLinkCodeAsync(admin)).Value;

        // owner lane, setup only: move the stored expiry one second into the past
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE notifications.user_channels
                   SET link_code_expires_at = now() - interval '1 second'
                 WHERE user_id = @u AND channel = 'telegram';
                """, conn);
            cmd.Parameters.AddWithValue("u", admin.UserId);
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Null(await channels.VerifyAsync(ticket.Code, "200200"));

        var state = (await channels.GetAsync(admin)).Value;
        Assert.False(state.Linked);
        Assert.Null(state.VerifiedAt);
    }

    // ---- fact 3 — a wrong code changes nothing, and burns nothing -----------------

    [Fact]
    public async Task a_wrong_code_changes_nothing_and_does_not_burn_the_live_code()
    {
        var tenant = await RegisterAsync("wrong");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);
        var admin = Admin(tenant);

        var ticket = (await channels.IssueLinkCodeAsync(admin)).Value;

        Assert.Null(await channels.VerifyAsync("ZZZZZZZZZZZZZZZZ", "300300"));

        // one character off — a near miss is a miss
        var nearMiss = ticket.Code[..15] + (ticket.Code[^1] == 'A' ? 'B' : 'A');
        Assert.Null(await channels.VerifyAsync(nearMiss, "300300"));
        Assert.False((await channels.GetAsync(admin)).Value.Linked);

        // the failures did not consume the live code
        Assert.Equal(admin.UserId, await channels.VerifyAsync(ticket.Code, "300300"));
    }

    // ---- fact 4 — the severity pick filters both ways; info is refused at the door -

    [Fact]
    public async Task severities_filter_both_ways_and_info_is_refused_at_the_door()
    {
        var tenant = await RegisterAsync("severity");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);

        var admin = Admin(tenant);                                        // en, warn-only
        var arabic = await AddUserAsync(tenant, "ar", "arabic");          // ar, critical-only
        var arabicCtx = Viewer(arabic, tenant.Tenant.TenantId);

        Assert.NotNull(await channels.VerifyAsync(
            (await channels.IssueLinkCodeAsync(admin)).Value.Code, "400001"));
        Assert.NotNull(await channels.VerifyAsync(
            (await channels.IssueLinkCodeAsync(arabicCtx)).Value.Code, "400002"));

        Assert.True((await channels.SetSeveritiesAsync(admin, ["warn"])).IsSuccess);
        Assert.True((await channels.SetSeveritiesAsync(arabicCtx, ["critical"])).IsSuccess);

        var asset = await CreateAssetAsync(tenant, "severity");
        var transport = new FakeTelegramTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        var warn = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
        await rig.PublishRaisedAsync(warn);
        var row = Assert.Single(await WaitRowsAsync(warn.AlertId, 1));
        Assert.Equal("400001", row.Recipient);
        Assert.Equal("en", row.Locale);

        var critical = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "critical");
        await rig.PublishRaisedAsync(critical);
        row = Assert.Single(await WaitRowsAsync(critical.AlertId, 1));
        Assert.Equal("400002", row.Recipient);
        Assert.Equal("ar", row.Locale);
        Assert.Equal("alert_raised", row.TemplateKey);

        // info is dashboard-only (plan/06 §6) — it never reaches a chat
        var info = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "info");
        await rig.PublishRaisedAsync(info);
        await AssertQuietAsync(info.AlertId);

        // and the door refuses it explicitly, changing nothing
        var refused = await channels.SetSeveritiesAsync(admin, ["info"]);
        Assert.False(refused.IsSuccess);
        Assert.Equal(NotificationErrors.InvalidSeverity, refused.Error);

        var unknown = await channels.SetSeveritiesAsync(admin, ["bogus"]);
        Assert.Equal(NotificationErrors.InvalidSeverity, unknown.Error);
        Assert.Equal(new[] { "warn" }, (await channels.GetAsync(admin)).Value.Severities);

        // duplicates collapse; the order the user chose is the order stored
        var deduped = await channels.SetSeveritiesAsync(arabicCtx, ["critical", "critical"]);
        Assert.Single(deduped.Value.Severities);
    }

    // ---- fact 5 — unlink is immediate, idempotent, and keeps the choice -----------

    [Fact]
    public async Task unlink_stops_the_next_push_immediately_and_keeps_the_choice()
    {
        var tenant = await RegisterAsync("unlink");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);
        var admin = Admin(tenant);

        Assert.NotNull(await channels.VerifyAsync(
            (await channels.IssueLinkCodeAsync(admin)).Value.Code, "500001"));
        await channels.SetSeveritiesAsync(admin, ["critical"]);

        var unlinked = await channels.UnlinkAsync(admin);
        Assert.True(unlinked.IsSuccess);
        Assert.False(unlinked.Value.Linked);
        Assert.Null(unlinked.Value.VerifiedAt);
        Assert.Equal(new[] { "critical" }, unlinked.Value.Severities);   // the mute survives

        Assert.True((await channels.UnlinkAsync(admin)).IsSuccess);      // idempotent: no row = success

        var asset = await CreateAssetAsync(tenant, "unlink");
        var transport = new FakeTelegramTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        var alert = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "critical");
        await rig.PublishRaisedAsync(alert);
        await AssertQuietAsync(alert.AlertId);                            // unlink is not eventual
        Assert.Empty(transport.Sent);

        // re-linking lands on the user's own choice, not on the default
        Assert.NotNull(await channels.VerifyAsync(
            (await channels.IssueLinkCodeAsync(admin)).Value.Code, "500002"));
        Assert.Equal(new[] { "critical" }, (await channels.GetAsync(admin)).Value.Severities);
    }

    // ---- fact 6 — template per type, recipient language, no subject ---------------

    [Fact]
    public async Task the_template_follows_the_type_in_the_recipient_language_with_no_subject()
    {
        var tenant = await RegisterAsync("templates");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);

        var arabic = await AddUserAsync(tenant, "ar", "arabic");
        var arabicCtx = Viewer(arabic, tenant.Tenant.TenantId);
        Assert.NotNull(await channels.VerifyAsync(
            (await channels.IssueLinkCodeAsync(arabicCtx)).Value.Code, "600001"));

        var asset = await CreateAssetAsync(tenant, "templates");
        var transport = new FakeTelegramTransport();
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

        // the same six telegram seeds, read in Arabic
        Assert.Contains(transport.Sent, p => p.Text.Contains("نبض — تنبيه"));
        Assert.Contains(transport.Sent, p => p.Text.Contains("جهاز متوقف") && p.Text.Contains("مدة الصمت"));
        Assert.Contains(transport.Sent, p => p.Text.Contains("تم الحل"));

        // Telegram has no subject: the email chrome must not leak into the chat
        Assert.All(transport.Sent, p => Assert.DoesNotContain("[Nabd]", p.Text));
        Assert.All(transport.Sent, p => Assert.DoesNotContain("[نبض]", p.Text));
    }

    // ---- fact 7 — the row is the evidence; the caller never sees a throw ----------

    [Fact]
    public async Task a_sent_push_is_one_honest_row_and_retries_end_in_the_row()
    {
        var tenant = await RegisterAsync("rows");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);
        var admin = Admin(tenant);
        Assert.NotNull(await channels.VerifyAsync(
            (await channels.IssueLinkCodeAsync(admin)).Value.Code, "700001"));

        var asset = await CreateAssetAsync(tenant, "rows");
        var transport = new FakeTelegramTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        // 1) a success is exactly one row, with its template and locale
        var first = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "critical");
        await rig.PublishRaisedAsync(first);                       // must not throw

        var sent = Assert.Single(await WaitRowsAsync(first.AlertId, 1));
        Assert.Equal("telegram", sent.Channel);
        Assert.Equal("sent", sent.Status);
        Assert.Equal(1, sent.Attempts);
        Assert.Null(sent.Error);
        Assert.Equal("alert_raised", sent.TemplateKey);
        Assert.Equal("en", sent.Locale);
        Assert.Equal("700001", sent.Recipient);
        Assert.NotNull(sent.SentAt);
        Assert.True(sent.SentAt > DateTimeOffset.UtcNow.AddMinutes(-5),
            "sent_at must be a real stamp, not the fixture clock");
        Assert.Single(transport.Sent);

        // 2) two failures then success → sent with attempts=3 (the row tells the story)
        transport.FailuresBeforeSuccess = 3;                       // calls 2 and 3 fail, 4 wins
        var retried = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
        await rig.PublishRaisedAsync(retried);

        var afterRetry = Assert.Single(await WaitRowsAsync(retried.AlertId, 1));
        Assert.Equal("sent", afterRetry.Status);
        Assert.Equal(3, afterRetry.Attempts);
        Assert.Null(afterRetry.Error);
        Assert.Equal(2, transport.Sent.Count);

        // 3) permanent failure → failed with attempts=3, and the alert is untouched
        transport.AlwaysFail = true;
        var doomed = await InsertAlertAsync(tenant, asset, AlertTypes.Offline, "warn");
        await rig.PublishRaisedAsync(doomed);

        var failed = Assert.Single(await WaitRowsAsync(doomed.AlertId, 1));
        Assert.Equal("failed", failed.Status);
        Assert.Equal(3, failed.Attempts);
        Assert.Contains("suite telegram down", failed.Error);
        Assert.Null(failed.SentAt);
        Assert.Equal(2, transport.Sent.Count);                     // no third success

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM alerting.alerts WHERE id = @a;", conn);
        cmd.Parameters.AddWithValue("a", doomed.AlertId);
        Assert.Equal("open", (string)(await cmd.ExecuteScalarAsync())!);
    }

    // ---- fact 8 — the chat id is an address, not a log line -----------------------

    [Fact]
    public async Task the_chat_id_lives_only_in_the_log_recipient()
    {
        var tenant = await RegisterAsync("privacy");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);
        var admin = Admin(tenant);
        Assert.NotNull(await channels.VerifyAsync(
            (await channels.IssueLinkCodeAsync(admin)).Value.Code, "800001"));

        var asset = await CreateAssetAsync(tenant, "privacy");
        var transport = new FakeTelegramTransport();
        await using var rig = await Rig.StartAsync(pg, transport);

        var ok = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
        await rig.PublishRaisedAsync(ok);
        Assert.Single(await WaitRowsAsync(ok.AlertId, 1));

        transport.AlwaysFail = true;
        var bad = await InsertAlertAsync(tenant, asset, AlertTypes.Threshold, "warn");
        await rig.PublishRaisedAsync(bad);
        Assert.Single(await WaitRowsAsync(bad.AlertId, 1));

        var logged = string.Join("\n",
            rig.SenderLog.Messages.Concat(rig.NotifierLog.Messages)
                .Concat(rig.SenderLog.Exceptions.Select(e => e?.Message ?? ""))
                .Concat(rig.NotifierLog.Exceptions.Select(e => e?.Message ?? "")));
        Assert.DoesNotContain("800001", logged);                   // the chat is not log material
        Assert.DoesNotContain("suite-telegram-token", logged);     // nor is the bot token

        Assert.Equal("800001", (await RowsAsync(ok.AlertId)).Single().Recipient);
        Assert.Equal("800001", (await RowsAsync(bad.AlertId)).Single().Recipient);
    }

    // ---- fact 9 — the wall ------------------------------------------------------

    [Fact]
    public async Task user_channels_is_tenant_walled_and_the_app_cannot_write_channel_config()
    {
        var a = await RegisterAsync("wall-a");
        var b = await RegisterAsync("wall-b");
        await ConfigureTelegramAsync();
        var channels = new TelegramChannels(pg.Notifications);
        var admin = Admin(a);
        Assert.NotNull(await channels.VerifyAsync(
            (await channels.IssueLinkCodeAsync(admin)).Value.Code, "900001"));

        // tenant A sees its own row through the app lane
        var (connA, txA) = await pg.Notifications.OpenTenantScopeAsync(a.Tenant.TenantId);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM notifications.user_channels;", connA, txA);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            await txA.CommitAsync();
        }
        finally
        {
            await connA.DisposeAsync();
        }

        // tenant B sees nothing — a chat is not a shared feed
        var (connB, txB) = await pg.Notifications.OpenTenantScopeAsync(b.Tenant.TenantId);
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM notifications.user_channels;", connB, txB);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
            await txB.CommitAsync();
        }
        finally
        {
            await connB.DisposeAsync();
        }

        // the app may read the channel row but never write it (secret door is owner-only)
        await using (var connC = await pg.Notifications.OpenPlatformReadAsync())
        {
            await using var write = new NpgsqlCommand("""
                UPDATE notifications.channel_config SET enabled = false WHERE channel = 'telegram';
                """, connC);
            var denied = await Assert.ThrowsAsync<PostgresException>(
                () => write.ExecuteNonQueryAsync());
            Assert.Equal("42501", denied.SqlState);                // insufficient_privilege
        }

        // and a row naming another tenant is rejected by RLS, not merely hidden
        var (connC2, txC2) = await pg.Notifications.OpenTenantScopeAsync(b.Tenant.TenantId);
        try
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO notifications.user_channels (user_id, tenant_id, channel)
                VALUES (@u, @t, 'telegram');
                """, connC2, txC2);
            ins.Parameters.AddWithValue("u", admin.UserId);
            ins.Parameters.AddWithValue("t", a.Tenant.TenantId);
            var denied = await Assert.ThrowsAsync<PostgresException>(
                () => ins.ExecuteNonQueryAsync());
            Assert.Equal("42501", denied.SqlState);
            await txC2.RollbackAsync();
        }
        finally
        {
            await connC2.DisposeAsync();
        }
    }

    // ---- fact 10 — the bot's parser is narrow by design ---------------------------

    [Fact]
    public void the_bot_parses_only_its_own_start_payload()
    {
        // mint: 16 Crockford chars, never the letters a code must not contain
        var minted = TelegramLinkCodes.Mint();
        Assert.Equal(16, minted.Length);
        Assert.All(minted, c => Assert.Contains(c, Crockford));
        Assert.NotEqual(minted, TelegramLinkCodes.Mint());

        // /start CODE, /start@bot CODE and a bare CODE all work …
        var code = "ABCDEFGH23456789";
        Assert.Equal(code, TelegramLinkCodes.Extract($"/start {code}"));
        Assert.Equal(code, TelegramLinkCodes.Extract($"/start@nabd_suite_bot {code}"));
        Assert.Equal(code, TelegramLinkCodes.Extract(code));

        // … lowercase is folded, and the confusable letters are normalized
        Assert.Equal("ABCDEFGH23456780", TelegramLinkCodes.Extract("abcdefgh2345678o"));
        Assert.Equal("ABCDEFGH23456781", TelegramLinkCodes.Extract("ABCDEFGH2345678l"));

        // ordinary chat text is silence, never a reply
        Assert.Null(TelegramLinkCodes.Extract("hello"));
        Assert.Null(TelegramLinkCodes.Extract("سلام"));
        Assert.Null(TelegramLinkCodes.Extract("/start"));
        Assert.Null(TelegramLinkCodes.Extract("/start@nabd_suite_bot"));
        Assert.Null(TelegramLinkCodes.Extract("short code"));
        Assert.Null(TelegramLinkCodes.Extract(""));
        Assert.Null(TelegramLinkCodes.Extract(null));
    }
}
