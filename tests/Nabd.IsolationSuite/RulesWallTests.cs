namespace Nabd.IsolationSuite;

using Npgsql;
using Nabd.Assets;
using Nabd.Assets.Contracts;
using Nabd.Identity;
using Nabd.Identity.Contracts;
using Nabd.Rules;
using Nabd.Rules.Contracts;
using Xunit;

// P4 spec 03 — the rules wall: RLS fail-shut on rules.rules, the CRUD ladder at the
// service layer (layer 2 survives with layer 1 absent, same promise as the P1/P2
// walls), the negative laws (duplicate, offline-refused), and FR-15's seeding law
// (mint → one rule per limited template channel, idempotent). Everything behind
// the REAL 0021 policies against the real container — no mocks on a wall.

public sealed class RulesWallTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static CallerContext Admin(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["tenant_admin"]);

    private static CallerContext Viewer(TenantRegistrationResult t) =>
        new(t.AdminUser.UserId, t.Tenant.TenantId, ["viewer"]);

    private async Task<TenantRegistrationResult> RegisterAsync(string label)
    {
        var registration = new TenantRegistration(pg.Db);
        var result = await registration.RegisterTenantAsync(new RegisterTenantRequest(
            Name: $"RulesWall {label}",
            Slug: $"rw-{label}-{Uid()}",
            AdminEmail: $"{Uid()}-{label}@rw.test",
            AdminDisplayName: $"{label} Admin",
            AdminPassword: "CorrectHorse1",
            AdminLocale: "en"));
        Assert.True(result.IsSuccess, $"SETUP failed (registration): {result.Error}");
        return result.Value;
    }

    /// <summary>A template + the fridge-shaped channels: temperature HAS limits 2–8,
    /// door has none — the seeding law's discriminating pair (FR-15).</summary>
    private async Task<Guid> EnsureTemplateAsync(string label)
    {
        var tplKey = $"rw-{label}-{Uid()}";
        await using (var conn = await pg.Owner.OpenConnectionAsync())
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO assets.asset_type_templates (key, name_ar, name_en)
                VALUES (@k, 'قالب', 'Wall Template');
                INSERT INTO assets.channel_definitions
                    (template_id, key, name_ar, name_en, unit, data_type, lower_limit, upper_limit)
                SELECT t.id, c.key, 'قناة', 'Channel', c.unit, 'number', c.lo, c.hi
                FROM (VALUES
                    ('temperature', '°C',    2.0::double precision, 8.0::double precision),
                    ('door',        'state', NULL::double precision,   NULL::double precision)
                ) AS c(key, unit, lo, hi)
                JOIN assets.asset_type_templates t ON t.key = @k;
                """, conn);
            ins.Parameters.AddWithValue("k", tplKey);
            await ins.ExecuteNonQueryAsync();
        }
        await using var sel = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM assets.asset_type_templates WHERE key = @k;", sel);
        cmd.Parameters.AddWithValue("k", tplKey);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> EnsureAssetAsync(TenantRegistrationResult tenant, Guid templateId)
    {
        var assets = new AssetCatalog(pg.Assets);
        var created = await assets.CreateAssetAsync(
            Admin(tenant),
            new CreateAssetRequest(tenant.Tenant.TenantId, templateId, $"Anchor {Uid()}", null, null));
        Assert.True(created.IsSuccess, $"SETUP failed (asset): {created.Error}");
        return created.Value.AssetId;
    }

    private async Task<DeviceMintResult> MintAsync(TenantRegistrationResult tenant, Guid assetId, string label)
    {
        var hasher = new DeviceTokenHasher();
        var devices = new DeviceLifecycle(pg.Assets, hasher);
        var minted = await devices.CreateDeviceAsync(
            Admin(tenant),
            new DeviceCreateRequest(tenant.Tenant.TenantId, assetId, $"Dev {label}"));
        Assert.True(minted.IsSuccess, $"SETUP failed (mint): {minted.Error}");
        return minted.Value;
    }

    private async Task<Guid> SeedRuleAsync(TenantRegistrationResult tenant, Guid deviceId, string channel)
    {
        // direct app-role seed INSIDE the tenant scope — the RLS-legal way to
        // place a foreign-tenant probe row is: there is none (fact 2 proves it).
        var rules = new DeviceRules(pg.Rules);
        var created = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, deviceId, channel, "below", ThresholdLow: 1.0));
        Assert.True(created.IsSuccess, $"SETUP failed (rule seed): {created.Error}");
        return created.Value.RuleId;
    }

    // ---- fact 1 — 0021's shape: the two tables exist with the §7 columns ----

    [Fact]
    public async Task the_two_tables_exist_with_the_plan_04_s7_columns()
    {
        var columns = new Dictionary<string, List<string>>();
        await using var conn = await pg.Owner.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'rules' AND table_name IN ('rules', 'rule_eval_state')
            ORDER BY table_name, ordinal_position;
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            columns.TryAdd(table, []);
            columns[table].Add(reader.GetString(1));
        }

        Assert.Equal([
            "id", "tenant_id", "device_id", "channel_key", "rule_type",
            "threshold_low", "threshold_high", "duration_seconds",
            "cooldown_minutes", "severity", "enabled"], columns["rules"]);
        Assert.Equal(
            ["rule_id", "last_state", "violating_since", "last_evaluated_at"], columns["rule_eval_state"]);
    }

    // ---- fact 2 — the RLS wall: fail-shut, cross-tenant invisible ------------

    [Fact]
    public async Task the_wall_refuses_every_cross_tenant_reach()
    {
        var tenantA = await RegisterAsync("a");
        var tenantB = await RegisterAsync("b");
        var template = await EnsureTemplateAsync("t");
        var assetB = await EnsureAssetAsync(tenantB, template);
        var deviceB = await MintAsync(tenantB, assetB, "b");
        await SeedRuleAsync(tenantB, deviceB.Device.DeviceId, "temperature");

        // 2a: the app role WITHOUT a tenant scope sees ZERO rows (missing_ok → NULL → no match)
        await using var naked = await pg.Assets.OpenPreAuthConnectionAsync();
        await using var nakedCmd = new NpgsqlCommand("SELECT count(*) FROM rules.rules;", naked);
        Assert.Equal(0L, (long)(await nakedCmd.ExecuteScalarAsync())!);

        // 2b: tenant A's service cannot LIST tenant B's device's rules (RLS hid the device: not-found)
        var rules = new DeviceRules(pg.Rules);
        var forbidden = await rules.ListByDeviceAsync(Admin(tenantA), deviceB.Device.DeviceId);
        Assert.False(forbidden.IsSuccess, "cross-tenant list must not succeed");
        Assert.Equal(RuleErrors.DeviceNotFound, forbidden.Error);

        // 2c: tenant A cannot CREATE on tenant B's device (RLS hides it the same way)
        var created = await rules.CreateRuleAsync(Admin(tenantA), new RuleCreateRequest(
            tenantA.Tenant.TenantId, deviceB.Device.DeviceId, "temperature", "above", ThresholdHigh: 9.0));
        Assert.False(created.IsSuccess, "cross-tenant create must not succeed");
        Assert.Equal(RuleErrors.DeviceNotFound, created.Error);

        // 2d: tenant A cannot DELETE tenant B's rule by guessing its id (RLS: 0 rows → not-found)
        var ruleId = (await rules.ListByDeviceAsync(Admin(tenantB), deviceB.Device.DeviceId)).Value[0].RuleId;
        var deleted = await rules.DeleteRuleAsync(Admin(tenantA), deviceB.Device.DeviceId, ruleId);
        Assert.False(deleted.IsSuccess, "cross-tenant delete must not succeed");
        Assert.Equal(RuleErrors.RuleNotFound, deleted.Error);
    }

    // ---- fact 3 — the CRUD ladder through the service ------------------------

    [Fact]
    public async Task the_crud_ladder_runs_end_to_end()
    {
        var tenant = await RegisterAsync("crud");
        var template = await EnsureTemplateAsync("crud");
        var asset = await EnsureAssetAsync(tenant, template);
        var device = await MintAsync(tenant, asset, "crud");
        var rules = new DeviceRules(pg.Rules);

        // the mint already seeded temperature's outside_range rule (FR-15, fact 6);
        // the ladder wants a clean slate — clearing the seed is itself a delete.
        foreach (var seeded in (await rules.ListByDeviceAsync(
                     Admin(tenant), device.Device.DeviceId)).Value)
            Assert.True((await rules.DeleteRuleAsync(
                Admin(tenant), device.Device.DeviceId, seeded.RuleId)).IsSuccess);

        // the matrix law: viewer reads, viewer never manages (07 §5 layer 2)
        var viewerWrite = await rules.CreateRuleAsync(Viewer(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, device.Device.DeviceId, "temperature", "above", ThresholdHigh: 9.0));
        Assert.False(viewerWrite.IsSuccess, "viewer must not create rules");
        Assert.Equal(IdentityErrors.Forbidden, viewerWrite.Error);

        // CREATE
        var created = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, device.Device.DeviceId, "temperature", "outside_range",
            ThresholdLow: 2.0, ThresholdHigh: 8.0));
        Assert.True(created.IsSuccess, created.Error);
        var rule = created.Value;
        Assert.Equal("outside_range", rule.RuleType);
        Assert.Equal(2.0, rule.ThresholdLow!.Value);
        Assert.Equal(8.0, rule.ThresholdHigh!.Value);
        Assert.Equal(60, rule.DurationSeconds);   // the §8 defaults ride creation
        Assert.Equal(60, rule.CooldownMinutes);
        Assert.Equal("warn", rule.Severity);
        Assert.True(rule.Enabled);

        // LIST (viewer may read)
        var listed = await rules.ListByDeviceAsync(Viewer(tenant), device.Device.DeviceId);
        Assert.True(listed.IsSuccess, listed.Error);
        Assert.Single(listed.Value);

        // UPDATE (PATCH semantics: only severity + enabled move)
        var updated = await rules.UpdateRuleAsync(Admin(tenant), device.Device.DeviceId, rule.RuleId,
            new RuleUpdateRequest(Severity: "critical", Enabled: false));
        Assert.True(updated.IsSuccess, updated.Error);
        Assert.Equal("critical", updated.Value.Severity);
        Assert.False(updated.Value.Enabled);
        Assert.Equal(2.0, updated.Value.ThresholdLow!.Value);   // untouched legs stay

        // DELETE
        var deleted = await rules.DeleteRuleAsync(Admin(tenant), device.Device.DeviceId, rule.RuleId);
        Assert.True(deleted.IsSuccess, deleted.Error);
        var after = await rules.ListByDeviceAsync(Admin(tenant), device.Device.DeviceId);
        Assert.Empty(after.Value);
    }

    // ---- fact 4 — the duplicate law: (device, channel, rule_type) is unique --

    [Fact]
    public async Task a_duplicate_device_channel_type_is_refused()
    {
        var tenant = await RegisterAsync("dup");
        var template = await EnsureTemplateAsync("dup");
        var asset = await EnsureAssetAsync(tenant, template);
        var device = await MintAsync(tenant, asset, "dup");
        var rules = new DeviceRules(pg.Rules);

        var first = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, device.Device.DeviceId, "temperature", "above", ThresholdHigh: 9.0));
        Assert.True(first.IsSuccess, first.Error);

        var second = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, device.Device.DeviceId, "temperature", "above", ThresholdHigh: 10.0));
        Assert.False(second.IsSuccess, "the duplicate must be refused");
        Assert.Equal(RuleErrors.DuplicateRule, second.Error);
    }

    // ---- fact 5 — the offline law: the CRUD door refuses rule_type=offline ---

    [Fact]
    public async Task the_crud_door_refuses_the_offline_type()
    {
        var tenant = await RegisterAsync("off");
        var template = await EnsureTemplateAsync("off");
        var asset = await EnsureAssetAsync(tenant, template);
        var device = await MintAsync(tenant, asset, "off");
        var rules = new DeviceRules(pg.Rules);

        var refused = await rules.CreateRuleAsync(Admin(tenant), new RuleCreateRequest(
            tenant.Tenant.TenantId, device.Device.DeviceId, "temperature", "offline"));
        Assert.False(refused.IsSuccess, "offline rules are the device-offline flow's (spec 06)");
        Assert.Equal(RuleErrors.OfflineRefused, refused.Error);
    }

    // ---- fact 6 — FR-15: mint seeds one rule per LIMITED channel --------------

    [Fact]
    public async Task minting_a_device_seeds_its_limited_channels_as_rules()
    {
        var tenant = await RegisterAsync("seed");
        var template = await EnsureTemplateAsync("seed");   // temperature 2–8, door none
        var asset = await EnsureAssetAsync(tenant, template);
        var device = await MintAsync(tenant, asset, "seed");

        var rules = new DeviceRules(pg.Rules);
        var seeded = await rules.ListByDeviceAsync(Admin(tenant), device.Device.DeviceId);

        Assert.True(seeded.IsSuccess, seeded.Error);
        var row = Assert.Single(seeded.Value);   // door has NO limits → no rule for it
        Assert.Equal("temperature", row.ChannelKey);
        Assert.Equal("outside_range", row.RuleType);
        Assert.Equal(2.0, row.ThresholdLow!.Value);
        Assert.Equal(8.0, row.ThresholdHigh!.Value);
        Assert.Equal(60, row.DurationSeconds);   // plan/06 §8 defaults, verbatim
        Assert.Equal(60, row.CooldownMinutes);
        Assert.Equal("warn", row.Severity);
        Assert.True(row.Enabled);
    }

    // ---- fact 7 — seeding idempotency: re-seed never duplicates ---------------

    [Fact]
    public async Task reseeding_never_duplicates_the_rules()
    {
        var tenant = await RegisterAsync("idem");
        var template = await EnsureTemplateAsync("idem");
        var asset = await EnsureAssetAsync(tenant, template);
        var device = await MintAsync(tenant, asset, "idem");
        var rules = new DeviceRules(pg.Rules);
        var before = (await rules.ListByDeviceAsync(Admin(tenant), device.Device.DeviceId)).Value.Count;
        Assert.Equal(1, before);

        // re-issue the token (a re-mint path) and re-run the seed shape once more
        var devices = new DeviceLifecycle(pg.Assets, new DeviceTokenHasher());
        var reissued = await devices.ReissueTokenAsync(Admin(tenant), device.Device.DeviceId);
        Assert.True(reissued.IsSuccess, reissued.Error);

        var after = (await rules.ListByDeviceAsync(Admin(tenant), device.Device.DeviceId)).Value.Count;
        Assert.Equal(1, after);   // ON CONFLICT law: still exactly one
    }

    // ---- fact 8 — the engine's memory: rule_eval_state rides the rule --------

    [Fact]
    public async Task the_eval_state_is_the_rules_memory_and_dies_with_it()
    {
        var tenant = await RegisterAsync("state");
        var template = await EnsureTemplateAsync("state");
        var asset = await EnsureAssetAsync(tenant, template);
        var device = await MintAsync(tenant, asset, "state");
        var ruleId = await SeedRuleAsync(tenant, device.Device.DeviceId, "temperature");

        // the engine writes its memory (plan/06 §3.4: transitions + checkpoint only)
        var (conn, tx) = await pg.Rules.OpenTenantScopeAsync(tenant.Tenant.TenantId);
        try
        {
            await using var up = new NpgsqlCommand("""
                INSERT INTO rules.rule_eval_state (rule_id, last_state, violating_since, last_evaluated_at)
                VALUES (@r, 'violating', now(), now())
                ON CONFLICT (rule_id) DO UPDATE SET last_state = 'violating';
                """, conn, tx);
            up.Parameters.AddWithValue("r", ruleId);
            await up.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
        finally
        {
            await conn.DisposeAsync();
        }

        await using var check = await pg.Owner.OpenConnectionAsync();
        await using var sel = new NpgsqlCommand(
            "SELECT last_state FROM rules.rule_eval_state WHERE rule_id = @r;", check);
        sel.Parameters.AddWithValue("r", ruleId);
        Assert.Equal("violating", (string?)(await sel.ExecuteScalarAsync()));

        // the memory dies WITH the rule (FK cascade) — no orphan state rows ever
        var rules = new DeviceRules(pg.Rules);
        var deleted = await rules.DeleteRuleAsync(Admin(tenant), device.Device.DeviceId, ruleId);
        Assert.True(deleted.IsSuccess, deleted.Error);
        await using var after = await pg.Owner.OpenConnectionAsync();
        await using var gone = new NpgsqlCommand(
            "SELECT count(*) FROM rules.rule_eval_state WHERE rule_id = @r;", after);
        gone.Parameters.AddWithValue("r", ruleId);
        Assert.Equal(0L, (long)(await gone.ExecuteScalarAsync())!);
    }
}
