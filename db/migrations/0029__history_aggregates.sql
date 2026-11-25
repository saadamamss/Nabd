-- 0029__history_aggregates.sql — P5 spec 04 (FR-41/FR-43, plan/04 §6/§10)
--
-- 1) مجمّعان مستمران: ساعة ويوم لكل (tenant_id, device_id, channel_key).
--    الاسمان bucket وtotal عقد مع spec 09: استهلاك الأسطول يجمع total
--    على bucket >= now() - interval '24 hours'، فلا يعاد تسميتهما.
-- 2) WITH NO DATA: الشكل الوحيد المسموح داخل معاملة _apply.sh (psql -1).
--    التعبئة الأولى أمر refresh موثّق بعد التطبيق (phases/P5/04 §Steps 2).
-- 3) materialized_only=false: الذيل غير المادّي يُقرأ من الجدول الخام،
--    فلا تظهر الساعة/اليوم الجاري فجوة كاذبة.
-- 4) nabd_app لا يقرأ المجمّعين مباشرة أبدًا (plan/04 §10: تسريب النطاق).
--    القراءة عبر دالة definer واحدة. nabd_admin (لوحة spec 09) يحتاج
--    USAGE على المخطط ثم SELECT على الجدولين — بدون USAGE تفشل المنح.
BEGIN;

CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry.readings_hourly
WITH (timescaledb.continuous) AS
SELECT public.time_bucket('1 hour', time, 'UTC'::text) AS bucket,
       tenant_id,
       device_id,
       channel_key,
       min(value) AS min_v,
       max(value) AS max_v,
       avg(value) AS avg_v,
       count(*)   AS total
  FROM telemetry.readings
 GROUP BY bucket, tenant_id, device_id, channel_key
WITH NO DATA;

ALTER MATERIALIZED VIEW telemetry.readings_hourly
    SET (timescaledb.materialized_only = false);

CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry.readings_daily
WITH (timescaledb.continuous) AS
SELECT public.time_bucket('1 day', time, 'UTC'::text) AS bucket,
       tenant_id,
       device_id,
       channel_key,
       min(value) AS min_v,
       max(value) AS max_v,
       avg(value) AS avg_v,
       count(*)   AS total
  FROM telemetry.readings
 GROUP BY bucket, tenant_id, device_id, channel_key
WITH NO DATA;

ALTER MATERIALIZED VIEW telemetry.readings_daily
    SET (timescaledb.materialized_only = false);

-- نافذة الساعية = 30 يومًا: نفس نافذة القراءات المتأخرة المقبولة في الكود.
SELECT add_continuous_aggregate_policy('telemetry.readings_hourly',
    start_offset      => interval '30 days',
    end_offset        => interval '1 hour',
    schedule_interval => interval '30 minutes',
    if_not_exists     => true);

-- اليومية = 180 يومًا: عمر الاحتفاظ الخام المخطط.
SELECT add_continuous_aggregate_policy('telemetry.readings_daily',
    start_offset      => interval '180 days',
    end_offset        => interval '1 day',
    schedule_interval => interval '1 day',
    if_not_exists     => true);

-- المنح: التطبيق ممنوع من الجدولين، واللوحة مسموحة بعد USAGE.
REVOKE ALL ON telemetry.readings_hourly, telemetry.readings_daily FROM PUBLIC;
REVOKE ALL ON telemetry.readings_hourly, telemetry.readings_daily FROM nabd_app;

GRANT USAGE ON SCHEMA telemetry TO nabd_admin;
GRANT SELECT ON telemetry.readings_hourly, telemetry.readings_daily TO nabd_admin;

-- قراءة السجل: صف واحد دائمًا، وحقيقة القناة هنا وحدها.
-- الأرشفة لا تُفلتر (is_active غير مذكور): سجل الأصل المؤرشف يبقى مقروءًا.
CREATE OR REPLACE FUNCTION assets.asset_history(
    p_tenant  uuid,
    p_asset   uuid,
    p_channel text,
    p_from    timestamptz,
    p_to      timestamptz)
RETURNS TABLE (state text, source text, bucket_seconds int, points jsonb)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = assets, telemetry, pg_temp
AS $$
DECLARE
    v_span     double precision;
    v_template uuid;
    v_channel  text := btrim(coalesce(p_channel, ''));
    v_base     int;
    v_source   text;
    v_width    int;
