using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Immanuel.KeyValue.Core;

/// <summary>Delivers a one-time password to the address that asked for it.</summary>
public interface IOtpSender
{
    /// <summary>Whether a real message can be sent at all. False means the caller should fall
    /// back to <see cref="AuthOptions.MasterOtp"/>.</summary>
    bool CanSend { get; }

    /// <summary>Sends the code. Returns false when the relay refused it.</summary>
    Task<bool> SendAsync(string email, string code, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends the code over SMTP when a relay is configured, and reports <see cref="CanSend"/> false
/// when one is not - that is the signal that turns the master code from appsettings on.
///
/// System.Net.Mail rather than a mail library: one short plain-text message per sign-in does not
/// justify a dependency, and the relay settings are the same either way.
/// </summary>
public sealed class SmtpOtpSender(IOptions<AuthOptions> options, ILogger<SmtpOtpSender> logger) : IOtpSender
{
    private readonly AuthOptions _auth = options.Value;

    public bool CanSend => _auth.Smtp.IsConfigured;

    public async Task<bool> SendAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        if (!CanSend) return false;

        var smtp = _auth.Smtp;

        try
        {
            using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseSsl };

            if (!string.IsNullOrWhiteSpace(smtp.UserName))
            {
                client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);
            }

            using var message = new MailMessage
            {
                From = new MailAddress(smtp.FromAddress!, smtp.FromName),
                Subject = $"{code} is your KeyValue sign-in code",
                Body = Body(code, _auth.OtpLifetimeMinutes),
                IsBodyHtml = false,
            };

            message.To.Add(email);

            await client.SendMailAsync(message, cancellationToken);

            logger.LogInformation("Sent a sign-in code to {Email}", email);
            return true;
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or IOException)
        {
            // The code itself never goes to the log - only the fact that delivery failed.
            logger.LogError(ex, "Could not send a sign-in code to {Email}", email);
            return false;
        }
    }

    private static string Body(string code, int minutes) => $"""
        Your sign-in code is {code}

        It is valid for {minutes} minutes and can be used once.

        If you did not ask to sign in, you can ignore this message - nothing has changed
        on your account.
        """;
}
