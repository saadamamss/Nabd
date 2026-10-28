-- rls-timescale.sql — P0 spike 03 (phases/P0/03-spike-rls.md)
-- Every experiment lives here as evidence; the findings paper cites this file.
-- NOTE: run via `docker exec -i nabd-postgres psql -U nabd -d nabd -f -` using the
-- plain-nabd session for SETUP only. Real RLS reads happen as `app_user`.

-- ============================================================
-- PART 1 — seed lab data (fixed UUIDs so the paper can cite them)
-- ============================================================

-- fixed tenant ids: A=aaaa..., B=bbbb... — readable in every later output
INSERT INTO identity.tenants (id, name) VALUES
  ('11111111-1111-1111-1111-111111111111', 'Tenant A - spike'),
  ('22222222-2222-2222-2222-222222222222', 'Tenant B - spike');

-- the asset-type machinery: one template, one channel definition
INSERT INTO assets.asset_type_templates (id, key, name_ar, name_en)
VALUES ('aaaaaaa1-0000-0000-0000-000000000000', 'freezer-spike', 'ثلاجة - تجربة', 'Freezer - spike');

INSERT INTO assets.channel_definitions (template_id, key, name_en, name_ar, unit, data_type,
                                        lower_limit, upper_limit)
VALUES ('aaaaaaa1-0000-0000-0000-000000000000', 'temp', 'Spike temp', 'درجة الحرارة - تجربة',
        '°C', 'number', -18.0, -12.0);

-- one asset per tenant (template wired in)
INSERT INTO assets.assets (tenant_id, template_id, name)
VALUES ('11111111-1111-1111-1111-111111111111', 'aaaaaaa1-0000-0000-0000-000000000000', 'Freezer A'),
       ('22222222-2222-2222-2222-222222222222', 'aaaaaaa1-0000-0000-0000-000000000000', 'Freezer B');

-- one device per tenant, token_hash faked (spike only)
INSERT INTO assets.devices (tenant_id, asset_id, template_id, name, token_hash)
SELECT asset.tenant_id, asset.id, asset.template_id, 'dev-' || asset.name, 'spike-hash-' || asset.name
FROM assets.assets asset;

-- 24 readings per device (one full day, hourly) — readings needed: time, received_at,
-- tenant_id (denormalized on purpose), device_id, channel_key, value
INSERT INTO telemetry.readings (time, received_at, tenant_id, device_id, channel_key, value)
SELECT d.last_capture + make_interval(hours => g.n),           -- 00:00, 01:00, ... 23:00
       d.last_capture + make_interval(hours => g.n, secs => 2),-- received 2 s later
       d.tenant_id, d.dvc_id, 'temp', -16 + g.n * 0.01          -- slightly rising temps
FROM (SELECT dev.id AS dvc_id, dev.tenant_id,
             date_trunc('day', now()) - INTERVAL '1 day' AS last_capture
      FROM assets.devices dev) AS d,
     generate_series(0, 23) AS g(n);

-- sanity: who owns what?
SELECT tenant_id, count(*) FROM telemetry.readings GROUP BY tenant_id ORDER BY tenant_id;

-- ============================================================
-- PART 2 — arm the double lock: policy + a Joe to look through
-- (Q1: does RLS filter readings for a normal user?)
-- ============================================================

-- the wall itself: a row is visible only when its tenant matches
-- the per-request custom setting 'app.current_tenant' (set by app code, SET LOCAL)
ALTER TABLE telemetry.readings ENABLE ROW LEVEL SECURITY;

CREATE POLICY readings_tenant_isolation ON telemetry.readings
    USING (tenant_id = current_setting('app.current_tenant')::uuid);

-- Joe: a normal role — NOT owner, NOT superuser → the only eyes RLS actually governs
CREATE ROLE app_user NOLOGIN;
GRANT USAGE ON SCHEMA telemetry TO app_user;
GRANT SELECT ON telemetry.readings TO app_user;

-- identity check for the paper: app_user must NOT bypass RLS
SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname IN ('nabd','app_user');

-- ============================================================
-- PART 3 — Q2: does the policy survive COMPRESSION of the chunk?
-- Hypothesis: compressed (columnar) storage still honors policies
-- because reads still go through the same relcache scan path.
-- ============================================================

-- turn on compression machinery for the table (metadata only; no chunk compressed yet)
ALTER TABLE telemetry.readings SET (timescaledb.compress);