BEGIN
    IF p_from IS NULL OR p_to IS NULL
       OR p_from >= p_to
       OR (p_to - p_from) > interval '366 days' THEN
        RETURN QUERY SELECT 'invalid_range'::text, NULL::text, NULL::int, NULL::jsonb;
        RETURN;
    END IF;

    SELECT a.template_id INTO v_template
      FROM assets.assets a
     WHERE a.id = p_asset AND a.tenant_id = p_tenant;

    IF v_template IS NULL THEN
        RETURN QUERY SELECT 'not_found'::text, NULL::text, NULL::int, NULL::jsonb;
        RETURN;
    END IF;

    IF v_channel = '' OR NOT EXISTS (
        SELECT 1
          FROM assets.channel_definitions cd
         WHERE cd.template_id = v_template
           AND cd.key = v_channel) THEN
        RETURN QUERY SELECT 'unknown_channel'::text, NULL::text, NULL::int, NULL::jsonb;
        RETURN;
    END IF;

    v_span := EXTRACT(EPOCH FROM (p_to - p_from));

    IF v_span <= 86400 THEN
        v_source := 'raw';    v_base := 1;
    ELSIF v_span <= 2592000 THEN
        v_source := 'hourly'; v_base := 3600;
    ELSE
        v_source := 'daily';  v_base := 86400;
    END IF;

    v_width := v_base * GREATEST(1, CEIL(v_span / (1500.0 * v_base)))::int;

    IF v_source = 'raw' THEN
        RETURN QUERY
        SELECT 'ok'::text, 'raw'::text, v_width,
               COALESCE(jsonb_agg(jsonb_build_object(
                   't', b.bucket, 'min', b.min_v, 'max', b.max_v,
                   'avg', b.avg_v, 'n', b.n) ORDER BY b.bucket), '[]'::jsonb)
          FROM (
            SELECT public.time_bucket(make_interval(secs => v_width), r.time, 'UTC'::text) AS bucket,
                   min(r.value) AS min_v,
                   max(r.value) AS max_v,
                   avg(r.value) AS avg_v,
                   count(*)    AS n
              FROM telemetry.readings r
              JOIN assets.devices d ON d.id = r.device_id AND d.tenant_id = p_tenant
             WHERE r.tenant_id = p_tenant
               AND d.asset_id = p_asset
               AND r.channel_key = v_channel
               AND r.time >= p_from AND r.time < p_to
             GROUP BY 1
          ) b;
    ELSIF v_source = 'hourly' THEN
        RETURN QUERY
        SELECT 'ok'::text, 'hourly'::text, v_width,
               COALESCE(jsonb_agg(jsonb_build_object(
                   't', b.bucket, 'min', b.min_v, 'max', b.max_v,
                   'avg', b.avg_v, 'n', b.n) ORDER BY b.bucket), '[]'::jsonb)
          FROM (
            SELECT public.time_bucket(make_interval(secs => v_width), h.bucket, 'UTC'::text) AS bucket,
                   min(h.min_v) AS min_v,
                   max(h.max_v) AS max_v,
                   sum(h.avg_v * h.total) / NULLIF(sum(h.total), 0) AS avg_v,
                   sum(h.total) AS n
              FROM telemetry.readings_hourly h
              JOIN assets.devices d ON d.id = h.device_id AND d.tenant_id = p_tenant
             WHERE h.tenant_id = p_tenant
               AND d.asset_id = p_asset
               AND h.channel_key = v_channel
               AND h.bucket >= public.time_bucket(make_interval(secs => v_width), p_from, 'UTC'::text)
               AND h.bucket <  p_to
             GROUP BY 1
          ) b;
    ELSE
        RETURN QUERY
        SELECT 'ok'::text, 'daily'::text, v_width,
               COALESCE(jsonb_agg(jsonb_build_object(
                   't', b.bucket, 'min', b.min_v, 'max', b.max_v,
                   'avg', b.avg_v, 'n', b.n) ORDER BY b.bucket), '[]'::jsonb)
          FROM (
            SELECT public.time_bucket(make_interval(secs => v_width), g.bucket, 'UTC'::text) AS bucket,
                   min(g.min_v) AS min_v,
                   max(g.max_v) AS max_v,
                   sum(g.avg_v * g.total) / NULLIF(sum(g.total), 0) AS avg_v,
                   sum(g.total) AS n
              FROM telemetry.readings_daily g
              JOIN assets.devices d ON d.id = g.device_id AND d.tenant_id = p_tenant
             WHERE g.tenant_id = p_tenant
               AND d.asset_id = p_asset
               AND g.channel_key = v_channel
               AND g.bucket >= public.time_bucket(make_interval(secs => v_width), p_from, 'UTC'::text)
               AND g.bucket <  p_to
             GROUP BY 1
          ) b;
    END IF;
END;
$$;

REVOKE ALL ON FUNCTION assets.asset_history(uuid, uuid, text, timestamptz, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assets.asset_history(uuid, uuid, text, timestamptz, timestamptz) TO nabd_app;

COMMIT;
