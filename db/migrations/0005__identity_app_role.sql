-- 0005__identity_app_role.sql — P1 spec 02 step 0: the application's least-privilege DB identity
-- Law: plan/07 §7 (secrets/least privilege), plan/04 §10 (RLS only bites non-owners),
--      plan/09-NFR-06 (isolation enforced, not bypassed) — the APP cannot live as table owner forever:
--      owner roles SKIP row security silently, which would make the P0-earned walls decorative.

DO $$
BEGIN
   IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nabd_app') THEN
      CREATE ROLE nabd_app LOGIN; -- PASSWORD is set OUTSIDE migrations (secret per NFR-08, lives in .env; ops run once per env)
   END IF;
END
$$;

-- the secret itself, set by env-reading ops step (done OUTSIDE migration files):
--   ALTER ROLE nabd_app PASSWORD '<from .env>' — applied by the compose/ops path, never committed.

-- NOTE (honesty): CREATE ROLE's PASSWORD clause cannot be parameterized inside DO blocks,
-- so the rotated password lives in .env (15-factor: NFR-08) and the migration keeps the
-- creation idempotent WITHOUT embedding the secret. Grant shape is what THIS file owns.

-- least privilege, schema by schema (EXPAND as rooms grow, never at schema creation):
GRANT USAGE ON SCHEMA platform, identity, assets, telemetry TO nabd_app;

-- identity: the CRUD rooms' own surface (spec 02 endpoints write these)
GRANT INSERT, UPDATE, SELECT, DELETE ON identity.tenants, identity.users,
                                        identity.user_roles, identity.refresh_tokens TO nabd_app;
GRANT SELECT, UPDATE ON identity.tenants TO nabd_app;  -- disabling tenant for spec 06 flows via status column

-- sequences the INSERT grants imply (postgres needs USAGE on the generator)
GRANT USAGE ON ALL SEQUENCES IN SCHEMA identity TO nabd_app;

-- deliberately NOT granted: any assets/telemetry WRITE (that's P2/P3's slice to open),
--                           platform.migrations_applied WRITE (the runner owns its memory alone).
-- (transaction shape: no explicit BEGIN/COMMIT here — psql implicit + runner tail owns it)
