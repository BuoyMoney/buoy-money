using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs;
using System.Security.Claims;

namespace BuoyAI.UnitTests.Controllers;

public class ConsentControllerTests
{
    private readonly Mock<IConsentService> _mockConsentService;
    private readonly Mock<ILogger<ConsentController>> _mockLogger;
    private readonly ConsentController _controller;

    public ConsentControllerTests()
    {
        _mockConsentService = new Mock<IConsentService>();
        _mockLogger = new Mock<ILogger<ConsentController>>();

        _controller = new ConsentController(_mockConsentService.Object, _mockLogger.Object);

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

    #region RecordConsent Tests

    [Fact]
    public async Task RecordConsent_ValidRequest_ReturnsOkWithSuccessMessage()
    {
        // Arrange
        var request = new RecordConsentRequest
        {
            ConsentType = "Terms",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentText = "I agree to terms and conditions"
        };

        _mockConsentService
            .Setup(s => s.RecordConsentAsync(123, It.IsAny<RecordConsentRequest>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RecordConsent(request);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var successProperty = value?.GetType().GetProperty("success")?.GetValue(value);
        var messageProperty = value?.GetType().GetProperty("message")?.GetValue(value);

        successProperty.Should().Be(true);
        messageProperty.Should().Be("Consent recorded successfully");

        _mockConsentService.Verify(s => s.RecordConsentAsync(123, It.IsAny<RecordConsentRequest>()), Times.Once);
    }

    [Fact]
    public async Task RecordConsent_InvalidRequest_ReturnsBadRequest()
    {
        // Arrange
        var request = new RecordConsentRequest
        {
            ConsentType = "Invalid",
            ConsentVersion = "1.0",
            HasConsented = true
        };

        _mockConsentService
            .Setup(s => s.RecordConsentAsync(123, It.IsAny<RecordConsentRequest>()))
            .ThrowsAsync(new ArgumentException("Invalid consent type"));

        // Act
        var result = await _controller.RecordConsent(request);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RecordConsent_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        var request = new RecordConsentRequest
        {
            ConsentType = "Terms",
            ConsentVersion = "1.0",
            HasConsented = true
        };

        _mockConsentService
            .Setup(s => s.RecordConsentAsync(123, It.IsAny<RecordConsentRequest>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.RecordConsent(request));
    }

    #endregion

    #region GetConsentHistory Tests

    [Fact]
    public async Task GetConsentHistory_HasHistory_ReturnsOkWithList()
    {
        // Arrange
        var history = new List<ConsentHistoryResponse>
        {
            new ConsentHistoryResponse
            {
                Id = 1,
                ConsentType = "Terms",
                ConsentVersion = "1.0",
                HasConsented = true,
                ConsentedAt = DateTime.UtcNow,
                ConsentText = "Terms text"
            },
            new ConsentHistoryResponse
            {
                Id = 2,
                ConsentType = "Privacy",
                ConsentVersion = "2.0",
                HasConsented = true,
                ConsentedAt = DateTime.UtcNow,
                ConsentText = "Privacy text"
            }
        };

        _mockConsentService
            .Setup(s => s.GetConsentHistoryAsync(123))
            .ReturnsAsync(history);

        // Act
        var result = await _controller.GetConsentHistory();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedHistory = okResult.Value.Should().BeAssignableTo<List<ConsentHistoryResponse>>().Subject;
        returnedHistory.Should().HaveCount(2);
        returnedHistory[0].ConsentType.Should().Be("Terms");
        returnedHistory[1].ConsentType.Should().Be("Privacy");

        _mockConsentService.Verify(s => s.GetConsentHistoryAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetConsentHistory_NoHistory_ReturnsOkWithEmptyList()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.GetConsentHistoryAsync(123))
            .ReturnsAsync(new List<ConsentHistoryResponse>());

        // Act
        var result = await _controller.GetConsentHistory();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedHistory = okResult.Value.Should().BeAssignableTo<List<ConsentHistoryResponse>>().Subject;
        returnedHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task GetConsentHistory_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.GetConsentHistoryAsync(123))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetConsentHistory());
    }

    #endregion

    #region CheckConsent Tests

    [Fact]
    public async Task CheckConsent_ValidType_ReturnsOkWithTrue()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.HasValidConsentAsync(123, "Terms"))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.CheckConsent("Terms");

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var hasConsentProperty = value?.GetType().GetProperty("hasConsent")?.GetValue(value);
        hasConsentProperty.Should().Be(true);

        _mockConsentService.Verify(s => s.HasValidConsentAsync(123, "Terms"), Times.Once);
    }

