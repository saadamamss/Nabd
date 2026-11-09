using System.Net;
using System.Net.Mail;
using Nabd.Identity.Contracts;

namespace Nabd.Identity;

/// <summary>
/// The real P7 sender, shipped NOW so the swap is config + one Program line,
/// never code. NOT exercised at P1 dev (the spec's own words): Program wires
/// DevLogEmailSender until the Hetzner stage. Every knob is env-driven
/// (NFR-08) and the class fails-shut if wired without its config.
/// </summary>

public sealed class SmtpEmailSender : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var host = Environment.GetEnvironmentVariable("NABD_SMTP_HOST")
            ?? throw new InvalidOperationException("NABD_SMTP_HOST must be set when SmtpEmailSender is wired (NFR-08: fail-shut).");
        var from = Environment.GetEnvironmentVariable("NABD_SMTP_FROM")
            ?? throw new InvalidOperationException("NABD_SMTP_FROM must be set when SmtpEmailSender is wired (NFR-08: fail-shut).");
        var port = Environment.GetEnvironmentVariable("NABD_SMTP_PORT") ?? "587";

        using var client = new SmtpClient(host, int.Parse(port)) { EnableSsl = true };

        // Optional credentials: for providers (Gmail/Brevo/...) — left empty
        // when using a local relay (P7 postfix), which needs none.
        var user = Environment.GetEnvironmentVariable("NABD_SMTP_USER");
        var pass = Environment.GetEnvironmentVariable("NABD_SMTP_PASS");
        if (user is not null && pass is not null)
            client.Credentials = new NetworkCredential(user, pass);

        using var mail = new MailMessage(from, message.To, message.Subject, message.BodyText);

        await client.SendMailAsync(mail, ct);
    }
}
