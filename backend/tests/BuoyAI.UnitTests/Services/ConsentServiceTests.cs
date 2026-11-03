using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Core.DTOs;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class ConsentServiceTests
{
    private readonly Mock<IUserConsentRepository> _mockConsentRepository;
    private readonly Mock<IUserRepository> _mockUserRepository;
    private readonly Mock<ILogger<ConsentService>> _mockLogger;
    private readonly ConsentService _service;

    public ConsentServiceTests()
    {
        _mockConsentRepository = new Mock<IUserConsentRepository>();
        _mockUserRepository = new Mock<IUserRepository>();
        _mockLogger = new Mock<ILogger<ConsentService>>();
        _service = new ConsentService(_mockConsentRepository.Object, _mockUserRepository.Object, _mockLogger.Object);
    }

    #region RecordConsentAsync Tests

    [Fact]
    public async Task RecordConsentAsync_WithValidRequest_ShouldCreateConsentRecord()
    {
        // Arrange
        var userId = 1;
        var user = new User { Id = userId, Username = "testuser", Email = "test@example.com" };
        var request = new RecordConsentRequest
        {
            ConsentType = "PLAID_TERMS",
            ConsentVersion = "1.0",
            HasConsented = true,
            ConsentText = "I agree to Plaid terms",
            IpAddress = "192.168.1.1",
            UserAgent = "Mozilla/5.0"
        };

        _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync(user);
        _mockConsentRepository.Setup(x => x.CreateAsync(It.IsAny<UserConsent>())).Returns(Task.FromResult<UserConsent>(null!));

        // Act
        await _service.RecordConsentAsync(userId, request);

        // Assert
        _mockConsentRepository.Verify(x => x.CreateAsync(It.Is<UserConsent>(c =>
            c.UserId == userId &&
            c.ConsentType == "PLAID_TERMS" &&
            c.ConsentVersion == "1.0" &&
            c.HasConsented == true &&
            c.ConsentText == "I agree to Plaid terms" &&
            c.IpAddress == "192.168.1.1" &&
            c.UserAgent == "Mozilla/5.0"
        )), Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Recorded consent") && v.ToString()!.Contains("PLAID_TERMS")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordConsentAsync_WithNonExistentUser_ShouldThrowArgumentException()
    {
        // Arrange
        var userId = 999;
        var request = new RecordConsentRequest
        {
            ConsentType = "PLAID_TERMS",
            ConsentVersion = "1.0",
            HasConsented = true
        };

        _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync((User?)null);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.RecordConsentAsync(userId, request));
        exception.Message.Should().Contain("User not found");
    }

    [Fact]
    public async Task RecordConsentAsync_WithEmptyConsentType_ShouldThrowArgumentException()
    {
        // Arrange
        var userId = 1;
        var user = new User { Id = userId };
        var request = new RecordConsentRequest
        {
            ConsentType = "",
            ConsentVersion = "1.0",
            HasConsented = true
        };

        _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync(user);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.RecordConsentAsync(userId, request));
        exception.Message.Should().Contain("ConsentType is required");
    }

    [Fact]
    public async Task RecordConsentAsync_WithEmptyConsentVersion_ShouldThrowArgumentException()
    {
        // Arrange
        var userId = 1;
        var user = new User { Id = userId };
        var request = new RecordConsentRequest
        {
            ConsentType = "PLAID_TERMS",
            ConsentVersion = "",
            HasConsented = true
        };

        _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync(user);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.RecordConsentAsync(userId, request));
        exception.Message.Should().Contain("ConsentVersion is required");
    }

    [Fact]
    public async Task RecordConsentAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        var user = new User { Id = userId };
        var request = new RecordConsentRequest
        {
            ConsentType = "PLAID_TERMS",
            ConsentVersion = "1.0",
            HasConsented = true
        };

        _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync(user);
        _mockConsentRepository.Setup(x => x.CreateAsync(It.IsAny<UserConsent>())).ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.RecordConsentAsync(userId, request));

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error recording consent")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region GetConsentHistoryAsync Tests

    [Fact]
    public async Task GetConsentHistoryAsync_WithExistingConsents_ShouldReturnOrderedHistory()
    {
        // Arrange
        var userId = 1;
        var consents = new List<UserConsent>
        {
            new UserConsent
            {
                Id = 1,
                UserId = userId,
                ConsentType = "PLAID_TERMS",
                ConsentVersion = "1.0",
                HasConsented = true,
                ConsentedAt = DateTime.UtcNow.AddDays(-10),
                ConsentText = "Version 1.0 terms"
            },
            new UserConsent
            {
                Id = 2,
                UserId = userId,
                ConsentType = "PLAID_TERMS",
                ConsentVersion = "2.0",
                HasConsented = true,
                ConsentedAt = DateTime.UtcNow,
                ConsentText = "Version 2.0 terms"
            }
        };

        _mockConsentRepository.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(consents);

        // Act
        var result = await _service.GetConsentHistoryAsync(userId);

        // Assert
        result.Should().HaveCount(2);
        result[0].ConsentVersion.Should().Be("2.0"); // Most recent first
        result[1].ConsentVersion.Should().Be("1.0");
        result[0].ConsentText.Should().Be("Version 2.0 terms");
    }

    [Fact]
    public async Task GetConsentHistoryAsync_WithNoConsents_ShouldReturnEmptyList()
    {
        // Arrange
        var userId = 1;
        _mockConsentRepository.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<UserConsent>());

        // Act
        var result = await _service.GetConsentHistoryAsync(userId);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetConsentHistoryAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        _mockConsentRepository.Setup(x => x.GetByUserIdAsync(userId)).ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.GetConsentHistoryAsync(userId));

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error getting consent history")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region HasValidConsentAsync Tests

    [Fact]
    public async Task HasValidConsentAsync_WithActiveConsent_ShouldReturnTrue()
    {
        // Arrange
        var userId = 1;
        var consentType = "PLAID_TERMS";
        var consent = new UserConsent
        {
            UserId = userId,
            ConsentType = consentType,
            HasConsented = true
        };

        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, consentType)).ReturnsAsync(consent);

        // Act
        var result = await _service.HasValidConsentAsync(userId, consentType);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasValidConsentAsync_WithRevokedConsent_ShouldReturnFalse()
    {
        // Arrange
        var userId = 1;
        var consentType = "PLAID_TERMS";
        var consent = new UserConsent
        {
            UserId = userId,
            ConsentType = consentType,
            HasConsented = false
        };

        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, consentType)).ReturnsAsync(consent);

        // Act
        var result = await _service.HasValidConsentAsync(userId, consentType);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasValidConsentAsync_WithNoConsent_ShouldReturnFalse()
    {
        // Arrange
        var userId = 1;
        var consentType = "PLAID_TERMS";

        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, consentType)).ReturnsAsync((UserConsent?)null);

        // Act
        var result = await _service.HasValidConsentAsync(userId, consentType);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasValidConsentAsync_WithEmptyConsentType_ShouldThrowArgumentException()
    {
        // Arrange
        var userId = 1;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.HasValidConsentAsync(userId, ""));
        exception.Message.Should().Contain("ConsentType is required");
    }

    [Fact]
    public async Task HasValidConsentAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        var consentType = "PLAID_TERMS";
        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, consentType)).ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.HasValidConsentAsync(userId, consentType));

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error checking consent")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region RevokeConsentAsync Tests

    [Fact]
    public async Task RevokeConsentAsync_WithValidType_ShouldCreateRevokeRecord()
    {
        // Arrange
        var userId = 1;
        var consentType = "PLAID_TERMS";

        _mockConsentRepository.Setup(x => x.CreateAsync(It.IsAny<UserConsent>())).Returns(Task.FromResult<UserConsent>(null!));

        // Act
        await _service.RevokeConsentAsync(userId, consentType);

        // Assert
        _mockConsentRepository.Verify(x => x.CreateAsync(It.Is<UserConsent>(c =>
            c.UserId == userId &&
            c.ConsentType == consentType &&
            c.ConsentVersion == "REVOKED" &&
            c.HasConsented == false &&
            c.ConsentText == "User revoked consent"
        )), Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Revoked consent") && v.ToString()!.Contains(consentType)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RevokeConsentAsync_WithEmptyConsentType_ShouldThrowArgumentException()
    {
        // Arrange
        var userId = 1;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.RevokeConsentAsync(userId, ""));
        exception.Message.Should().Contain("ConsentType is required");
    }

    [Fact]
    public async Task RevokeConsentAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        var consentType = "PLAID_TERMS";
        _mockConsentRepository.Setup(x => x.CreateAsync(It.IsAny<UserConsent>())).ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.RevokeConsentAsync(userId, consentType));

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error revoking consent")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region GetPlaidConsentDataAsync Tests

    [Fact]
    public async Task GetPlaidConsentDataAsync_WithBothConsentsAccepted_ShouldReturnAllTrue()
    {
        // Arrange
        var userId = 1;
        var termsConsent = new UserConsent
        {
            UserId = userId,
            ConsentType = "PLAID_TERMS",
            HasConsented = true
        };
        var privacyConsent = new UserConsent
        {
            UserId = userId,
            ConsentType = "PLAID_PRIVACY",
            HasConsented = true
        };

        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_TERMS")).ReturnsAsync(termsConsent);
        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_PRIVACY")).ReturnsAsync(privacyConsent);

        // Act
        var result = await _service.GetPlaidConsentDataAsync(userId);

        // Assert
        result.DataSharing.Should().BeTrue();
        result.TransactionAccess.Should().BeTrue();
        result.AccountAccess.Should().BeTrue();
        result.BalanceAccess.Should().BeTrue();
        result.IdentityAccess.Should().BeTrue();
        result.TermsAccepted.Should().BeTrue();
        result.PrivacyPolicyAccepted.Should().BeTrue();
    }

    [Fact]
    public async Task GetPlaidConsentDataAsync_WithNoConsents_ShouldReturnAllFalse()
    {
        // Arrange
        var userId = 1;

        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_TERMS")).ReturnsAsync((UserConsent?)null);
        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_PRIVACY")).ReturnsAsync((UserConsent?)null);

        // Act
        var result = await _service.GetPlaidConsentDataAsync(userId);

        // Assert
        result.DataSharing.Should().BeFalse();
        result.TransactionAccess.Should().BeFalse();
        result.AccountAccess.Should().BeFalse();
        result.BalanceAccess.Should().BeFalse();
        result.IdentityAccess.Should().BeFalse();
        result.TermsAccepted.Should().BeFalse();
        result.PrivacyPolicyAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task GetPlaidConsentDataAsync_WithOnlyTermsAccepted_ShouldReturnPartialTrue()
    {
        // Arrange
        var userId = 1;
        var termsConsent = new UserConsent
        {
            UserId = userId,
            ConsentType = "PLAID_TERMS",
            HasConsented = true
        };

        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_TERMS")).ReturnsAsync(termsConsent);
        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_PRIVACY")).ReturnsAsync((UserConsent?)null);

        // Act
        var result = await _service.GetPlaidConsentDataAsync(userId);

        // Assert
        result.TermsAccepted.Should().BeTrue();
        result.DataSharing.Should().BeTrue();
        result.PrivacyPolicyAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task GetPlaidConsentDataAsync_WithRevokedConsents_ShouldReturnAllFalse()
    {
        // Arrange
        var userId = 1;
        var termsConsent = new UserConsent
        {
            UserId = userId,
            ConsentType = "PLAID_TERMS",
            HasConsented = false // Revoked
        };
        var privacyConsent = new UserConsent
        {
            UserId = userId,
            ConsentType = "PLAID_PRIVACY",
            HasConsented = false // Revoked
        };

        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_TERMS")).ReturnsAsync(termsConsent);
        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_PRIVACY")).ReturnsAsync(privacyConsent);

        // Act
        var result = await _service.GetPlaidConsentDataAsync(userId);

        // Assert
        result.DataSharing.Should().BeFalse();
        result.TermsAccepted.Should().BeFalse();
        result.PrivacyPolicyAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task GetPlaidConsentDataAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        _mockConsentRepository.Setup(x => x.GetLatestConsentAsync(userId, "PLAID_TERMS")).ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.GetPlaidConsentDataAsync(userId));

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error getting Plaid consent data")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion
}
