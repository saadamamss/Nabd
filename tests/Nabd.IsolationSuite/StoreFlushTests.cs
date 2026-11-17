namespace Nabd.IsolationSuite;

using Microsoft.Extensions.Logging;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.TelemetryStore;
using Nabd.TelemetryStore.Contracts;
using Npgsql;
using Xunit;

// P4 spec 01 — STEP 3: the flush store's facts against the REAL schema (real
// migrations, real FK — readings.device_id REFERENCES assets.devices, so the
// devices here are MINTED through the proven service paths, never invented):
// (a) fact 4 (redelivery idempotence) + R4's cross-tenant law: ONE flush
//     carries rows from TWO tenants; the broker's redelivery stores nothing;
// (b) fact 5: 600 rows → EXACTLY 2 command round-trips (chunk 500) — spied
//     from OUTSIDE via Npgsql's own documented command-completion log event
//     (NpgsqlEventId.CommandExecutionCompleted = 2001; Npgsql 10 has no public
//     command interceptor — verified in the DLL) — and all 600 land;
// (c) fact 12 (store half): the R10 classification — a REAL 23514 (a check
//     constraint the test manufactures in its own disposable DB) classifies
//     deterministic; a dead port classifies transient with an empty SqlState;
//     a cancelled token rethrows OCE UNWRAPPED (never StoreFlushException).

