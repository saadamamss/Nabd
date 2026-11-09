namespace Nabd.Identity;

/// <summary>
/// The ONE bilingual reset template (P1 spec 05; 04 §9). The locale is a
/// parameter — the full localized UI is P5's job, not a subsystem here.
/// Shape promise the suite depends on: the reset URL is its own line, so the
/// e2e evidence (and the suite) can lift the token out of the terminal.
/// </summary>
public static class ResetMailTemplate
{
    public static string LinkBase =>
        (Environment.GetEnvironmentVariable("NABD_RESET_LINK_BASE") ?? "https://reset.nabd.local/").TrimEnd('/') + "/";

    public static (string Subject, string Body) Render(string locale, string token)
    {
        var link = $"{LinkBase}{token}";

        return locale == "ar"
            ? (Subject: "نبد — إعادة تعيين كلمة المرور",
               Body: $"""
                مرحبًا،

                تلقّينا طلبًا لإعادة تعيين كلمة مرورك في نبد.

                افتح الرابط التالي خلال ۱۵ دقيقة — يعمل مرة واحدة فقط:

                {link}

                إن لم تطلب ذلك، تجاهل هذه الرسالة — لَم يتغيّر شيء.
                """)
            : (Subject: "Nabd — reset your password",
               Body: $"""
                Hello,

                We received a request to reset your Nabd password.

                Open this link within 15 minutes — it works exactly once:

                {link}

                If you didn't request this, ignore this email — nothing changes.
                """);
    }
}