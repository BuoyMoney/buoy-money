using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using System.Threading;

namespace BuoyAI.UnitTests.Services;

public class DataRetentionServiceTests
{
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<IServiceScope> _serviceScopeMock;
    private readonly Mock<IDataRetentionManager> _retentionManagerMock;
    private readonly Mock<IDataRetentionAuditService> _auditServiceMock;
    private readonly Mock<ILogger<DataRetentionService>> _loggerMock;
    private readonly DataRetentionService _dataRetentionService;

    public DataRetentionServiceTests()
    {
        _serviceProviderMock = new Mock<IServiceProvider>();
        _serviceScopeMock = new Mock<IServiceScope>();
        _retentionManagerMock = new Mock<IDataRetentionManager>();
        _auditServiceMock = new Mock<IDataRetentionAuditService>();
        _loggerMock = new Mock<ILogger<DataRetentionService>>();

        var serviceScopeFactoryMock = new Mock<IServiceScopeFactory>();
        serviceScopeFactoryMock
            .Setup(x => x.CreateScope())
            .Returns(_serviceScopeMock.Object);

        _serviceScopeMock
            .Setup(x => x.ServiceProvider)
            .Returns(_serviceProviderMock.Object);

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(IDataRetentionManager)))
            .Returns(_retentionManagerMock.Object);

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(IDataRetentionAuditService)))
            .Returns(_auditServiceMock.Object);

        _dataRetentionService = new DataRetentionService(
            serviceScopeFactoryMock.Object,
            _loggerMock.Object,
            new BuoyAI.Infrastructure.Telemetry.BuoyAiMetrics(new TestMeterFactory())
        );
    }

    [Fact]
    public async Task StartAsync_ShouldStartSuccessfully()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        
        // Setup cleanup methods to return immediately
        _retentionManagerMock.Setup(x => x.CleanupClosedUserAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _retentionManagerMock.Setup(x => x.CleanupSecurityLogsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _retentionManagerMock.Setup(x => x.CleanupErrorLogsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _retentionManagerMock.Setup(x => x.CleanupOldAIInsightsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _retentionManagerMock.Setup(x => x.CleanupAIRefreshUsageAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _retentionManagerMock.Setup(x => x.CleanupDisconnectedAccountDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _retentionManagerMock.Setup(x => x.CleanupExpiredPlaidTokensAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        _auditServiceMock
            .Setup(x => x.LogRetentionExecutionAsync(It.IsAny<object>()))
            .Returns(Task.CompletedTask);

        // Act
        var startTask = _dataRetentionService.StartAsync(cts.Token);
        
        // Give it a moment to start
        await Task.Delay(100);
        
        // Cancel to stop the service
        cts.Cancel();
        
        // Wait for completion
        await startTask;

        // Assert
        startTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ShouldStopGracefully()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        
        // Start the service
        var startTask = _dataRetentionService.StartAsync(cts.Token);
        await Task.Delay(50); // Let it start
        
        // Act
        var stopTask = _dataRetentionService.StopAsync(cts.Token);
        
        // Assert
        await stopTask;
        stopTask.IsCompleted.Should().BeTrue();
        
        // Cleanup
        cts.Cancel();
        try { await startTask; } catch { /* Expected when cancelled */ }
    }

    [Fact]
    public void DataRetentionSummary_ShouldInitializeWithDefaults()
    {
        // Act
        var summary = new DataRetentionService.DataRetentionSummary();

        // Assert
        summary.DeletedClosedAccounts.Should().Be(0);
        summary.DeletedSecurityLogs.Should().Be(0);
        summary.DeletedErrorLogs.Should().Be(0);
        summary.DeletedAIInsights.Should().Be(0);
        summary.DeletedAIRefreshData.Should().Be(0);
        summary.DeletedDisconnectedTransactions.Should().Be(0);
        summary.DeletedExpiredTokens.Should().Be(0);
        summary.ExecutedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DataRetentionSummary_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var expectedTime = new DateTime(2025, 9, 9, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var summary = new DataRetentionService.DataRetentionSummary
        {
            DeletedClosedAccounts = 5,
            DeletedSecurityLogs = 100,
            DeletedErrorLogs = 50,
            DeletedAIInsights = 25,
            DeletedAIRefreshData = 10,
            DeletedDisconnectedTransactions = 3,
            DeletedExpiredTokens = 8,
            ExecutedAt = expectedTime
        };

        // Assert
        summary.DeletedClosedAccounts.Should().Be(5);
        summary.DeletedSecurityLogs.Should().Be(100);
        summary.DeletedErrorLogs.Should().Be(50);
        summary.DeletedAIInsights.Should().Be(25);
        summary.DeletedAIRefreshData.Should().Be(10);
        summary.DeletedDisconnectedTransactions.Should().Be(3);
        summary.DeletedExpiredTokens.Should().Be(8);
        summary.ExecutedAt.Should().Be(expectedTime);
    }
}
