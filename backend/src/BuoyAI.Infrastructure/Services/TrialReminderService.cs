using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The connect-your-bank sequence for trial users who have not connected an account. Two
/// emails, and both stop the moment a bank is connected:
///
///   1. <c>DaysAfterSignup</c> (default 1): "the 2-minute step that makes BuoyAI work", plus an
///      in-app/push copy pointing at Accounts.
///   2. <c>SecondDaysAfterFirst</c> (default 3) after that: a plain note from the founder
///      asking what stopped them, meant to be replied to.
///
/// This is the ACTIVATION sequence, distinct from the trial-ENDING email that Stripe's
/// <c>trial_will_end</c> webhook drives a few days before expiry: this one asks "have you
/// connected anything yet", the other asks "are you going to pay".
///
/// It replaced a single day-7 email that told people they had not started their trial yet
/// and linked to billing (trials are cardless and start at signup). Of 30 people who got it,
/// one connected afterward. Of those who connected at all, half did it within hours of
/// signing up, which is why step 1 now goes out on day 1.
///
/// Sweep rather than a per-user scheduled job, matching TrialReconciliationService. The same
/// population is re-evaluated every pass, so <c>User.TrialReminderSentAt</c> and
/// <c>User.TrialReminder2SentAt</c> are what keep it a sequence instead of a mailing loop
/// (migrations 0026 and 0043).
///
/// Off by default. It emails real users, so it stays dark until someone turns it on per
/// environment.
/// </summary>
public class TrialReminderService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    /// <summary>Cap per pass, so a first run against a large backlog cannot become a blast.</summary>
    private const int BatchLimit = 200;

    public const string ConnectNudgeNotificationType = "connect_bank_nudge";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TrialReminderService> _logger;

    public TrialReminderService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TrialReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Email:TrialReminder:Enabled", false))
        {
            _logger.LogInformation(
                "Connect reminder sweep disabled (Email:TrialReminder:Enabled=false). No connect-your-bank emails will be sent.");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connect reminder sweep failed; will retry next interval");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One pass: step 1 first, then step 2. Internal rather than private so the selection
    /// rules, which decide who receives real email, are directly testable without the host.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        // Days into the trial before step 1. Long enough that we are not emailing someone
        // mid-signup, short enough to catch them while the intent is still fresh.
        var afterDays = _configuration.GetValue("Email:TrialReminder:DaysAfterSignup", 1);

        // Days between step 1 and step 2.
        var secondAfterDays = _configuration.GetValue("Email:TrialReminder:SecondDaysAfterFirst", 3);

        // Runway the recipient must still have. Both emails ask for an action that is only
        // worth taking with trial left to use it; a long-dormant trial that expires tomorrow
        // belongs to the ENDING email instead.
        var minDaysRemaining = _configuration.GetValue("Email:TrialReminder:MinDaysRemaining", 7);

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var preferences = scope.ServiceProvider.GetRequiredService<IEmailPreferencesService>();
        var notifications = scope.ServiceProvider.GetService<INotificationService>();

        var now = DateTime.UtcNow;
        var endsAfter = now.AddDays(minDaysRemaining);

        // Shared rules: on an active trial with runway left, and not activated. The app is
        // built around connected accounts, so someone with none has not started; someone with
        // several has, and telling them to get started reads as a system that does not know
        // who they are.
        var unactivated = context.Users
            .Where(u => u.IsTrialActive)
            .Where(u => u.TrialEndDate != null && u.TrialEndDate > endsAfter)
            .Where(u => !context.Accounts.Any(a => a.UserId == u.Id));

        var startedBefore = now.AddDays(-afterDays);
        var stepOne = await unactivated
            .Where(u => u.TrialReminderSentAt == null)
            .Where(u => u.TrialStartDate != null && u.TrialStartDate <= startedBefore)
            .OrderBy(u => u.TrialStartDate)
            .Take(BatchLimit)
            .ToListAsync(ct);

        var firstSentBefore = now.AddDays(-secondAfterDays);
        var stepTwo = await unactivated
            .Where(u => u.TrialReminderSentAt != null && u.TrialReminderSentAt <= firstSentBefore)
            .Where(u => u.TrialReminder2SentAt == null)
            .OrderBy(u => u.TrialReminderSentAt)
            .Take(BatchLimit)
            .ToListAsync(ct);

        if (stepOne.Count == 0 && stepTwo.Count == 0)
        {
            return;
        }

        var sent = 0;
        foreach (var user in stepOne)
        {
            if (ct.IsCancellationRequested) break;
            if (await SendStepAsync(1, user, now, emailService, preferences))
            {
                sent++;
                await NotifyInAppAsync(notifications, user.Id);
            }
        }

        foreach (var user in stepTwo)
        {
            if (ct.IsCancellationRequested) break;
            if (await SendStepAsync(2, user, now, emailService, preferences))
            {
                sent++;
            }
        }

        // Single save for the batch. A crash before this point re-sends at most one batch,
        // which is the safer direction than marking-then-failing-to-send.
        await context.SaveChangesAsync(ct);

        if (sent > 0)
        {
            _logger.LogInformation(
                "Connect reminder sweep sent {Sent} email(s) ({StepOne} step 1 and {StepTwo} step 2 candidates)",
                sent, stepOne.Count, stepTwo.Count);
        }
    }

    /// <summary>
    /// Sends one step to one user and marks it. Returns true when an email actually went out.
    /// A failed send leaves the marker null so the next pass retries; one bad address must not
    /// stop the batch.
    /// </summary>
    private async Task<bool> SendStepAsync(
        int step, User user, DateTime now, IEmailService emailService, IEmailPreferencesService preferences)
    {
        try
        {
            // Respect the user's opt-out. Marked as sent either way: someone who opted out has
            // been "handled", and leaving the marker null would re-check them forever.
            if (!await preferences.ShouldSendEmailAsync(user.Id, "trial_expiration"))
            {
                Mark(step, user, now);
                return false;
            }

            var firstName = user.FirstName ?? user.Username ?? "there";
            var daysLeft = user.TrialEndDate.HasValue
                ? Math.Max(0, (int)Math.Ceiling((user.TrialEndDate.Value - now).TotalDays))
                : 0;

            await emailService.SendConnectReminderAsync(user.Email, firstName, step, daysLeft);

            Mark(step, user, now);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send connect reminder {Step} to user {UserId}", step, user.Id);
            return false;
        }
    }

    private static void Mark(int step, User user, DateTime now)
    {
        if (step == 1)
        {
            user.TrialReminderSentAt = now;
        }
        else
        {
            user.TrialReminder2SentAt = now;
        }
    }

    /// <summary>
    /// The in-app copy of step 1, which NotificationService also pushes to the phone when the
    /// user has allowed notifications. A bonus channel: failures are logged, never retried.
    /// </summary>
    private async Task NotifyInAppAsync(INotificationService? notifications, int userId)
    {
        if (notifications == null) return;
        try
        {
            await notifications.CreateNotificationAsync(userId, new CreateNotificationRequest
            {
                Type = ConnectNudgeNotificationType,
                Title = "Connect your bank to get started",
                Message = "It takes about two minutes. Buoy Money sorts your spending and helps you build a budget from it.",
                ActionUrl = "/accounts",
                ActionText = "Connect my bank",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connect reminder notification failed for user {UserId}", userId);
        }
    }
}
