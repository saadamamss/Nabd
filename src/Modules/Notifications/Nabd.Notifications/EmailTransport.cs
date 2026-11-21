namespace Nabd.Notifications;

using System.Net;
using System.Net.Mail;

/// <summary>
/// The ONE seam where this room touches the outside world (D8f). The net fakes this
/// interface to make SMTP succeed, fail, or fail twice then succeed — no network and
/// no credentials in tests. Production is `SmtpEmailTransport`.
/// </summary>
public interface IEmailTransport
{
    Task SendAsync(
        EmailSettings settings, string to, string subject, string body,
        CancellationToken ct = default);
}

/// <summary>System.Net.Mail (D8f): zero new dependencies; EnableSsl does STARTTLS on 587.</summary>
public sealed class SmtpEmailTransport : IEmailTransport
{
    public async Task SendAsync(
        EmailSettings settings, string to, string subject, string body,
        CancellationToken ct = default)
    {
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = true,
            Timeout = 15_000,
        };
        if (settings.User is not null && settings.Password is not null)
            client.Credentials = new NetworkCredential(settings.User, settings.Password);

        using var mail = new MailMessage
        {
            From = new MailAddress(settings.From, settings.SenderName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };
        mail.To.Add(to);

        await client.SendMailAsync(mail, ct);
    }
}
