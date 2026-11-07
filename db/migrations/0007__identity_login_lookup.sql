-- 0007__identity_login_lookup.sql — P1 spec 03 step 0: the pre-auth lookup door.
-- Law: plan/04 §10 (RLS bites apps, not owners; exceptions are audited, not backdoors)
--      · plan/07 §3 row 1 (login finds user by email — unique across tenants).
-- Problem: identity.users is RLS-walled (0006 missing_ok). The app role with no
--      app.current_tenant set sees ZERO rows — fail-shut, correct for every
--      management lane. But LOGIN is pre-auth: no tenant is known yet, and we
--      will NOT punch an OR-hole in a shared policy for it.
-- Answer: one SECURITY DEFINER function — fixed SQL, fixed columns, one parameter.
--      It runs as the migration owner (reads users past RLS) and only nabd_app
--      gets EXECUTE. Surface = the SQL text itself, immutable and auditable.
-- Anti-enumeration duty lives in the CALLER (dummy argon2id burn on unknown email);
--      this function returning zero rows is normal business, not an error.

BEGIN;

CREATE OR REPLACE FUNCTION identity.find_login_user(p_email text)
RETURNS TABLE (
    user_id       uuid,
    tenant_id     uuid,
    password_hash text,
    locale        text
)
LANGUAGE sql STABLE
SECURITY DEFINER                      -- runs as migration owner: reads users past RLS
SET search_path = identity, pg_temp   -- object resolution cannot be hijacked
AS $$
    SELECT u.id, u.tenant_id, u.password_hash::text, u.locale
    FROM identity.users u
    WHERE u.email = p_email           -- citext equality: case-insensitive by type
    LIMIT 1;
$$;

COMMENT ON FUNCTION identity.find_login_user(text) IS
    'P1 spec 03 audited exception: the sole pre-auth path into identity.users (plan/04 §10). Never widen its surface; login/refresh flows only.';

REVOKE ALL ON FUNCTION identity.find_login_user(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION identity.find_login_user(text) TO nabd_app;

COMMIT;