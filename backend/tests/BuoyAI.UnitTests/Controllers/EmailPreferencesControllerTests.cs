using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs.EmailPreferences;
using System.Security.Claims;

namespace BuoyAI.UnitTests.Controllers;

public class EmailPreferencesControllerTests
{
    private readonly Mock<IEmailPreferencesService> _mockEmailPreferencesService;
    private readonly Mock<ILogger<EmailPreferencesController>> _mockLogger;
    private readonly EmailPreferencesController _controller;

    public EmailPreferencesControllerTests()
    {
        _mockEmailPreferencesService = new Mock<IEmailPreferencesService>();
        _mockLogger = new Mock<ILogger<EmailPreferencesController>>();

        _controller = new EmailPreferencesController(
            _mockEmailPreferencesService.Object,
            _mockLogger.Object);

        // Setup authenticated user context
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "123")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = claimsPrincipal
            }
        };
    }

    #region GetEmailPreferences Tests

    [Fact]
    public async Task GetEmailPreferences_PreferencesExist_ReturnsOkWithPreferences()
    {
        // Arrange
        var preferences = new UserEmailPreferencesDto
        {
            Id = 1,
            UserId = 123,
            PlaidConnectionIssues = true,
            BudgetAlerts = true,
            FeatureAnnouncements = false,
            UpdatedAt = DateTime.UtcNow
        };

        _mockEmailPreferencesService
            .Setup(s => s.GetUserEmailPreferencesAsync(123))
            .ReturnsAsync(preferences);

        // Act
        var result = await _controller.GetEmailPreferences();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedPreferences = okResult.Value.Should().BeAssignableTo<UserEmailPreferencesDto>().Subject;
        returnedPreferences.UserId.Should().Be(123);
        returnedPreferences.PlaidConnectionIssues.Should().BeTrue();
        returnedPreferences.BudgetAlerts.Should().BeTrue();

        _mockEmailPreferencesService.Verify(s => s.GetUserEmailPreferencesAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetEmailPreferences_PreferencesNotFound_ReturnsNotFound()
    {
        // Arrange
        _mockEmailPreferencesService
            .Setup(s => s.GetUserEmailPreferencesAsync(123))
            .ReturnsAsync((UserEmailPreferencesDto?)null);

        // Act
        var result = await _controller.GetEmailPreferences();

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetEmailPreferences_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockEmailPreferencesService
            .Setup(s => s.GetUserEmailPreferencesAsync(123))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetEmailPreferences());
    }

    #endregion

    #region UpdateEmailPreferences Tests

    [Fact]
    public async Task UpdateEmailPreferences_ValidRequest_ReturnsOkWithUpdatedPreferences()
    {
        // Arrange
        var request = new UpdateEmailPreferencesRequest
        {
            PlaidConnectionIssues = true,
            BudgetAlerts = false,
            FeatureAnnouncements = true
        };

        var updatedPreferences = new UserEmailPreferencesDto
        {
            Id = 1,
            UserId = 123,
            PlaidConnectionIssues = true,
            BudgetAlerts = false,
            FeatureAnnouncements = true,
            UpdatedAt = DateTime.UtcNow
        };

        _mockEmailPreferencesService
            .Setup(s => s.UpdateEmailPreferencesAsync(123, request))
            .ReturnsAsync(updatedPreferences);

        // Act
        var result = await _controller.UpdateEmailPreferences(request);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var messageProperty = value?.GetType().GetProperty("message")?.GetValue(value);
        var preferencesProperty = value?.GetType().GetProperty("preferences")?.GetValue(value);

        messageProperty.Should().Be("Email preferences updated successfully");
        preferencesProperty.Should().BeAssignableTo<UserEmailPreferencesDto>();

        _mockEmailPreferencesService.Verify(s => s.UpdateEmailPreferencesAsync(123, request), Times.Once);
    }

    [Fact]
    public async Task UpdateEmailPreferences_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        var request = new UpdateEmailPreferencesRequest
        {
            BudgetAlerts = true
        };

        _mockEmailPreferencesService
            .Setup(s => s.UpdateEmailPreferencesAsync(123, request))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.UpdateEmailPreferences(request));
    }

    #endregion

    #region GetEmailCategories Tests

    [Fact]
    public async Task GetEmailCategories_ValidRequest_ReturnsOkWithCategories()
    {
        // Arrange
        var categories = new EmailPreferenceCategoriesDto
        {
            Security = new SecurityEmailsDto(),
            AccountActivity = new AccountActivityDto
            {
                PlaidConnectionIssues = true,
                SubscriptionChanges = true
            },
            FinancialInsights = new FinancialInsightsDto
            {
                BudgetAlerts = true
            }
        };

        _mockEmailPreferencesService
            .Setup(s => s.GetEmailCategoriesAsync(123))
            .ReturnsAsync(categories);

        // Act
        var result = await _controller.GetEmailCategories();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedCategories = okResult.Value.Should().BeAssignableTo<EmailPreferenceCategoriesDto>().Subject;
        returnedCategories.AccountActivity.PlaidConnectionIssues.Should().BeTrue();
        returnedCategories.FinancialInsights.BudgetAlerts.Should().BeTrue();

        _mockEmailPreferencesService.Verify(s => s.GetEmailCategoriesAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetEmailCategories_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockEmailPreferencesService
            .Setup(s => s.GetEmailCategoriesAsync(123))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetEmailCategories());
    }

    #endregion

    #region CheckEmailType Tests

    [Fact]
    public async Task CheckEmailType_EnabledNonSecurityEmail_ReturnsOkWithShouldSendTrue()
    {
        // Arrange
        _mockEmailPreferencesService
            .Setup(s => s.ShouldSendEmailAsync(123, "budget_alert"))
            .ReturnsAsync(true);
        
        _mockEmailPreferencesService
            .Setup(s => s.IsSecurityEmail("budget_alert"))
            .Returns(false);

        // Act
        var result = await _controller.CheckEmailType("budget_alert");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var shouldSendProperty = value?.GetType().GetProperty("shouldSend")?.GetValue(value);
        var isSecurityProperty = value?.GetType().GetProperty("isSecurityEmail")?.GetValue(value);

        shouldSendProperty.Should().Be(true);
        isSecurityProperty.Should().Be(false);
    }

    [Fact]
    public async Task CheckEmailType_DisabledNonSecurityEmail_ReturnsOkWithShouldSendFalse()
    {
        // Arrange
        _mockEmailPreferencesService
            .Setup(s => s.ShouldSendEmailAsync(123, "promotional_offer"))
            .ReturnsAsync(false);
        
        _mockEmailPreferencesService
            .Setup(s => s.IsSecurityEmail("promotional_offer"))
            .Returns(false);

        // Act
        var result = await _controller.CheckEmailType("promotional_offer");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var shouldSendProperty = value?.GetType().GetProperty("shouldSend")?.GetValue(value);
        var isSecurityProperty = value?.GetType().GetProperty("isSecurityEmail")?.GetValue(value);

        shouldSendProperty.Should().Be(false);
        isSecurityProperty.Should().Be(false);
    }

    [Fact]
    public async Task CheckEmailType_SecurityEmail_ReturnsOkWithSecurityFlag()
    {
        // Arrange
        _mockEmailPreferencesService
            .Setup(s => s.ShouldSendEmailAsync(123, "password_reset"))
            .ReturnsAsync(true);
        
        _mockEmailPreferencesService
            .Setup(s => s.IsSecurityEmail("password_reset"))
            .Returns(true);

        // Act
        var result = await _controller.CheckEmailType("password_reset");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var shouldSendProperty = value?.GetType().GetProperty("shouldSend")?.GetValue(value);
        var isSecurityProperty = value?.GetType().GetProperty("isSecurityEmail")?.GetValue(value);

        shouldSendProperty.Should().Be(true);
        isSecurityProperty.Should().Be(true);
    }

    [Fact]
    public async Task CheckEmailType_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockEmailPreferencesService
            .Setup(s => s.ShouldSendEmailAsync(123, "budget_alert"))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.CheckEmailType("budget_alert"));
    }

    #endregion

    #region ResetToDefaults Tests

    [Fact]
    public async Task ResetToDefaults_ValidRequest_ReturnsOkWithDefaultPreferences()
    {
        // Arrange
        var defaultPreferences = new UserEmailPreferencesDto
        {
            Id = 1,
            UserId = 123,
            PlaidConnectionIssues = true,
            BudgetAlerts = true,
            FeatureAnnouncements = false,
            UpdatedAt = DateTime.UtcNow
        };

        _mockEmailPreferencesService
            .Setup(s => s.UpdateEmailPreferencesAsync(123, It.IsAny<UpdateEmailPreferencesRequest>()))
            .ReturnsAsync(defaultPreferences);

        // Act
        var result = await _controller.ResetToDefaults();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var messageProperty = value?.GetType().GetProperty("message")?.GetValue(value);
        var preferencesProperty = value?.GetType().GetProperty("preferences")?.GetValue(value);

        messageProperty.Should().Be("Email preferences reset to defaults");
        preferencesProperty.Should().BeAssignableTo<UserEmailPreferencesDto>();

        _mockEmailPreferencesService.Verify(
            s => s.UpdateEmailPreferencesAsync(123, It.IsAny<UpdateEmailPreferencesRequest>()), 
            Times.Once);
    }

    [Fact]
    public async Task ResetToDefaults_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockEmailPreferencesService
            .Setup(s => s.UpdateEmailPreferencesAsync(123, It.IsAny<UpdateEmailPreferencesRequest>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.ResetToDefaults());
    }

    #endregion
}
