-- 0030__feedback.sql — P5 spec 06 (FR-… ; plan/01 §7.3، §7.5)
-- 1) غرفة جديدة: feedback، جدولها الوحيد feedback.messages، جداره fail-shut.
-- 2) التطبيق INSERT فقط — لا قراءة، لا تعديل: صفّه لا يُقرأ من نفس الدور الذي كتبه.
-- 3) الدالة الاستثنائية: submit_message — تُختِم المؤلّف من الدليل وتعدّ نافذة الـ30 ثانية
--    (لا SELECT لـnabd_app تعدّ به)، ثم تُدرج. نمط 0027 verify_telegram_link.
BEGIN;

-- 1) الغرفة
CREATE SCHEMA IF NOT EXISTS feedback;
GRANT USAGE ON SCHEMA feedback TO nabd_app, nabd_admin;

-- صفّ واحد لكل رسالة. user_id بلا مفتاح خارجي عن قصد (F10): الرسالة دليل،
-- فلا تختفي بحذف حساب — البريد المختوم يبقى ويقرأه المالك.
CREATE TABLE IF NOT EXISTS feedback.messages (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           uuid NOT NULL REFERENCES identity.tenants(id),
    user_id             uuid NOT NULL,
    author_email        text NOT NULL,
    author_display_name text,
    page                text NOT NULL,
    locale              text NOT NULL,
    message             text NOT NULL,
    contact_email       text,
    created_at          timestamptz NOT NULL DEFAULT now(),

    -- F3: الحدود تُقاس بعد trim (الدالة تُدرج btrim، والقيد يحرس الباب الخلفي أيضًا)
    CONSTRAINT feedback_message_len
        CHECK (length(message) BETWEEN 1 AND 2000),
    CONSTRAINT feedback_locale_known
        CHECK (locale IN ('ar', 'en')),
    CONSTRAINT feedback_page_len
        CHECK (length(page) BETWEEN 1 AND 200),
    CONSTRAINT feedback_contact_len
        CHECK (contact_email IS NULL OR length(contact_email) <= 254),
    CONSTRAINT feedback_author_len
        CHECK (length(author_email) <= 254)
);

-- قراءة المالك: الأحدث أولًا داخل المنشأة (F9)
CREATE INDEX IF NOT EXISTS feedback_messages_tenant_idx
    ON feedback.messages (tenant_id, created_at DESC);

-- نافذة المعدّل تقرأ آخر صف للمستخدم (F4)
CREATE INDEX IF NOT EXISTS feedback_messages_user_idx
    ON feedback.messages (user_id, created_at DESC);

-- 2) الجدار (نمط 0021/0022: NULLIF يبطل '' أيضًا) — الكتابة المباشرة مسموحة
--    لمنشأة الجلسة وحدها؛ أي منشأة أخرى تُرفض بالسياسة لا بالكود.
ALTER TABLE feedback.messages ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_feedback_messages ON feedback.messages;
CREATE POLICY tenant_isolation_feedback_messages ON feedback.messages
    FOR INSERT
    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid);

-- INSERT فقط — لا SELECT/UPDATE/DELETE لـnabd_app إطلاقًا (اختبار الحقيقة 5)
GRANT INSERT ON feedback.messages TO nabd_app;
-- المالك يقرأ (BYPASSRLS عبر 0011، والمنحة تبقى مطلوبة)
GRANT SELECT ON feedback.messages TO nabd_admin;

-- 3) الاستثناء الوحيد: رمز الجلسة → صفّ مختوم. لا شيء من الجسم يُصدَّق:
--    p_tenant/p_user من الرمز، والبريد والاسم من identity.users.
CREATE OR REPLACE FUNCTION feedback.submit_message(
    p_tenant  uuid,
    p_user    uuid,
    p_page    text,
    p_locale  text,
    p_message text,
    p_email   text)
RETURNS TABLE(id uuid, created_at timestamptz, accepted boolean, retry_after_seconds int)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = feedback, pg_temp
AS $$
DECLARE
    v_email   text;
    v_name    text;
    v_last    timestamptz;
    v_wait    int;
    v_id      uuid;
    v_at      timestamptz;
BEGIN
    -- المؤلّف يجب أن يكون مستخدمًا في هذه المنشأة بالذات، وإلا: لا ختم ولا صف
    -- (اقتران p_user بـp_tenant هو الحارس — لا يُقرأ من الجسم شيء)
    SELECT u.email, u.display_name
      INTO v_email, v_name
      FROM identity.users u
     WHERE u.id = p_user
       AND u.tenant_id = p_tenant;

    IF v_email IS NULL THEN
        RETURN QUERY SELECT NULL::uuid, NULL::timestamptz, false, NULL::int;
        RETURN;
    END IF;

    -- F4: نافذة 30 ثانية بساعة القاعدة، 1 لكل مستخدم
    SELECT max(m.created_at) INTO v_last
      FROM feedback.messages m
     WHERE m.user_id = p_user;

    IF v_last IS NOT NULL AND v_last > now() - interval '30 seconds' THEN
        v_wait := GREATEST(1, ceil(extract(epoch FROM (v_last + interval '30 seconds' - now())))::int);
        RETURN QUERY SELECT NULL::uuid, NULL::timestamptz, false, v_wait;
        RETURN;
    END IF;

    INSERT INTO feedback.messages AS m
        (tenant_id, user_id, author_email, author_display_name,
         page, locale, message, contact_email)
    VALUES
        (p_tenant, p_user, v_email, v_name,
         p_page, p_locale, btrim(p_message), nullif(btrim(p_email), ''))
    RETURNING m.id, m.created_at INTO v_id, v_at;

    RETURN QUERY SELECT v_id, v_at, true, NULL::int;
END;
$$;

-- الدالة ليست عامة: nabd_app وحدها تناديها (nabd_admin يقرأ الجدول لا يُرسل)
REVOKE ALL ON FUNCTION feedback.submit_message(uuid, uuid, text, text, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION feedback.submit_message(uuid, uuid, text, text, text, text) TO nabd_app;

COMMIT;
