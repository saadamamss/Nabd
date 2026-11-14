-- 0018__telemetry_grants.sql — P3 spec 04 (the ADR-005 spike's FINDING 1):
-- telemetry.readings and telemetry.device_status were created in 0003 with NO
-- grants — the app identity cannot write them at all. The store port lands
-- now, so nabd_app gets the WRITE grants, mirroring 0012's least-privilege
-- pattern:
--   readings:    INSERT + SELECT only — readings are append-only truth; no
--                UPDATE/DELETE from the app path (idempotency makes re-insert
--                a no-op via readings_identity_uq, 0003).
--   device_status: INSERT + UPDATE + SELECT — the flush is an UPSERT on the
--                device_id PK; no DELETE (a stale device's status ages, it is
--                not erased by the write path).
-- raw_rejects grants arrive with spec 06's migration (renumbered 0018 → 0019 —
-- the ledger is forward-only; see phases/P3/06 annotation).

GRANT INSERT, SELECT ON telemetry.readings TO nabd_app;
GRANT INSERT, UPDATE, SELECT ON telemetry.device_status TO nabd_app;
