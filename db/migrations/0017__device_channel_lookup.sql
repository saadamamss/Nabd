-- 0017__device_channel_lookup.sql — P3 spec 03: the validation pipeline's
-- channel edge reads a device's template catalog BEFORE any caller context
-- exists (the broker's doors already proved identity; this is a lookup).
-- 0012's fail-shut RLS answers ZERO rows to a direct pre-auth SELECT on
-- assets.devices (the policy needs app.current_tenant; a pre-auth connection
-- sets none) — exactly the trap 0014/0016 solved for their two lookups.
-- Third twin of the narrow-exception law: ONE SECURITY DEFINER function, one
-- query, no table access beyond it. The RLS wall stays absolute for direct
-- access; this function is the only window, and it is auditable like 0014.
-- Empty array = unknown / revoked / deactivated device OR a template with no
-- channels — the pipeline reads all of those UNIFORMLY as E422_UNKNOWN_CHANNEL
-- (anti-enumeration: nothing tells a stranger what exists).

CREATE OR REPLACE FUNCTION assets.device_channel_keys_by_id(p_id uuid)
RETURNS text[]
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = assets, pg_temp
AS $$
    SELECT coalesce(array_agg(cd.key ORDER BY cd.key), '{}')
    FROM assets.devices d
    JOIN assets.channel_definitions cd ON cd.template_id = d.template_id
    WHERE d.id = p_id
      AND d.token_revoked_at IS NULL
      AND d.is_active;
$$;

REVOKE EXECUTE ON FUNCTION assets.device_channel_keys_by_id(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assets.device_channel_keys_by_id(uuid) TO nabd_app;