-- what chunks exist? (expect exactly ONE — yesterday, holding our 48 rows)
SELECT show_chunks('telemetry.readings');

-- compress EVERY chunk shown (only yesterday has data).
-- compress_chunk semantics: convert rows to columnar batch storage; readable, still a table
SELECT compress_chunk(c) FROM show_chunks('telemetry.readings') AS c;

-- proof it really compressed (expect a compressed=>t row):
SELECT chunk_name, is_compressed
FROM timescaledb_information.chunks
WHERE hypertable_name = 'readings';

-- the money query: Joe reads again, IDENTICAL expectations (24/24, mirrored) over compressed data
-- (same two role blocks as Part 2's reads — re-run them after this; identical output = Q2 PASS)

-- ⚠️ REALITY HIT before compression: "compression cannot be used on table with row security"
-- (known open limitation, github.com/timescale/timescaledb#6425). Remediation trials follow.

-- ============================================================
-- PART 4 — Q2 remediation trials
-- ============================================================

-- TRIAL 1: swap the enable-order. Compression first (RS off), RLS added AFTER.
-- If the engine only checks RLS at compression-enable time, we may legally hold both.
ALTER TABLE telemetry.readings DISABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS readings_tenant_isolation ON telemetry.readings;
ALTER TABLE telemetry.readings SET (timescaledb.compress);
ALTER TABLE telemetry.readings ENABLE ROW LEVEL SECURITY;
CREATE POLICY readings_tenant_isolation ON telemetry.readings
    USING (tenant_id = current_setting('app.current_tenant')::uuid);

-- now compress for real
SELECT compress_chunk(c) FROM show_chunks('telemetry.readings') AS c;
SELECT chunk_name, is_compressed FROM timescaledb_information.chunks
WHERE hypertable_name = 'readings';

-- THE dangerous half: does the policy actually FIRE over compressed rows?
-- (same Joe blocks; identical 24/24 mirrored output = both locks alive; anything else = LEAK)

-- ⚠️ RESULT: ENABLE RLS *after* compression ALSO fails
-- ("operation not supported on hypertables that have compression enabled")
-- → RLS × compression mutually exclusive in BOTH orders. Trial 1 dead.

-- ============================================================
-- PART 5 — Trial 2: view-based tenant wall (replacement construction)
-- RLS off on readings; the wall = a view whose WHERE filters tenant_id
-- by the same per-request setting; Joe loses his direct table grant.
-- ============================================================

-- leak-demo evidence (ran before this part): app_user direct SELECT saw 48 rows, both tenants

-- clean the inert residue from Trial 1 (policy without enabled RLS = dead code)
DROP POLICY IF EXISTS readings_tenant_isolation ON telemetry.readings;

-- the wall: one view, filter welded into its definition
CREATE VIEW telemetry.readings_per_tenant AS
    SELECT * FROM telemetry.readings
    WHERE tenant_id = current_setting('app.current_tenant')::uuid;

-- retire Joe's direct grant — the wall is now the ONLY door
REVOKE SELECT ON telemetry.readings FROM app_user;
GRANT SELECT ON telemetry.readings_per_tenant TO app_user;

-- the three outcomes that prove (or disprove) the construction:
--   1. Joe direct on base table  → PERMISSION DENIED
--   2. Joe via view, tenant A    → 24 rows
--   3. Joe via view, tenant B    → 24 rows (other one)
-- This view-over-compressed-chunks is exactly how dashboards would read (P1).

-- ✅ RESULT (2026-10-28): all three outcomes verified —
--   direct base read → "permission denied for table readings"
--   view read as tenant A → 24 · tenant B → 24 (over the COMPRESSED chunk)
-- → Trial 2 = the construction. Consequence for plan/04 §10: the "second lock"
--   on the readings hypertable is a tenant-filtered definer VIEWS wall, not RLS.

-- ============================================================
-- PART 6 — Experiment B (Q3): continuous aggregates + tenant scoping
-- Hypothesis to test honestly: base-table policies/walls don't reach
-- the agg's materialized storage. Expect: app_user through the agg sees BOTH tenants.
-- ============================================================

-- the agg itself: one hourly bucket per (tenant, device) — plan/04 §6 shape
CREATE MATERIALIZED VIEW telemetry.readings_hourly
    WITH (timescaledb.continuous) AS
SELECT time_bucket(INTERVAL '1 hour', r.time) AS bucket,
       r.tenant_id,
       r.device_id,
       min(r.value) AS min_value,
       max(r.value) AS max_value,
       avg(r.value) AS avg_value
FROM telemetry.readings r
GROUP BY bucket, r.tenant_id, r.device_id
WITH NO DATA;

CALL refresh_continuous_aggregate('telemetry.readings_hourly', NULL, NULL);

-- give app_user SELECT to the agg (the FIRST thing a tenant dashboard would ask for)
GRANT SELECT ON telemetry.readings_hourly TO app_user;

-- the leak probe: Joe with tenant A's setting reads the agg *directly* (no wall).
-- EXPECTED (honest): bucket totals for BOTH tenants = the wall didn't reach agg storage.

-- ✅ RESULT (2026-10-28): LEAK CONFIRMED — app_user + current_setting('A') saw BOTH
-- tenants (24+24) straight off readings_hourly; base-table walls don't reach agg storage.
-- (a parsing miss first: refresh_continuous_aggregate is a PROCEDURE → CALL, not SELECT)
-- (microsurprise for the paper: refresh NOTICE said "already up-to-date" while data
--  appeared; accepted as done-work-before-parse-error, deeper check deferred to P1)

-- ============================================================
-- PART 7 — Q3 remediation: tenant-filtered view ON the agg (plan/04 §10's fix)
-- Same trade as Trial 2: revoke the direct agg grant, grant only a wall view.
-- ============================================================

CREATE VIEW telemetry.readings_hourly_per_tenant AS
    SELECT * FROM telemetry.readings_hourly
    WHERE tenant_id = current_setting('app.current_tenant')::uuid;

REVOKE SELECT ON telemetry.readings_hourly FROM app_user;
GRANT SELECT ON telemetry.readings_hourly_per_tenant TO app_user;

-- the three outcomes (mirror of Trial 2):
--   1. Joe direct on the agg  → PERMISSION DENIED
--   2. Joe via agg view, A    → 24 buckets, only tenant A
--   3. Joe via agg view, B    → 24 buckets, only tenant B

-- ✅ RESULT (2026-10-28): all three verified (denied / 24 / 24 mirrored).
-- → Q3 verdict: PASS-WITH-NOTES — wall = tenant-filtered view ON the agg
--   (`readings_hourly_per_tenant`), direct agg grants revoked.
-- → plan/06 dashboard read-path rule: read through *_per_tenant walls, never raw.

-- ============================================================
-- PART 8 — Experiment C (Q4): the ingest_writer role
-- Q: can the writer bulk-INSERT across tenants (nothing else)?
-- NOTE it's NOLOGIN — a "personality", not a login; assumed via SET ROLE.
-- ============================================================

CREATE ROLE ingest_writer NOLOGIN;
GRANT USAGE ON SCHEMA telemetry TO ingest_writer;
GRANT INSERT ON telemetry.readings, telemetry.device_status TO ingest_writer;
-- deliberately NO SELECT / UPDATE / DELETE / schema USAGE elsewhere

-- probes as ingest_writer:
--   1. INSERT two tenants in one batch        → should WORK (payload-driven tenant_id,
--      trust boundary = code layer: device's real tenant is looked up by token, not taken from payload — paper note)
--   2. SELECT                                 → ER: INSERT privilege only
--   3. UPDATE a value in the bulk             → ER: INSERT privilege only
--   4. DELETE that bulk                       → ER: INSERT privilege only

-- ✅ RESULT (2026-10-28): batch with LITERAL tenant/device uuids → INSERT 0 2 ✅
--   SELECT/UPDATE/DELETE → "permission denied" ×3 ✅
--   BONUS FINDING: a probe INSERT that sub-queried assets.devices for the device id
--   was DENIED (schema assets unavailable to the writer) → the DB itself proves the
--   trust boundary: resolution lives app-side; the writer gets literal, trusted values.

-- ============================================================
-- VERDICT (full paper: docs/runbook/p0-spikes/rls-timescale.md):
-- PASS-WITH-NOTES. RLS works as baseline (Q1); RLS×compression mutually
-- exclusive (Q2, issue #6425) → wall = *_per_tenant views; cagg leaks through
-- base walls (Q3) → view-on-agg; writer path verified incl. the no-self-lookup
-- trust boundary (Q4). plan/04 §10 amended accordingly.
-- ============================================================
