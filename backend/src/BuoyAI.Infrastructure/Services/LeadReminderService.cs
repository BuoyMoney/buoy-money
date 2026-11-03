using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Sends one reminder to a captured lead whose reserved trial is still unredeemed.
///
/// Capture sends a single confirmation and then nothing, ever. A visitor who handed over
/// their email and did not register that same day was never contacted again, so the whole
/// point of collecting the address was lost the moment they closed the tab. This closes
/// that gap and nothing more: one nudge, then silence.
///
/// Deliberately conservative about who qualifies. A lead is only reminded when it is
/// unredeemed, still subscribed, was actually sent its original confirmation, and has had
/// time to act on it. Reminding someone who never received the first email would be
/// referring back to a message they never saw.
/// </summary>
public class LeadReminderService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    /// <summary>Cap per pass, so a first run against a backlog cannot become a blast.</summary>
    private const int BatchLimit = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LeadReminderService> _logger;

    public LeadReminderService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<LeadReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ON by default, deliberately. This is the follow-up a visitor implicitly asked
        // for when they handed over their email, and the only reason to collect the
        // address at all. Defaulting it off is how this codebase ended up with a folder of
        // fully written lifecycle emails that have never sent a single message: the flag
        // nobody remembers to flip is indistinguishable from a feature that was never
        // built. Set Email:LeadReminder:Enabled=false to stop it.
        if (!_configuration.GetValue("Email:LeadReminder:Enabled", true))
        {
            _logger.LogInformation(
                "Lead reminder sweep disabled (Email:LeadReminder:Enabled=false). No reminders will be sent.");
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a bad pass kill the loop; the next one retries.
                _logger.LogError(ex, "Lead reminder sweep failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var afterDays = _configuration.GetValue("Email:LeadReminder:DaysAfterCapture", 3);
        var cutoff = DateTime.UtcNow.AddDays(-afterDays);

        var due = await context.MarketingLeads
            .Where(l => l.ReminderSentAt == null
                        && l.RedeemedAt == null
                        && l.UnsubscribedAt == null
                        // Zero-day rows are recorded contacts (existing account or spent
                        // trial) that registration will never honour. The reminder promises
                        // a reserved trial, so it must never go to them.
                        && l.OfferedTrialDays > 0
                        // Only remind someone who actually got the first email. Referring
                        // back to a message they never received reads as a cold pitch.
                        && l.ConfirmationSentAt != null
                        && l.CreatedAt <= cutoff)
            .OrderBy(l => l.CreatedAt)
            .Take(BatchLimit)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        _logger.LogInformation("Lead reminder sweep: {Count} lead(s) due", due.Count);

        foreach (var lead in due)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await emailService.SendExtendedTrialReminderAsync(
                    lead.Email, lead.UnsubscribeToken);

                // Recorded only after a successful send, so a failure leaves the row for
                // the next pass rather than marking a reminder that never went out.
                lead.ReminderSentAt = DateTime.UtcNow;
                lead.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // One bad address must not stop the rest of the batch.
                _logger.LogError(ex, "Failed to send trial reminder for lead {LeadId}", lead.Id);
            }
        }
    }
}
