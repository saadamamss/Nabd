-- 0014__device_token_lookup.sql — P2 spec 03 review finding 1 (fail-shut RLS
-- makes a direct pre-auth lookup by nabd_app return ZERO rows always). The
-- broker's auth door (spec 05) needs hash→device-id BEFORE any caller exists.
-- Law: the exception must exist somewhere — make it narrow and audited
-- (P1 BYPASSRLS lesson generalized): ONE SECURITY DEFINER function, one query,
-- no table access beyond it. The RLS wall stays absolute for direct access.

CREATE OR REPLACE FUNCTION assets.device_id_by_token_hash(p_hash text)
RETURNS uuid
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = assets, pg_temp
AS $$
    SELECT id FROM assets.devices
    WHERE token_hash = p_hash
      AND token_revoked_at IS NULL
      AND is_active;
$$;

REVOKE EXECUTE ON FUNCTION assets.device_id_by_token_hash(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assets.device_id_by_token_hash(text) TO nabd_app;
