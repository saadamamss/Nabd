-- 0025__offline_watchdog.sql — P4 spec 07 (FR-31, plan/06 §4, plan/04 §6)
-- 1) نافذة الانقطاع كبيانات لكل قاعدة (افتراضي §8 = 30).
-- 2) backfill: كل جهاز موجود يحصل على قاعدة offline واحدة.
-- 3) نافذة قراءة واحدة للمراقب (SECURITY DEFINER) — الجدار المباشر يبقى مطلقًا.
BEGIN;

ALTER TABLE rules.rules
    ADD COLUMN IF NOT EXISTS offline_after_minutes int NOT NULL DEFAULT 30
    CHECK (offline_after_minutes >= 1);

-- الأجهزة التي سُكّت قبل 0025: القاعدة موجودة الآن، والقاعدة الحتمية
-- (partial unique index من 0021) تجعل التكرار مستحيلًا.
INSERT INTO rules.rules (tenant_id, device_id, rule_type, offline_after_minutes)
SELECT d.tenant_id, d.id, 'offline', 30
FROM assets.devices d
WHERE NOT EXISTS (
    SELECT 1 FROM rules.rules r
    WHERE r.device_id = d.id AND r.rule_type = 'offline')
ON CONFLICT DO NOTHING;

-- نافذة المراقب: صف واحد لكل (جهاز نشط غير مسحوب، قاعدته offline مفعّلة، وله
-- last_seen مسجّل). SQL ثابت، بلا معاملات، وEXECUTE لـnabd_app فقط.
-- لا asset_id هنا: Alerting يحلّ الأصل من الجهاز (نفس قانون spec 05).
CREATE OR REPLACE FUNCTION rules.offline_watchdog_candidates()
RETURNS TABLE (
    tenant_id             uuid,
    device_id             uuid,
    rule_id               uuid,
    severity              text,
    cooldown_minutes      int,
    duration_seconds      int,
    offline_after_minutes int,
    last_seen_at          timestamptz,
    state_last_state      text,
    violating_since       timestamptz,
    fired                 boolean
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = rules, assets, telemetry, pg_temp
AS $$
    SELECT r.tenant_id, r.device_id, r.id,
           r.severity, r.cooldown_minutes, r.duration_seconds, r.offline_after_minutes,
           ds.last_seen_at,
           st.last_state, st.violating_since, COALESCE(st.fired, false)
      FROM rules.rules r
      JOIN assets.devices d           ON d.id = r.device_id
      JOIN telemetry.device_status ds ON ds.device_id = r.device_id
      LEFT JOIN rules.rule_eval_state st ON st.rule_id = r.id
     WHERE r.rule_type = 'offline'
       AND r.enabled
       AND d.is_active
       AND d.token_revoked_at IS NULL;
$$;

REVOKE EXECUTE ON FUNCTION rules.offline_watchdog_candidates() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION rules.offline_watchdog_candidates() TO nabd_app;

COMMIT;