public sealed class StoreFlushTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Base = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Received = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture Pg;

    public StoreFlushTests(PostgresFixture pg) => Pg = pg;

    // ---- seeding: the SAME proven paths SetResolverTests pins -----------------

    private sealed record TenantSeed(Guid TenantId, CallerContext Admin);

    private async Task<TenantSeed> SeedTenantAsync(string label)
    {
        var registration = new TenantRegistration(Pg.Db);
        var registered = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"StoreFlush {label}",
            Slug: $"sf-{label}-{Guid.NewGuid().ToString("N")[..12]}",
            AdminEmail: $"{Guid.NewGuid().ToString("N")[..12]}-{label}@sf.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(registered.IsSuccess, $"SETUP (registration): {registered.Error}");
        return new TenantSeed(
            registered.Value.Tenant.TenantId,
            new CallerContext(registered.Value.AdminUser.UserId, registered.Value.Tenant.TenantId, ["tenant_admin"]));
    }

    private async Task<Guid> FridgeTemplateIdAsync()
    {
        await using var conn = await Pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM assets.asset_type_templates WHERE key = 'fridge';", conn);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> CreateAssetAsync(CallerContext admin, Guid tenantId, Guid templateId)
    {
        var assets = new AssetCatalog(Pg.Assets);
        var asset = await assets.CreateAssetAsync(admin, new CreateAssetRequest(
            tenantId, templateId, $"SF anchor {Guid.NewGuid():N}".AsSpan(0, 24).ToString(), null, null));
        Assert.True(asset.IsSuccess, $"SETUP (asset): {asset.Error}");
        return asset.Value.AssetId;
    }

    private async Task<Guid> MintAsync(CallerContext admin, Guid tenantId, Guid assetId)
    {
        var devices = new DeviceLifecycle(Pg.Assets, new DeviceTokenHasher());
        var minted = await devices.CreateDeviceAsync(admin, new DeviceCreateRequest(
            tenantId, assetId, $"SF sensor {Guid.NewGuid():N}".AsSpan(0, 24).ToString()));
        Assert.True(minted.IsSuccess, $"SETUP (device): {minted.Error}");
        return minted.Value.Device.DeviceId;
    }

    private async Task<Guid> SeedDeviceAsync(string label)
    {
        var t = await SeedTenantAsync(label);
        var asset = await CreateAssetAsync(t.Admin, t.TenantId, await FridgeTemplateIdAsync());
        return await MintAsync(t.Admin, t.TenantId, asset);
    }

    private static List<FlushReadingDto> Readings(Guid tenant, Guid device, int count, long firstSeq)
        => Enumerable.Range(0, count).Select(i => new FlushReadingDto(
            tenant, device, "t",
            Base.AddMinutes(i),                     // unique time per row (the partition key)
            (double)i, firstSeq + i, Received)).ToList();

    private async Task<int> CountRowsAsync(params Guid[] deviceIds)
    {
        await using var conn = await Pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM telemetry.readings WHERE device_id = ANY(@ids);", conn);
        cmd.Parameters.AddWithValue("ids", deviceIds);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The fact-5 spy: the STORE built around a door whose data source carries
    /// a counting logger factory. We count Npgsql's OWN documented completion
    /// events (id 2001) — observation from OUTSIDE the code, no internal counter.
    /// </summary>
    private static (PostgresReadingsStore Store, List<int> Commands) SpyStore()
    {
        var commands = new List<int>();
        var factory = LoggerFactory.Create(b => b.AddProvider(new CommandCountingProvider(commands)));
        var cs = $"Host={Environment.GetEnvironmentVariable("NABD_DB_HOST")}"
                 + $";Port={Environment.GetEnvironmentVariable("NABD_DB_PORT")}"
                 + $";Database={Environment.GetEnvironmentVariable("NABD_DB_NAME")}"
                 + ";Username=nabd_app;Password=suite-local-password";
        var dataSource = new NpgsqlDataSourceBuilder(cs).UseLoggerFactory(factory).Build();
        return (new PostgresReadingsStore(new TelemetryStoreDb(dataSource)), commands);
    }

    private sealed class CommandCountingProvider(List<int> ids) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Counter(ids);
        public void Dispose() { }

        private sealed class Counter(List<int> ids) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Information
                    && eventId.Id == NpgsqlEventId.CommandExecutionCompleted)
                    ids.Add(eventId.Id);
            }
        }
    }

    // ---- fact 4 + R4: cross-tenant in ONE flush; redelivery stores nothing ----

    [Fact]
    public async Task a_redelivery_stores_nothing_twice_across_tenants_in_one_flush()
    {
        var t1 = await SeedTenantAsync("t1");
        var t2 = await SeedTenantAsync("t2");
        var fridge = await FridgeTemplateIdAsync();
        var a1 = await CreateAssetAsync(t1.Admin, t1.TenantId, fridge);
        var a2 = await CreateAssetAsync(t2.Admin, t2.TenantId, fridge);
        var d1 = await MintAsync(t1.Admin, t1.TenantId, a1);
        var d2 = await MintAsync(t2.Admin, t2.TenantId, a2);

        var store = new PostgresReadingsStore(new TelemetryStoreDb());
        // ONE flush, TWO tenants: tenant rides per row (R4 — the identity key
        // is conflict-safe across tenants; readings has no RLS, 0003/0018).
        var flush = Readings(t1.TenantId, d1, 100, firstSeq: 1)
            .Concat(Readings(t2.TenantId, d2, 100, firstSeq: 1)).ToList();

        await store.StoreFlushAsync(flush);   // commit 1
        await store.StoreFlushAsync(flush);   // the broker's redelivery world
        await store.StoreFlushAsync(flush);   // crash-between-commit-and-ack world

        Assert.Equal(200, await CountRowsAsync(d1, d2));   // 200 stored ONCE — idempotent
    }

    // ---- fact 5: 600 rows → EXACTLY 2 command round-trips (chunk 500) ---------

    [Fact]
    public async Task six_hundred_rows_pay_exactly_two_store_round_trips()
    {
        var d = await SeedDeviceAsync("f5");   // the FK needs a REAL device

        var (store, commands) = SpyStore();
        await store.StoreFlushAsync(Readings(await TenantOfAsync(d), d, 600, firstSeq: 1));

        Assert.Equal(2, commands.Count);       // 500 + 100 — one statement per chunk
        Assert.Equal(600, await CountRowsAsync(d));
    }

    // ---- fact 12 (store half): R10 classification ------------------------------

    [Fact]
    public async Task the_store_classifies_deterministic_transient_and_cancellation_per_r10()
    {
        var d = await SeedDeviceAsync("d12");
        var tenant = await TenantOfAsync(d);
        var store = new PostgresReadingsStore(new TelemetryStoreDb());

        // (1) DETERMINISTIC, for real: the test manufactures a 23514 world in
        // its OWN disposable DB (channel 'poison-channel' is schema-lawful
        // content — the constraint makes it row-content-dependent forever).
        await using (var conn = await Pg.Owner.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            "ALTER TABLE telemetry.readings ADD CONSTRAINT sr_no_poison CHECK (channel_key <> 'poison-channel');", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        var pge = await Assert.ThrowsAsync<StoreFlushException>(() => store.StoreFlushAsync(
        [
            new FlushReadingDto(tenant, d, "t", Base.AddMinutes(0), 1.0, 1, Received),
            new FlushReadingDto(tenant, d, "poison-channel", Base.AddMinutes(1), 1.0, 2, Received),
        ]));
        Assert.True(pge.IsDeterministic, "23514 is R10's deterministic class — the ladder's world");
        Assert.Equal("23514", pge.SqlState);

        // (2) TRANSIENT, for real: a dead port — the driver fails with no
        // SQLSTATE at all; empty SqlState + not-deterministic (abort-all).
        var dead = new PostgresReadingsStore(new TelemetryStoreDb(new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=nabd;Username=nabd_app;Password=x").Build()));
        var dead_ = await Assert.ThrowsAsync<StoreFlushException>(() => dead.StoreFlushAsync(
            Readings(tenant, d, 1, firstSeq: 10)));
        Assert.False(dead_.IsDeterministic, "an unknown error is transient, never a receipt");
        Assert.Equal(string.Empty, dead_.SqlState);

        // (3) CANCELLATION: pre-cancelled token → OCE rethrown UNWRAPPED —
        // wrapping it would make the pump read shutdown as a failure verdict.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.StoreFlushAsync(Readings(tenant, d, 1, firstSeq: 20), cts.Token));
    }

    private async Task<Guid> TenantOfAsync(Guid deviceId)
    {
        await using var conn = await Pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT tenant_id FROM assets.devices WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", deviceId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }
}