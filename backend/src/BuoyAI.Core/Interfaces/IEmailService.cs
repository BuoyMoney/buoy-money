using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Email;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for sending emails including 2FA codes
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends a fully-rendered email (HTML + plain text). Used by the admin broadcast
    /// pipeline (#552), which composes its own body. Bypasses per-type preference checks —
    /// the caller is responsible for honoring EmailPreferences.
    /// </summary>
    Task SendRawEmailAsync(string toEmail, string subject, string htmlContent, string textContent);

    /// <summary>
    /// Sends a two-factor authentication code via email
    /// </summary>
    Task SendTwoFactorCodeAsync(string email, string code, string purpose, string? ipAddress = null);
    
    /// <summary>
    /// Sends a welcome email to new users
    /// </summary>
    Task SendWelcomeEmailAsync(string email, string firstName);
    
    /// <summary>
    /// Sends a password reset email
    /// </summary>
    Task SendPasswordResetEmailAsync(string email, string resetLink);
    
    /// <summary>
    /// Sends MFA upgrade reminder email
    /// </summary>
    Task SendMfaUpgradeReminderAsync(string email, string firstName, int daysLeft);
    
    /// <summary>
    /// Sends notification that MFA grace period has ended
    /// </summary>
    Task SendMfaGracePeriodEndedAsync(string email, string firstName);
    
    // Account Activity Emails
    Task SendPlaidConnectionIssueAsync(string email, string firstName, string institutionName);
    Task SendPlaidReconnectionSuccessAsync(string email, string firstName, string institutionName);
    Task SendSubscriptionChangedAsync(string email, string firstName, string oldTier, string newTier);
    
    // Trial Emails (triggered by Stripe webhooks)
    Task SendTrialStartedAsync(string email, string firstName, int trialDays);
    Task SendTrialEndingAsync(string email, string firstName, int daysRemaining, string planName, decimal amount);
    Task SendTrialInvitationAsync(string email, string firstName, int trialDays);

    /// <summary>
    /// One of the two connect-your-bank reminders sent to trial users who have not connected
    /// an account: step 1 explains the two-minute step, step 2 is a personal note asking what
    /// stopped them. Failures propagate so the sweep leaves the user unmarked and retries.
    /// </summary>
    Task SendConnectReminderAsync(string email, string firstName, int step, int trialDaysLeft);
    
    // Financial Insights Emails
    Task SendBudgetAlertAsync(string email, string firstName, string categoryName, decimal spentAmount, decimal budgetAmount, decimal percentUsed);
    Task SendSavingsGoalMilestoneAsync(string email, string firstName, string goalName, decimal currentAmount, decimal targetAmount, decimal percentComplete);
    Task SendLargeTransactionAlertAsync(string email, string firstName, decimal amount, string merchant, string category);
    Task SendMonthlySummaryAsync(string email, string firstName, decimal totalIncome, decimal totalExpenses, decimal netSavings, string topCategory);
    
    // Marketing & Updates Emails
    Task SendFeatureAnnouncementAsync(string email, string firstName, string featureName, string description);
    Task SendTipsAndBestPracticesAsync(string email, string firstName, string tipTitle, string tipContent);

    /// <summary>
    /// Prompts the user to invite a friend, carrying their own referral code and link.
    /// Gated on the ReferralInvites preference.
    /// </summary>
    Task SendReferralInviteAsync(string email, string firstName, string referralCode, string referralLink);
    Task SendProductUpdateAsync(string email, string firstName, string version, List<string> updates);
    Task SendPromotionalOfferAsync(string email, string firstName, string offerTitle, string offerDescription, string promoCode, DateTime expiresAt);

    /// <summary>
    /// Confirms an extended free-trial offer to an email captured on the marketing site.
    /// Sent before any account exists, so there is no first name and no email-preferences
    /// row to consult: this IS the opt-in, and it is the only message we send unprompted.
    /// </summary>
    Task SendExtendedTrialOfferAsync(string email, int trialDays, int standardTrialDays, CalculatorSnapshotDto? calculator, string unsubscribeToken);

    /// <summary>
    /// Reply to a marketing capture from an address that cannot claim a trial (it already has
    /// an account, or already used its lifetime trial). Points them at sign-in rather than
    /// promising days we would refuse at registration. Still delivers any calculator numbers
    /// they asked us to save, because that part of the promise we can keep.
    /// </summary>
    Task SendExistingAccountOfferAsync(string email, CalculatorSnapshotDto? calculator, string unsubscribeToken);

    /// <summary>
    /// One reminder that a captured lead's reserved trial is still available. Sent once,
    /// ever, to someone who gave us their email and did not go on to register.
    /// </summary>
    Task SendExtendedTrialReminderAsync(string email, string unsubscribeToken);
    
    // Billing & Payment Emails
    Task SendPaymentFailedAsync(string email, string firstName, string planName, decimal amount, string lastFourDigits, int retryAttempt, DateTime? nextRetryDate);
    
    // Achievements & Gamification Emails
    Task SendBadgeEarnedAsync(string email, string firstName, string badgeName, string badgeDescription, string badgeIcon);
    Task SendMilestoneReachedAsync(string email, string firstName, string milestoneName, string description);
    Task SendStreakNotificationAsync(string email, string firstName, int streakDays, string streakType);
    
    // Security & Device Notifications
    Task SendNewDeviceLoginAsync(string email, string firstName, string deviceName, string location, string ipAddress, DateTime loginTime);
    Task SendSuspiciousLoginAttemptAsync(string email, string firstName, string location, string ipAddress, int riskScore, DateTime attemptTime);

    /// <summary>Tells the account owner their data was just downloaded (time and format only, no data). Never throws.</summary>
    Task SendDataExportedAsync(string email, string firstName, string format, DateTime exportedAt);

    // Family Account Emails
    Task SendFamilyInvitationAsync(string email, string inviterName, string familyName, string roleName, string invitationLink);
    Task SendFamilyInvitationAcceptedAsync(string email, string firstName, string memberName, string memberEmail, string roleName);
    Task SendFamilyMemberRemovedAsync(string email, string firstName, string familyName, string removedByName);
    Task SendFamilyRoleChangedAsync(string email, string firstName, string oldRole, string newRole, string changedByName);
    Task SendFamilyMemberLeftAsync(string email, string firstName, string memberName, string memberEmail);
    
    // Account Management Emails
    Task SendAccountDeletionConfirmationAsync(string email, string firstName);
}
