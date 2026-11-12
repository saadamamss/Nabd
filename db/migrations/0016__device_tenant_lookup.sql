-- 0016__device_tenant_lookup.sql — P2 spec 05: the EMQX authorization door needs
-- tenant_id from a device id, but 0012's fail-shut RLS answers zero rows to any
-- direct pre-auth lookup. Second twin of the narrow-exception law (0014 was the
-- first): ONE SECURITY DEFINER function, one query, no table access beyond it.
-- Returns NULL for unknown / revoked / deactivated devices — the ACL door reads
-- that as DENY, so revocation enforcement rides the same lookup.

CREATE OR REPLACE FUNCTION assets.device_tenant_by_id(p_id uuid)
RETURNS uuid
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = assets, pg_temp
AS $$
    SELECT tenant_id FROM assets.devices
    WHERE id = p_id
      AND token_revoked_at IS NULL
      AND is_active;
$$;

REVOKE EXECUTE ON FUNCTION assets.device_tenant_by_id(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assets.device_tenant_by_id(uuid) TO nabd_app;
