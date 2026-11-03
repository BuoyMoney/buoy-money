using BuoyAI.Core.DTOs.EmailPreferences;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

public class EmailPreferencesService : IEmailPreferencesService
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<EmailPreferencesService> _logger;
    
    // Map email types to their category and preference field
    private static readonly Dictionary<string, (EmailCategory Category, string? PreferenceField)> EmailTypeMapping = new()
    {
        // ===== SECURITY - ALWAYS SENT (Cannot be disabled) =====
        ["password_reset"] = (EmailCategory.Security, null),
        ["mfa_code"] = (EmailCategory.Security, null),
        ["mfa_setup"] = (EmailCategory.Security, null),
        ["mfa_change"] = (EmailCategory.Security, null),
        ["security_alert"] = (EmailCategory.Security, null),
        ["welcome"] = (EmailCategory.Security, null),
        ["account_deletion"] = (EmailCategory.Security, null),
        
        // ===== PLAID CONNECTION (Configurable) =====
        ["plaid_update_required"] = (EmailCategory.PlaidConnection, nameof(UserEmailPreferences.PlaidConnectionIssues)),
        ["plaid_expiration_warning"] = (EmailCategory.PlaidConnection, nameof(UserEmailPreferences.PlaidConnectionIssues)),
        ["plaid_pending_disconnect"] = (EmailCategory.PlaidConnection, nameof(UserEmailPreferences.PlaidConnectionIssues)),
        ["plaid_reconnected"] = (EmailCategory.PlaidConnection, nameof(UserEmailPreferences.PlaidReconnectionSuccess)),
        ["plaid_connection_restored"] = (EmailCategory.PlaidConnection, nameof(UserEmailPreferences.PlaidReconnectionSuccess)),
        
        // ===== SUBSCRIPTION (Configurable) =====
        ["subscription_change"] = (EmailCategory.Subscription, nameof(UserEmailPreferences.SubscriptionChanges)),
        ["subscription_created"] = (EmailCategory.Subscription, nameof(UserEmailPreferences.SubscriptionChanges)),
        ["subscription_cancelled"] = (EmailCategory.Subscription, nameof(UserEmailPreferences.SubscriptionChanges)),
        ["payment_failure"] = (EmailCategory.Subscription, nameof(UserEmailPreferences.SubscriptionChanges)),
        ["trial_expiration"] = (EmailCategory.Subscription, nameof(UserEmailPreferences.TrialExpirationWarnings)),
        ["trial_ending_soon"] = (EmailCategory.Subscription, nameof(UserEmailPreferences.TrialExpirationWarnings)),
        
        // ===== BUDGET (Configurable) =====
        ["budget_alert"] = (EmailCategory.Budget, nameof(UserEmailPreferences.BudgetAlerts)),
        ["budget_exceeded"] = (EmailCategory.Budget, nameof(UserEmailPreferences.BudgetAlerts)),
        ["budget_warning"] = (EmailCategory.Budget, nameof(UserEmailPreferences.BudgetAlerts)),
        
        // ===== SAVINGS (Configurable) =====
        ["savings_goal_reached"] = (EmailCategory.Savings, nameof(UserEmailPreferences.SavingsGoalMilestones)),
        ["savings_milestone"] = (EmailCategory.Savings, nameof(UserEmailPreferences.SavingsGoalMilestones)),
        ["savings_progress"] = (EmailCategory.Savings, nameof(UserEmailPreferences.SavingsGoalMilestones)),
        
        // ===== TRANSACTION ALERTS (Configurable) =====
        ["large_transaction"] = (EmailCategory.Transaction, nameof(UserEmailPreferences.LargeTransactionAlerts)),
        ["unusual_spending"] = (EmailCategory.Transaction, nameof(UserEmailPreferences.LargeTransactionAlerts)),
        
        // ===== REPORTS (Configurable) =====
        ["monthly_summary"] = (EmailCategory.Report, nameof(UserEmailPreferences.MonthlySummary)),
        ["financial_summary"] = (EmailCategory.Report, nameof(UserEmailPreferences.MonthlySummary)),
        
        // ===== MARKETING (Configurable) =====
        ["feature_announcement"] = (EmailCategory.Marketing, nameof(UserEmailPreferences.FeatureAnnouncements)),
        ["new_feature"] = (EmailCategory.Marketing, nameof(UserEmailPreferences.FeatureAnnouncements)),
        ["tips"] = (EmailCategory.Marketing, nameof(UserEmailPreferences.TipsAndBestPractices)),
        ["best_practices"] = (EmailCategory.Marketing, nameof(UserEmailPreferences.TipsAndBestPractices)),
        ["referral_invite"] = (EmailCategory.Marketing, nameof(UserEmailPreferences.ReferralInvites)),
        ["product_update"] = (EmailCategory.Marketing, nameof(UserEmailPreferences.ProductUpdates)),
        ["promotional"] = (EmailCategory.Marketing, nameof(UserEmailPreferences.PromotionalOffers)),
        ["special_offer"] = (EmailCategory.Marketing, nameof(UserEmailPreferences.PromotionalOffers)),
        
        // ===== ACHIEVEMENTS (Configurable) =====
        ["badge_earned"] = (EmailCategory.Achievement, nameof(UserEmailPreferences.BadgeEarned)),
        ["achievement_unlocked"] = (EmailCategory.Achievement, nameof(UserEmailPreferences.BadgeEarned)),
        ["milestone"] = (EmailCategory.Achievement, nameof(UserEmailPreferences.MilestoneReached)),
        ["streak"] = (EmailCategory.Achievement, nameof(UserEmailPreferences.StreakNotifications))
    };
    
    public EmailPreferencesService(
        BuoyAIDbContext context,
        ILogger<EmailPreferencesService> logger)
    {
        _context = context;
        _logger = logger;
    }
    
    public async Task<UserEmailPreferencesDto?> GetUserEmailPreferencesAsync(int userId)
    {
        try
        {
            var preferences = await _context.UserEmailPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId);
            
            if (preferences == null)
            {
                _logger.LogInformation("No email preferences found for user {UserId}, creating defaults", userId);
                return await CreateDefaultPreferencesAsync(userId);
            }
            
            return MapToDto(preferences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting email preferences for user {UserId}", userId);
            throw;
        }
    }
    
    public async Task<UserEmailPreferencesDto> GetPreferencesAsync(string email)
    {
        try
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == email);
            
            if (user == null)
            {
                // For non-registered users, return all preferences as true
                return GetDefaultDto();
            }
            
            var preferences = await _context.UserEmailPreferences
                .FirstOrDefaultAsync(p => p.UserId == user.Id);
            
            if (preferences == null)
            {
                _logger.LogInformation("No email preferences found for user {Email}, returning defaults", email);
                return GetDefaultDto();
            }
            
            return MapToDto(preferences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting email preferences for email {Email}", email);
            // Return defaults on error to avoid blocking emails
            return GetDefaultDto();
        }
    }
    
    public async Task<UserEmailPreferencesDto> UpdateEmailPreferencesAsync(int userId, UpdateEmailPreferencesRequest request)
    {
        try
        {
            var preferences = await _context.UserEmailPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId);
            
            if (preferences == null)
            {
                _logger.LogInformation("No preferences found for user {UserId}, creating new", userId);
                preferences = new UserEmailPreferences { UserId = userId };
                _context.UserEmailPreferences.Add(preferences);
            }
            
            // Update only the fields that were provided (not null)
            if (request.PlaidConnectionIssues.HasValue)
                preferences.PlaidConnectionIssues = request.PlaidConnectionIssues.Value;
            if (request.PlaidReconnectionSuccess.HasValue)
                preferences.PlaidReconnectionSuccess = request.PlaidReconnectionSuccess.Value;
            if (request.SubscriptionChanges.HasValue)
                preferences.SubscriptionChanges = request.SubscriptionChanges.Value;
            if (request.TrialExpirationWarnings.HasValue)
                preferences.TrialExpirationWarnings = request.TrialExpirationWarnings.Value;
            
            if (request.BudgetAlerts.HasValue)
                preferences.BudgetAlerts = request.BudgetAlerts.Value;
            if (request.SavingsGoalMilestones.HasValue)
                preferences.SavingsGoalMilestones = request.SavingsGoalMilestones.Value;
            if (request.LargeTransactionAlerts.HasValue)
                preferences.LargeTransactionAlerts = request.LargeTransactionAlerts.Value;
            if (request.MonthlySummary.HasValue)
                preferences.MonthlySummary = request.MonthlySummary.Value;
            
            if (request.FeatureAnnouncements.HasValue)
                preferences.FeatureAnnouncements = request.FeatureAnnouncements.Value;
            if (request.TipsAndBestPractices.HasValue)
                preferences.TipsAndBestPractices = request.TipsAndBestPractices.Value;
            if (request.ReferralInvites.HasValue)
                preferences.ReferralInvites = request.ReferralInvites.Value;
            if (request.ProductUpdates.HasValue)
                preferences.ProductUpdates = request.ProductUpdates.Value;
            if (request.PromotionalOffers.HasValue)
                preferences.PromotionalOffers = request.PromotionalOffers.Value;
            
            if (request.BadgeEarned.HasValue)
                preferences.BadgeEarned = request.BadgeEarned.Value;
            if (request.MilestoneReached.HasValue)
                preferences.MilestoneReached = request.MilestoneReached.Value;
            if (request.StreakNotifications.HasValue)
                preferences.StreakNotifications = request.StreakNotifications.Value;
            
            preferences.UpdatedAt = DateTime.UtcNow;
            
            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Updated email preferences for user {UserId}", userId);
            
            return MapToDto(preferences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating email preferences for user {UserId}", userId);
            throw;
        }
    }
    
    public async Task<UserEmailPreferencesDto> CreateDefaultPreferencesAsync(int userId)
    {
        try
        {
            var preferences = new UserEmailPreferences
            {
                UserId = userId,
                // Defaults are set in the entity class
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            
            _context.UserEmailPreferences.Add(preferences);
            await _context.SaveChangesAsync();
            
            _logger.LogInformation("Created default email preferences for user {UserId}", userId);
            
            return MapToDto(preferences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating default email preferences for user {UserId}", userId);
            throw;
        }
    }
    
    public async Task<bool> ShouldSendEmailAsync(int userId, string emailType)
    {
        try
        {
            // Security emails always send
            if (IsSecurityEmail(emailType))
            {
                _logger.LogDebug("Email type {EmailType} is security-critical, always sending", emailType);
                return true;
            }
            
            // Check if we have a mapping for this email type
            if (!EmailTypeMapping.TryGetValue(emailType.ToLower(), out var mapping))
            {
                _logger.LogWarning("Unknown email type {EmailType}, defaulting to send", emailType);
                return true; // Default to sending if unknown type
            }
            
            // If no preference field (shouldn't happen for non-security), send it
            if (string.IsNullOrEmpty(mapping.PreferenceField))
            {
                return true;
            }
            
            // Get user preferences
            var preferences = await _context.UserEmailPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId);
            
            if (preferences == null)
            {
                _logger.LogInformation("No preferences found for user {UserId}, creating defaults and sending email", userId);
                await CreateDefaultPreferencesAsync(userId);
                return true; // Default values are mostly true
            }
            
            // Use reflection to get the preference value
            var property = typeof(UserEmailPreferences).GetProperty(mapping.PreferenceField);
            if (property == null)
            {
                _logger.LogError("Could not find property {PropertyName} on UserEmailPreferences", mapping.PreferenceField);
                return true;
            }
            
            var value = property.GetValue(preferences);
            var shouldSend = value as bool? ?? true;
            
            _logger.LogDebug("Email type {EmailType} for user {UserId}: shouldSend={ShouldSend}", 
                emailType, userId, shouldSend);
            
            return shouldSend;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking email preferences for user {UserId}, emailType {EmailType}", 
                userId, emailType);
            // On error, default to sending (better to send than not send)
            return true;
        }
    }
    
    public bool IsSecurityEmail(string emailType)
    {
        if (!EmailTypeMapping.TryGetValue(emailType.ToLower(), out var mapping))
        {
            return false;
        }
        
        return mapping.Category == EmailCategory.Security;
    }
    
    public async Task<EmailPreferenceCategoriesDto> GetEmailCategoriesAsync(int userId)
    {
        var preferences = await GetUserEmailPreferencesAsync(userId);
        
        if (preferences == null)
        {
            preferences = await CreateDefaultPreferencesAsync(userId);
        }
        
        return new EmailPreferenceCategoriesDto
        {
            Security = new SecurityEmailsDto(),
            AccountActivity = new AccountActivityDto
            {
                PlaidConnectionIssues = preferences.PlaidConnectionIssues,
                PlaidReconnectionSuccess = preferences.PlaidReconnectionSuccess,
                SubscriptionChanges = preferences.SubscriptionChanges,
                TrialExpirationWarnings = preferences.TrialExpirationWarnings
            },
            FinancialInsights = new FinancialInsightsDto
            {
                BudgetAlerts = preferences.BudgetAlerts,
                SavingsGoalMilestones = preferences.SavingsGoalMilestones,
                LargeTransactionAlerts = preferences.LargeTransactionAlerts,
                MonthlySummary = preferences.MonthlySummary
            },
            MarketingUpdates = new MarketingUpdatesDto
            {
                FeatureAnnouncements = preferences.FeatureAnnouncements,
                TipsAndBestPractices = preferences.TipsAndBestPractices,
                ReferralInvites = preferences.ReferralInvites,
                ProductUpdates = preferences.ProductUpdates,
                PromotionalOffers = preferences.PromotionalOffers
            },
            Achievements = new AchievementsDto
            {
                BadgeEarned = preferences.BadgeEarned,
                MilestoneReached = preferences.MilestoneReached,
                StreakNotifications = preferences.StreakNotifications
            }
        };
    }
    
    private static UserEmailPreferencesDto MapToDto(UserEmailPreferences preferences)
    {
        return new UserEmailPreferencesDto
        {
            Id = preferences.Id,
            UserId = preferences.UserId,
            PlaidConnectionIssues = preferences.PlaidConnectionIssues,
            PlaidReconnectionSuccess = preferences.PlaidReconnectionSuccess,
            SubscriptionChanges = preferences.SubscriptionChanges,
            TrialExpirationWarnings = preferences.TrialExpirationWarnings,
            BudgetAlerts = preferences.BudgetAlerts,
            SavingsGoalMilestones = preferences.SavingsGoalMilestones,
            LargeTransactionAlerts = preferences.LargeTransactionAlerts,
            MonthlySummary = preferences.MonthlySummary,
            FeatureAnnouncements = preferences.FeatureAnnouncements,
            TipsAndBestPractices = preferences.TipsAndBestPractices,
            ReferralInvites = preferences.ReferralInvites,
            ProductUpdates = preferences.ProductUpdates,
            PromotionalOffers = preferences.PromotionalOffers,
            BadgeEarned = preferences.BadgeEarned,
            MilestoneReached = preferences.MilestoneReached,
            StreakNotifications = preferences.StreakNotifications,
            UpdatedAt = preferences.UpdatedAt
        };
    }
    
    private static UserEmailPreferencesDto GetDefaultDto()
    {
        return new UserEmailPreferencesDto
        {
            Id = 0,
            UserId = 0,
            PlaidConnectionIssues = true,
            PlaidReconnectionSuccess = true,
            SubscriptionChanges = true,
            TrialExpirationWarnings = true,
            BudgetAlerts = true,
            SavingsGoalMilestones = true,
            LargeTransactionAlerts = true,
            MonthlySummary = true,
            FeatureAnnouncements = true,
            TipsAndBestPractices = true,
            ReferralInvites = true,
            ProductUpdates = true,
            PromotionalOffers = true,
            BadgeEarned = true,
            MilestoneReached = true,
            StreakNotifications = true,
            UpdatedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Email categories for classification
/// </summary>
internal enum EmailCategory
{
    Security,           // Always sent
    PlaidConnection,    // Configurable
    Subscription,       // Configurable
    Budget,            // Configurable
    Savings,           // Configurable
    Transaction,       // Configurable
    Report,            // Configurable
    Marketing,         // Configurable
    Achievement        // Configurable
}
