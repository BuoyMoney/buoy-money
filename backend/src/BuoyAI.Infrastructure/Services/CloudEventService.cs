using System.Globalization;
using System.Net;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Cloud;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Tells a Cloud customer about something a Cloud service saw: the AI allowance at 80 or 100 percent, a new or
/// re-paired server on their account, or a Cloud bank connection that needs them. The Cloud account id is the
/// customer's Stripe customer id, so the customer is the user who holds that id here.
/// The fix for each of these happens on the customer's own server, so the notices carry no in-app link.
/// Each event becomes one in-app notification (which also sends the push copy, with the user's push preferences)
/// and one email. A pairing notice is a security email and always goes out; the others follow email preferences.
/// </summary>
public class CloudEventService : ICloudEventService
{
    public const string AiAllowanceType = "ai_allowance";
    public const string ServerPairedType = "server_paired";
    public const string BankAttentionType = "bank_needs_attention";

    private readonly BuoyAIDbContext _db;
    private readonly INotificationService _notifications;
    private readonly IEmailService _email;
    private readonly IEmailPreferencesService _emailPreferences;
    private readonly ILogger<CloudEventService> _logger;

    public CloudEventService(
        BuoyAIDbContext db,
        INotificationService notifications,
        IEmailService email,
        IEmailPreferencesService emailPreferences,
        ILogger<CloudEventService> logger)
    {
        _db = db;
        _notifications = notifications;
        _email = email;
        _emailPreferences = emailPreferences;
        _logger = logger;
    }

    public async Task<CloudEventOutcome> HandleAsync(CloudEvent cloudEvent, CancellationToken cancellationToken)
    {
        var type = cloudEvent.Type ?? string.Empty;
        if (type is not (AiAllowanceType or ServerPairedType or BankAttentionType))
        {
            _logger.LogInformation("Ignored a Cloud event of unknown type {Type}", Truncate(type, 40));
            return CloudEventOutcome.UnknownType;
        }

        var accountId = cloudEvent.AccountId?.Trim();
        if (string.IsNullOrEmpty(accountId) || accountId.Length > 255)
        {
            return CloudEventOutcome.Invalid;
        }

        // Two live rows with one customer id would be a data problem; tell nobody rather than the wrong person.
        var users = await _db.Users.AsNoTracking()
            .Where(u => u.StripeCustomerId == accountId && !u.IsDeleted)
            .Select(u => new { u.Id, u.Email, u.FirstName })
            .Take(2)
            .ToListAsync(cancellationToken);
        if (users.Count != 1)
        {
            _logger.LogWarning("Cloud event {Type} for an account with {Count} matching users; nobody was told", type, users.Count);
            return CloudEventOutcome.UnknownAccount;
        }
        var user = users[0];

        var message = type switch
        {
            AiAllowanceType => AiAllowance(cloudEvent),
            ServerPairedType => ServerPaired(cloudEvent),
            _ => BankAttention(cloudEvent),
        };
        if (message == null)
        {
            return CloudEventOutcome.Invalid;
        }

        await _notifications.CreateNotificationAsync(user.Id, new CreateNotificationRequest
        {
            Type = message.NotificationType,
            Title = message.Title,
            Message = message.Body,
            ActionUrl = message.ActionUrl,
            ActionText = message.ActionText,
        });

        await TrySendEmailAsync(user.Id, user.Email, user.FirstName, message);
        return CloudEventOutcome.Delivered;
    }

