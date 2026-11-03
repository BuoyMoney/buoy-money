using BuoyAI.Core.DTOs.EmailPreferences;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for managing user email notification preferences
/// </summary>
public interface IEmailPreferencesService
{
    /// <summary>
    /// Get user's email preferences
    /// </summary>
    Task<UserEmailPreferencesDto?> GetUserEmailPreferencesAsync(int userId);
    
    /// <summary>
    /// Get user's email preferences by email address
    /// </summary>
    Task<UserEmailPreferencesDto> GetPreferencesAsync(string email);
    
    /// <summary>
    /// Update user's email preferences
    /// </summary>
    Task<UserEmailPreferencesDto> UpdateEmailPreferencesAsync(int userId, UpdateEmailPreferencesRequest request);
    
    /// <summary>
    /// Create default email preferences for a new user
    /// Called automatically during user registration
    /// </summary>
    Task<UserEmailPreferencesDto> CreateDefaultPreferencesAsync(int userId);
    
    /// <summary>
    /// Check if an email should be sent based on user preferences
    /// Security-critical emails always return true
    /// </summary>
    /// <param name="userId">User ID to check preferences for</param>
    /// <param name="emailType">Type of email (e.g., "plaid_update_required", "budget_alert", "password_reset")</param>
    /// <returns>True if email should be sent, false otherwise</returns>
    Task<bool> ShouldSendEmailAsync(int userId, string emailType);
    
    /// <summary>
    /// Check if an email type is security-critical (always sent)
    /// </summary>
    bool IsSecurityEmail(string emailType);
    
    /// <summary>
    /// Get all email preference categories with descriptions
    /// Useful for frontend UI display
    /// </summary>
    Task<EmailPreferenceCategoriesDto> GetEmailCategoriesAsync(int userId);
}
