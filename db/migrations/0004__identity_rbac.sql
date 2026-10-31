-- 0004__identity_rbac.sql — P1 spec 01: RBAC machinery + tenant-wall policies
-- Law: plan/04 §4 rows 3–5 (roles/user_roles/refresh_tokens) + §10 (RLS on plain tables)
-- Written against REALITY of 0001 (frozen, per gate law): the 0004 file carries the drift fixes.

BEGIN;

-- ============================================================
-- (a) role-code repair: 0001 shipped the older model (users.role,
--     codes owner/operator/viewer). P1 law is roles-as-table with
--     viewer/tenant_admin/platform_admin (plan/02 FR-03, plan/04 §4 row 3).
--     No data to migrate at P1 (dev-only users, none authenticated yet):
--     drop the column; user_roles below is the union of its replacement.
-- ============================================================
ALTER TABLE identity.users DROP COLUMN IF EXISTS role;

CREATE TABLE IF NOT EXISTS identity.roles (
    code        text PRIMARY KEY CHECK (code IN ('viewer','tenant_admin','platform_admin')),
    description text NOT NULL
);

INSERT INTO identity.roles (code, description) VALUES
    ('viewer',         'read-only everywhere (plan/07 §5 verb check)'),
    ('tenant_admin',   'manage users/roles inside own tenant (FR-04)'),
    ('platform_admin', 'platform staff; admin room; audited BYPASSRLS path (FR-61)')
ON CONFLICT (code) DO NOTHING;

-- which person wears which hat, IN WHICH tenant (composite PK per plan/04 §4 row 4)
CREATE TABLE IF NOT EXISTS identity.user_roles (
    user_id   uuid NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
    role_code text NOT NULL REFERENCES identity.roles(code),
    tenant_id uuid NOT NULL REFERENCES identity.tenants(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, role_code, tenant_id)
);

-- ============================================================
-- (b) tenants alignment: plan/04 §4 row 1 wants slug + plan ref
--     (0001 shipped name/locale/is_active; is_active ≙ boolean status — kept,
--     with a code comment pointing at the §10 wording; no data-migration needed)
-- ============================================================
ALTER TABLE identity.tenants ADD COLUMN IF NOT EXISTS slug     text;        -- URL-safe identity (P1: SIGNUP dedupe)
ALTER TABLE identity.tenants ADD COLUMN IF NOT EXISTS plan_ref text NOT NULL DEFAULT 'starter'
                                                                CHECK (plan_ref IN ('starter','pro','business'));
-- plan_ref is a STRING at MVP by §4's own note ("real billing plan FK arrives in V1")
CREATE UNIQUE INDEX IF NOT EXISTS uq_tenants_slug ON identity.tenants (slug) WHERE slug IS NOT NULL;

-- ============================================================
-- (c) refresh_tokens: revocable sessions (plan/07 §3 row 2)
--     token stored ONLY hashed; revoke = timestamp; rotation on each use (spec 03)
-- ============================================================
CREATE TABLE IF NOT EXISTS identity.refresh_tokens (
    id         bigserial   PRIMARY KEY,
    user_id    uuid        NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
    token_hash text        NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    revoked_at timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_refresh_token_hash ON identity.refresh_tokens (token_hash);
CREATE INDEX IF NOT EXISTS ix_refresh_user_active ON identity.refresh_tokens (user_id)
    WHERE revoked_at IS NULL;   -- the one query pattern P1 needs (active sessions per user)

-- ============================================================
-- (d) tenant wall: RLS on tenant-scoped PLAIN tables (plan/04 §10 Layer 2).
--     spike-03 result does NOT bite here: none of these are Timescale-machined
--     hypertables — RLS is legal. app.current_tenant set per transaction
--     (pool discipline: SET LOCAL right after BEGIN).
--     platform_admin reach = the DEDICATED audited BYPASSRLS role (spec 06),
--     never a broad grant here (04 §10: "audited exception, not a backdoor").
-- ============================================================
ALTER TABLE identity.tenants        ENABLE ROW LEVEL SECURITY;
ALTER TABLE identity.users          ENABLE ROW LEVEL SECURITY;
ALTER TABLE identity.user_roles     ENABLE ROW LEVEL SECURITY;
ALTER TABLE identity.refresh_tokens ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_tenants ON identity.tenants
    USING (id = current_setting('app.current_tenant')::uuid);
CREATE POLICY tenant_isolation_users ON identity.users
    USING (tenant_id = current_setting('app.current_tenant')::uuid);
CREATE POLICY tenant_isolation_user_roles ON identity.user_roles
    USING (tenant_id = current_setting('app.current_tenant')::uuid);
CREATE POLICY tenant_isolation_refresh_tokens ON identity.refresh_tokens
    USING (EXISTS (
        SELECT 1 FROM identity.users u
        WHERE u.id = user_id
          AND u.tenant_id = current_setting('app.current_tenant')::uuid
    ));

COMMIT;