    private async Task TrySendEmailAsync(int userId, string? email, string? firstName, Message message)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }
        try
        {
            if (!await _emailPreferences.ShouldSendEmailAsync(userId, message.EmailType))
            {
                return;
            }
            var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hi," : $"Hi {firstName},";
            var text = $"{greeting}\n\n{message.Body}\n\n{message.EmailExtra}\n\nThe Buoy Money team";
            var html =
                $"<p>{WebUtility.HtmlEncode(greeting)}</p>" +
                $"<p>{WebUtility.HtmlEncode(message.Body)}</p>" +
                $"<p>{WebUtility.HtmlEncode(message.EmailExtra)}</p>" +
                "<p>The Buoy Money team</p>";
            await _email.SendRawEmailAsync(email, message.Title, html, text);
        }
        catch (Exception ex)
        {
            // The in-app notification and push already went out; an email problem must not fail the event.
            _logger.LogWarning(ex, "Could not send the Cloud {Type} email to user {UserId}", message.NotificationType, userId);
        }
    }

    private static Message? AiAllowance(CloudEvent e)
    {
        if (e.Level is not (80 or 100))
        {
            return null;
        }
        var used = Dollars(e.UsedMicros);
        var allowance = Dollars(e.AllowanceMicros);
        var resets = e.PeriodEnd is { } end ? $" Your allowance resets on {end:MMMM d}." : string.Empty;
        if (e.Level == 80)
        {
            return new Message(
                "cloud_ai_allowance",
                "budget_warning",
                "You have used 80% of your AI allowance",
                $"Your server has used {used} of its {allowance} monthly AI allowance.{resets}",
                "You can see where it went in Settings, AI usage, on your server.",
                null,
                null);
        }

        var body = e.PaygEnabled == true
            ? $"Your server has used its {allowance} monthly AI allowance. Pay as you go is on, so AI keeps working up to the limit you set.{resets}"
            : $"Your server has used its {allowance} monthly AI allowance, so AI features are paused.{resets}";
        var extra = e.PaygEnabled == true
            ? "Extra use is added to your next bill. You can change the limit in Settings, AI usage."
            : "Your server's admin can turn on pay as you go in Settings, AI usage, on the server, or wait for the allowance to reset.";
        return new Message("cloud_ai_allowance", "budget_exceeded", "Your AI allowance is used up", body, extra, null, null);
    }

    private static Message? ServerPaired(CloudEvent e)
    {
        var name = Truncate(e.ServerName?.Trim(), 80);
        if (string.IsNullOrEmpty(name))
        {
            name = "A server";
        }
        else
        {
            name = $"The server \"{name}\"";
        }
        return e.ReplacedKeys == true
            ? new Message(
                "cloud_server_paired",
                "security_alert",
                "A server on your BuoyAI Cloud account was paired again",
                $"{name} was paired again with new keys. Bank data to it is paused for a day as a safety step.",
                "If this was not you, revoke the server in your Cloud settings now and change your password.",
                null,
                null)
            : new Message(
                "cloud_server_paired",
                "security_alert",
                "A new server joined your BuoyAI Cloud account",
                $"{name} was paired with your BuoyAI Cloud account. No bank data is sent to it until you confirm it.",
                "If this was not you, do not confirm it. Revoke it in your Cloud settings and change your password.",
                null,
                null);
    }

    private static Message BankAttention(CloudEvent e)
    {
        var body = e.Reason switch
        {
            "ITEM_LOGIN_REQUIRED" => "One of your Cloud bank connections needs you to sign in to your bank again.",
            "PENDING_EXPIRATION" => "One of your Cloud bank connections will expire soon and needs renewing.",
            "PENDING_DISCONNECT" => "One of your Cloud bank connections will be disconnected soon unless you reconnect it.",
            "NEW_ACCOUNTS_AVAILABLE" => "One of your Cloud bank connections has new accounts at the bank. Reconnect it to add them.",
            "USER_PERMISSION_REVOKED" => "Access to one of your Cloud bank connections was revoked at the bank. Connect the bank again to restore it.",
            _ => "One of your Cloud bank connections needs attention.",
        };
        return new Message(
            "plaid_update_required",
            "plaid_update_required",
            "A bank connection needs attention",
            body,
            "Open Accounts on your self-hosted server and reconnect the bank. New transactions do not come in until then.",
            null,
            null);
    }

    private static string Dollars(long? micros) =>
        ((micros ?? 0) / 1_000_000m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));

    private static string? Truncate(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

    private sealed record Message(
        string NotificationType,
        string EmailType,
        string Title,
        string Body,
        string EmailExtra,
        string? ActionUrl,
        string? ActionText);
}
