using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Email;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Resend implementation of the email service.
/// Uses template classes in BuoyAI.Infrastructure.Email for consistent branding:
/// - SecurityEmailTemplates: 2FA, password reset, login alerts
/// - AccountEmailTemplates: Welcome, verification, account management
/// - BillingEmailTemplates: Subscription, payment, trial emails
/// - FinancialAlertEmailTemplates: Budget alerts, savings milestones, transaction alerts
/// - FamilyEmailTemplates: Family invitations, role changes
/// - GamificationEmailTemplates: Badges, milestones, streaks
/// - MarketingEmailTemplates: Announcements, tips, promotions
/// </summary>
public class ResendEmailService : IEmailService
{
    private readonly IEmailTransport _transport;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResendEmailService> _logger;
    private readonly IEmailPreferencesService _emailPreferencesService;
    private readonly BuoyAiMetrics _metrics;
    private readonly string _fromEmail;
    private readonly string _fromName;
    private readonly string _replyToEmail;

    /// <summary>
    /// Host every link in an email points at. Staging and production set this to themselves,
    /// so a message sent from staging keeps the reader on staging. Hardcoding the production
    /// host here (as this file used to) sends staging testers to a site that knows nothing
    /// about their token.
    /// </summary>
    private readonly string _baseUrl;

    public ResendEmailService(
        IEmailTransport transport,
        IConfiguration configuration,
        ILogger<ResendEmailService> logger,
        IEmailPreferencesService emailPreferencesService,
        BuoyAiMetrics metrics)
    {
        _transport = transport;
        _configuration = configuration;
        _logger = logger;
        _emailPreferencesService = emailPreferencesService;
        _metrics = metrics;
        _fromEmail = configuration["Resend:FromEmail"] ?? "noreply@buoy-ai.com";
        _fromName = configuration["Resend:FromName"] ?? Brand.Name;
        _replyToEmail = configuration["Resend:ReplyToEmail"] ?? "support@buoy-ai.com";
        _baseUrl = (configuration["App:BaseUrl"] ?? "https://buoy-ai.com").TrimEnd('/');
    }

    // ==================== SECURITY EMAILS ====================
    // Security emails are always sent - they cannot be disabled by user preferences

    public async Task SendTwoFactorCodeAsync(string email, string code, string purpose, string? ipAddress = null)
    {
        try
        {
            var subject = GetSubjectForPurpose(purpose);
            var (htmlContent, textContent) = SecurityEmailTemplates.TwoFactorCode(code, purpose, ipAddress);

            await SendEmailAsync(email, subject, htmlContent, textContent, template: "two_factor");

            _logger.LogInformation("2FA email sent successfully to {Email} for purpose: {Purpose}", email, purpose);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending 2FA email to {Email}", email);
            throw;
        }
    }

    public async Task SendPasswordResetEmailAsync(string email, string resetLink)
    {
        try
        {
            var subject = "Reset Your Buoy Money Password";
            var (htmlContent, textContent) = SecurityEmailTemplates.PasswordReset(resetLink);

            await SendEmailAsync(email, subject, htmlContent, textContent, template: "password_reset");

            _logger.LogInformation("Password reset email sent successfully to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending password reset email to {Email}", email);
            throw;
        }
    }

    public async Task SendMfaUpgradeReminderAsync(string email, string firstName, int daysRemaining)
    {
        try
        {
            var subject = $"Action Required: Upgrade Your Security ({daysRemaining} days remaining)";
            var (htmlContent, textContent) = SecurityEmailTemplates.MfaUpgrade(firstName, daysRemaining);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("MFA upgrade reminder sent successfully to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending MFA upgrade reminder to {Email}", email);
        }
    }

    public async Task SendMfaGracePeriodEndedAsync(string email, string firstName)
    {
        try
        {
            var subject = "Security Upgrade Required - Access Restricted";
            var (htmlContent, textContent) = SecurityEmailTemplates.MfaGracePeriodEnded(firstName);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("MFA grace period ended notification sent successfully to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending MFA grace period ended notification to {Email}", email);
        }
    }

