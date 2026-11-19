-- 0022__alerting.sql — P4 spec 05 (FR-32/FR-33, plan/04 §8, plan/06 §5.1–5.3)
-- alerts = خيط قصة واحد لكل (tenant, asset, rule)؛ alert_events = تدقيق كامل؛
-- notification_log = سجل الإرسال (يكتبه سبيك 08). الجدار بنمط 0012/0021 (NULLIF).
BEGIN;

CREATE SCHEMA IF NOT EXISTS alerting;

-- 1) التنبيهات (plan/04 §8 + عمودا ack + عمودا wall/thread)
CREATE TABLE IF NOT EXISTS alerting.alerts (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       uuid NOT NULL REFERENCES identity.tenants(id),
    asset_id        uuid NOT NULL REFERENCES assets.assets(id),
    -- بلا FK بقرار: حذف القاعدة لا يمحو تاريخ التنبيه ولا يُمنع (السبيك 03 يبقى 204)
    rule_id         uuid NOT NULL,
    type            text NOT NULL CHECK (type IN ('threshold', 'offline')),
    severity        text NOT NULL CHECK (severity IN ('info', 'warn', 'critical')),
    status          text NOT NULL CHECK (status IN ('open', 'acknowledged', 'resolved')),
    opened_at       timestamptz NOT NULL,
    resolved_at     timestamptz,
    acknowledged_at timestamptz,
    acknowledged_by uuid,
    title           jsonb NOT NULL,          -- {"en": "...", "ar": "..."} (00 D3)
    detail          jsonb NOT NULL,          -- القراءة المرصودة + القناة + العتبات

    -- خيط القصة الواحد (plan/06 §5.1): صف واحد لكل مفتاح، وإعادة الرفع upsert
    CONSTRAINT alerts_one_per_key UNIQUE (tenant_id, asset_id, rule_id),

    -- الشكل في البيانات: resolved ⟺ resolved_at
    CONSTRAINT alerts_resolved_shape
        CHECK ((status = 'resolved') = (resolved_at IS NOT NULL)),

    -- ack يعني: من ومتى (الحل بعد ack لا يمسحهما — القيد أحادي الاتجاه)
    CONSTRAINT alerts_ack_fields
        CHECK (status <> 'acknowledged'
               OR (acknowledged_at IS NOT NULL AND acknowledged_by IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS alerts_tenant_opened_idx
    ON alerting.alerts (tenant_id, opened_at DESC);
CREATE INDEX IF NOT EXISTS alerts_tenant_status_idx
    ON alerting.alerts (tenant_id, status);

-- 2) سجل الدورة (plan/04 §8 + العمودان a/b): كل انتقال، وكل كتم، يترك أثرًا (plan/06 §7)
CREATE TABLE IF NOT EXISTS alerting.alert_events (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid NOT NULL REFERENCES identity.tenants(id),
    alert_id      uuid NOT NULL REFERENCES alerting.alerts(id) ON DELETE CASCADE,
    event         text NOT NULL CHECK (event IN (
                      'opened', 'deduplicated', 'suppressed', 'reopened',
                      'acknowledged', 'resolved')),
    actor_user_id uuid,                      -- من فعلها (NULL = النظام)
    reason        text,                      -- 'open' | 'cooldown' | 'condition_restored' | 'manual'
    detail        jsonb,                     -- دليل القراءة المكبوتة/المُعاد رفعها
    at            timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS alert_events_alert_idx
    ON alerting.alert_events (alert_id, at);

-- 3) سجل الإرسال (plan/04 §9) — يُنشأ الآن، ويكتبه سبيك 08
CREATE TABLE IF NOT EXISTS alerting.notification_log (
    id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES identity.tenants(id),
    alert_id  uuid NOT NULL REFERENCES alerting.alerts(id) ON DELETE CASCADE,
    channel   text NOT NULL CHECK (channel IN ('email', 'telegram', 'dashboard')),
    recipient text,
    status    text NOT NULL CHECK (status IN ('sent', 'failed', 'suppressed')),
    sent_at   timestamptz
);

CREATE INDEX IF NOT EXISTS notification_log_alert_idx
    ON alerting.notification_log (alert_id);

-- 4) الجدار (0012/0021 idiom): fail-shut — GUC غائب/فارغ = صفر صفوف
ALTER TABLE alerting.alerts ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_alerts ON alerting.alerts;
CREATE POLICY tenant_isolation_alerts ON alerting.alerts
    USING (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE alerting.alert_events ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_alert_events ON alerting.alert_events;
CREATE POLICY tenant_isolation_alert_events ON alerting.alert_events
    USING (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE alerting.notification_log ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_notification_log ON alerting.notification_log;
CREATE POLICY tenant_isolation_notification_log ON alerting.notification_log
    USING (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid);

-- 5) أقل صلاحية ممكنة (0005/0012 idiom)
GRANT USAGE ON SCHEMA alerting TO nabd_app;
GRANT SELECT, INSERT, UPDATE ON alerting.alerts TO nabd_app;          -- لا DELETE: السجل يبقى
GRANT SELECT, INSERT ON alerting.alert_events TO nabd_app;            -- append-only
GRANT SELECT, INSERT, UPDATE ON alerting.notification_log TO nabd_app;

COMMIT;
