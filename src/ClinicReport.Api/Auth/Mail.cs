using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using ClinicReport.Api.Data;
using Microsoft.Extensions.Options;

namespace ClinicReport.Api.Auth;

/// <summary>Outgoing mail (Hosted mode: sign-up confirmation). In development, Mailpit on localhost:2525 catches it.</summary>
public class MailOptions
{
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 25;
    public bool EnableSsl { get; set; }
    public string? UserName { get; set; }

    /// <summary>From /run/secrets/Mail__Password, never from appsettings.</summary>
    public string? Password { get; set; }

    public string From { get; set; } = "ClinicReport <no-reply@localhost>";

    /// <summary>A server that accepts the connection and never answers must not hold a sign-up up.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

public record OutgoingMail(string To, string Subject, string Body);

public interface IMailSender
{
    Task SendAsync(OutgoingMail mail, CancellationToken ct = default);
}

public sealed class SmtpMailSender(IOptions<MailOptions> options) : IMailSender
{
    public async Task SendAsync(OutgoingMail mail, CancellationToken ct = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.SmtpHost))
            throw new InvalidOperationException("Mail:SmtpHost is not set.");
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.EnableSsl, Timeout = o.TimeoutSeconds * 1000 };
        if (!string.IsNullOrEmpty(o.UserName))
            client.Credentials = new NetworkCredential(o.UserName, o.Password);
        using var message = new MailMessage(MailAddress.TryCreate(o.From, out var from) ? from : new MailAddress("no-reply@localhost"), new MailAddress(mail.To))
        {
            Subject = mail.Subject, Body = mail.Body, BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        await client.SendMailAsync(message, timeout.Token);
    }
}

/// <summary>
/// The sign-up's email address is confirmed with a one-time link. Only the token's hash is kept, in the catalog;
/// using it, or sending a new one, makes the old one worthless.
/// </summary>
public static class EmailVerification
{
    public static readonly TimeSpan ValidFor = TimeSpan.FromDays(2);

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>A new token for the tenant (to be saved); returns the token for the link.</summary>
    public static string NewToken(Tenant tenant, DateTime now)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        tenant.EmailTokenHash = Hash(token);
        tenant.EmailTokenExpiresAt = now + ValidFor;
        return token;
    }

    /// <summary>Confirms the email when the token is the tenant's current one and not expired; the token is then used up.</summary>
    public static bool TryConfirm(Tenant tenant, string? token, DateTime now)
    {
        if (tenant.EmailTokenHash is null || tenant.EmailTokenExpiresAt is not { } expires || expires < now || string.IsNullOrEmpty(token))
            return false;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(tenant.EmailTokenHash)))
            return false;
        tenant.EmailVerifiedAt = now;
        tenant.EmailTokenHash = null;
        tenant.EmailTokenExpiresAt = null;
        return true;
    }

    /// <summary>Sends the link to the owner. A failure is logged (without the address or token) and reported, not thrown: the clinic exists either way.</summary>
    public static async Task<bool> SendAsync(Tenant tenant, string token, string clinicUrl, IMailSender mail, ILogger logger, CancellationToken ct)
    {
        var link = $"{clinicUrl}verify-email?token={token}";
        try
        {
            await mail.SendAsync(new OutgoingMail(tenant.OwnerEmail!, $"Confirm your email for {tenant.Name}",
                $"""
                Welcome to ClinicReport.

                Confirm this email address to start writing reports at {clinicUrl}:

                {link}

                The link works once and expires in {ValidFor.TotalDays:0} days. If you did not sign up, ignore this email.
                """), ct);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The confirmation email for tenant {Slug} was not sent.", tenant.Slug);
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
