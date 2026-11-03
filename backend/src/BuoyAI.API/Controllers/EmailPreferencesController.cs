using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.EmailPreferences;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Manages user email notification preferences
/// </summary>
[ApiController]
[Route("api/email-preferences")]
[Authorize]
public class EmailPreferencesController : BaseController
{
    private readonly IEmailPreferencesService _emailPreferencesService;
    private readonly ILogger<EmailPreferencesController> _logger;

    public EmailPreferencesController(
        IEmailPreferencesService emailPreferencesService,
        ILogger<EmailPreferencesController> logger)
    {
        _emailPreferencesService = emailPreferencesService;
        _logger = logger;
    }

    /// <summary>
    /// Get current user's email preferences
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(UserEmailPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEmailPreferences()
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("Getting email preferences for user {UserId}", userId);

        var preferences = await _emailPreferencesService.GetUserEmailPreferencesAsync(userId);

        if (preferences == null)
        {
            return NotFound(new { message = "Email preferences not found" });
        }

        return Ok(preferences);
    }

    /// <summary>
    /// Update user's email preferences
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(UserEmailPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult>UpdateEmailPreferences([FromBody] UpdateEmailPreferencesRequest request)
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("Updating email preferences for user {UserId}", userId);

        var preferences = await _emailPreferencesService.UpdateEmailPreferencesAsync(userId, request);

        return Ok(new
        {
            message = "Email preferences updated successfully",
            preferences
        });
    }

    /// <summary>
    /// Get all email preference categories with current settings
    /// Useful for displaying preference UI with descriptions
    /// </summary>
    [HttpGet("categories")]
    [ProducesResponseType(typeof(EmailPreferenceCategoriesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEmailCategories()
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("Getting email preference categories for user {UserId}", userId);

        var categories = await _emailPreferencesService.GetEmailCategoriesAsync(userId);

        return Ok(categories);
    }

    /// <summary>
    /// Check if a specific email type would be sent for the current user
    /// Useful for testing preferences
    /// </summary>
    [HttpGet("check/{emailType}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckEmailType(string emailType)
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("Checking if email type {EmailType} would send for user {UserId}", emailType, userId);

        var shouldSend = await _emailPreferencesService.ShouldSendEmailAsync(userId, emailType);
        var isSecurityEmail = _emailPreferencesService.IsSecurityEmail(emailType);

        return Ok(new
        {
            emailType,
            shouldSend,
            isSecurityEmail,
            message = isSecurityEmail
                ? "This is a security-critical email that always sends"
                : shouldSend
                    ? "This email would be sent based on your preferences"
                    : "This email is currently disabled in your preferences"
        });
    }

    /// <summary>
    /// Reset email preferences to defaults
    /// </summary>
    [HttpPost("reset")]
    [ProducesResponseType(typeof(UserEmailPreferencesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetToDefaults()
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("Resetting email preferences to defaults for user {UserId}", userId);

        // Update with all default values
        var request = new UpdateEmailPreferencesRequest
        {
            // Account Activity - defaults to true
            PlaidConnectionIssues = true,
            PlaidReconnectionSuccess = true,
            SubscriptionChanges = true,
            TrialExpirationWarnings = true,

            // Financial Insights - defaults to true except opt-ins
            BudgetAlerts = true,
            SavingsGoalMilestones = true,
            LargeTransactionAlerts = false, // Opt-in
            MonthlySummary = false, // Opt-in

            // Marketing - all opt-in (default false)
            FeatureAnnouncements = false,
            TipsAndBestPractices = false,
            ProductUpdates = false,
            PromotionalOffers = false,

            // Achievements - defaults to true except streaks
            BadgeEarned = true,
            MilestoneReached = true,
            StreakNotifications = false
        };

        var preferences = await _emailPreferencesService.UpdateEmailPreferencesAsync(userId, request);

        return Ok(new
        {
            message = "Email preferences reset to defaults",
            preferences
        });
    }
}
