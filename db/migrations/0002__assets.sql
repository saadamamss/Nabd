-- 0002__assets.sql — templates + channels + assets + devices (plan/04 §5)
CREATE SCHEMA IF NOT EXISTS assets;

CREATE TABLE IF NOT EXISTS assets.asset_type_templates (
    id      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key     text NOT NULL UNIQUE,               -- 'freezer', 'tank', ...
    name_ar text NOT NULL,
    name_en text NOT NULL,
    alive   boolean NOT NULL DEFAULT true       -- soft delete for templates
);

CREATE TABLE IF NOT EXISTS assets.channel_definitions (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    template_id uuid NOT NULL REFERENCES assets.asset_type_templates(id),
    key         text NOT NULL,
    name_ar     text NOT NULL,
    name_en     text NOT NULL,
    unit        text NOT NULL,
    data_type   text NOT NULL CHECK (data_type IN ('number','bool')),
    lower_limit double precision,
    upper_limit double precision,
    UNIQUE (template_id, key)                   -- one 'temp' limit-set per template
);

CREATE TABLE IF NOT EXISTS assets.assets (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id  uuid NOT NULL REFERENCES identity.tenants(id),
    template_id uuid NOT NULL REFERENCES assets.asset_type_templates(id),
    name       text NOT NULL,
    location   text,
    is_active  boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS assets_tenant_idx ON assets.assets (tenant_id);

CREATE TABLE IF NOT EXISTS assets.devices (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       uuid NOT NULL REFERENCES identity.tenants(id),
    asset_id        uuid NOT NULL REFERENCES assets.assets(id),
    template_id     uuid NOT NULL REFERENCES assets.asset_type_templates(id),
    name            text NOT NULL,
    token_hash      text NOT NULL,
    token_issued_at timestamptz NOT NULL DEFAULT now(),
    token_revoked_at timestamptz,               -- NULL = still valid
    is_active       boolean NOT NULL DEFAULT true
    -- NO last_seen column: that lives in telemetry.device_status (v0.2 fix A5)
);
