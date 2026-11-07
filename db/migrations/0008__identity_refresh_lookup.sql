-- 0008__identity_refresh_lookup.sql — P1 spec 03: the refresh/logout pre-auth door.
-- Same audited-exception pattern as 0007 (plan/04 §10). refresh_tokens is
-- RLS-walled on the USER's tenant (0004 + 0006 missing_ok), but refresh and
-- logout are PRE-auth: no tenant is known, only the presented token's hash.
-- Function is keyed by token_hash — UNIQUE per 0004 — so LIMIT 1 is guaranteed.

BEGIN;

CREATE OR REPLACE FUNCTION identity.find_refresh_session(p_hash text)
RETURNS TABLE (
    user_id    uuid,
    tenant_id  uuid,
    revoked_at timestamptz,
    expires_at timestamptz
)
LANGUAGE sql STABLE
SECURITY DEFINER
SET search_path = identity, pg_temp
AS $$
    SELECT u.id, u.tenant_id, r.revoked_at, r.expires_at
    FROM identity.refresh_tokens r
    JOIN identity.users u ON u.id = r.user_id
    WHERE r.token_hash = p_hash
    LIMIT 1;
$$;

COMMENT ON FUNCTION identity.find_refresh_session(text) IS
    'P1 spec 03 audited exception #2: sole pre-auth lookup of refresh_tokens by hash (plan/04 §10). Never widen its surface; refresh + logout only.';

REVOKE ALL ON FUNCTION identity.find_refresh_session(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION identity.find_refresh_session(text) TO nabd_app;

COMMIT;