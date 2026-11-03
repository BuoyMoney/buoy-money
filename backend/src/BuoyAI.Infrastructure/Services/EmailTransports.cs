using BuoyAI.Core.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using Resend;

namespace BuoyAI.Infrastructure.Services;

/// <summary>Hosted: Resend, exactly the call <c>ResendEmailService</c> made before the transport existed.</summary>
public class ResendEmailTransport : IEmailTransport
{
  private readonly IResend _resend;

  public ResendEmailTransport(IResend resend) => _resend = resend;

  public bool IsConfigured => true;

  public Task SendAsync(OutboundEmail message, CancellationToken cancellationToken = default) =>
    _resend.EmailSendAsync(new EmailMessage
    {
      From = $"{message.FromName} <{message.FromAddress}>",
      To = [message.To],
      ReplyTo = message.ReplyTo == null ? null : [message.ReplyTo],
      Subject = message.Subject,
      HtmlBody = message.HtmlBody,
      TextBody = message.TextBody
    }, cancellationToken);
}

/// <summary>
/// Self-host without a mail server: nothing is sent, and the log says so once per message so the
/// operator can see why a reset or invite email never arrived. <see cref="IsConfigured"/> is
/// false, which lets sign-up skip email verification (no code could ever be delivered).
/// </summary>
public class NoEmailTransport : IEmailTransport
{
  private readonly ILogger<NoEmailTransport> _logger;

  public NoEmailTransport(ILogger<NoEmailTransport> logger) => _logger = logger;

  public bool IsConfigured => false;

  public Task SendAsync(OutboundEmail message, CancellationToken cancellationToken = default)
  {
    _logger.LogWarning("Email not sent (no mail server configured: set Smtp__Host and Smtp__FromAddress): \"{Subject}\"", message.Subject);
    return Task.CompletedTask;
  }
}

/// <summary>
/// Self-host with a mail server. Settings (all optional except Host):
/// <c>Smtp:Host</c>, <c>Smtp:Port</c> (default 587), <c>Smtp:Security</c> = starttls (default), tls
/// (implicit TLS, usually port 465) or none, <c>Smtp:Username</c>, <c>Smtp:Password</c>,
/// <c>Smtp:FromAddress</c> (default: the username when it is an address), <c>Smtp:FromName</c>
/// (default "Buoy Money"). The From address is always the configured one: most SMTP servers refuse
/// to relay for a sender they do not host.
/// </summary>
public class SmtpEmailTransport : IEmailTransport
{
  private readonly IConfiguration _configuration;

  public SmtpEmailTransport(IConfiguration configuration) => _configuration = configuration;

  public bool IsConfigured => true;

  /// <summary>True only when a host is set AND a sender can be resolved, so a half-configured
  /// server behaves as "no mail" (verification skipped, email feature off) rather than promising
  /// messages it can never send.</summary>
  public static bool IsSmtpConfigured(IConfiguration configuration) =>
    !string.IsNullOrWhiteSpace(configuration["Smtp:Host"]) && ResolveFromAddress(configuration) != null;

  public static string? ResolveFromAddress(IConfiguration configuration)
  {
    var fromAddress = configuration["Smtp:FromAddress"];
    if (!string.IsNullOrWhiteSpace(fromAddress)) return fromAddress;
    var username = configuration["Smtp:Username"];
    return username != null && username.Contains('@') ? username : null;
  }

  public async Task SendAsync(OutboundEmail message, CancellationToken cancellationToken = default)
  {
    var host = _configuration["Smtp:Host"]!;
    var port = int.TryParse(_configuration["Smtp:Port"], out var p) ? p : 587;
    var username = _configuration["Smtp:Username"];
    var password = _configuration["Smtp:Password"];
    var fromAddress = ResolveFromAddress(_configuration);
    if (string.IsNullOrWhiteSpace(fromAddress))
    {
      // Never fall back to the hosted sender (noreply@buoy-ai.com): a home server claiming that
      // domain fails SPF/DMARC and is spoofing.
      throw new InvalidOperationException(
        "Smtp:FromAddress is required when Smtp:Host is set (or Smtp:Username must be an email address).");
    }
    var fromName = _configuration["Smtp:FromName"] is { Length: > 0 } n ? n : "Buoy Money";

    var security = (_configuration["Smtp:Security"] ?? "starttls").Trim().ToLowerInvariant() switch
    {
      "none" => SecureSocketOptions.None,
      "tls" or "ssl" => SecureSocketOptions.SslOnConnect,
      "starttls" => SecureSocketOptions.StartTls,
      var other => throw new InvalidOperationException($"Smtp:Security must be starttls, tls or none (got '{other}').")
    };

    var mime = new MimeMessage();
    mime.From.Add(new MailboxAddress(fromName, fromAddress));
    mime.To.Add(MailboxAddress.Parse(message.To));
    // Hosted's reply-to is support@buoy-ai.com, which is wrong for a home server: use the
    // operator's own address, or none.
    if (_configuration["Smtp:ReplyTo"] is { Length: > 0 } replyTo) mime.ReplyTo.Add(MailboxAddress.Parse(replyTo));
    mime.Subject = message.Subject;
    mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

    using var client = new SmtpClient();
    client.Timeout = 20_000;
    await client.ConnectAsync(host, port, security, cancellationToken);
    if (!string.IsNullOrEmpty(username))
    {
      await client.AuthenticateAsync(username, password ?? string.Empty, cancellationToken);
    }
    await client.SendAsync(mime, cancellationToken);
    await client.DisconnectAsync(true, cancellationToken);
  }
}
