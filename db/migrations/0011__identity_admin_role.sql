-- 0011__identity_admin_role.sql — the platform_admin DB identity (FR-61;
-- plan/04 §10 line 143 "the audited exception, not a backdoor";
-- plan/07 §9 Admin-actions row). Spec 06, phases/P1/06-admin-stub.md.
-- Naming honesty: the spec draft numbered this file 0005 at phase-start, but
-- main had already reached 0010 (RULES §8: reality wins, drift in NOTES.md).

-- Secret-free and idempotent (0005's law): the PASSWORD never lives in the
-- ledger — ops sets it once per environment from .env, exactly like nabd_app.
DO $$ BEGIN
   IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nabd_admin') THEN
      CREATE ROLE nabd_admin LOGIN BYPASSRLS;
   END IF;
END
 $$;

-- BYPASSRLS is NOT an ACL grant: it disarms ROW SECURITY only. Every ordinary
-- table privilege still gates every query — so this role receives EXACTLY the
-- FR-61 surface and nothing else. This resolves the spec's "verify against the
-- real DDL" by explicit grants (least-privilege), not by ownership convention.
GRANT USAGE ON SCHEMA identity TO nabd_admin;
GRANT SELECT, UPDATE ON identity.tenants TO nabd_admin;

-- What this role must NEVER hold: grants on other schemas, on token tables, or
-- on any SECURITY DEFINER function — an audit answer for "who bypassed the wall"
-- is worthless if the wall-breaker can also rewrite history. AdminWallTests reads
-- pg_roles as the probe (rolbypassrls + rolcanlogin → exactly one row).