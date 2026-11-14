-- 0019__telemetry_raw_rejects.sql — P3 spec 06 (ADR-005 VETO consequence):
-- every MQTT-path rejection gets a DESTINATION (plan/05 §7: nothing silently
-- dropped). The RabbitMQ-DLQ branch is DEAD (VETO, 2026-11-13); the raw-rejects
-- table is the verdict-shaped destination — kept SIMPLE on purpose:
--   • plain table, NOT a hypertable: this is forensics, not a time series.
--     `time` carries the server receive time at MVP (an unparseable payload has
--     no trustworthy event time to carry).
--   • tenant_id/device_id NULLABLE: a rejection may precede identity resolution.
--   • payload bytea: the RAW bytes, verbatim — recovery first, forensics always.
--   • NO UPDATE/DELETE grants for nabd_app: raw forensics are append-only truth
--     (the same law as readings; cap + cleanup is a P6 ledger item).
-- NUMBERING: 0018 was taken by spec 04's telemetry write grants; this lands as
-- 0019 (renumbered twice, logged in phases/P3/NOTES — the ledger is forward-only).

CREATE TABLE IF NOT EXISTS telemetry.raw_rejects (
    time        timestamptz NOT NULL,
    received_at timestamptz NOT NULL,
    tenant_id   uuid NULL,
    device_id   uuid NULL REFERENCES assets.devices(id),
    reason      text NOT NULL,
    payload     bytea NOT NULL
);

-- forensics' read shape: by reason over time (the metrics query) and by device
CREATE INDEX IF NOT EXISTS raw_rejects_reason_idx
    ON telemetry.raw_rejects (reason, time DESC);

CREATE INDEX IF NOT EXISTS raw_rejects_device_idx
    ON telemetry.raw_rejects (device_id, time DESC);

GRANT INSERT, SELECT ON telemetry.raw_rejects TO nabd_app;