    [Fact]
    public async Task CheckConsent_NoValidConsent_ReturnsOkWithFalse()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.HasValidConsentAsync(123, "Privacy"))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.CheckConsent("Privacy");

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var hasConsentProperty = value?.GetType().GetProperty("hasConsent")?.GetValue(value);
        hasConsentProperty.Should().Be(false);
    }

    [Fact]
    public async Task CheckConsent_InvalidType_ReturnsBadRequest()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.HasValidConsentAsync(123, "Invalid"))
            .ThrowsAsync(new ArgumentException("Invalid consent type"));

        // Act
        var result = await _controller.CheckConsent("Invalid");

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CheckConsent_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.HasValidConsentAsync(123, "Terms"))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.CheckConsent("Terms"));
    }

    #endregion

    #region RevokeConsent Tests

    [Fact]
    public async Task RevokeConsent_ValidType_ReturnsOkWithSuccessMessage()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.RevokeConsentAsync(123, "Terms"))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RevokeConsent("Terms");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var value = okResult.Value;
        var successProperty = value?.GetType().GetProperty("success")?.GetValue(value);
        var messageProperty = value?.GetType().GetProperty("message")?.GetValue(value);

        successProperty.Should().Be(true);
        messageProperty.Should().Be("Consent revoked successfully");

        _mockConsentService.Verify(s => s.RevokeConsentAsync(123, "Terms"), Times.Once);
    }

    [Fact]
    public async Task RevokeConsent_InvalidType_ReturnsBadRequest()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.RevokeConsentAsync(123, "Invalid"))
            .ThrowsAsync(new ArgumentException("Invalid consent type"));

        // Act
        var result = await _controller.RevokeConsent("Invalid");

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RevokeConsent_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.RevokeConsentAsync(123, "Terms"))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.RevokeConsent("Terms"));
    }

    #endregion

    #region GetPlaidConsentData Tests

    [Fact]
    public async Task GetPlaidConsentData_ValidRequest_ReturnsOkWithData()
    {
        // Arrange
        var consentData = new PlaidConsentData
        {
            DataSharing = true,
            TransactionAccess = true,
            AccountAccess = true,
            BalanceAccess = true,
            IdentityAccess = false,
            TermsAccepted = true,
            PrivacyPolicyAccepted = true
        };

        _mockConsentService
            .Setup(s => s.GetPlaidConsentDataAsync(123))
            .ReturnsAsync(consentData);

        // Act
        var result = await _controller.GetPlaidConsentData();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedData = okResult.Value.Should().BeAssignableTo<PlaidConsentData>().Subject;
        returnedData.DataSharing.Should().BeTrue();
        returnedData.TransactionAccess.Should().BeTrue();
        returnedData.AccountAccess.Should().BeTrue();
        returnedData.BalanceAccess.Should().BeTrue();
        returnedData.IdentityAccess.Should().BeFalse();
        returnedData.TermsAccepted.Should().BeTrue();
        returnedData.PrivacyPolicyAccepted.Should().BeTrue();

        _mockConsentService.Verify(s => s.GetPlaidConsentDataAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetPlaidConsentData_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockConsentService
            .Setup(s => s.GetPlaidConsentDataAsync(123))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetPlaidConsentData());
    }

    #endregion
}
