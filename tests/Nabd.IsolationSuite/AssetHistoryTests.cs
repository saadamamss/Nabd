namespace Nabd.IsolationSuite;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Xunit;

// P5 spec 04 (4a) — the history read path: honest buckets over raw + continuous
// aggregates (FR-41/FR-43, plan/04 §6/§10, plan/09 §4.1).
//
// The net pins the server rules, not the chart:
//   * source/width selection and the 1,500-point cap (58 s × 1,490 at 24 h);
//   * the tenant wall at the function AND at the reader;
//   * the grants that keep the app off the caggs while the console lane reads them;
//   * the aggregate arithmetic (weighted across devices, real counts at the edges);
//   * gaps are absent, archived assets stay readable, an empty range still reports
//     its resolution (the REV 4 pin), and the door's status codes.
//
// Seeding: tenants through the REAL registration door; templates/channels/assets/
// devices/readings through the owner lane (fixtures, the same class of act as the
// suite's ALTER ROLE passwords). The app lane is exercised by the reader itself.

public sealed class AssetHistoryTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly PostgresFixture pg;

    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.UseEnvironment("Development"));
    private readonly HttpClient _client;
    private Guid? _templateId;

    public AssetHistoryTests(PostgresFixture pg)
    {
        this.pg = pg;
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---- seeding -------------------------------------------------------------

    private async Task<Guid> TemplateAsync()
    {
        if (_templateId is { } known) return known;

        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
            VALUES (@k, 'قالب تاريخ', 'History Template')
            RETURNING id
            """, conn))
        {
            cmd.Parameters.AddWithValue("k", $"hist-{Uid()}");
            _templateId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        await using (var ch = new NpgsqlCommand("""
            INSERT INTO assets.channel_definitions
                (template_id, key, name_ar, name_en, unit, data_type, lower_limit, upper_limit)
            VALUES (@t, 'temperature', 'حرارة', 'Temperature', '°C', 'number', 2.0, 8.0),
                   (@t, 'door', 'حالة الباب', 'Door', 'state', 'bool', NULL, NULL)
            """, conn))
        {
            ch.Parameters.AddWithValue("t", _templateId.Value);
            await ch.ExecuteNonQueryAsync();
        }

        return _templateId.Value;
    }

    private async Task<Guid> TenantAsync(string label) => (await RegisterAsync(label)).TenantId;

    private async Task<(Guid TenantId, string Email)> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var email = $"{Uid()}-{label}@hist.test";
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"History {label}",
            Slug: $"hist-{label}-{Uid()}",
            AdminEmail: email,
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return (result.Value.Tenant.TenantId, email);
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

    private async Task<Guid> DeviceAsync(Guid tenantId, Guid assetId)
    {
        var template = await TemplateAsync();
        var id = Guid.NewGuid();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO assets.devices (id, tenant_id, asset_id, template_id, name, token_hash)
            VALUES (@id, @tenant, @asset, @template, 'Probe', @hash)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("asset", assetId);
        cmd.Parameters.AddWithValue("template", template);
        cmd.Parameters.AddWithValue("hash", $"hash-{id:N}");
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task SeedReadingAsync(
        Guid tenantId, Guid deviceId, DateTimeOffset time, double value,
        string channel = "temperature", long seq = 1)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO telemetry.readings
                (time, received_at, tenant_id, device_id, channel_key, value, seq)
            VALUES (@t, now(), @tenant, @device, @channel, @value, @seq)
            """, conn);
        cmd.Parameters.AddWithValue("t", time.UtcDateTime);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("device", deviceId);
        cmd.Parameters.AddWithValue("channel", channel);
        cmd.Parameters.AddWithValue("value", value);
        cmd.Parameters.AddWithValue("seq", seq);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>One device, 1,490 readings on the 58 s grid: a full raw day at the cap.</summary>
    private async Task SeedFullDayAsync(Guid tenantId, Guid deviceId)
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO telemetry.readings
                (time, received_at, tenant_id, device_id, channel_key, value, seq)
            SELECT '2026-11-24 00:00:00+00'::timestamptz + (i * interval '58 seconds'),
                   now(), @tenant, @device, 'temperature', 5.0, i
              FROM generate_series(0, 1489) AS i
            """, conn);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("device", deviceId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static CallerContext AdminOf(Guid tenantId) =>
        new(Guid.NewGuid(), tenantId, ["tenant_admin"]);

    private static readonly DateTimeOffset RawFrom = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RawTo = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    private Task<Nabd.SharedKernel.Result<AssetHistoryResult>> ReadAsync(
        Guid tenantId, Guid assetId, string? channel, DateTimeOffset from, DateTimeOffset to) =>
        new AssetHistoryReader(pg.Assets).ReadAsync(
            AdminOf(tenantId), assetId, channel, from, to);

    // ---- fact 1: source selection, the server's rule (C1) --------------------

    [Fact]
    public async Task source_selection_switches_at_the_documented_boundaries()
    {
        var tenant = await TenantAsync("source");
        var asset = await AssetAsync(tenant, "source");

        // exactly 24 h → raw
        var raw = await ReadAsync(tenant, asset, "temperature", RawFrom, RawTo);
        Assert.True(raw.IsSuccess, raw.Error);
        Assert.Equal("raw", raw.Value.Source);

        // 24 h + 1 min → hourly (the cagg may be unmaterialized: real-time aggregation covers it)
        var hourly = await ReadAsync(tenant, asset, "temperature", RawFrom, RawTo.AddMinutes(1));
        Assert.True(hourly.IsSuccess, hourly.Error);
        Assert.Equal("hourly", hourly.Value.Source);

        // exactly 30 d → hourly; 30 d + 1 min → daily
        var thirty = await ReadAsync(tenant, asset, "temperature", RawTo.AddDays(-30), RawTo);
        Assert.True(thirty.IsSuccess, thirty.Error);
        Assert.Equal("hourly", thirty.Value.Source);

        var daily = await ReadAsync(tenant, asset, "temperature", RawTo.AddDays(-30).AddMinutes(-1), RawTo);
        Assert.True(daily.IsSuccess, daily.Error);
        Assert.Equal("daily", daily.Value.Source);
    }

    // ---- fact 2: the point cap, pinned as 58 s × 1,490 ----------------------

    [Fact]
    public async Task a_full_raw_day_is_fifty_eight_second_buckets_and_never_more_than_the_cap()
    {
        var tenant = await TenantAsync("cap");
        var asset = await AssetAsync(tenant, "cap");
        var device = await DeviceAsync(tenant, asset);
        await SeedFullDayAsync(tenant, device);

        var result = await ReadAsync(tenant, asset, "temperature", RawFrom, RawTo);
        Assert.True(result.IsSuccess, result.Error);

        Assert.Equal("raw", result.Value.Source);
        Assert.Equal(58, result.Value.BucketSeconds);
        Assert.Equal(1_490, result.Value.Points.Count);
        Assert.All(result.Value.Points, p => Assert.Equal(1, p.N));

        // the width is a whole number of seconds and the series is under the cap
        Assert.True(result.Value.Points.Count <= 1_500);

        // one hour is 3 s buckets (3,600 / 1,500), never 1 s × 3,600 points
        var hour = await ReadAsync(tenant, asset, "temperature", RawTo.AddHours(-1), RawTo);
        Assert.True(hour.IsSuccess, hour.Error);
        Assert.Equal(3, hour.Value.BucketSeconds);
    }

    // ---- fact 3: the tenant wall, at the function and at the reader ----------

    [Fact]
    public async Task another_tenants_asset_is_not_found_and_returns_no_point()
    {
        var tenantA = await TenantAsync("a");
        var tenantB = await TenantAsync("b");
        var assetA = await AssetAsync(tenantA, "a-asset");
        var deviceA = await DeviceAsync(tenantA, assetA);
        await SeedReadingAsync(tenantA, deviceA, T0, 5.0);

        // the reader: B's caller on A's asset
        var refused = await ReadAsync(tenantB, assetA, "temperature", T0.AddHours(-1), T0.AddHours(1));
        Assert.False(refused.IsSuccess);
        Assert.Equal(AssetErrors.AssetNotFound, refused.Error);

        // the function directly: B's tenant + A's asset is not_found, zero points
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT state, jsonb_array_length(points)
              FROM assets.asset_history(@tenant, @asset, 'temperature', @from, @to)
            """, conn);
        cmd.Parameters.AddWithValue("tenant", tenantB);
        cmd.Parameters.AddWithValue("asset", assetA);
        cmd.Parameters.AddWithValue("from", T0.AddHours(-1).UtcDateTime);
        cmd.Parameters.AddWithValue("to", T0.AddHours(1).UtcDateTime);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("not_found", reader.GetString(0));
        if (!reader.IsDBNull(1))
            Assert.Equal(0, reader.GetInt32(1));
    }

    // ---- fact 4: the grants — the app never reads a cagg, the console can ----

    [Fact]
    public async Task the_app_cannot_read_the_caggs_but_the_console_lane_can()
    {
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT has_table_privilege('nabd_app', 'telemetry.readings_hourly', 'SELECT'),
                   has_table_privilege('nabd_app', 'telemetry.readings_daily',  'SELECT'),
                   has_schema_privilege('nabd_admin', 'telemetry', 'USAGE'),
                   has_table_privilege('nabd_admin', 'telemetry.readings_hourly', 'SELECT'),
                   has_table_privilege('nabd_admin', 'telemetry.readings_daily',  'SELECT'),
                   has_function_privilege('nabd_app',
                       'assets.asset_history(uuid,uuid,text,timestamptz,timestamptz)', 'EXECUTE'),
                   EXISTS (
                       SELECT 1
                         FROM pg_proc p,
                              aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                        WHERE p.oid = 'assets.asset_history(uuid,uuid,text,timestamptz,timestamptz)'::regprocedure
                          AND a.grantee = 0)
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.False(reader.GetBoolean(0), "nabd_app must never SELECT a cagg");
        Assert.False(reader.GetBoolean(1), "nabd_app must never SELECT a cagg");
        Assert.True(reader.GetBoolean(2), "nabd_admin needs schema USAGE before any table grant");
        Assert.True(reader.GetBoolean(3), "spec 09's console reads the hourly cagg");
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5), "the app calls the function, not the view");
        Assert.False(reader.GetBoolean(6), "PUBLIC must not execute the definer function");
    }

    // ---- fact 5: the arithmetic — weighted across devices, real edge counts --

    [Fact]
    public async Task a_two_device_bucket_is_the_weighted_mean_and_edges_keep_their_real_count()
    {
        var tenant = await TenantAsync("avg");
        var asset = await AssetAsync(tenant, "two probes");
        var one = await DeviceAsync(tenant, asset);
        var two = await DeviceAsync(tenant, asset);

        // 18 Nov 10:05 → 4.0 and 10:45 → 6.0 on device one; 10:20 → 9.0 on device two
        await SeedReadingAsync(tenant, one, T0.AddMinutes(5), 4.0, seq: 1);
        await SeedReadingAsync(tenant, one, T0.AddMinutes(45), 6.0, seq: 2);
        await SeedReadingAsync(tenant, two, T0.AddMinutes(20), 9.0, seq: 3);

        // 7 days → hourly; one bucket at 10:00 with all three readings
        var result = await ReadAsync(tenant, asset, "temperature", T0.AddDays(-3), T0.AddDays(3));
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("hourly", result.Value.Source);

        var bucket = Assert.Single(result.Value.Points, p => p.T == T0);
        Assert.Equal(4, bucket.Min);
        Assert.Equal(9, bucket.Max);
        Assert.Equal(19.0 / 3.0, bucket.Avg, 6);
        Assert.Equal(3, bucket.N);

        // a partial edge: a 1 h window starting mid-hour keeps the real count, never padded
        var edge = await ReadAsync(tenant, asset, "temperature", T0.AddMinutes(10), T0.AddMinutes(30));
        Assert.True(edge.IsSuccess, edge.Error);
        var partial = Assert.Single(edge.Value.Points);
        Assert.Equal(1, partial.N);
        Assert.Equal(9, partial.Avg, 6);
    }

    // ---- fact 6: validation — one code per fact -----------------------------

    [Fact]
    public async Task range_and_channel_validation_answer_the_documented_codes()
    {
        var tenant = await TenantAsync("valid");
        var asset = await AssetAsync(tenant, "valid");

        var backwards = await ReadAsync(tenant, asset, "temperature", T0, T0.AddHours(-1));
        Assert.False(backwards.IsSuccess);
        Assert.Equal(HistoryErrors.InvalidRange, backwards.Error);

        var tooLong = await ReadAsync(tenant, asset, "temperature", T0.AddDays(-400), T0);
        Assert.False(tooLong.IsSuccess);
        Assert.Equal(HistoryErrors.InvalidRange, tooLong.Error);

        var blank = await ReadAsync(tenant, asset, "  ", T0.AddHours(-1), T0);
        Assert.False(blank.IsSuccess);
        Assert.Equal(HistoryErrors.UnknownChannel, blank.Error);

        var missing = await ReadAsync(tenant, asset, null, T0.AddHours(-1), T0);
        Assert.False(missing.IsSuccess);
        Assert.Equal(HistoryErrors.UnknownChannel, missing.Error);

        // a channel that exists in the catalog but not in THIS asset's template
        var foreign = await ReadAsync(tenant, asset, "level", T0.AddHours(-1), T0);
        Assert.False(foreign.IsSuccess);
        Assert.Equal(HistoryErrors.UnknownChannel, foreign.Error);
    }

    // ---- fact 7: an empty range still reports its resolution (REV 4) --------

    [Fact]
    public async Task an_empty_range_is_ok_with_its_resolution_and_no_points()
    {
        var tenant = await TenantAsync("empty");
        var asset = await AssetAsync(tenant, "silent");

        var result = await ReadAsync(tenant, asset, "temperature", RawFrom, RawTo);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("raw", result.Value.Source);
        Assert.Equal(58, result.Value.BucketSeconds);
        Assert.Empty(result.Value.Points);
    }

    // ---- fact 8: gaps are gaps — no synthetic zero --------------------------

    [Fact]
    public async Task a_missing_hour_is_absent_never_a_zero()
    {
        var tenant = await TenantAsync("gap");
        var asset = await AssetAsync(tenant, "gappy");
        var device = await DeviceAsync(tenant, asset);

        await SeedReadingAsync(tenant, device, T0, 5.0);
        await SeedReadingAsync(tenant, device, T0.AddHours(3), 7.0);

        // 7 days → hourly; two points, and none at 11:00/12:00
        var result = await ReadAsync(tenant, asset, "temperature", T0.AddDays(-1), T0.AddDays(6));
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("hourly", result.Value.Source);

        Assert.Equal(2, result.Value.Points.Count);
        Assert.DoesNotContain(result.Value.Points, p => p.T == T0.AddHours(1));
        Assert.DoesNotContain(result.Value.Points, p => p.T == T0.AddHours(2));
    }

    // ---- fact 9: archived is soft — history stays readable ------------------

    [Fact]
    public async Task an_archived_asset_keeps_its_history()
    {
        var tenant = await TenantAsync("arch");
        var archived = await AssetAsync(tenant, "archived", active: false);
        var device = await DeviceAsync(tenant, archived);
        await SeedReadingAsync(tenant, device, T0, 6.0);

        var result = await ReadAsync(tenant, archived, "temperature", T0.AddHours(-1), T0.AddHours(1));
        Assert.True(result.IsSuccess, result.Error);
        Assert.Single(result.Value.Points);
        Assert.Equal(6, result.Value.Points[0].Avg, 6);
    }

    // ---- facts 3 + 6 at the door: status codes over real HTTP ---------------

    [Fact]
    public async Task the_door_answers_200_404_and_422_with_the_documented_codes()
    {
        var (seller, adminEmail) = await RegisterAsync("door");
        var asset = await AssetAsync(seller, "door-asset");
        var device = await DeviceAsync(seller, asset);
        await SeedReadingAsync(seller, device, T0, 6.0);

        // a real login through the real door, as the owner's UI does
        var login = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = adminEmail, password = "CorrectHorse1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString()!;

        async Task<HttpResponseMessage> GetAsync(string assetId, string query, string? asToken = null)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/assets/{assetId}/history?{query}");
            if (asToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", asToken);
            return await _client.SendAsync(request);
        }

        string Range(DateTimeOffset from, DateTimeOffset to) =>
            $"channel=temperature&from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}"
            + $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}";

        var dayFrom = T0.AddHours(-12);
        var dayTo = T0.AddHours(12);

        var ok = await GetAsync(asset.ToString(), Range(dayFrom, dayTo), token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()))
        {
            var root = doc.RootElement;
            Assert.Equal("raw", root.GetProperty("source").GetString());
            Assert.Equal(58, root.GetProperty("bucketSeconds").GetInt32());
            Assert.Equal("temperature", root.GetProperty("channel").GetString());
            Assert.Single(root.GetProperty("points").EnumerateArray());
        }

        // cross-tenant id → 404 with the door's existing code
        var other = await TenantAsync("door-b");
        var otherAsset = await AssetAsync(other, "other");
        var cross = await GetAsync(otherAsset.ToString(), Range(dayFrom, dayTo), token);
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Contains("assets:asset_not_found", await cross.Content.ReadAsStringAsync());

        // foreign channel → 422 history:unknown_channel
        var badChannel = await GetAsync(asset.ToString(),
            $"channel=level&from={Uri.EscapeDataString(dayFrom.ToString("O"))}"
            + $"&to={Uri.EscapeDataString(dayTo.ToString("O"))}", token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badChannel.StatusCode);
        Assert.Contains("history:unknown_channel", await badChannel.Content.ReadAsStringAsync());

        // missing bound → 422 history:invalid_range
        var missingBound = await GetAsync(asset.ToString(), "channel=temperature", token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingBound.StatusCode);
        Assert.Contains("history:invalid_range", await missingBound.Content.ReadAsStringAsync());

        // anonymous → 401
        var anonymous = await GetAsync(asset.ToString(), Range(dayFrom, dayTo));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
