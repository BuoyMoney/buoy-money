namespace BuoyAI.Core.Interfaces;

/// <summary>One finished email, ready to hand to a mail service.</summary>
public record OutboundEmail(
  string FromName,
  string FromAddress,
  string To,
  string? ReplyTo,
  string Subject,
  string HtmlBody,
  string TextBody);

/// <summary>
/// The single place an email leaves the app. <c>ResendEmailService</c> builds every message
/// (templates, preferences, subjects) and ends here. Hosted sends through Resend; a self-hosted
/// server sends through any SMTP server it is pointed at, or sends nothing when none is set up.
/// </summary>
public interface IEmailTransport
{
  /// <summary>False when no mail service is configured, so every send is a logged no-op.</summary>
  bool IsConfigured { get; }

  Task SendAsync(OutboundEmail message, CancellationToken cancellationToken = default);
}
