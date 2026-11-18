-- 0021__rules.sql — P4 spec 03 (FR-30, plan/04 §7, plan/06 §8): the threshold
-- rules exist as DATA WITH DOORS. The Rules room owns the schema; the CRUD
-- doors refuse `offline` (the device-offline flow owns those rows, spec 06).
-- RLS fail-shut (0012 idiom); the UNIQUE law is the seeding idempotency's
-- foundation (FR-15: mint → seeded; re-seed/reissue never duplicates).

BEGIN;

CREATE SCHEMA IF NOT EXISTS rules;

-- 1) the rules themselves (plan/04 §7, columns verbatim)
CREATE TABLE IF NOT EXISTS rules.rules (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id        uuid NOT NULL REFERENCES identity.tenants(id),
    device_id        uuid NOT NULL REFERENCES assets.devices(id),
    channel_key      text,
    rule_type        text NOT NULL CHECK (rule_type IN ('above','below','outside_range','offline')),
    threshold_low    double precision,
    threshold_high   double precision,
    duration_seconds int  NOT NULL DEFAULT 60 CHECK (duration_seconds >= 1),
    cooldown_minutes int  NOT NULL DEFAULT 60 CHECK (cooldown_minutes >= 0),
    severity         text NOT NULL DEFAULT 'warn' CHECK (severity IN ('info','warn','critical')),
    enabled          boolean NOT NULL DEFAULT true,

    -- the type/threshold law, in the DATA (fail-shut beyond any app bug):
    CONSTRAINT rules_threshold_shape CHECK (
        (rule_type = 'above'         AND threshold_high IS NOT NULL AND threshold_low IS NULL)
     OR (rule_type = 'below'         AND threshold_low  IS NOT NULL AND threshold_high IS NULL)
     OR (rule_type = 'outside_range' AND threshold_low  IS NOT NULL AND threshold_high IS NOT NULL)
     OR (rule_type = 'offline')),

    -- offline rules hang off the DEVICE: no channel, ever (plan/06 §4)
    CONSTRAINT rules_offline_has_no_channel CHECK (
        rule_type = 'offline' OR channel_key IS NOT NULL)
);

-- the dedup/idempotency law (fact 4 + FR-15): one rule per (device, channel, type)
ALTER TABLE rules.rules ADD CONSTRAINT rules_unique_per_channel
    UNIQUE (device_id, channel_key, rule_type);
-- offline rules are per-device: at most ONE per device (channel is NULL there,
-- and plain UNIQUE treats NULLs as distinct — this partial index closes that)
CREATE UNIQUE INDEX IF NOT EXISTS rules_unique_offline_per_device
    ON rules.rules (device_id) WHERE rule_type = 'offline';

CREATE INDEX IF NOT EXISTS rules_device_channel_idx ON rules.rules (device_id, channel_key);

-- 2) the engine's memory (plan/04 §7 + plan/06 §3.4): one row per rule,
--    written on transitions + the periodic checkpoint — NEVER per reading.
--    No tenant_id: it rides the rule's identity; dies WITH it (FK cascade).
CREATE TABLE IF NOT EXISTS rules.rule_eval_state (
    rule_id           uuid PRIMARY KEY REFERENCES rules.rules(id) ON DELETE CASCADE,
    last_state        text NOT NULL CHECK (last_state IN ('normal','violating')),
    violating_since   timestamptz,
    last_evaluated_at timestamptz NOT NULL DEFAULT now()
);

-- 3) the wall (0012 idiom): fail-shut RLS — missing app.current_tenant = no rows.
--    NULLIF guards a real pooled-session fact: a custom GUC that was SET LOCAL once
--    keeps an EMPTY string after COMMIT (it is defined, just reset), so the bare
--    cast would raise 22P02 instead of hiding rows. Empty = missing = zero rows.
ALTER TABLE rules.rules ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_rules ON rules.rules;
CREATE POLICY tenant_isolation_rules ON rules.rules
    USING (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid);

-- rule_eval_state carries no tenant column: its protection is the GRANT shape
-- (the engine's lane only — no app read path in this spec; spec 04 wires it).

-- 4) least-privilege grants for the app consumer (0005/0012 idiom)
GRANT USAGE ON SCHEMA rules TO nabd_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON rules.rules TO nabd_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON rules.rule_eval_state TO nabd_app;

COMMIT;