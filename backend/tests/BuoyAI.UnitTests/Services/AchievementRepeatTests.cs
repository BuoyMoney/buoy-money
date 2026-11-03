using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Repeatable ("oops") achievements: TimesEarned counting, revocation-as-flag
/// (so the count survives), and threshold meta-badges (x10 unlocks).
/// </summary>
public class AchievementRepeatTests : IDisposable
{
  private readonly BuoyAIDbContext _context;
  private readonly GamificationService _service;

  public AchievementRepeatTests()
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

  private Task<UserAchievement?> RowAsync(int userId, string key) =>
    _context.Set<UserAchievement>()
      .FirstOrDefaultAsync(a => a.UserId == userId && a.AchievementKey == key);

  [Fact]
  public async Task AwardAsync_RepeatableBadge_IncrementsOneRow()
  {
    for (var i = 0; i < 3; i++)
    {
      await _service.AwardAsync(1, AchievementDefinitions.SelectiveAmnesia);
    }

    var rows = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == 1 && a.AchievementKey == AchievementDefinitions.SelectiveAmnesia)
      .ToListAsync();
    rows.Should().HaveCount(1);
    rows[0].TimesEarned.Should().Be(3);
    rows[0].LastEarnedAt.Should().NotBeNull();
  }

  [Fact]
  public async Task AwardAsync_SingleAwardBadge_StaysAtOne()
  {
    await _service.AwardAsync(1, AchievementDefinitions.FirstBudget);
    await _service.AwardAsync(1, AchievementDefinitions.FirstBudget);

    var row = await RowAsync(1, AchievementDefinitions.FirstBudget);
    row!.TimesEarned.Should().Be(1);
  }

  [Fact]
  public async Task AwardAsync_TenthSelectiveAmnesia_UnlocksChronicAmnesia_Once()
  {
    for (var i = 0; i < 11; i++)
    {
      await _service.AwardAsync(1, AchievementDefinitions.SelectiveAmnesia);
    }

    var metas = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == 1 && a.AchievementKey == AchievementDefinitions.ChronicAmnesia)
      .ToListAsync();
    metas.Should().HaveCount(1);
    metas[0].TimesEarned.Should().Be(1);

    (await RowAsync(1, AchievementDefinitions.SelectiveAmnesia))!
      .TimesEarned.Should().Be(11);
  }

  [Fact]
  public async Task AwardAsync_NinthEarn_DoesNotUnlockMeta()
  {
    for (var i = 0; i < 9; i++)
    {
      await _service.AwardAsync(1, AchievementDefinitions.SelectiveAmnesia);
    }

    (await RowAsync(1, AchievementDefinitions.ChronicAmnesia)).Should().BeNull();
  }

  private async Task SeedTimeTravelerConditionAsync(int userId, decimal rolloverBalance)
  {
    var budget = new Budget
    {
      UserId = userId,
      Name = "Test Budget",
      IsActive = true,
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = new DateTime(2026, 8, 1),
      CurrentPeriodEnd = new DateTime(2026, 8, 31),
      Categories = new List<BudgetCategory>
      {
        new()
        {
          CategoryId = (int)TransactionCategory.Travel,
          IsIncome = false,
          AllowRollover = true,
          AllocatedAmount = 300m,
          RolloverBalance = rolloverBalance
        }
      }
    };
    _context.Budgets.Add(budget);
    await _context.SaveChangesAsync();
  }

  [Fact]
  public async Task Revocation_KeepsRowAndCount_ReEarnIncrements()
  {
    const int userId = 7;
    await SeedTimeTravelerConditionAsync(userId, -350m);

    // Earn.
    await _service.CheckAndAwardAchievementsAsync(userId);
    var row = await RowAsync(userId, AchievementDefinitions.TimeTraveler);
    row!.RevokedAt.Should().BeNull();
    row.TimesEarned.Should().Be(1);

    // Condition stops holding: revoked (flagged, not deleted).
    var category = await _context.Set<BudgetCategory>().FirstAsync();
    category.RolloverBalance = 0m;
    await _context.SaveChangesAsync();
    await _service.CheckAndAwardAchievementsAsync(userId);

    row = await RowAsync(userId, AchievementDefinitions.TimeTraveler);
    row!.RevokedAt.Should().NotBeNull();
    (await _service.GetAchievementsAsync(userId))
      .Should().NotContain(a => a.AchievementKey == AchievementDefinitions.TimeTraveler);

    // Condition holds again: same row un-revokes with the count bumped.
    category.RolloverBalance = -400m;
    await _context.SaveChangesAsync();
    await _service.CheckAndAwardAchievementsAsync(userId);

    row = await RowAsync(userId, AchievementDefinitions.TimeTraveler);
    row!.RevokedAt.Should().BeNull();
    row.TimesEarned.Should().Be(2);

    var all = await _context.Set<UserAchievement>()
      .Where(a => a.UserId == userId && a.AchievementKey == AchievementDefinitions.TimeTraveler)
      .ToListAsync();
    all.Should().HaveCount(1);
  }

  [Fact]
  public async Task Catalog_CarriesTimesEarned_ForRepeatableBadges()
  {
    await _service.AwardAsync(1, AchievementDefinitions.SelectiveAmnesia);
    await _service.AwardAsync(1, AchievementDefinitions.SelectiveAmnesia);

    var catalog = await _service.GetAchievementCatalogAsync(1);
    var amnesia = catalog.Single(c => c.Key == AchievementDefinitions.SelectiveAmnesia);
    amnesia.IsEarned.Should().BeTrue();
    amnesia.TimesEarned.Should().Be(2);
  }
}
