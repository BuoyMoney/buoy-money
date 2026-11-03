using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.API.Controllers;
using FluentAssertions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace BuoyAI.UnitTests.Controllers;

public class DataRetentionControllerTests
{
    private readonly Mock<IDataRetentionManager> _retentionManagerMock;
    private readonly Mock<IDataRetentionAuditService> _auditServiceMock;
    private readonly Mock<IAuthService> _authServiceMock;
    private readonly Mock<ILogger<DataRetentionController>> _loggerMock;
    private readonly DataRetentionController _controller;

    public DataRetentionControllerTests()
    {
        _retentionManagerMock = new Mock<IDataRetentionManager>();
        _auditServiceMock = new Mock<IDataRetentionAuditService>();
        _authServiceMock = new Mock<IAuthService>();
        _loggerMock = new Mock<ILogger<DataRetentionController>>();

        _controller = new DataRetentionController(
            _retentionManagerMock.Object,
            _auditServiceMock.Object,
            _authServiceMock.Object,
            _loggerMock.Object
        );

        // Setup user context for controller
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "123")
        }));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    [Fact]
    public async Task DeleteUserAccount_ShouldReturnOk_WhenDeletionSucceeds()
    {
        // Arrange — the endpoint delegates to the full deletion orchestration
        var userId = 123;
        _authServiceMock
            .Setup(x => x.DeleteAccountAsync(userId, It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteUserAccount();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult?.Value.Should().NotBeNull();

        _authServiceMock.Verify(x => x.DeleteAccountAsync(userId, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task DeleteUserAccount_ShouldReturnInternalServerError_WhenDeletionFails()
    {
        // Arrange
        var userId = 123;
        _authServiceMock
            .Setup(x => x.DeleteAccountAsync(userId, It.IsAny<string?>()))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteUserAccount();

        // Assert
        result.Should().BeOfType<ObjectResult>();
        var objectResult = result as ObjectResult;
        objectResult?.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task DeleteUserAccount_ShouldPropagateException_WhenExceptionOccurs()
    {
        // Arrange
        var userId = 123;
        _authServiceMock
            .Setup(x => x.DeleteAccountAsync(userId, It.IsAny<string?>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.DeleteUserAccount());
    }

    [Fact]
    public async Task RequestDataDeletion_ShouldReturnAccepted_WhenRequestIsValid()
    {
        // Arrange
        var userId = 123;
        var request = new DataDeletionRequest
        {
            DataTypes = new List<string> { "transactions", "insights" },
            Reason = "Privacy concern"
        };

        _auditServiceMock
            .Setup(x => x.LogUserDataDeletionAsync(userId, "user_request", "partial_deletion"))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RequestDataDeletion(request);

        // Assert
        result.Should().BeOfType<AcceptedResult>();
        var acceptedResult = result as AcceptedResult;
        acceptedResult?.Value.Should().NotBeNull();

        _auditServiceMock.Verify(x => x.LogUserDataDeletionAsync(userId, "user_request", "partial_deletion"), Times.Once);
    }

    [Fact]
    public async Task TriggerManualCleanup_ShouldReturnOk_WhenCleanupSucceeds()
    {
        // Arrange
        _retentionManagerMock.Setup(x => x.CleanupClosedUserAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);
        _retentionManagerMock.Setup(x => x.CleanupSecurityLogsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100);
        _retentionManagerMock.Setup(x => x.CleanupErrorLogsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(50);
        _retentionManagerMock.Setup(x => x.CleanupOldAIInsightsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(25);
        _retentionManagerMock.Setup(x => x.CleanupAIRefreshUsageAsync(It.IsAny<CancellationToken>())).ReturnsAsync(10);
        _retentionManagerMock.Setup(x => x.CleanupDisconnectedAccountDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        _retentionManagerMock.Setup(x => x.CleanupExpiredPlaidTokensAsync(It.IsAny<CancellationToken>())).ReturnsAsync(8);

        _auditServiceMock
            .Setup(x => x.LogRetentionExecutionAsync(It.IsAny<object>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.TriggerManualCleanup();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult?.Value.Should().NotBeNull();

        // Verify all cleanup methods were called
        _retentionManagerMock.Verify(x => x.CleanupClosedUserAccountsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _retentionManagerMock.Verify(x => x.CleanupSecurityLogsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _retentionManagerMock.Verify(x => x.CleanupErrorLogsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _retentionManagerMock.Verify(x => x.CleanupOldAIInsightsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _retentionManagerMock.Verify(x => x.CleanupAIRefreshUsageAsync(It.IsAny<CancellationToken>()), Times.Once);
        _retentionManagerMock.Verify(x => x.CleanupDisconnectedAccountDataAsync(It.IsAny<CancellationToken>()), Times.Once);
        _retentionManagerMock.Verify(x => x.CleanupExpiredPlaidTokensAsync(It.IsAny<CancellationToken>()), Times.Once);

        _auditServiceMock.Verify(x => x.LogRetentionExecutionAsync(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task TriggerManualCleanup_ShouldPropagateException_WhenExceptionOccurs()
    {
        // Arrange
        _retentionManagerMock
            .Setup(x => x.CleanupClosedUserAccountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.TriggerManualCleanup());
    }

    [Fact]
    public async Task GetAuditLogs_ShouldReturnOk_WhenLogsExist()
    {
        // Arrange
        var fromDate = DateTime.UtcNow.AddDays(-7);
        var toDate = DateTime.UtcNow;
        var auditLogs = new List<object>
        {
            new { Id = 1, Action = "delete_user", CreatedAt = DateTime.UtcNow.AddDays(-1) },
            new { Id = 2, Action = "cleanup_logs", CreatedAt = DateTime.UtcNow.AddDays(-2) }
        };

        _auditServiceMock
            .Setup(x => x.GetRetentionAuditLogsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(auditLogs);

        // Act
        var result = await _controller.GetAuditLogs(fromDate, toDate);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult?.Value.Should().NotBeNull();

        _auditServiceMock.Verify(x => x.GetRetentionAuditLogsAsync(fromDate, toDate), Times.Once);
    }

    [Fact]
    public async Task GetAuditLogs_ShouldUseDefaultDates_WhenDatesNotProvided()
    {
        // Arrange
        var auditLogs = new List<object>();

        _auditServiceMock
            .Setup(x => x.GetRetentionAuditLogsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(auditLogs);

        // Act
        var result = await _controller.GetAuditLogs();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        
        _auditServiceMock.Verify(x => x.GetRetentionAuditLogsAsync(
            It.Is<DateTime>(d => d <= DateTime.UtcNow.AddDays(-29)), // Approximately 30 days ago
            It.Is<DateTime>(d => d >= DateTime.UtcNow.AddMinutes(-1))  // Approximately now
        ), Times.Once);
    }
}
