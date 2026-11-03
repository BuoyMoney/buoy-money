using System;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Categorization calls write a durable usage row with the provider's own token counts and
/// the cost from the maintained pricing table, under the "categorization" context.
/// </summary>
public class AICategorizationUsageRecordingTests
{
    private const int UserId = 8;
    private readonly Mock<IAIUsageTracker> _usage = new();
    private readonly Mock<ICostTrackingService> _cost = new();

    private AICategorizationService CreateService(bool withTrackers = true)
    {
        var config = new ConfigurationBuilder().Build();
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new AICategorizationService(
            new Mock<ITransactionRepository>().Object,
            new Mock<IUserRepository>().Object,
            new Mock<IEntitlementService>().Object,
            new Mock<ICategorizationRuleService>().Object,
            new Mock<ITransactionEmbeddingService>().Object,
            new Mock<ICategoryRepository>().Object,
            new BuoyAIDbContext(options),
            config,
            new Mock<ILogger<AICategorizationService>>().Object,
            new AIModelConfiguration(config),
            TestGeminiUsage.Create(),
            withTrackers ? _usage.Object : null,
            withTrackers ? _cost.Object : null);
    }

    [Fact]
    public async Task RecordCategorizationUsage_WritesRowWithRealTokensCostAndContext()
    {
        _cost.Setup(c => c.CalculateCost("gemini-3.5-flash-lite", 1800, 40)).Returns(0.00064m);

        await CreateService().RecordCategorizationUsageAsync(UserId, "gemini-3.5-flash-lite", 1800, 40);

        _usage.Verify(u => u.RecordCallAsync(It.Is<AiUsageEntry>(e =>
            e.UserId == UserId && e.Model == "gemini-3.5-flash-lite" && e.InputTokens == 1800 && e.OutputTokens == 40
            && !e.TokensEstimated && e.Cost == 0.00064m && e.RequestContext == AICategorizationService.UsageContext)), Times.Once);
    }

    [Fact]
    public async Task RecordCategorizationUsage_NoTokenCounts_RecordsNothing()
    {
        await CreateService().RecordCategorizationUsageAsync(UserId, "gemini-3.5-flash-lite", null, 40);

        _usage.Verify(u => u.RecordCallAsync(It.IsAny<AiUsageEntry>()), Times.Never);
    }

    [Fact]
    public async Task RecordCategorizationUsage_NoTrackerSupplied_IsANoOp()
    {
        var act = () => CreateService(withTrackers: false)
            .RecordCategorizationUsageAsync(UserId, "gemini-3.5-flash-lite", 10, 5);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordCategorizationUsage_TrackerThrows_DoesNotPropagate()
    {
        _usage.Setup(u => u.RecordCallAsync(It.IsAny<AiUsageEntry>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => CreateService().RecordCategorizationUsageAsync(UserId, "gemini-3.5-flash-lite", 10, 5);

        await act.Should().NotThrowAsync();
    }
}
