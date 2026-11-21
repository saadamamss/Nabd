-- 0027__telegram.sql — P4 spec 09 (FR-34, FR-38; plan/04 §9, plan/06 §6)
-- 1) user_channels: محادثة واحدة لكل مستخدم، الرمز مُهشَّر و10 دقائق، الشدّات JSON.
-- 2) الدالة الاستثنائية الوحيدة: verify_telegram_link — عامل البوت بلا منشأة.
-- 3) ستة قوالب تلغرام (بلا subject) + قيد دليل على notification_log.
BEGIN;

-- 1) القناة الشخصية (plan/04 §9)
CREATE TABLE IF NOT EXISTS notifications.user_channels (
    user_id     uuid NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
    tenant_id   uuid NOT NULL REFERENCES identity.tenants(id),
    channel     text NOT NULL CHECK (channel = 'telegram'),
    external_id text,                          -- chat id، NULL حتى الربط
    verified_at timestamptz,
    severities  jsonb NOT NULL DEFAULT '["warn","critical"]'::jsonb,

    -- رمز الربط: hash فقط، وصلاحية معه (FR-38: 10 دقائق)
    link_code_hash       bytea,
    link_code_expires_at timestamptz,

    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),

    PRIMARY KEY (user_id, channel),

    -- مربوط ⟺ له عنوان (لا صف "نصف مربوط")
    CONSTRAINT user_channels_linked_shape
        CHECK ((verified_at IS NULL) = (external_id IS NULL)),

    -- الرمز ورمزه معًا أو لا
    CONSTRAINT user_channels_code_shape
        CHECK ((link_code_hash IS NULL) = (link_code_expires_at IS NULL)),

    CONSTRAINT user_channels_hash_len
        CHECK (link_code_hash IS NULL OR octet_length(link_code_hash) = 32),

    -- plan/06 §6: الشدّتان الوحيدتان القابلتان للدفع (info لوحة فقط)
    CONSTRAINT user_channels_severities_array CHECK (jsonb_typeof(severities) = 'array'),
    CONSTRAINT user_channels_severities_known
        CHECK (severities <@ '["warn","critical"]'::jsonb)
);

-- محادثة واحدة لا تخدم حسابين: إعادة الربط تنقلها (الدالة تتولى الفصل)
CREATE UNIQUE INDEX IF NOT EXISTS user_channels_one_chat
    ON notifications.user_channels (channel, external_id)
    WHERE external_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS user_channels_tenant_idx
    ON notifications.user_channels (tenant_id, channel);

-- الجدار (نمط 0021/0022: NULLIF يبطل '' أيضًا)
ALTER TABLE notifications.user_channels ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_user_channels ON notifications.user_channels;
CREATE POLICY tenant_isolation_user_channels ON notifications.user_channels
    USING (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid);

-- التطبيق يكتب صفه الشخصي فقط (الـRLS تحكم أيهما)
GRANT SELECT, INSERT, UPDATE, DELETE ON notifications.user_channels TO nabd_app;

-- 2) الاستثناء الوحيد: من msg → user. لا قراءة جدول مباشرة للعامل.
CREATE OR REPLACE FUNCTION notifications.verify_telegram_link(
    p_code_hash bytea, p_chat_id text)
RETURNS TABLE(user_id uuid, tenant_id uuid)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = notifications, pg_temp
AS $$
DECLARE
    v_user   uuid;
    v_tenant uuid;
BEGIN
    SELECT uc.user_id, uc.tenant_id INTO v_user, v_tenant
      FROM notifications.user_channels uc
     WHERE uc.channel = 'telegram'
       AND uc.link_code_hash = p_code_hash
       AND uc.link_code_expires_at > now()
     FOR UPDATE;

    IF v_user IS NULL THEN
        RETURN;                       -- رمز خاطئ/منتهٍ/مستخدم: لا شيء يتغير
    END IF;

    -- المحادثة تُفصل عن أي حساب آخر (من يملك المحادثة يملكها الآن)
    -- alias إجباري: أسماء OUT (user_id/tenant_id) متغيرات، والعمود يحمل الاسم نفسه
    UPDATE notifications.user_channels uc
       SET external_id = NULL, verified_at = NULL, updated_at = now()
     WHERE uc.channel = 'telegram'
       AND uc.external_id = p_chat_id
       AND uc.user_id <> v_user;

    -- الربط + استهلاك الرمز (مرة واحدة)
    UPDATE notifications.user_channels uc
       SET external_id = p_chat_id,
           verified_at = now(),
           link_code_hash = NULL,
           link_code_expires_at = NULL,
           updated_at = now()
     WHERE uc.user_id = v_user AND uc.channel = 'telegram';

    RETURN QUERY SELECT v_user, v_tenant;
END $$;

REVOKE EXECUTE ON FUNCTION notifications.verify_telegram_link(bytea, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION notifications.verify_telegram_link(bytea, text) TO nabd_app;

-- 3) الدليل: صف تلغرام بلا قالب/لغة ليس دليلًا (نظير قيد البريد في 0026)
ALTER TABLE alerting.notification_log
    DROP CONSTRAINT IF EXISTS notification_log_telegram_shape;
ALTER TABLE alerting.notification_log
    ADD CONSTRAINT notification_log_telegram_shape
    CHECK (channel <> 'telegram' OR (template_key IS NOT NULL AND locale IS NOT NULL));

-- 4) القوالب الستة (00 D3: لغتان؛ تلغرام بلا subject)
INSERT INTO notifications.message_templates (key, channel, locale, subject, body) VALUES
('alert_raised', 'telegram', 'en', NULL,
 E'Nabd — alert ({{severity}})\n\n{{title}}\n\nOpened: {{time}} UTC\nAlert: {{alert_id}}'),
('alert_raised', 'telegram', 'ar', NULL,
 E'نبض — تنبيه ({{severity}})\n\n{{title}}\n\nوقت الفتح: {{time}} UTC\nرقم التنبيه: {{alert_id}}'),
('device_offline', 'telegram', 'en', NULL,
 E'Nabd — device offline\n\n{{title}}\n\nSilent for: {{gap}} minutes\nOpened: {{time}} UTC'),
('device_offline', 'telegram', 'ar', NULL,
 E'نبض — جهاز متوقف\n\n{{title}}\n\nمدة الصمت: {{gap}} دقيقة\nوقت الفتح: {{time}} UTC'),
('alert_resolved', 'telegram', 'en', NULL,
 E'Nabd — resolved\n\n{{title}}\n\nResolved: {{time}} UTC\nReason: {{reason}}'),
('alert_resolved', 'telegram', 'ar', NULL,
 E'نبض — تم الحل\n\n{{title}}\n\nوقت الحل: {{time}} UTC\nالسبب: {{reason}}')
ON CONFLICT (key, channel, locale) DO NOTHING;

COMMIT;
