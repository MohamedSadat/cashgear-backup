using System.Net.Sockets;
using CashGear.Backup.Agent.Api;
using CashGear.Backup.Core;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CashGear.Backup.Agent.Notifications;

internal interface IEmailSender
{
    /// <summary>Failures are <see cref="AgentException"/>s with the server's answer; never with the password.</summary>
    Task SendAsync(EmailConfig email, SecureSecret? password, string subject, string body, CancellationToken ct);
}

/// <summary>
/// Plain-text mail through the configured SMTP server (MailKit). STARTTLS is required, not opportunistic, when
/// chosen; no protocol log is kept (it would contain the login).
/// </summary>
internal sealed class SmtpEmailSender : IEmailSender
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(EmailConfig email, SecureSecret? password, string subject, string body, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(email.From));
        foreach (var recipient in email.Recipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = (int)s_timeout.TotalMilliseconds };
        try
        {
            var security = email.Security switch
            {
                SmtpSecurityMode.Tls => SecureSocketOptions.SslOnConnect,
                SmtpSecurityMode.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            };
            await client.ConnectAsync(email.Host, email.EffectivePort, security, ct);
            if (email.Username.Length > 0)
            {
                if (password is null)
                {
                    throw new AgentException(AgentError.FailedPrecondition, "The email settings have a username but no password.");
                }

                await client.AuthenticateAsync(email.Username, password.Reveal(), ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or MailKit.Security.AuthenticationException
            or System.Security.Authentication.AuthenticationException or SslHandshakeException
            or SocketException or IOException or ServiceNotConnectedException or ServiceNotAuthenticatedException or TimeoutException)
        {
            throw new AgentException(AgentError.FailedPrecondition, $"The email could not be sent through {email.Host}:{email.EffectivePort}: {ex.Message}");
        }
    }
}
