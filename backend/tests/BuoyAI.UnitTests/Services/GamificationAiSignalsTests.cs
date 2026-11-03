using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.MCP.Tools;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The AI-facing badge surface: oops-badge history (revocation-inclusive), the derived
/// behavioral signals, and the get_gamification_summary MCP tool that carries both.
/// </summary>
public class GamificationAiSignalsTests : IDisposable
{
  private readonly BuoyAIDbContext _context;
  private readonly GamificationService _service;

  public GamificationAiSignalsTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);

    var mockSpending = new Mock<IBudgetSpendingService>();
    mockSpending.Setup(x => x.GetCategorySpendingAsync(It.IsAny<int>()))
      .ReturnsAsync(new Dictionary<int, decimal>());

    _service = new GamificationService(
      _context,
      mockSpending.Object,
      Mock.Of<INotificationService>(),
      Mock.Of<IEmailService>(),
      new Mock<ILogger<GamificationService>>().Object);
  }

  public void Dispose() => _context.Dispose();

  [Fact]
  public async Task OopsHistory_IncludesRevokedRows_WithCountAndHeldState()
  {
    _context.Set<UserAchievement>().Add(new UserAchievement
    {
      UserId = 1,
      AchievementKey = AchievementDefinitions.TimeTraveler,
      Title = "Time Traveler",
      Category = "oops",
      TimesEarned = 4,
      EarnedAt = DateTime.UtcNow.AddDays(-40),
      LastEarnedAt = DateTime.UtcNow.AddDays(-3),
      RevokedAt = DateTime.UtcNow.AddDays(-1) // condition since fixed
    });
    await _context.SaveChangesAsync();

    var history = await _service.GetOopsBadgeHistoryAsync(1);

    var tt = history.Single(h => h.Key == AchievementDefinitions.TimeTraveler);
    tt.TimesEarned.Should().Be(4);
    tt.CurrentlyHeld.Should().BeFalse("revoked = fixed, but the history remains");
  }

  [Fact]
  public async Task OopsHistory_ExcludesPositiveAchievements()
  {
    await _service.AwardAsync(1, AchievementDefinitions.FirstBudget);
    await _service.AwardAsync(1, AchievementDefinitions.SelectiveAmnesia);

    var history = await _service.GetOopsBadgeHistoryAsync(1);

    history.Should().OnlyContain(h => AchievementDefinitions.IsNegative(h.Key));
    history.Should().ContainSingle(h => h.Key == AchievementDefinitions.SelectiveAmnesia);
  }

  [Theory]
  [InlineData(1, false)]
  [InlineData(2, true)]
  [InlineData(3, true)]
  [InlineData(7, true)]
  public void Signals_SelectiveAmnesia_ThresholdsAtTwoEarns(int times, bool expectSignal)
  {
    var signals = OopsBadgeSignals.Build(new List<(string, int, DateTime?, bool)>
    {
      (AchievementDefinitions.SelectiveAmnesia, times, DateTime.UtcNow, true)
    });

    if (expectSignal)
    {
      signals.Should().ContainSingle(s => s.Contains("rollover debt"));
    }
    else
    {
      signals.Should().BeEmpty();
    }
  }

  [Fact]
  public void Signals_HighRepeatAmnesia_RecommendsBudgetRework()
  {
    var signals = OopsBadgeSignals.Build(new List<(string, int, DateTime?, bool)>
    {
      (AchievementDefinitions.SelectiveAmnesia, 5, new DateTime(2026, 8, 30), true)
    });

    signals.Single().Should().Contain("5 times")
      .And.Contain("even if the user believes the budget is sound")
      .And.Contain("reworking");
  }

  [Fact]
  public void Signals_MetaBadges_EscalatePriority()
  {
    var signals = OopsBadgeSignals.Build(new List<(string, int, DateTime?, bool)>
    {
      (AchievementDefinitions.ChronicAmnesia, 1, DateTime.UtcNow, true),
      (AchievementDefinitions.TimeLord, 1, DateTime.UtcNow, true)
    });

    signals.Should().HaveCount(2);
    signals.Should().Contain(s => s.Contains("high priority"));
  }

  [Fact]
  public void GamificationReadTools_SitsInGamificationDomain()
  {
    McpToolExecutor.DomainForToolClass(nameof(GamificationReadTools))
      .Should().Be("gamification");
  }

  [Fact]
  public async Task Tool_SummaryJson_CarriesRepeatCountsAndSignals()
  {
    var gamification = new Mock<IGamificationService>();
    gamification.Setup(g => g.GetAchievementsAsync(7)).ReturnsAsync(new List<UserAchievementDto>
    {
      new() { AchievementKey = AchievementDefinitions.FirstBudget, Title = "First Budget", Points = 50, EarnedAt = DateTime.UtcNow }
    });
    gamification.Setup(g => g.GetStreaksAsync(7)).ReturnsAsync(new List<UserStreakDto>
    {
      new() { DisplayName = "Under Budget", CurrentCount = 6, BestCount = 12 }
    });
    gamification.Setup(g => g.GetOopsBadgeHistoryAsync(7)).ReturnsAsync(new List<OopsBadgeHistoryDto>
    {
      new()
      {
        Key = AchievementDefinitions.SelectiveAmnesia,
        Title = "Selective Amnesia",
        TimesEarned = 4,
        LastEarnedAt = new DateTime(2026, 8, 30),
        CurrentlyHeld = true
      }
    });

    var tool = new GamificationReadTools(
      gamification.Object, new Mock<ILogger<GamificationReadTools>>().Object);

    var json = await tool.GetGamificationSummaryAsync(7);

    json.Should().Contain("\"TimesEarned\": 4")
      .And.Contain("Selective Amnesia")
      .And.Contain("BehavioralSignals")
      .And.Contain("rollover debt")
      .And.Contain("Under Budget");
  }
}
