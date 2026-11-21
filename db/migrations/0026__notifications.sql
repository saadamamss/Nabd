-- 0026__notifications.sql — P4 spec 08 (FR-34, acceptance #2 email leg, plan/04 §9)
-- 1) مخطط notifications: قناة واحدة (بريد الآن، تلغرام في 0027) + قوالب بلغتين.
-- 2) السجل موجود في 0022 (alerting.notification_log) — نضيف إليه ما يجعله دليلًا.
-- 3) الجدار: الجدولان platform-level (قراءة فقط للتطبيق)، والسجل يبقى محصورًا بالمنشأة.
BEGIN;

CREATE SCHEMA IF NOT EXISTS notifications;

-- 1) إعداد القناة: غير السري في jsonb، والسري ciphertext AES-256-GCM (nonce 12/tag 16).
CREATE TABLE IF NOT EXISTS notifications.channel_config (
    channel      text PRIMARY KEY CHECK (channel IN ('email', 'telegram')),
    enabled      boolean NOT NULL DEFAULT false,
    config       jsonb   NOT NULL DEFAULT '{}'::jsonb,
    secret_enc   bytea   NOT NULL,
    secret_nonce bytea   NOT NULL CHECK (octet_length(secret_nonce) = 12),
    secret_tag   bytea   NOT NULL CHECK (octet_length(secret_tag) = 16),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

-- 2) القوالب: المفتاح × القناة × اللغة. subject للبريد فقط ⇒ قيد صريح.
CREATE TABLE IF NOT EXISTS notifications.message_templates (
    key     text NOT NULL CHECK (key IN ('alert_raised', 'alert_resolved', 'device_offline')),
    channel text NOT NULL CHECK (channel IN ('email', 'telegram')),
    locale  text NOT NULL CHECK (locale IN ('ar', 'en')),
    subject text,
    body    text NOT NULL,
    PRIMARY KEY (key, channel, locale),
    CONSTRAINT email_template_has_subject
        CHECK (channel <> 'email' OR subject IS NOT NULL)
);

-- 3) السجل (0022) + أعمدة الدليل. القيد يمنع صفّ بريد بلا قالب أو لغة.
ALTER TABLE alerting.notification_log
    ADD COLUMN IF NOT EXISTS template_key text,
    ADD COLUMN IF NOT EXISTS locale       text,
    ADD COLUMN IF NOT EXISTS attempts     int NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    ADD COLUMN IF NOT EXISTS error        text;

ALTER TABLE alerting.notification_log
    ADD CONSTRAINT notification_log_email_shape
    CHECK (channel <> 'email' OR (template_key IS NOT NULL AND locale IS NOT NULL));

-- 4) الجدار (نمط 0021/0022، بصيغة fail-shut التي تُبطل '' أيضًا).
ALTER TABLE notifications.channel_config ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS read_channel_config ON notifications.channel_config;
CREATE POLICY read_channel_config ON notifications.channel_config
    FOR SELECT USING (true);          -- إعداد منصّة: كل التطبيق يقرأ، لا أحد يكتب

ALTER TABLE notifications.message_templates ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS read_message_templates ON notifications.message_templates;
CREATE POLICY read_message_templates ON notifications.message_templates
    FOR SELECT USING (true);          -- قوالب منصّة: تُقرأ فقط

GRANT USAGE ON SCHEMA notifications TO nabd_app;
GRANT SELECT ON notifications.channel_config, notifications.message_templates TO nabd_app;
-- لا INSERT/UPDATE/DELETE: الكتابة بالهجرة وبوابة الأسرار (owner) فقط.

-- 5) القوالب الستة (00 D3: لغتان). {{title}} يحمل اسم الأصل والقراءة/الفرق أصلًا.
INSERT INTO notifications.message_templates (key, channel, locale, subject, body) VALUES
('alert_raised', 'email', 'en',
 '[Nabd] {{severity}} — {{title}}',
 E'Nabd alert ({{severity}})\n\n{{title}}\n\nOpened: {{time}} UTC\nAlert: {{alert_id}}\n\nYou receive this because your account belongs to this tenant.\n'),
('alert_raised', 'email', 'ar',
 '[نبض] {{severity}} — {{title}}',
 E'تنبيه من نبض ({{severity}})\n\n{{title}}\n\nوقت الفتح: {{time}} UTC\nرقم التنبيه: {{alert_id}}\n\nوصلتك هذه الرسالة لأن حسابك ضمن هذه المنشأة.\n'),
('device_offline', 'email', 'en',
 '[Nabd] Device offline — {{title}}',
 E'Nabd alert ({{severity}})\n\n{{title}}\n\nSilent for: {{gap}} minutes\nOpened: {{time}} UTC\nAlert: {{alert_id}}\n'),
('device_offline', 'email', 'ar',
 '[نبض] جهاز متوقف — {{title}}',
 E'تنبيه من نبض ({{severity}})\n\n{{title}}\n\nمدة الصمت: {{gap}} دقيقة\nوقت الفتح: {{time}} UTC\nرقم التنبيه: {{alert_id}}\n'),
('alert_resolved', 'email', 'en',
 '[Nabd] Resolved — {{title}}',
 E'The alert below is resolved.\n\n{{title}}\n\nResolved: {{time}} UTC\nReason: {{reason}}\nAlert: {{alert_id}}\n'),
('alert_resolved', 'email', 'ar',
 '[نبض] تم الحل — {{title}}',
 E'تم حلّ التنبيه التالي.\n\n{{title}}\n\nوقت الحل: {{time}} UTC\nالسبب: {{reason}}\nرقم التنبيه: {{alert_id}}\n')
ON CONFLICT (key, channel, locale) DO NOTHING;

COMMIT;
