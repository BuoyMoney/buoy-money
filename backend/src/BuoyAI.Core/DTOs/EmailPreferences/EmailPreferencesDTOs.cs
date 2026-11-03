namespace BuoyAI.Core.DTOs.EmailPreferences;

/// <summary>
/// DTO for user email preferences (GET response)
/// </summary>
public class UserEmailPreferencesDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    
    // Account Activity
    public bool PlaidConnectionIssues { get; set; }
    public bool PlaidReconnectionSuccess { get; set; }
    public bool SubscriptionChanges { get; set; }
    public bool TrialExpirationWarnings { get; set; }
    
    // Financial Insights
    public bool BudgetAlerts { get; set; }
    public bool SavingsGoalMilestones { get; set; }
    public bool LargeTransactionAlerts { get; set; }
    public bool MonthlySummary { get; set; }
    
    // Marketing & Updates
    public bool FeatureAnnouncements { get; set; }
    public bool TipsAndBestPractices { get; set; }
    public bool ReferralInvites { get; set; }
    public bool ProductUpdates { get; set; }
    public bool PromotionalOffers { get; set; }
    
    // Achievements & Gamification
    public bool BadgeEarned { get; set; }
    public bool MilestoneReached { get; set; }
    public bool StreakNotifications { get; set; }
    
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Request to update email preferences
/// </summary>
public class UpdateEmailPreferencesRequest
{
    // Account Activity
    public bool? PlaidConnectionIssues { get; set; }
    public bool? PlaidReconnectionSuccess { get; set; }
    public bool? SubscriptionChanges { get; set; }
    public bool? TrialExpirationWarnings { get; set; }
    
    // Financial Insights
    public bool? BudgetAlerts { get; set; }
    public bool? SavingsGoalMilestones { get; set; }
    public bool? LargeTransactionAlerts { get; set; }
    public bool? MonthlySummary { get; set; }
    
    // Marketing & Updates
    public bool? FeatureAnnouncements { get; set; }
    public bool? TipsAndBestPractices { get; set; }
    public bool? ReferralInvites { get; set; }
    public bool? ProductUpdates { get; set; }
    public bool? PromotionalOffers { get; set; }
    
    // Achievements & Gamification
    public bool? BadgeEarned { get; set; }
    public bool? MilestoneReached { get; set; }
    public bool? StreakNotifications { get; set; }
}

/// <summary>
/// Email preference categories for organization
/// </summary>
public class EmailPreferenceCategoriesDto
{
    public SecurityEmailsDto Security { get; set; } = new();
    public AccountActivityDto AccountActivity { get; set; } = new();
    public FinancialInsightsDto FinancialInsights { get; set; } = new();
    public MarketingUpdatesDto MarketingUpdates { get; set; } = new();
    public AchievementsDto Achievements { get; set; } = new();
}

public class SecurityEmailsDto
{
    public string Description { get; set; } = "Security-critical emails that cannot be disabled";
    public List<EmailTypeInfoDto> EmailTypes { get; set; } = new()
    {
        new() { Name = "Password Reset", Description = "When you request a password reset", AlwaysEnabled = true },
        new() { Name = "MFA Codes", Description = "Two-factor authentication codes", AlwaysEnabled = true },
        new() { Name = "MFA Changes", Description = "When MFA methods are added or removed", AlwaysEnabled = true },
        new() { Name = "Security Alerts", Description = "Suspicious login attempts and account changes", AlwaysEnabled = true },
        new() { Name = "Welcome Email", Description = "Initial account creation confirmation", AlwaysEnabled = true }
    };
}

public class AccountActivityDto
{
    public bool PlaidConnectionIssues { get; set; }
    public bool PlaidReconnectionSuccess { get; set; }
    public bool SubscriptionChanges { get; set; }
    public bool TrialExpirationWarnings { get; set; }
}

public class FinancialInsightsDto
{
    public bool BudgetAlerts { get; set; }
    public bool SavingsGoalMilestones { get; set; }
    public bool LargeTransactionAlerts { get; set; }
    public bool MonthlySummary { get; set; }
}

public class MarketingUpdatesDto
{
    public bool FeatureAnnouncements { get; set; }
    public bool TipsAndBestPractices { get; set; }
    public bool ReferralInvites { get; set; }
    public bool ProductUpdates { get; set; }
    public bool PromotionalOffers { get; set; }
}

public class AchievementsDto
{
    public bool BadgeEarned { get; set; }
    public bool MilestoneReached { get; set; }
    public bool StreakNotifications { get; set; }
}

public class EmailTypeInfoDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool AlwaysEnabled { get; set; }
}
