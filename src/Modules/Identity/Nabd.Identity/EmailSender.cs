using Nabd.Identity.Contracts;

namespace Nabd.Identity;

/// <summary>
/// P1 spec 05 honest defaults (plan/07 §3 row 4): SMTP is abstracted, not hidden.
/// NO raw reset token is ever logged EXCEPT in the dev outbox below — THAT is
/// the P1 delivery mechanism, deliberate and documented (walker rule: ACCIDENTAL
/// token logging is a violation; the deliberate outbox is THE feature).
/// </summary>

public sealed class DevLogEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Console.WriteLine($"""
            --- EMAIL (dev outbox) -----------------------------------------
            to:      {message.To}
            subject: {message.Subject}
            body:
            {message.BodyText}

            -----------------------------------------------------------------
            """);

        return Task.CompletedTask;
    }
}