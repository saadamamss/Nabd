-- 0024__rule_eval_fired.sql — إصلاح عيب سبيك 04: علم "أُشعل" يجب أن يعيش في القاعدة لا في الذاكرة.
-- بلا العمود، كل checkpoint يمسح الكاش فيعود fired=false: (أ) عودة تأتي بعد إعادة تشغيل
-- بلا قراءة مخالفة بينهما لا تُصدر ConditionRestored فيبقى التنبيه مفتوحًا،
-- (ب) كل checkpoint يعيد نشر CrossingDetected لنفس النوبة. الحالة الدائمة هي الحقيقة.
BEGIN;

ALTER TABLE rules.rule_eval_state
    ADD COLUMN IF NOT EXISTS fired boolean NOT NULL DEFAULT false;

-- لا معنى لـ"أُشعل" خارج نوبة انتهاك: العلم صحيح فقط ما دام last_state = 'violating'.
ALTER TABLE rules.rule_eval_state
    ADD CONSTRAINT rule_eval_fired_requires_violating
    CHECK (NOT fired OR last_state = 'violating');

COMMIT;
