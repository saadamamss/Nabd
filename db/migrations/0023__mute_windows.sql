-- 0023__mute_windows.sql — P4 spec 06 (FR-35, plan/06 §5.4): نوافذ كتم أسبوعية لكل قاعدة.
-- UTC، [start,end) في دقائق اليوم. الجدار بنمط 0012/0021 (NULLIF). الحذف مسموح: إعداد تشغيلي لا تاريخ.
BEGIN;

CREATE TABLE IF NOT EXISTS rules.mute_windows (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id    uuid NOT NULL REFERENCES identity.tenants(id),
    rule_id      uuid NOT NULL REFERENCES rules.rules(id) ON DELETE CASCADE,
    day_of_week  smallint NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),      -- 0=الأحد
    start_minute smallint NOT NULL CHECK (start_minute BETWEEN 0 AND 1439),  -- 0..23:59
    end_minute   smallint NOT NULL CHECK (end_minute BETWEEN 1 AND 1440),    -- حتى 24:00
    CONSTRAINT mute_window_range CHECK (start_minute < end_minute)
);

CREATE INDEX IF NOT EXISTS mute_windows_rule_idx
    ON rules.mute_windows (rule_id, day_of_week, start_minute);

ALTER TABLE rules.mute_windows ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_mute_windows ON rules.mute_windows;
CREATE POLICY tenant_isolation_mute_windows ON rules.mute_windows
    USING (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid);

GRANT SELECT, INSERT, UPDATE, DELETE ON rules.mute_windows TO nabd_app;

COMMIT;
