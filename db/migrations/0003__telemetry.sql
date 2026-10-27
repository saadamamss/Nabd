-- 0003__telemetry.sql — the one big table (plan/04 §6)
CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;

CREATE SCHEMA IF NOT EXISTS telemetry;

CREATE TABLE IF NOT EXISTS telemetry.readings (
    time        timestamptz NOT NULL,
    received_at timestamptz NOT NULL,
    tenant_id   uuid NOT NULL,
    device_id   uuid NOT NULL REFERENCES assets.devices(id),
    channel_key text NOT NULL,
    value       double precision NOT NULL,
    seq         bigint NOT NULL DEFAULT 0
);

-- the magic that makes 2000/sec boring (plan/04 §6)
SELECT create_hypertable('telemetry.readings', 'time',
    chunk_interval => INTERVAL '1 day',
    if_not_exists => TRUE);

-- unique key MUST include the partition column 'time'
-- NULLS NOT DISTINCT (PG15+): seq=0 duplicates collapse when devices omit seq
ALTER TABLE telemetry.readings
    ADD CONSTRAINT readings_identity_uq
    UNIQUE NULLS NOT DISTINCT (device_id, channel_key, time, seq);

-- history-query shape
CREATE INDEX IF NOT EXISTS readings_history_idx
    ON telemetry.readings (tenant_id, device_id, time DESC);

CREATE TABLE IF NOT EXISTS telemetry.device_status (
    device_id       uuid PRIMARY KEY REFERENCES assets.devices(id),
    tenant_id       uuid NOT NULL,
    last_seen_at    timestamptz NOT NULL,
    last_event_time timestamptz NOT NULL
    -- owned by TelemetryStore room (v0.2 fix A5) — NOT Assets
);
