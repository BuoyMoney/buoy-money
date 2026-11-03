using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Prompts established users to invite someone, by email and in-app notification.
///
/// **Targeting is the whole design.** Asking someone to recommend a product they have not
/// used is worse than silence: it advertises that we are not paying attention, and it burns
/// the one invite prompt they were ever going to read. So this only reaches people who have
/// actually connected an account and stuck around — the same activation signal the referral
/// reward itself pays on.
///
/// This is the mistake the trial reminder made first time round, where the sweep would have
/// mailed an activation nudge to someone with 108 transactions and to someone whose trial
/// expired the next day. Both were caught by asking who would actually receive it.
///
/// Sent once per user. There is no cadence and no follow-up: one ignored invite prompt is a
/// non-event, a recurring one is a reason to unsubscribe.
///
/// Off by default. It emails real people.
/// </summary>
public class ReferralInvitePromptService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    private const int BatchLimit = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReferralInvitePromptService> _logger;

    public ReferralInvitePromptService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ReferralInvitePromptService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Referral:InvitePrompt:Enabled", false))
        {
            _logger.LogInformation(
                "Referral invite prompt disabled (Referral:InvitePrompt:Enabled=false). No invite emails will be sent.");
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
                _logger.LogError(ex, "Referral invite prompt sweep failed; will retry next interval");
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
    /// One pass. Internal so the targeting rules — which decide who gets asked to shill for
    /// us — are directly testable.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        // How settled someone must be before we ask. Long enough that they have formed an
        // opinion worth passing on; asking during onboarding asks for a recommendation of
        // something they have not seen.
        var afterDays = _configuration.GetValue("Referral:InvitePrompt:DaysSinceSignup", 21);

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var referralService = scope.ServiceProvider.GetRequiredService<IReferralService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var now = DateTime.UtcNow;
        var joinedBefore = now.AddDays(-afterDays);

        var candidates = await context.Users
            .Where(u => u.ReferralInvitePromptedAt == null)
            .Where(u => !u.IsDeleted)
            .Where(u => u.CreatedAt <= joinedBefore)
            // Activated. Someone who never connected an account has no experience to
            // recommend, and asking them to invite a friend reads as a system that has not
            // noticed they never got started.
            .Where(u => context.Accounts.Any(a => a.UserId == u.Id))
            .OrderBy(u => u.CreatedAt)
            .Take(BatchLimit)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return;
        }

        var sent = 0;
        foreach (var user in candidates)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var summary = await referralService.GetSummaryAsync(user.Id);
                var firstName = user.FirstName ?? user.Username ?? "there";

                // The email checks the ReferralInvites preference itself and no-ops when the
                // user has opted out, so the marker below covers both cases: asked, or
                // deliberately not asked. Either way we do not revisit them.
                await emailService.SendReferralInviteAsync(
                    user.Email, firstName, summary.Code, summary.Link);

                // In-app too. Someone who never opens marketing email still sees this, and it
                // carries the code to somewhere they can copy it.
                await notifications.CreateNotificationAsync(user.Id, new CreateNotificationRequest
                {
                    Type = "referral_invite",
                    Title = "🤝 Invite a friend",
                    Message =
                        "When a friend joins with your code and connects an account, you both get a free month.",
                    ActionUrl = "/profile",
                    ActionText = "Get your code",
                });

                user.ReferralInvitePromptedAt = now;
                sent++;
            }
            catch (Exception ex)
            {
                // Unmarked, so it retries. One bad address must not stop the batch.
                _logger.LogError(ex, "Failed to send referral invite prompt to user {UserId}", user.Id);
            }
        }

        await context.SaveChangesAsync(ct);

        if (sent > 0)
        {
            _logger.LogInformation(
                "Referral invite prompt sweep reached {Sent} of {Candidates} candidate(s)", sent, candidates.Count);
        }
    }
}
