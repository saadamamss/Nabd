-- 0020__device_set_resolver.sql — P4 spec 01 (the batching lever, Move B):
-- the flush resolves EVERY device in ONE call. 0016 answers one device per
-- call and 0017 one catalog per call; at the target (2,000 readings/s,
-- 100 ms flushes) that is ~40 lookup round-trips/s for RESOLUTION alone.
-- The set-based twin answers ONE query per flush for all of them.
-- Third twin of the narrow-exception law (0014/0016/0017): SECURITY DEFINER,
-- STABLE, one fixed query, no table access beyond it — the RLS wall stays
-- absolute for direct access; this function is the only window, auditable
-- like its twins.
-- The uniform law rides UNCHANGED: a MISSING row = unknown / revoked /
-- deactivated (the flush reads absence exactly as 0017's null — E422-class,
-- anti-enumeration: nothing tells a stranger what exists). A PRESENT row
-- with an EMPTY channel_keys = a live device whose template has no lawful
-- channels — the flush rejects its messages UNIFORMLY with the missing row
-- (0017's coalesce-'{}' law, set-shaped).
-- THE NULL-AGG TRAP (caught by the suite, 2026-11-16): with LEFT JOIN, a
-- no-channel device yields ONE joined row with cd.key = NULL — and
-- array_agg over it produces {NULL} (a ONE-element array), NOT NULL, so
-- coalesce never fires. The FILTER excludes that artifact (cd.key is NOT
-- NULL by schema — no lawful channel is ever dropped), the aggregate sees
-- zero rows, coalesce answers '{}'. 0017 never met this trap because its
-- INNER JOIN yields zero rows for a no-channel device.

CREATE OR REPLACE FUNCTION assets.device_resolve_by_ids(p_ids uuid[])
RETURNS TABLE(device_id uuid, tenant_id uuid, channel_keys text[])
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = assets, pg_temp
AS $$
    SELECT d.id, d.tenant_id,
           coalesce(array_agg(cd.key ORDER BY cd.key) FILTER (WHERE cd.key IS NOT NULL), '{}')
    FROM unnest(p_ids) AS u(id)
    JOIN assets.devices d
      ON d.id = u.id
     AND d.token_revoked_at IS NULL
     AND d.is_active
    LEFT JOIN assets.channel_definitions cd ON cd.template_id = d.template_id
    GROUP BY d.id, d.tenant_id
$$;

REVOKE EXECUTE ON FUNCTION assets.device_resolve_by_ids(uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assets.device_resolve_by_ids(uuid[]) TO nabd_app;
