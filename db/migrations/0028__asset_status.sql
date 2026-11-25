-- 0028__asset_status.sql — P5 spec 03 (FR-40, plan/03 §3, plan/06 §5.1)
-- 1) حالة الأصل مشتقّة، لا عمود مخزَّن: الصف المفتوح في alerting.alerts هو الحقيقة،
--    وlast_seen من telemetry.device_status. دالة واحدة STABLE SECURITY DEFINER
--    تقرأ عبر المخططات الثلاثة (نمط 0016/0017/0025) وتُمنح لـnabd_app وحدها.
-- 2) «muted» ليس حالة أصل (B12): نافذة الهدوء تُسكت الإشعار فقط، والصف يُفتح
--    فيبقى status الحقيقي ظاهرًا. الدالة لا تعرف كلمة muted إطلاقًا.
-- 3) إشعار الدفع من الصف نفسه: trigger AFTER على alerting.alerts ينشر
--    {tenantId, assetId, alertId, kind, severity, status, since} على قناة
--    nabd_events. التريجر يعمل عند COMMIT بحكم قانون NOTIFY في Postgres، فيغطي
--    كل الكتّاب: المحرّك والمراقب (ProcessingHost) وack/resolve (ApiHost) وpsql.
--    (تعديل موثّق على B4: مشترك على الناقل في مضيف واحد كان سيُفوّت كتابات المضيف
--     الآخر — التفاصيل في phases/P5/NOTES.md.)
BEGIN;

-- الحالة المشتقّة لكل أصل في مستأجر واحد.
-- since = opened_at للتنبيه الفائز، وNULL عندما يكون الأصل سليمًا.
-- التسلسل (B7): offline (أي شدّة) > critical > warn/info، والأحدث ضمن الفئة.
CREATE OR REPLACE FUNCTION assets.asset_status_by_tenant(p_tenant uuid)
RETURNS TABLE (
    asset_id     uuid,
    status       text,
    since        timestamptz,
    alert_id     uuid,
    severity     text,
    last_seen_at timestamptz,
    is_active    boolean
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = assets, alerting, telemetry, pg_temp
AS $$
    SELECT a.id,
           CASE
               WHEN live.id IS NULL            THEN 'healthy'
               WHEN live.type = 'offline'      THEN 'offline'
               WHEN live.severity = 'critical' THEN 'alert'
               ELSE 'warning'
           END,
           live.opened_at,
           live.id,
           live.severity,
           seen.last_seen_at,
           a.is_active
      FROM assets.assets a
      LEFT JOIN LATERAL (
          SELECT al.id, al.type, al.severity, al.opened_at
            FROM alerting.alerts al
           WHERE al.asset_id = a.id
             AND al.status IN ('open', 'acknowledged')
           ORDER BY CASE WHEN al.type = 'offline'      THEN 3
                         WHEN al.severity = 'critical' THEN 2
                         ELSE 1
                    END DESC,
                    al.opened_at DESC
           LIMIT 1
      ) live ON true
      LEFT JOIN LATERAL (
          SELECT max(ds.last_seen_at) AS last_seen_at
            FROM assets.devices d
            JOIN telemetry.device_status ds ON ds.device_id = d.id
           WHERE d.asset_id = a.id
      ) seen ON true
     WHERE a.tenant_id = p_tenant;
$$;

REVOKE ALL ON FUNCTION assets.asset_status_by_tenant(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assets.asset_status_by_tenant(uuid) TO nabd_app;

-- تلميح الدفع: AFTER trigger، بلا قراءة صفوف، والحِمل صغير.
-- AFTER + NOTIFY = يُسلَّم عند نجاح الـCOMMIT فقط (لا إشعار لكتابة لم تثبت).
CREATE OR REPLACE FUNCTION alerting.notify_status_change()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM pg_notify('nabd_events', json_build_object(
        'tenantId', NEW.tenant_id,
        'assetId',  NEW.asset_id,
        'alertId',  NEW.id,
        'kind',     CASE WHEN TG_OP = 'INSERT' THEN 'raised' ELSE 'changed' END,
        'severity', NEW.severity,
        'status',   NEW.status,
        'since',    NEW.opened_at
    )::text);
    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS alerts_notify_status ON alerting.alerts;
CREATE TRIGGER alerts_notify_status
    AFTER INSERT OR UPDATE OF status, severity ON alerting.alerts
    FOR EACH ROW EXECUTE FUNCTION alerting.notify_status_change();

COMMIT;
