-- 0012__assets_wall.sql — P2 spec 01: the assets wall (plan/04 §5, §10)
-- Law: multi-tenancy enforcement is DB-level; fail SHUT like identity (0006 idiom).
-- Templates stay unwalled: global config, SELECT-only for nabd_app (writes = migrations only).

BEGIN;

-- 1) tenant-scoped: assets.assets
DROP POLICY IF EXISTS tenant_isolation_assets ON assets.assets;
ALTER TABLE assets.assets ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_assets ON assets.assets
    USING (tenant_id = current_setting('app.current_tenant', true)::uuid);

-- 2) tenant-scoped: assets.devices
DROP POLICY IF EXISTS tenant_isolation_devices ON assets.devices;
ALTER TABLE assets.devices ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_devices ON assets.devices
    USING (tenant_id = current_setting('app.current_tenant', true)::uuid);

-- 3) the credentials audit log (plan/04 §5) — append-only by GRANT shape
CREATE TABLE IF NOT EXISTS assets.device_credentials_log (
    id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id uuid NOT NULL REFERENCES assets.devices(id),
    tenant_id uuid NOT NULL REFERENCES identity.tenants(id),
    action    text NOT NULL CHECK (action IN ('issued','revoked')),
    actor     text NOT NULL,
    at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS device_credentials_log_device_idx
    ON assets.device_credentials_log (device_id);

ALTER TABLE assets.device_credentials_log ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_device_credentials ON assets.device_credentials_log;
CREATE POLICY tenant_isolation_device_credentials ON assets.device_credentials_log
    USING (tenant_id = current_setting('app.current_tenant', true)::uuid);

-- 4) least-privilege grants for the app consumer (0005 idiom)
GRANT SELECT, INSERT, UPDATE, DELETE ON assets.assets, assets.devices TO nabd_app;
GRANT SELECT ON assets.asset_type_templates, assets.channel_definitions TO nabd_app;
GRANT INSERT, SELECT ON assets.device_credentials_log TO nabd_app;  -- append-only: NO UPDATE, NO DELETE

COMMIT;
