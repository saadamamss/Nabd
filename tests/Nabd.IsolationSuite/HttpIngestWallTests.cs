namespace Nabd.IsolationSuite;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Ingestion;
using Nabd.Ingestion.Contracts;
using Npgsql;
using Nabd.SharedKernel;
using Nabd.TelemetryStore.Contracts;
using Xunit;

// P3 spec 05 — the HTTP ingest door's wall (plan/05 §4, VETO ruling:
// 200-only-after-DB-commit, bounded concurrency, 429 on saturation). The REAL
// host boots in-process (WebApplicationFactory — the HttpWallTests shape), the
// device is minted through the REAL P2 lifecycle (token shown once, hash
// stored), and every store-side proof is read back through the OWNER lane.
// The door rides the SAME ValidationPipeline the MQTT path uses — spec 03's
// facts carry over untouched; these facts prove the TRANSPORT laws: auth lane,
// size caps, catalog-to-HTTP mapping, quota, replay idempotency, clock clamp.

public sealed class HttpIngestWallTests : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private readonly PostgresFixture pg;

    // ---- log capture (the security-event assertion's instrument) ---------------

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Lines);
        public void Dispose() { }

        private sealed class Capture(string cat, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) sink.Enqueue($"{logLevel}: {formatter(state, exception)} [{cat}]");
            }
        }
    }

    private readonly CaptureLoggerProvider _logs = new();
    private readonly WebApplicationFactory<Program> _factory = null!;
    private readonly HttpClient _client = null!;

    public HttpIngestWallTests(PostgresFixture pg)
    {
        this.pg = pg;
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");     // same shape as the owner's Postman runs
                b.ConfigureLogging(l => l.Services.AddSingleton<ILoggerProvider>(_logs));
            });
        _client = _factory.CreateClient();
    }

    // ---- seeding: tenant (real door) → template+channels (owner lane) → --------
    // ---- asset (real door) → device + REAL token (real lifecycle) -------------

    private async Task<(Guid Device, string Token, TenantRegistrationResult Tenant)> SeedDeviceAsync()
    {
        var registration = new TenantRegistration(pg.Db);
        var reg = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"Ingest {Uid()}", Slug: $"ig-{Uid()}",
            AdminEmail: $"{Uid()}@ig.test", AdminDisplayName: "Ingest Admin",
            AdminPassword: "CorrectHorse1", AdminLocale: "en"));
        Assert.True(reg.IsSuccess, $"SETUP failed (registration): {reg.Error}");
        var tenantId = reg.Value.Tenant.TenantId;

        var templateKey = $"ig-{Uid()}";
        var template = Guid.NewGuid();
        var asset = Guid.NewGuid();
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var batch = new NpgsqlCommand("""
                INSERT INTO assets.asset_type_templates (id, key, name_ar, name_en) VALUES (@tpl, @k, 'قالب', 'Ingest Template');
                INSERT INTO assets.channel_definitions (template_id, key, name_ar, name_en, unit, data_type)
                    VALUES (@tpl, 'temperature', 'حرارة', 'Temperature', '°C', 'number'),
                           (@tpl, 'humidity', 'رطوبة', 'Humidity', '%', 'number'),
                           (@tpl, 'door', 'باب', 'Door', 'state', 'number');
                INSERT INTO assets.assets (id, tenant_id, template_id, name) VALUES (@a, @t, @tpl, 'Ingest Asset');
                """, conn);
            batch.Parameters.AddWithValue("tpl", template);
            batch.Parameters.AddWithValue("k", templateKey);
            batch.Parameters.AddWithValue("t", tenantId);
            batch.Parameters.AddWithValue("a", asset);
            await batch.ExecuteNonQueryAsync();
        }

        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var minted = await devices.CreateDeviceAsync(
            new CallerContext(reg.Value.AdminUser.UserId, tenantId, ["tenant_admin"]),
            new DeviceCreateRequest(TargetTenantId: tenantId, AssetId: asset, Name: "Ingest Device"));
        Assert.True(minted.IsSuccess, $"SETUP failed (mint): {minted.Error}");
        return (minted.Value.Device.DeviceId, minted.Value.Token, reg.Value);
    }

    // ---- payload composers -----------------------------------------------------

    private static string Payload(string ts, long seq, params (string Channel, double Value)[] readings)
    {
        var rr = string.Join(",", readings.Select(r =>
            $$"""{"t":{{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"c":"{{r.Channel}}"}"""));
        return $$"""{"v":1,"ts":"{{ts}}","seq":{{seq}},"rr":[{{rr}}]}""";
    }

    private static string PayloadWithOverride(string ts, long seq, params (string Channel, double Value, string? OverrideTs)[] readings)
    {
        var rr = string.Join(",", readings.Select(r =>
            r.OverrideTs is null
                ? $$"""{"t":{{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"c":"{{r.Channel}}"}"""
                : $$"""{"t":{{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"c":"{{r.Channel}}","ts":"{{r.OverrideTs}}"}"""));
        return $$"""{"v":1,"ts":"{{ts}}","seq":{{seq}},"rr":[{{rr}}]}""";
    }

    private async Task<HttpResponseMessage> PostAsync(string? token, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/v1") { Content = content };
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(req);
    }

    private static (int Status, string? Code, JsonElement Body) Read(HttpResponseMessage res, string json)
    {
        var body = JsonDocument.Parse(json).RootElement;
        var code = body.TryGetProperty("code", out var c) ? c.GetString() : null;
        return ((int)res.StatusCode, code, body);
    }

    // ---- fact 1: anonymous → 401 with the catalog code -------------------------

    [Fact]
    public async Task an_anonymous_post_is_401_with_the_catalog_code()
    {
        var (_, token, _) = await SeedDeviceAsync();   // seeded anyway: the door must not care
        var res = await PostAsync(null, Payload("2026-11-14T12:00:00Z", 1, ("temperature", 22.5)));
        var (status, code, _) = Read(res, await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (HttpStatusCode)status);
        Assert.Equal(IngestionErrors.E401_AUTH, code);
    }

    // ---- fact 2: a revoked token is 401 AND the door logs the security event ---

    [Fact]
    public async Task a_revoked_token_is_401_and_the_security_event_is_logged()
    {
        var (device, token, tenant) = await SeedDeviceAsync();
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var revoke = await devices.RevokeDeviceAsync(
            new CallerContext(tenant.AdminUser.UserId, tenant.Tenant.TenantId, ["tenant_admin"]), device);
        Assert.True(revoke.IsSuccess, $"SETUP failed (revoke): {revoke.Error}");

        var res = await PostAsync(token, Payload("2026-11-14T12:00:00Z", 1, ("temperature", 22.5)));
        var (status, code, _) = Read(res, await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (HttpStatusCode)status);
        Assert.Equal(IngestionErrors.E401_AUTH, code);

        Assert.Contains(_logs.Lines, l => l.Contains(IngestionErrors.E401_AUTH));   // the security event, on record
    }

    // ---- fact 3: 501 readings → 4xx (the HTTP batch cap is 500, plan/05 §4) ----

    [Fact]
    public async Task a_501_reading_batch_is_rejected_4xx()
    {
        var (_, token, _) = await SeedDeviceAsync();
        var big = Payload("2026-11-14T12:00:00Z", 1,
            Enumerable.Range(0, 501).Select(i => ("temperature", (double)i)).ToArray());

        var res = await PostAsync(token, big);
        var (status, code, _) = Read(res, await res.Content.ReadAsStringAsync());

        Assert.True(status is 400 or 413 or 422, $"expected 4xx, got {status}");
        Assert.Equal(IngestionErrors.DLQ_INVALID, code);   // structural → the parse cap, not a channel verdict
    }

    // ---- fact 4: body over 1 MB → 413 -------------------------------------------

    [Fact]
    public async Task a_body_over_1mb_is_413()
    {
        var (_, token, _) = await SeedDeviceAsync();
        var fat = Payload("2026-11-14T12:00:00Z", 1, ("temperature", 22.5)) + " " + new string('x', 1_100_000);

        var res = await PostAsync(token, fat);
        var (status, _, _) = Read(res, await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (HttpStatusCode)status);
    }

    // ---- fact 5: unknown channel → 422 with the catalog code --------------------

    [Fact]
    public async Task an_unknown_channel_is_422_with_the_catalog_code()
    {
        var (_, token, _) = await SeedDeviceAsync();
        var res = await PostAsync(token, Payload("2026-11-14T12:00:00Z", 1, ("pressure", 1.0)));
        var (status, code, _) = Read(res, await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (HttpStatusCode)status);
        Assert.Equal(IngestionErrors.E422_UNKNOWN_CHANNEL, code);
    }

    // ---- fact 6: an infinite value (1e400) → 422 E422_RANGE ---------------------

    [Fact]
    public async Task an_infinite_value_is_422_range()
    {
        var (_, token, _) = await SeedDeviceAsync();
        var res = await PostAsync(token, """{"v":1,"ts":"2026-11-14T12:00:00Z","seq":1,"rr":[{"t":1e400,"c":"temperature"}]}""");
        var (status, code, _) = Read(res, await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (HttpStatusCode)status);
        Assert.Equal(IngestionErrors.E422_RANGE, code);
    }

    // ---- fact 7: a bare NaN literal never parses (DLQ_INVALID, 400) -------------

    [Fact]
    public async Task a_bare_nan_literal_is_400_invalid_not_range()
    {
        var (_, token, _) = await SeedDeviceAsync();
        var res = await PostAsync(token, """{"v":1,"ts":"2026-11-14T12:00:00Z","seq":1,"rr":[{"t":NaN,"c":"temperature"}]}""");
        var (status, code, _) = Read(res, await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (HttpStatusCode)status);
        Assert.Equal(IngestionErrors.DLQ_INVALID, code);   // verified parser fact: NaN throws AT parse
    }

    // ---- fact 8: the quota — the 31st request in minute 1 is 429 + hint ---------

    [Fact]
    public async Task the_31st_request_in_minute_one_is_429_with_a_backoff_hint()
    {
        var (_, token, _) = await SeedDeviceAsync();
        var payload = Payload("2026-11-14T12:00:00Z", 1, ("temperature", 22.5));

        HttpResponseMessage? last = null;
        for (var i = 0; i < 31; i++)
        {
            last = await PostAsync(token, payload);
        }

        var (status, code, _) = Read(last!, await last!.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.TooManyRequests, (HttpStatusCode)status);
        Assert.Equal(IngestionErrors.E429_QUOTA, code);
        Assert.True(last.Headers.Contains("Retry-After"), "the backoff hint must ride the 429");
    }

    // ---- fact 9: re-POST the same batch ×2 — both 200, rows unchanged -----------

    [Fact]
    public async Task reposting_the_same_batch_never_duplicates()
    {
        var (device, token, _) = await SeedDeviceAsync();
        var payload = Payload("2026-11-14T12:00:00Z", 7, ("temperature", 22.5), ("humidity", 61.0), ("door", 1.0));

        var r1 = await PostAsync(token, payload);
        var r2 = await PostAsync(token, payload);
        var (s1, _, _) = Read(r1, await r1.Content.ReadAsStringAsync());
        var (s2, _, _) = Read(r2, await r2.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)s1);
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)s2);

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM telemetry.readings WHERE device_id = @d", conn);
        cmd.Parameters.AddWithValue("d", device);
        Assert.Equal(3, (long)(await cmd.ExecuteScalarAsync())!);
    }

    // ---- fact 10: far-future ts → clamped, flagged, STILL 200 -------------------

    [Fact]
    public async Task a_far_future_ts_is_clamped_with_a_warning_still_200()
    {
        var (device, token, _) = await SeedDeviceAsync();
        var future = DateTime.UtcNow.AddMinutes(10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var res = await PostAsync(token, Payload(future, 1, ("temperature", 22.5)));
        var (status, _, body) = Read(res, await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)status);              // clamp NEVER rejects
        Assert.Contains(IngestionErrors.E422_TS,
            body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!));

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT time = received_at FROM telemetry.readings WHERE device_id = @d", conn);
        cmd.Parameters.AddWithValue("d", device);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!,
            "the clamped event time IS the server's receive time");
    }

    // ---- fact 11: per-reading ts override lands with its OWN time ----------------

    [Fact]
    public async Task a_per_reading_ts_override_lands_with_its_own_time()
    {
        var (device, token, _) = await SeedDeviceAsync();
        var raw = DateTime.UtcNow.AddSeconds(-60);
        var t0 = new DateTime(raw.Ticks - (raw.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);   // whole seconds — the composer's precision
        var t0s = t0.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var over = t0.AddSeconds(120).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var res = await PostAsync(token, PayloadWithOverride(t0s, 1,
            ("temperature", 22.5, null), ("humidity", 61.0, over)));
        var (status, _, _) = Read(res, await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)status);

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT channel_key, time FROM telemetry.readings WHERE device_id = @d ORDER BY time
            """, conn);
        cmd.Parameters.AddWithValue("d", device);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(string Ch, DateTime T)>();
        while (await r.ReadAsync())
            rows.Add((r.GetString(0), r.GetFieldValue<DateTime>(1)));

        Assert.Equal(2, rows.Count);
        Assert.Equal(("temperature", t0), (rows[0].Ch, rows[0].T.ToUniversalTime()));
        Assert.Equal(("humidity", t0.AddSeconds(120)), (rows[1].Ch, rows[1].T.ToUniversalTime()));
    }

    // ---- fact 12: saturation sheds — bound reached → 429 BEFORE the store -------

    [Fact]
    public async Task the_door_sheds_when_the_concurrency_bound_is_reached()
    {
        var device = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var door = new HttpIngestDoor(
            new FixedDevices(device),
            new DeviceRateLimiter(),
            new ValidationPipeline(new FakeChannels(Keys("temperature")), new FakeIdentity()),
            new BlockingStore(gate.Task),
            new SilentTracker(),
            new RejectionCounters(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpIngestDoor>.Instance,
            concurrencyBound: 1);   // the spike's shape, tightened for a deterministic fact

        var first = door.IngestAsync("tok", Encoding.UTF8.GetBytes(Payload("2026-11-14T12:00:00Z", 1, ("temperature", 22.5))));
        var shed = await door.IngestAsync("tok", Encoding.UTF8.GetBytes(Payload("2026-11-14T12:00:00Z", 2, ("temperature", 23.5))));

        Assert.Equal(HttpStatusCode.TooManyRequests, (HttpStatusCode)shed.Status);
        Assert.Equal(IngestionErrors.E429_QUOTA, shed.Code);
        Assert.True(shed.RetryAfterSeconds is not null, "sheds carry the hint too");

        gate.SetResult();
        var landed = await first;
        Assert.Equal(200, landed.Status);
    }

    // ---- fact 13: the health/ready JSON carries the rejection counters ----------

    [Fact]
    public async Task health_ready_json_carries_the_rejection_counters()
    {
        var (_, token, _) = await SeedDeviceAsync();
        var res = await PostAsync(token, Payload("2026-11-14T12:00:00Z", 1, ("pressure", 1.0)));
        var (status, _, _) = Read(res, await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (HttpStatusCode)status);

        var health = await _client.GetAsync("/health/ready");
        var body = await health.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("rejected", out var rejected),
            "the readiness JSON must carry the rejected map (spec 06's metrics floor)");
        Assert.True(rejected.TryGetProperty(IngestionErrors.E422_UNKNOWN_CHANNEL, out var n));
        Assert.True(n.GetInt64() >= 1, "a 422 that just happened must be counted");
    }

    // ---- fakes for the door-level fact ------------------------------------------

    private sealed class FixedDevices(Guid device) : IDeviceLifecycle
    {
        public Task<Guid?> VerifyTokenAsync(string presentedToken, CancellationToken ct = default) => Task.FromResult<Guid?>(device);
        public Task<bool> BrokerAuthenticateAsync(Guid deviceId, string presentedToken, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AuthorizePublishAsync(Guid deviceId, string topic, string action, CancellationToken ct = default) => Task.FromResult(true);

        // The door only ever calls the three pre-auth probes above; the caller-
        // scoped lifecycle operations have no meaning on a fake (spec 05's fact
        // exercises the door, not the admin surface).
        public Task<Result<DeviceMintResult>> CreateDeviceAsync(CallerContext acting, DeviceCreateRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("not exercised by the door's facts");
        public Task<Result<IReadOnlyList<DeviceSnapshot2>>> ListByAssetAsync(CallerContext acting, Guid assetId, CancellationToken ct = default)
            => throw new NotSupportedException("not exercised by the door's facts");
        public Task<Result<DeviceMintResult>> ReissueTokenAsync(CallerContext acting, Guid deviceId, CancellationToken ct = default)
            => throw new NotSupportedException("not exercised by the door's facts");
        public Task<Result<Unit>> RevokeDeviceAsync(CallerContext acting, Guid deviceId, CancellationToken ct = default)
            => throw new NotSupportedException("not exercised by the door's facts");
    }

    private sealed class FakeChannels(IReadOnlySet<string> keys) : IDeviceChannels
    {
        public Task<IReadOnlySet<string>?> GetChannelKeysAsync(Guid deviceId, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<string>?>(keys);
    }

    private static IReadOnlySet<string> Keys(params string[] k) => new HashSet<string>(k);

    private sealed class FakeIdentity : IIngestionIdentity
    {
        public Task<Guid?> ResolveTenantIdAsync(Guid deviceId, CancellationToken ct = default) => Task.FromResult<Guid?>(Guid.NewGuid());
    }

    private sealed class BlockingStore(Task until) : IReadingsStore
    {
        public async Task StoreBatchAsync(ReadingBatchDto batch, CancellationToken ct = default) => await until;

        public async Task StoreFlushAsync(IReadOnlyList<FlushReadingDto> readings, CancellationToken ct = default) => await until;

        public Task<IReadOnlyList<ReadingValueDto>> GetHistoryAsync(HistoryQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReadingValueDto>>([]);
    }

    private sealed class SilentTracker : IDeviceStatusTracker
    {
        public Task MarkSeenAsync(Guid deviceId, Guid tenantId, DateTimeOffset lastSeenUtc, DateTimeOffset lastEventTimeUtc, CancellationToken ct = default) => Task.CompletedTask;

        public DateTimeOffset? LastSeen(Guid deviceId) => null;   // spec 07's read side
    }
}