    public async Task SendNewDeviceLoginAsync(string email, string firstName, string deviceName, string location, string ipAddress, DateTime loginTime)
    {
        // Security alerts always sent - cannot be disabled
        try
        {
            var subject = "New Device Sign-In to Your Buoy Money Account";
            var (htmlContent, textContent) = SecurityEmailTemplates.NewDeviceLogin(firstName, deviceName, location, ipAddress, loginTime);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("New device login email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending new device login email to {Email}", email);
        }
    }

    public async Task SendSuspiciousLoginAttemptAsync(string email, string firstName, string location, string ipAddress, int riskScore, DateTime attemptTime)
    {
        // Security alerts always sent - cannot be disabled
        try
        {
            var subject = "⚠️ Suspicious Login Attempt Detected";
            var (htmlContent, textContent) = SecurityEmailTemplates.SuspiciousLogin(firstName, location, ipAddress, riskScore, attemptTime);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Suspicious login alert sent to {Email}, risk score: {RiskScore}", email, riskScore);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending suspicious login alert to {Email}", email);
        }
    }

    public async Task SendDataExportedAsync(string email, string firstName, string format, DateTime exportedAt)
    {
        // Security notice, always sent. Must never fail or slow an export: self-host installs may
        // have no email provider configured, so every failure is logged and swallowed.
        try
        {
            var subject = "Your Buoy Money data was downloaded";
            var (htmlContent, textContent) = SecurityEmailTemplates.DataExported(firstName, format, exportedAt);

            await SendEmailAsync(email, subject, htmlContent, textContent, "data_exported");

            _logger.LogInformation("Data export notice sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send the data export notice to {Email}", email);
        }
    }

    // ==================== ACCOUNT EMAILS ====================

    public async Task SendWelcomeEmailAsync(string email, string firstName)
    {
        // Welcome email always sent - critical onboarding
        try
        {
            var trialDays = _configuration.GetValue<int>("Subscription:TrialPeriodDays", 14);
            var subject = "Welcome to Buoy Money! 🚀";
            // Only sent when no trial was granted (AuthService.SendFirstEmailAsync); a trial
            // signup gets the trial invitation instead. This used to claim a trial every time.
            var (htmlContent, textContent) = AccountEmailTemplates.Welcome(
                firstName, hasTrial: false, trialDays: trialDays, accountsUrl: $"{_baseUrl}/app/accounts");

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Welcome email sent successfully to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending welcome email to {Email}", email);
            // Don't throw for welcome emails - they're not critical
        }
    }

    // ==================== PLAID CONNECTION EMAILS ====================

    public async Task SendPlaidConnectionIssueAsync(string email, string firstName, string institutionName)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.PlaidConnectionIssues) return;

        try
        {
            var subject = $"Action Required: {institutionName} Connection Issue";
            var (htmlContent, textContent) = FinancialAlertEmailTemplates.PlaidConnectionAlert(
                firstName, 
                $"{institutionName} Account", 
                institutionName, 
                "disconnected", 
                null);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Plaid connection issue email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Plaid connection issue email to {Email}", email);
        }
    }

    public async Task SendPlaidReconnectionSuccessAsync(string email, string firstName, string institutionName)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.PlaidConnectionIssues) return;

        try
        {
            var subject = $"✅ {institutionName} Reconnected Successfully";
            var (htmlContent, textContent) = FinancialAlertEmailTemplates.PlaidConnectionAlert(
                firstName, 
                $"{institutionName} Account", 
                institutionName, 
                "reconnected", 
                null);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Plaid reconnection success email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Plaid reconnection success email to {Email}", email);
        }
    }

    // ==================== BILLING EMAILS ====================

    public async Task SendSubscriptionChangedAsync(string email, string firstName, string oldTier, string newTier)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.SubscriptionChanges) return;

        try
        {
            var changeType = GetSubscriptionChangeType(oldTier, newTier);
            var subject = $"Subscription Updated: {oldTier} → {newTier}";
            var (price, billingPeriod) = GetPlanPricing(newTier);
            var (htmlContent, textContent) = BillingEmailTemplates.SubscriptionChanged(
                firstName, 
                newTier, 
                changeType, 
                price, 
                billingPeriod, 
                DateTime.UtcNow.AddMonths(1));

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Subscription changed email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending subscription changed email to {Email}", email);
        }
    }

    public async Task SendTrialStartedAsync(string email, string firstName, int trialDays)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.TrialExpirationWarnings) return;

        try
        {
            var subject = $"Welcome to Buoy Money! Your {trialDays}-Day Free Trial Has Started";
            var (htmlContent, textContent) = BillingEmailTemplates.TrialStarted(firstName, trialDays);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Trial started email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending trial started email to {Email}", email);
        }
    }

    public async Task SendTrialEndingAsync(string email, string firstName, int daysRemaining, string planName, decimal amount)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.TrialExpirationWarnings) return;

        try
        {
            var subject = daysRemaining <= 1 
                ? "Your Trial Ends Tomorrow - Card Will Be Charged"
                : $"Your Trial Ends in {daysRemaining} Days";
            var (htmlContent, textContent) = BillingEmailTemplates.TrialEnding(firstName, daysRemaining, planName, amount);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Trial ending email sent to {Email}, {DaysRemaining} days remaining", email, daysRemaining);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending trial ending email to {Email}", email);
        }
    }

    public async Task SendTrialInvitationAsync(string email, string firstName, int trialDays)
    {
        try
        {
            // The trial has already started by the time this sends; the old subject ("Start
            // your free trial!") told people there was still something to do to get it.
            var subject = $"Welcome to Buoy Money, {firstName}. Your free trial is on";
            var (htmlContent, textContent) = BillingEmailTemplates.TrialInvitation(
                firstName, trialDays, $"{_baseUrl}/app/accounts");

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Trial invitation email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending trial invitation email to {Email}", email);
        }
    }

    public async Task SendConnectReminderAsync(string email, string firstName, int step, int trialDaysLeft)
    {
        // No try/catch: failures propagate so the sweep leaves the user unmarked and retries.
        // The method this replaced swallowed errors, so a failed send was recorded as sent.
        var subject = ActivationEmailTemplates.ConnectReminderSubject(step, firstName);
        var (htmlContent, textContent) = ActivationEmailTemplates.ConnectReminder(
            step, firstName, trialDaysLeft,
            $"{_baseUrl}/app/accounts", $"{_baseUrl}/app/settings/email-preferences");

        // Signed by the founder, so it comes from him by name. Replies go to the usual
        // reply-to inbox, which he reads.
        var fromName = _configuration["Email:TrialReminder:FromName"] ?? $"Jonathan at {Brand.Name}";

        await SendEmailAsync(email, subject, htmlContent, textContent,
            template: $"connect_reminder_{step}", fromName: fromName);

        _logger.LogInformation("Connect reminder {Step} sent", step);
    }

    public async Task SendPaymentFailedAsync(string email, string firstName, string planName, decimal amount, string lastFourDigits, int retryAttempt, DateTime? nextRetryDate)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.SubscriptionChanges) return; // Reuse subscription preference for payment notifications

        try
        {
            var subject = retryAttempt >= 3 
                ? "⚠️ Urgent: Payment Failed - Action Required"
                : "Payment Failed - Please Update Payment Method";
            var (htmlContent, textContent) = BillingEmailTemplates.PaymentFailed(
                firstName, planName, amount, lastFourDigits, retryAttempt, nextRetryDate);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Payment failed email sent to {Email}, attempt {Attempt}", email, retryAttempt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending payment failed email to {Email}", email);
        }
    }

    // ==================== FINANCIAL ALERTS ====================

    public async Task SendBudgetAlertAsync(string email, string firstName, string categoryName, decimal spentAmount, decimal budgetAmount, decimal percentUsed)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.BudgetAlerts) return;

        try
        {
            var alertType = percentUsed >= 100 ? "exceeded" : "warning";
            var subject = $"Budget Alert: {categoryName} at {percentUsed:F0}%";
            var (htmlContent, textContent) = FinancialAlertEmailTemplates.BudgetAlert(
                firstName, 
                categoryName, 
                spentAmount, 
                budgetAmount, 
                (int)percentUsed, 
                alertType);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Budget alert email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending budget alert email to {Email}", email);
        }
    }

    public async Task SendSavingsGoalMilestoneAsync(string email, string firstName, string goalName, decimal currentAmount, decimal targetAmount, decimal percentComplete)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.SavingsGoalMilestones) return;

        try
        {
            var milestoneType = percentComplete >= 100 ? "complete" : percentComplete >= 50 ? "halfway" : "started";
            var subject = $"🎉 {percentComplete:F0}% of Your {goalName} Goal!";
            var (htmlContent, textContent) = FinancialAlertEmailTemplates.SavingsGoalMilestone(
                firstName, 
                goalName, 
                currentAmount, 
                targetAmount, 
                (int)percentComplete, 
                milestoneType);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Savings goal milestone email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending savings goal milestone email to {Email}", email);
        }
    }

    public async Task SendLargeTransactionAlertAsync(string email, string firstName, decimal amount, string merchant, string category)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.LargeTransactionAlerts) return;

        try
        {
            var subject = $"Large Transaction: ${amount:N2} at {merchant}";
            var (htmlContent, textContent) = FinancialAlertEmailTemplates.LargeTransactionAlert(
                firstName, 
                merchant, 
                amount, 
                "Primary Account", 
                DateTime.UtcNow, 
                category);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Large transaction alert email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending large transaction alert email to {Email}", email);
        }
    }

    public async Task SendMonthlySummaryAsync(string email, string firstName, decimal totalIncome, decimal totalExpenses, decimal netSavings, string topCategory)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.MonthlySummary) return;

        try
        {
            var monthName = DateTime.Now.AddMonths(-1).ToString("MMMM yyyy");
            var subject = $"Monthly Summary: {monthName}";
            var (htmlContent, textContent) = FinancialAlertEmailTemplates.MonthlySummary(
                firstName, 
                monthName, 
                totalIncome, 
                totalExpenses, 
                netSavings, 
                topCategory, 
                totalExpenses * 0.3m, // Approximate top category amount
                3, // Budgets on track
                4); // Total budgets

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Monthly summary email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending monthly summary email to {Email}", email);
        }
    }

    // ==================== MARKETING EMAILS ====================

    public async Task SendFeatureAnnouncementAsync(string email, string firstName, string featureName, string description)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.FeatureAnnouncements) return;

        try
        {
            var subject = $"New Feature: {featureName}";
            var (htmlContent, textContent) = MarketingEmailTemplates.FeatureAnnouncement(
                firstName, 
                featureName, 
                description, 
                "Try It Now", 
                $"{_baseUrl}/app/dashboard",
                null);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Feature announcement email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending feature announcement email to {Email}", email);
        }
    }

    public async Task SendReferralInviteAsync(string email, string firstName, string referralCode, string referralLink)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.ReferralInvites) return;

        try
        {
            var (htmlContent, textContent) =
                MarketingEmailTemplates.ReferralInvite(firstName, referralCode, referralLink);

            await SendRawEmailAsync(email, "Know someone who'd like Buoy Money?", htmlContent, textContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending referral invite email to {Email}", email);
        }
    }

    public async Task SendTipsAndBestPracticesAsync(string email, string firstName, string tipTitle, string tipContent)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.TipsAndBestPractices) return;

        try
        {
            var subject = $"Financial Tip: {tipTitle}";
            var (htmlContent, textContent) = MarketingEmailTemplates.TipsAndBestPractices(
                firstName, 
                tipTitle, 
                tipContent, 
                null,  // tipImageUrl
                null); // additionalTips

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Tips and best practices email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending tips and best practices email to {Email}", email);
        }
    }

    public async Task SendProductUpdateAsync(string email, string firstName, string version, List<string> updates)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.ProductUpdates) return;

        try
        {
            var subject = $"Product Update: Version {version}";
            var (htmlContent, textContent) = MarketingEmailTemplates.ProductUpdate(
                firstName, 
                $"What's New in v{version}",
                updates, 
                new List<string>(), // Bug fixes
                null); // releaseNotesUrl

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Product update email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending product update email to {Email}", email);
        }
    }

    public async Task SendPromotionalOfferAsync(string email, string firstName, string offerTitle, string offerDescription, string promoCode, DateTime expiresAt)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.PromotionalOffers) return;

        try
        {
            var subject = $"Special Offer: {offerTitle}";
            var (htmlContent, textContent) = MarketingEmailTemplates.PromotionalOffer(
                firstName, 
                offerTitle, 
                offerDescription, 
                promoCode, 
                20, // discountPercent
                expiresAt, 
                $"{_baseUrl}/app/upgrade");

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Promotional offer email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending promotional offer email to {Email}", email);
        }
    }

    public async Task SendExtendedTrialOfferAsync(
        string email, int trialDays, int standardTrialDays, CalculatorSnapshotDto? calculator,
        string unsubscribeToken)
    {
        // No preference lookup: there is no account yet, and this message IS the thing the
        // visitor just asked for. It is transactional, not a broadcast.
        //
        // Unlike the other marketing sends, failures propagate. The caller records
        // confirmation_sent_at only when this returns, so swallowing here would mark an
        // email delivered that never left the building and make it unretryable.
        var lines = ToCalculatorLines(calculator);

        var subject = "Your free month of Buoy Money is reserved";
        var (htmlContent, textContent) = MarketingEmailTemplates.ExtendedTrialOffer(
            trialDays, standardTrialDays, $"{_baseUrl}/app/register", lines,
            MarketingEmailTemplates.UnsubscribeUrl(_baseUrl, unsubscribeToken));

        await SendEmailAsync(email, subject, htmlContent, textContent);

        _logger.LogInformation("Extended-trial offer email sent ({Days} days)", trialDays);
    }

    public async Task SendExtendedTrialReminderAsync(
        string email, string unsubscribeToken)
    {
        // Failures propagate, matching the offer email: the sweep records reminder_sent_at
        // only when this returns, so swallowing here would mark a reminder delivered that
        // never left the building and make the row unretryable.
        var subject = "Your longer free trial is still waiting";
        var (htmlContent, textContent) = MarketingEmailTemplates.ExtendedTrialReminder(
            $"{_baseUrl}/app/register",
            MarketingEmailTemplates.UnsubscribeUrl(_baseUrl, unsubscribeToken));

        await SendEmailAsync(email, subject, htmlContent, textContent);

        _logger.LogInformation("Extended-trial reminder sent");
    }

    public async Task SendExistingAccountOfferAsync(
        string email, CalculatorSnapshotDto? calculator, string unsubscribeToken)
    {
        // Same contract as the offer email: no preference lookup (this is a direct reply to
        // something they just did) and failures propagate so the row stays retryable.
        var lines = ToCalculatorLines(calculator);

        var (htmlContent, textContent) = MarketingEmailTemplates.ExistingAccountOffer(
            $"{_baseUrl}/app/login", lines,
            MarketingEmailTemplates.UnsubscribeUrl(_baseUrl, unsubscribeToken));

        await SendEmailAsync(email, "You already have a Buoy Money account", htmlContent, textContent);

        _logger.LogInformation("Existing-account reply sent to a captured lead");
    }

    /// <summary>Formats a calculator snapshot for the lead emails. Null in, null out.</summary>
    private static MarketingEmailTemplates.CalculatorLines? ToCalculatorLines(CalculatorSnapshotDto? c) =>
        c == null
            ? null
            : new MarketingEmailTemplates.CalculatorLines(
                MonthlyBudget: c.MonthlyBudget.ToString("C0"),
                DailyAllowance: c.DailyAllowance.ToString("C2"),
                Banked: c.Banked.ToString("C0"),
                Yearly: c.Yearly.ToString("C0"),
                SkipsPerWeek: c.SkipsPerWeek);

    // ==================== GAMIFICATION EMAILS ====================

    public async Task SendBadgeEarnedAsync(string email, string firstName, string badgeName, string badgeDescription, string badgeIcon)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.BadgeEarned) return;

        try
        {
            var subject = $"🏆 Badge Earned: {badgeName}";
            var (htmlContent, textContent) = GamificationEmailTemplates.BadgeEarned(
                firstName, 
                badgeName, 
                badgeDescription, 
                badgeIcon, 
                5); // totalBadges

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Badge earned email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending badge earned email to {Email}", email);
        }
    }

    public async Task SendMilestoneReachedAsync(string email, string firstName, string milestoneName, string description)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.MilestoneReached) return;

        try
        {
            var subject = $"🎯 Milestone: {milestoneName}";
            var (htmlContent, textContent) = GamificationEmailTemplates.MilestoneReached(
                firstName, 
                milestoneName, 
                description, 
                1, // milestoneValue
                "achievement"); // milestoneUnit

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Milestone reached email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending milestone reached email to {Email}", email);
        }
    }

    public async Task SendStreakNotificationAsync(string email, string firstName, int streakDays, string streakType)
    {
        var prefs = await _emailPreferencesService.GetPreferencesAsync(email);
        if (!prefs.StreakNotifications) return;

        try
        {
            var subject = $"🔥 {streakDays}-Day Streak!";
            var (htmlContent, textContent) = GamificationEmailTemplates.StreakNotification(
                firstName, 
                streakDays, 
                streakType, 
                isAtRisk: false);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Streak notification email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending streak notification email to {Email}", email);
        }
    }

    // ==================== FAMILY EMAILS ====================

    public async Task SendFamilyInvitationAsync(string email, string inviterName, string familyName, string roleName, string invitationLink)
    {
        // Family invitations always sent - critical action
        try
        {
            var subject = $"You're invited to join {familyName} on Buoy Money!";
            var (htmlContent, textContent) = FamilyEmailTemplates.FamilyInvitation(
                inviterName, 
                familyName, 
                email, 
                invitationLink, 
                roleName);

            await SendEmailAsync(email, subject, htmlContent, textContent, template: "family_invitation");

            _logger.LogInformation("Family invitation email sent to {Email} for family {FamilyName}", email, familyName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending family invitation email to {Email}", email);
            throw;
        }
    }

    public async Task SendFamilyInvitationAcceptedAsync(string email, string firstName, string memberName, string memberEmail, string roleName)
    {
        try
        {
            var subject = $"{memberName} has joined your family!";
            var (htmlContent, textContent) = FamilyEmailTemplates.FamilyInvitationAccepted(
                firstName, 
                memberName, 
                "Your Family", // familyName
                roleName);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Family invitation accepted email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending family invitation accepted email to {Email}", email);
        }
    }

    public async Task SendFamilyMemberRemovedAsync(string email, string firstName, string familyName, string removedByName)
    {
        try
        {
            var subject = $"You've been removed from {familyName}";
            var (htmlContent, textContent) = FamilyEmailTemplates.FamilyMemberRemoved(
                firstName, 
                familyName, 
                removedByName);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Family member removed email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending family member removed email to {Email}", email);
        }
    }

    public async Task SendFamilyRoleChangedAsync(string email, string firstName, string oldRole, string newRole, string changedByName)
    {
        try
        {
            var isUpgrade = IsRoleUpgrade(oldRole, newRole);
            var subject = isUpgrade 
                ? $"Your family role has been upgraded to {newRole}" 
                : $"Your family role has been changed to {newRole}";
            var (htmlContent, textContent) = FamilyEmailTemplates.FamilyRoleChanged(
                firstName, 
                "Your Family", // familyName 
                oldRole, 
                newRole, 
                changedByName);

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Family role changed email sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending family role changed email to {Email}", email);
        }
    }

    public async Task SendFamilyMemberLeftAsync(string email, string firstName, string memberName, string memberEmail)
    {
        try
        {
            var subject = $"{memberName} has left your family";
            var (htmlContent, textContent) = FamilyEmailTemplates.FamilyMemberLeft(
                firstName, 
                memberName, 
                "Your Family"); // familyName

            await SendEmailAsync(email, subject, htmlContent, textContent);

            _logger.LogInformation("Family member left notification sent to {Email}", email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending family member left email to {Email}", email);
        }
    }

    // ==================== HELPER METHODS ====================

    /// <inheritdoc />
    public Task SendRawEmailAsync(string toEmail, string subject, string htmlContent, string textContent)
        => SendEmailAsync(toEmail, subject, htmlContent, textContent, template: "raw");

    private async Task SendEmailAsync(string email, string subject, string htmlContent, string textContent, string template = "generic", string? fromName = null)
    {
        // Templates are written with the default product name; a server with its own Brand:Name gets that name instead.
        var message = new OutboundEmail(
            Brand.Apply(fromName ?? _fromName), _fromEmail, email, _replyToEmail, Brand.Apply(subject), Brand.Apply(htmlContent), Brand.Apply(textContent));

        // Instrument the single delivery choke point so a degraded/expired Resend key (which
        // silently blackholes 2FA codes, password-reset links, and family invites) is visible
        // and alertable instead of only surfacing as support tickets days later.
        try
        {
            await _transport.SendAsync(message);
            _metrics.RecordEmailSent(template, "success");
        }
        catch (Exception ex)
        {
            _metrics.RecordEmailSent(template, "failure", ex.GetType().Name);
            throw;
        }
    }

    private static string GetSubjectForPurpose(string purpose)
    {
        return purpose.ToLowerInvariant() switch
        {
            "login" => "Your Buoy Money Login Code",
            "password-reset" => "Your Buoy Money Password Reset Code",
            "account-verification" => "Verify Your Buoy Money Account",
            "sensitive-action" => "Security Verification Required",
            _ => "Your Buoy Money Verification Code"
        };
    }

    private static string GetSubscriptionChangeType(string oldTier, string newTier)
    {
        var tierHierarchy = new Dictionary<string, int>
        {
            { "Free", 0 },
            { "Trial", 1 },
            { "Basic", 2 },
            { "Premium", 3 },
            { "Family", 4 }
        };

        var oldLevel = tierHierarchy.GetValueOrDefault(oldTier, 0);
        var newLevel = tierHierarchy.GetValueOrDefault(newTier, 0);

        if (newLevel > oldLevel) return "upgrade";
        if (newLevel < oldLevel) return "downgrade";
        return "change";
    }

    private static (decimal price, string period) GetPlanPricing(string tier)
    {
        return tier switch
        {
            "Premium" => (9.99m, "month"),
            "Family" => (14.99m, "month"),
            "Basic" => (4.99m, "month"),
            _ => (0m, "month")
        };
    }

    private static bool IsRoleUpgrade(string oldRole, string newRole)
    {
        var roleHierarchy = new Dictionary<string, int>
        {
            { "Read Only", 1 },
            { "ReadOnly", 1 },
            { "Member", 2 },
            { "Budget Admin", 3 },
            { "BudgetAdmin", 3 },
            { "Owner", 4 }
        };

        var oldLevel = roleHierarchy.GetValueOrDefault(oldRole, 0);
        var newLevel = roleHierarchy.GetValueOrDefault(newRole, 0);

        return newLevel > oldLevel;
    }

    #region Account Management Emails

    public async Task SendAccountDeletionConfirmationAsync(string email, string firstName)
    {
        var (html, text) = AccountEmailTemplates.AccountDeleted(firstName);
        await SendEmailAsync(email, "Your Buoy Money Account Has Been Deleted", html, text);
    }

    #endregion
}
