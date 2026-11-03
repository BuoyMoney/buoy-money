using BuoyAI.Core.DTOs.EmailPreferences;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class EmailPreferencesServiceTests : IDisposable
{
    private readonly BuoyAIDbContext _context;
    private readonly Mock<ILogger<EmailPreferencesService>> _mockLogger;
    private readonly EmailPreferencesService _service;

    public EmailPreferencesServiceTests()
    {
        // Use in-memory database for testing
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new BuoyAIDbContext(options);
        _mockLogger = new Mock<ILogger<EmailPreferencesService>>();
        _service = new EmailPreferencesService(_context, _mockLogger.Object);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GetUserEmailPreferencesAsync

    [Fact]
    public async Task GetUserEmailPreferencesAsync_ExistingPreferences_ReturnsDto()
    {
        // Arrange
        var userId = 1;
        var preferences = new UserEmailPreferences
        {
            UserId = userId,
            PlaidConnectionIssues = false,
            BudgetAlerts = true,
            FeatureAnnouncements = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetUserEmailPreferencesAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
        result.PlaidConnectionIssues.Should().BeFalse();
        result.BudgetAlerts.Should().BeTrue();
        result.FeatureAnnouncements.Should().BeFalse();
    }

    [Fact]
    public async Task GetUserEmailPreferencesAsync_NoPreferences_CreatesDefaults()
    {
        // Arrange
        var userId = 1;

        // Act
        var result = await _service.GetUserEmailPreferencesAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
        // Essential notifications should be true by default
        result.PlaidConnectionIssues.Should().BeTrue();
        result.BudgetAlerts.Should().BeTrue();
        // Product news and the user's own-data report are opt-OUT (default on) — see the
        // reasoning on each property in UserEmailPreferences.
        result.FeatureAnnouncements.Should().BeTrue();
        result.MonthlySummary.Should().BeTrue();
        result.TipsAndBestPractices.Should().BeTrue();
        result.ProductUpdates.Should().BeTrue();
        // Advertising stays opt-IN. GDPR/PECR require prior consent for direct marketing,
        // so this one must not follow the others.
        result.PromotionalOffers.Should().BeFalse();
        // Unrelated to marketing: a noisy per-transaction alert stays opt-in.
        result.LargeTransactionAlerts.Should().BeFalse();

        // Verify preferences were saved to database
        var savedPreferences = await _context.UserEmailPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        savedPreferences.Should().NotBeNull();
    }

    #endregion

    #region GetPreferencesAsync

    [Fact]
    public async Task GetPreferencesAsync_ExistingUser_ReturnsPreferences()
    {
        // Arrange
        var email = "test@example.com";
        var user = new User { Email = email, Username = "testuser" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var preferences = new UserEmailPreferences
        {
            UserId = user.Id,
            PlaidConnectionIssues = false,
            MonthlySummary = true
        };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPreferencesAsync(email);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(user.Id);
        result.PlaidConnectionIssues.Should().BeFalse();
        result.MonthlySummary.Should().BeTrue();
    }

    [Fact]
    public async Task GetPreferencesAsync_NonExistentUser_ReturnsDefaults()
    {
        // Arrange
        var email = "nonexistent@example.com";

        // Act
        var result = await _service.GetPreferencesAsync(email);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(0);
        // Should return all defaults as true
        result.PlaidConnectionIssues.Should().BeTrue();
        result.BudgetAlerts.Should().BeTrue();
    }

    [Fact]
    public async Task GetPreferencesAsync_UserWithoutPreferences_ReturnsDefaults()
    {
        // Arrange
        var email = "test@example.com";
        var user = new User { Email = email, Username = "testuser" };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPreferencesAsync(email);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(0); // Returns default DTO with 0
        result.PlaidConnectionIssues.Should().BeTrue();
    }

    #endregion

    #region UpdateEmailPreferencesAsync

    [Fact]
    public async Task UpdateEmailPreferencesAsync_ExistingPreferences_UpdatesFields()
    {
        // Arrange
        var userId = 1;
        var preferences = new UserEmailPreferences
        {
            UserId = userId,
            PlaidConnectionIssues = true,
            BudgetAlerts = true,
            FeatureAnnouncements = true
        };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        var request = new UpdateEmailPreferencesRequest
        {
            PlaidConnectionIssues = false,
            BudgetAlerts = false
            // FeatureAnnouncements not provided, should remain unchanged
        };

        // Act
        var result = await _service.UpdateEmailPreferencesAsync(userId, request);

        // Assert
        result.Should().NotBeNull();
        result.PlaidConnectionIssues.Should().BeFalse();
        result.BudgetAlerts.Should().BeFalse();
        result.FeatureAnnouncements.Should().BeTrue(); // Unchanged
    }

    [Fact]
    public async Task UpdateEmailPreferencesAsync_NoExistingPreferences_CreatesNew()
    {
        // Arrange
        var userId = 1;
        var request = new UpdateEmailPreferencesRequest
        {
            PlaidConnectionIssues = false,
            BudgetAlerts = true
        };

        // Act
        var result = await _service.UpdateEmailPreferencesAsync(userId, request);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(userId);
        result.PlaidConnectionIssues.Should().BeFalse();
        result.BudgetAlerts.Should().BeTrue();

        // Verify saved to database
        var savedPreferences = await _context.UserEmailPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        savedPreferences.Should().NotBeNull();
        savedPreferences!.PlaidConnectionIssues.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateEmailPreferencesAsync_AllFields_UpdatesAll()
    {
        // Arrange
        var userId = 1;
        var request = new UpdateEmailPreferencesRequest
        {
            PlaidConnectionIssues = false,
            PlaidReconnectionSuccess = false,
            SubscriptionChanges = false,
            TrialExpirationWarnings = false,
            BudgetAlerts = false,
            SavingsGoalMilestones = false,
            LargeTransactionAlerts = false,
            MonthlySummary = false,
            FeatureAnnouncements = false,
            TipsAndBestPractices = false,
            ProductUpdates = false,
            PromotionalOffers = false,
            BadgeEarned = false,
            MilestoneReached = false,
            StreakNotifications = false
        };

        // Act
        var result = await _service.UpdateEmailPreferencesAsync(userId, request);

        // Assert
        result.Should().NotBeNull();
        result.PlaidConnectionIssues.Should().BeFalse();
        result.PlaidReconnectionSuccess.Should().BeFalse();
        result.SubscriptionChanges.Should().BeFalse();
        result.TrialExpirationWarnings.Should().BeFalse();
        result.BudgetAlerts.Should().BeFalse();
        result.SavingsGoalMilestones.Should().BeFalse();
        result.LargeTransactionAlerts.Should().BeFalse();
        result.MonthlySummary.Should().BeFalse();
        result.FeatureAnnouncements.Should().BeFalse();
        result.TipsAndBestPractices.Should().BeFalse();
        result.ProductUpdates.Should().BeFalse();
        result.PromotionalOffers.Should().BeFalse();
        result.BadgeEarned.Should().BeFalse();
        result.MilestoneReached.Should().BeFalse();
        result.StreakNotifications.Should().BeFalse();
    }

    #endregion

    #region CreateDefaultPreferencesAsync

    [Fact]
    public async Task CreateDefaultPreferencesAsync_CreatesWithDefaults()
    {
        // Arrange
        var userId = 1;

        // Act
        var result = await _service.CreateDefaultPreferencesAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(userId);
        // Essential notifications should be true by default
        result.PlaidConnectionIssues.Should().BeTrue();
        result.BudgetAlerts.Should().BeTrue();
        // Product news is opt-OUT (default on); advertising stays opt-IN.
        result.FeatureAnnouncements.Should().BeTrue();
        result.PromotionalOffers.Should().BeFalse();
        result.LargeTransactionAlerts.Should().BeFalse();

        // Verify saved to database
        var savedPreferences = await _context.UserEmailPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        savedPreferences.Should().NotBeNull();
    }

    #endregion

    #region ShouldSendEmailAsync

    [Fact]
    public async Task ShouldSendEmailAsync_SecurityEmail_AlwaysReturnsTrue()
    {
        // Arrange
        var userId = 1;
        var preferences = new UserEmailPreferences
        {
            UserId = userId,
            PlaidConnectionIssues = false // All preferences disabled
        };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        // Act - Test various security email types
        var passwordReset = await _service.ShouldSendEmailAsync(userId, "password_reset");
        var mfaCode = await _service.ShouldSendEmailAsync(userId, "mfa_code");
        var securityAlert = await _service.ShouldSendEmailAsync(userId, "security_alert");

        // Assert
        passwordReset.Should().BeTrue();
        mfaCode.Should().BeTrue();
        securityAlert.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldSendEmailAsync_PreferenceEnabled_ReturnsTrue()
    {
        // Arrange
        var userId = 1;
        var preferences = new UserEmailPreferences
        {
            UserId = userId,
            PlaidConnectionIssues = true,
            BudgetAlerts = true
        };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        // Act
        var plaidUpdate = await _service.ShouldSendEmailAsync(userId, "plaid_update_required");
        var budgetAlert = await _service.ShouldSendEmailAsync(userId, "budget_alert");

        // Assert
        plaidUpdate.Should().BeTrue();
        budgetAlert.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldSendEmailAsync_PreferenceDisabled_ReturnsFalse()
    {
        // Arrange
        var userId = 1;
        var preferences = new UserEmailPreferences
        {
            UserId = userId,
            PlaidConnectionIssues = false,
            BudgetAlerts = false
        };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        // Act
        var plaidUpdate = await _service.ShouldSendEmailAsync(userId, "plaid_update_required");
        var budgetAlert = await _service.ShouldSendEmailAsync(userId, "budget_alert");

        // Assert
        plaidUpdate.Should().BeFalse();
        budgetAlert.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldSendEmailAsync_NoPreferences_CreatesDefaultsAndReturnsTrue()
    {
        // Arrange
        var userId = 1;

        // Act
        var result = await _service.ShouldSendEmailAsync(userId, "budget_alert");

        // Assert
        result.Should().BeTrue();

        // Verify preferences were created
        var savedPreferences = await _context.UserEmailPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        savedPreferences.Should().NotBeNull();
    }

    [Fact]
    public async Task ShouldSendEmailAsync_UnknownEmailType_ReturnsTrue()
    {
        // Arrange
        var userId = 1;
        var preferences = new UserEmailPreferences { UserId = userId };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ShouldSendEmailAsync(userId, "unknown_email_type");

        // Assert - Should default to sending
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldSendEmailAsync_CaseInsensitive_Works()
    {
        // Arrange
        var userId = 1;
        var preferences = new UserEmailPreferences
        {
            UserId = userId,
            BudgetAlerts = true
        };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        // Act - Test with different casing
        var lowercase = await _service.ShouldSendEmailAsync(userId, "budget_alert");
        var uppercase = await _service.ShouldSendEmailAsync(userId, "BUDGET_ALERT");
        var mixedcase = await _service.ShouldSendEmailAsync(userId, "Budget_Alert");

        // Assert - All should work
        lowercase.Should().BeTrue();
        uppercase.Should().BeTrue();
        mixedcase.Should().BeTrue();
    }

    #endregion

    #region IsSecurityEmail

    [Fact]
    public void IsSecurityEmail_SecurityTypes_ReturnsTrue()
    {
        // Act & Assert
        _service.IsSecurityEmail("password_reset").Should().BeTrue();
        _service.IsSecurityEmail("mfa_code").Should().BeTrue();
        _service.IsSecurityEmail("security_alert").Should().BeTrue();
        _service.IsSecurityEmail("welcome").Should().BeTrue();
        _service.IsSecurityEmail("account_deletion").Should().BeTrue();
    }

    [Fact]
    public void IsSecurityEmail_NonSecurityTypes_ReturnsFalse()
    {
        // Act & Assert
        _service.IsSecurityEmail("budget_alert").Should().BeFalse();
        _service.IsSecurityEmail("plaid_update_required").Should().BeFalse();
        _service.IsSecurityEmail("promotional").Should().BeFalse();
    }

    [Fact]
    public void IsSecurityEmail_UnknownType_ReturnsFalse()
    {
        // Act
        var result = _service.IsSecurityEmail("unknown_type");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsSecurityEmail_CaseInsensitive_Works()
    {
        // Act & Assert
        _service.IsSecurityEmail("PASSWORD_RESET").Should().BeTrue();
        _service.IsSecurityEmail("Password_Reset").Should().BeTrue();
        _service.IsSecurityEmail("password_reset").Should().BeTrue();
    }

    #endregion

    #region GetEmailCategoriesAsync

    [Fact]
    public async Task GetEmailCategoriesAsync_ExistingPreferences_ReturnsCategories()
    {
        // Arrange
        var userId = 1;
        var preferences = new UserEmailPreferences
        {
            UserId = userId,
            PlaidConnectionIssues = false,
            BudgetAlerts = true,
            FeatureAnnouncements = false,
            BadgeEarned = true
        };
        _context.UserEmailPreferences.Add(preferences);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetEmailCategoriesAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result.Security.Should().NotBeNull();
        
        result.AccountActivity.Should().NotBeNull();
        result.AccountActivity.PlaidConnectionIssues.Should().BeFalse();
        
        result.FinancialInsights.Should().NotBeNull();
        result.FinancialInsights.BudgetAlerts.Should().BeTrue();
        
        result.MarketingUpdates.Should().NotBeNull();
        result.MarketingUpdates.FeatureAnnouncements.Should().BeFalse();
        
        result.Achievements.Should().NotBeNull();
        result.Achievements.BadgeEarned.Should().BeTrue();
    }

    [Fact]
    public async Task GetEmailCategoriesAsync_NoPreferences_CreatesDefaultsAndReturnsCategories()
    {
        // Arrange
        var userId = 1;

        // Act
        var result = await _service.GetEmailCategoriesAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result.Security.Should().NotBeNull();
        result.AccountActivity.Should().NotBeNull();
        result.FinancialInsights.Should().NotBeNull();
        result.MarketingUpdates.Should().NotBeNull();
        result.Achievements.Should().NotBeNull();

        // Verify preferences were created
        var savedPreferences = await _context.UserEmailPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        savedPreferences.Should().NotBeNull();
    }

    #endregion
}
