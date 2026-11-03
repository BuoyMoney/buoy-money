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

public class GamificationServiceTests : IDisposable
{
  private readonly BuoyAIDbContext _context;
  private readonly GamificationService _service;

  public GamificationServiceTests()
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

  private async Task SeedBudgetWithCategoryAsync(int userId, BudgetCategory category)
  {
    var budget = new Budget
    {
      UserId = userId,
      Name = "Test Budget",
      IsActive = true,
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = new DateTime(2026, 7, 1),
      CurrentPeriodEnd = new DateTime(2026, 7, 31),
      Categories = new List<BudgetCategory> { category }
    };
    _context.Budgets.Add(budget);
    await _context.SaveChangesAsync();
  }

  private async Task<bool> HasTimeTravelerAsync(int userId) =>
    await _context.Set<UserAchievement>()
      .AnyAsync(a => a.UserId == userId && a.AchievementKey == AchievementDefinitions.TimeTraveler);

  [Fact]
  public async Task CheckAndAwardAchievementsAsync_CarriedOverspendEatsWholeBudget_AwardsTimeTraveler()
  {
    // Arrange: rollover on, carried debt of -350 against a 300 budget → effective -50.
    var userId = 1;
    await SeedBudgetWithCategoryAsync(userId, new BudgetCategory
    {
      CategoryId = (int)TransactionCategory.Travel,
      IsIncome = false,
      AllowRollover = true,
      AllocatedAmount = 300m,
      RolloverBalance = -350m
    });

    // Act
    await _service.CheckAndAwardAchievementsAsync(userId);

    // Assert
    (await HasTimeTravelerAsync(userId)).Should().BeTrue();
    var earned = await _context.Set<UserAchievement>()
      .FirstAsync(a => a.AchievementKey == AchievementDefinitions.TimeTraveler);
    earned.Points.Should().Be(0); // cautionary achievements carry no points
  }

  [Fact]
  public async Task CheckAndAwardAchievementsAsync_OverspendWithinBudget_DoesNotAwardTimeTraveler()
  {
    // Arrange: overspent, but the carried debt (-50) leaves 250 of the 300 budget.
    var userId = 2;
    await SeedBudgetWithCategoryAsync(userId, new BudgetCategory
    {
      CategoryId = (int)TransactionCategory.Travel,
      IsIncome = false,
      AllowRollover = true,
      AllocatedAmount = 300m,
      RolloverBalance = -50m
    });

    // Act
    await _service.CheckAndAwardAchievementsAsync(userId);

    // Assert
    (await HasTimeTravelerAsync(userId)).Should().BeFalse();
  }

  [Fact]
  public async Task CheckAndAwardAchievementsAsync_RolloverDisabled_DoesNotAwardTimeTraveler()
  {
    // Arrange: effective would be negative, but rollover is off so no carryover applies.
    var userId = 3;
    await SeedBudgetWithCategoryAsync(userId, new BudgetCategory
    {
      CategoryId = (int)TransactionCategory.Travel,
      IsIncome = false,
      AllowRollover = false,
      AllocatedAmount = 300m,
      RolloverBalance = -350m
    });

    // Act
    await _service.CheckAndAwardAchievementsAsync(userId);

    // Assert
    (await HasTimeTravelerAsync(userId)).Should().BeFalse();
  }

  [Fact]
  public async Task GetAchievementCatalogAsync_ReturnsEveryDefinition()
  {
    var catalog = await _service.GetAchievementCatalogAsync(99);
    catalog.Should().HaveCount(AchievementDefinitions.All.Count);
  }

  [Fact]
  public async Task GetAchievementCatalogAsync_LockedGoodAchievement_IsMaskedSilhouette()
  {
    // User 99 has earned nothing.
    var catalog = await _service.GetAchievementCatalogAsync(99);
    var firstBudget = catalog.First(c => c.Key == AchievementDefinitions.FirstBudget);

    firstBudget.IsEarned.Should().BeFalse();
    firstBudget.IsNegative.Should().BeFalse();
    firstBudget.Title.Should().BeNull(); // hidden until earned
    firstBudget.Description.Should().BeNull();
    firstBudget.Icon.Should().BeNull();
    firstBudget.Points.Should().BeGreaterThan(0); // placeholder hint still available
  }

  [Fact]
  public async Task GetAchievementCatalogAsync_NegativeAchievement_IsAlwaysRevealed()
  {
    // Even unearned, cautionary achievements are fully described.
    var catalog = await _service.GetAchievementCatalogAsync(99);
    var timeTraveler = catalog.First(c => c.Key == AchievementDefinitions.TimeTraveler);

    timeTraveler.IsNegative.Should().BeTrue();
    timeTraveler.IsEarned.Should().BeFalse();
    timeTraveler.Title.Should().NotBeNullOrEmpty();
    timeTraveler.Description.Should().NotBeNullOrEmpty();
  }

  [Fact]
  public async Task GetAchievementCatalogAsync_EarnedAchievement_IsRevealedWithDate()
  {
    var userId = 42;
    _context.Set<UserAchievement>().Add(new UserAchievement
    {
      UserId = userId,
      AchievementKey = AchievementDefinitions.FirstBudget,
      Title = "First Budget",
      Description = "Created your first budget",
      Icon = "🎯",
      Category = "budget",
      Points = 50,
      EarnedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
      IsNew = false
    });
    await _context.SaveChangesAsync();

    var catalog = await _service.GetAchievementCatalogAsync(userId);
    var firstBudget = catalog.First(c => c.Key == AchievementDefinitions.FirstBudget);

    firstBudget.IsEarned.Should().BeTrue();
    firstBudget.Title.Should().Be("First Budget");
    firstBudget.EarnedAt.Should().NotBeNull();
  }

  private UserAchievement Earned(int userId, string key, string category, int points) => new()
  {
    UserId = userId,
    AchievementKey = key,
    Title = key,
    Category = category,
    Points = points,
    EarnedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    IsNew = false
  };

  [Fact]
  public async Task CheckAndAward_TimeTravelerConditionCleared_RevokesBadge()
  {
    // The overspend that earned Time Traveler is now fixed (rollover balance 0), so the
    // cautionary badge should be revoked.
    var userId = 50;
    await SeedBudgetWithCategoryAsync(userId, new BudgetCategory
    {
      CategoryId = (int)TransactionCategory.Travel,
      IsIncome = false,
      AllowRollover = true,
      AllocatedAmount = 300m,
      RolloverBalance = 0m // no longer overspent-into-next-month
    });
    _context.Set<UserAchievement>().Add(Earned(userId, AchievementDefinitions.TimeTraveler, "oops", 0));
    await _context.SaveChangesAsync();

    await _service.CheckAndAwardAchievementsAsync(userId);

    // Revocation flags the row (preserving TimesEarned) instead of deleting it,
    // so the badge is hidden from reads but the row survives.
    var row = await _context.Set<UserAchievement>()
      .SingleAsync(a => a.UserId == userId && a.AchievementKey == AchievementDefinitions.TimeTraveler);
    row.RevokedAt.Should().NotBeNull();
    (await _service.GetAchievementsAsync(userId))
      .Should().NotContain(a => a.AchievementKey == AchievementDefinitions.TimeTraveler);
  }

  [Fact]
  public async Task CheckAndAward_CountUpNoLongerHitting_RevokesCountUpChampion()
  {
    // A count-up category no longer hits its target (spend moved away), so Count-Up Champion
    // is revoked.
    var userId = 51;
    await SeedBudgetWithCategoryAsync(userId, new BudgetCategory
    {
      CategoryId = (int)TransactionCategory.Entertainment,
      IsIncome = false,
      AllocatedAmount = 200m,
      IsCountUpEnabled = true // spending is 0 (mocked) < 200 -> target not hit
    });
    _context.Set<UserAchievement>().Add(Earned(userId, AchievementDefinitions.CountUpChampion, "count_up", 75));
    await _context.SaveChangesAsync();

    await _service.CheckAndAwardAchievementsAsync(userId);

    var row = await _context.Set<UserAchievement>()
      .SingleAsync(a => a.UserId == userId && a.AchievementKey == AchievementDefinitions.CountUpChampion);
    row.RevokedAt.Should().NotBeNull();
    (await _service.GetAchievementsAsync(userId))
      .Should().NotContain(a => a.AchievementKey == AchievementDefinitions.CountUpChampion);
  }

  [Fact]
  public async Task CheckAndAward_MilestoneNotRevoked_EvenIfNoLongerQualifying()
  {
    // Milestones are permanent: $100 Club stays even though the user now has no savings.
    var userId = 52;
    _context.Set<UserAchievement>().Add(Earned(userId, AchievementDefinitions.HundredClub, "savings", 75));
    await _context.SaveChangesAsync();

    await _service.CheckAndAwardAchievementsAsync(userId);

    (await HasAsync(userId, AchievementDefinitions.HundredClub)).Should().BeTrue();
  }

  [Fact]
  public async Task GetAchievementCatalogAsync_AssignsTierByPoints_AndNullForOops()
  {
    var catalog = await _service.GetAchievementCatalogAsync(1);

    // Century Club (1000 pts) -> Platinum; First Budget (50 pts) -> Bronze.
    catalog.First(c => c.Key == AchievementDefinitions.CenturyClub).Tier.Should().Be("Platinum");
    catalog.First(c => c.Key == AchievementDefinitions.FirstBudget).Tier.Should().Be("Bronze");
    // Cautionary achievements are not tiered.
    catalog.First(c => c.Key == AchievementDefinitions.TimeTraveler).Tier.Should().BeNull();
  }

  [Fact]
  public async Task GetAchievementCatalogAsync_ComputesRarityAcrossEarners()
  {
    // Two users have achievements; both have FirstBudget, only one has QuizComplete.
    void Earn(int uid, string key) => _context.Set<UserAchievement>().Add(new UserAchievement
    {
      UserId = uid,
      AchievementKey = key,
      Title = key,
      Category = "x",
      Points = 0,
      EarnedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
      IsNew = false
    });
    Earn(1, AchievementDefinitions.FirstBudget);
    Earn(2, AchievementDefinitions.FirstBudget);
    Earn(2, AchievementDefinitions.QuizComplete);
    await _context.SaveChangesAsync();

    var catalog = await _service.GetAchievementCatalogAsync(1);
    // Population = 2 users with any achievement.
    catalog.First(c => c.Key == AchievementDefinitions.FirstBudget).PercentEarned.Should().Be(100);
    catalog.First(c => c.Key == AchievementDefinitions.QuizComplete).PercentEarned.Should().Be(50);
  }

  [Fact]
  public void GetProgress_AtLevelFloor_IsZero()
  {
    // 100 pts is exactly the Saver floor -> 0% through the Saver level.
    LevelDefinitions.GetProgress(100).Should().Be(0.0);
  }

  [Fact]
  public void GetProgress_MidLevel_IsFraction()
  {
    // Saver [100..300): 200 pts is halfway through the 200-pt span.
    LevelDefinitions.GetProgress(200).Should().BeApproximately(0.5, 0.0001);
  }

  [Fact]
  public void GetProgress_AtTopLevel_IsOne()
  {
    // Legend (5000) is the top level -> full bar, never a stuck 0 or 0.5.
    LevelDefinitions.GetProgress(6000).Should().Be(1.0);
  }

  // ── Budget-history achievements ──────────────────────────────────────────

  private async Task<int> SeedBudgetAsync(int userId)
  {
    var budget = new Budget
    {
      UserId = userId,
      Name = "Test Budget",
      IsActive = true,
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = new DateTime(2026, 7, 1),
      CurrentPeriodEnd = new DateTime(2026, 7, 31)
    };
    _context.Budgets.Add(budget);
    await _context.SaveChangesAsync();
    return budget.Id;
  }

  private void AddPeriodSnapshot(int budgetId, int monthIndex, decimal allocated, decimal spent)
  {
    _context.Set<BudgetPeriodSnapshot>().Add(new BudgetPeriodSnapshot
    {
      BudgetId = budgetId,
      PeriodStart = new DateTime(2026, monthIndex, 1),
      PeriodEnd = new DateTime(2026, monthIndex, 28),
      TotalAllocated = allocated,
      TotalSpent = spent,
      CreatedAt = new DateTime(2026, monthIndex, 28)
    });
  }

  private async Task<bool> HasAsync(int userId, string key) =>
    await _context.Set<UserAchievement>()
      .AnyAsync(a => a.UserId == userId && a.AchievementKey == key);

  [Fact]
  public async Task CheckAndAward_ThreeConsecutiveUnderBudgetPeriods_AwardsBudgetMaster()
  {
    var userId = 7;
    var budgetId = await SeedBudgetAsync(userId);
    AddPeriodSnapshot(budgetId, 4, allocated: 1000, spent: 800);
    AddPeriodSnapshot(budgetId, 5, allocated: 1000, spent: 950);
    AddPeriodSnapshot(budgetId, 6, allocated: 1000, spent: 700);
    await _context.SaveChangesAsync();

    await _service.CheckAndAwardAchievementsAsync(userId);

    (await HasAsync(userId, AchievementDefinitions.BudgetMaster)).Should().BeTrue();
  }

  [Fact]
  public async Task CheckAndAward_OverBudgetBreaksTheRun_DoesNotAwardBudgetMaster()
  {
    var userId = 8;
    var budgetId = await SeedBudgetAsync(userId);
    AddPeriodSnapshot(budgetId, 4, allocated: 1000, spent: 800); // under
    AddPeriodSnapshot(budgetId, 5, allocated: 1000, spent: 1200); // over — breaks the run
    AddPeriodSnapshot(budgetId, 6, allocated: 1000, spent: 700); // under (only 1 in a row)
    await _context.SaveChangesAsync();

    await _service.CheckAndAwardAchievementsAsync(userId);

    (await HasAsync(userId, AchievementDefinitions.BudgetMaster)).Should().BeFalse();
  }

  [Fact]
  public async Task CheckAndAward_AllCategoriesUnderInLatestPeriod_AwardsCategoryChampion()
  {
    var userId = 9;
    var budgetId = await SeedBudgetAsync(userId);
    var snapshot = new BudgetPeriodSnapshot
    {
      BudgetId = budgetId,
      PeriodStart = new DateTime(2026, 6, 1),
      PeriodEnd = new DateTime(2026, 6, 30),
      TotalAllocated = 500,
      TotalSpent = 400,
      CreatedAt = new DateTime(2026, 6, 30),
      CategorySnapshots = new List<BudgetPeriodCategorySnapshot>
      {
        new() { CategoryId = 1, CategoryName = "Rent", AllocatedAmount = 300, SpentAmount = 280, IsIncome = false },
        new() { CategoryId = 2, CategoryName = "Food", AllocatedAmount = 200, SpentAmount = 150, IsIncome = false },
      }
    };
    _context.Set<BudgetPeriodSnapshot>().Add(snapshot);
    await _context.SaveChangesAsync();

    await _service.CheckAndAwardAchievementsAsync(userId);

    (await HasAsync(userId, AchievementDefinitions.CategoryChampion)).Should().BeTrue();
  }

  [Fact]
  public async Task CheckAndAward_OneCategoryOverInLatestPeriod_DoesNotAwardCategoryChampion()
  {
    var userId = 10;
    var budgetId = await SeedBudgetAsync(userId);
    var snapshot = new BudgetPeriodSnapshot
    {
      BudgetId = budgetId,
      PeriodStart = new DateTime(2026, 6, 1),
      PeriodEnd = new DateTime(2026, 6, 30),
      TotalAllocated = 500,
      TotalSpent = 520,
      CreatedAt = new DateTime(2026, 6, 30),
      CategorySnapshots = new List<BudgetPeriodCategorySnapshot>
      {
        new() { CategoryId = 1, CategoryName = "Rent", AllocatedAmount = 300, SpentAmount = 280, IsIncome = false },
        new() { CategoryId = 2, CategoryName = "Food", AllocatedAmount = 200, SpentAmount = 240, IsIncome = false }, // over
      }
    };
    _context.Set<BudgetPeriodSnapshot>().Add(snapshot);
    await _context.SaveChangesAsync();

    await _service.CheckAndAwardAchievementsAsync(userId);

    (await HasAsync(userId, AchievementDefinitions.CategoryChampion)).Should().BeFalse();
  }

  [Fact]
  public async Task UpdateStreaks_MissedDayWithFreeze_ConsumesFreezeAndContinues()
  {
    // A missed day is covered by a banked freeze: the streak continues and the freeze
    // is spent, instead of resetting to 1.
    var userId = 11;
    await SeedStreakBudgetAsync(userId);
    var today = DateTime.UtcNow.Date;
    _context.Set<UserStreak>().Add(new UserStreak
    {
      UserId = userId,
      StreakType = "under_budget",
      CurrentCount = 5,
      BestCount = 5,
      StreakFreezes = 1,
      LastIncrementDate = today.AddDays(-2), // one day missed
      StreakStartDate = today.AddDays(-6),
      CreatedAt = today.AddDays(-6),
      UpdatedAt = today.AddDays(-2)
    });
    await _context.SaveChangesAsync();

    await _service.UpdateStreaksAsync(userId);

    var streak = await _context.Set<UserStreak>()
      .FirstAsync(s => s.UserId == userId && s.StreakType == "under_budget");
    streak.CurrentCount.Should().Be(6); // continued, not reset
    streak.StreakFreezes.Should().Be(0); // freeze consumed
  }

  [Fact]
  public async Task UpdateStreaks_MissedDayWithoutFreeze_Resets()
  {
    var userId = 12;
    await SeedStreakBudgetAsync(userId);
    var today = DateTime.UtcNow.Date;
    _context.Set<UserStreak>().Add(new UserStreak
    {
      UserId = userId,
      StreakType = "under_budget",
      CurrentCount = 5,
      BestCount = 5,
      StreakFreezes = 0,
      LastIncrementDate = today.AddDays(-2),
      StreakStartDate = today.AddDays(-6),
      CreatedAt = today.AddDays(-6),
      UpdatedAt = today.AddDays(-2)
    });
    await _context.SaveChangesAsync();

    await _service.UpdateStreaksAsync(userId);

    var streak = await _context.Set<UserStreak>()
      .FirstAsync(s => s.UserId == userId && s.StreakType == "under_budget");
    streak.CurrentCount.Should().Be(1); // no freeze -> broke and restarted
  }

  [Fact]
  public async Task UpdateStreaks_ReachingAFullWeek_EarnsAFreeze()
  {
    var userId = 13;
    await SeedStreakBudgetAsync(userId);
    var today = DateTime.UtcNow.Date;
    _context.Set<UserStreak>().Add(new UserStreak
    {
      UserId = userId,
      StreakType = "under_budget",
      CurrentCount = 6, // will hit 7 on this consecutive-day update
      BestCount = 6,
      StreakFreezes = 0,
      LastIncrementDate = today.AddDays(-1),
      StreakStartDate = today.AddDays(-6),
      CreatedAt = today.AddDays(-6),
      UpdatedAt = today.AddDays(-1)
    });
    await _context.SaveChangesAsync();

    await _service.UpdateStreaksAsync(userId);

    var streak = await _context.Set<UserStreak>()
      .FirstAsync(s => s.UserId == userId && s.StreakType == "under_budget");
    streak.CurrentCount.Should().Be(7);
    streak.StreakFreezes.Should().Be(1); // earned at the weekly milestone
  }

  [Fact]
  public async Task UpdateStreaks_FamilyMemberUnderFamilyBudget_AdvancesFamilyStreak()
  {
    var userId = 30;
    const int familyId = 7;
    _context.Set<User>().Add(new User
    {
      Id = userId,
      Email = "member@x.com",
      FamilyId = familyId
    });
    _context.Budgets.Add(new Budget
    {
      FamilyId = familyId,
      Name = "Family Budget",
      IsActive = true,
      IsPrimary = true,
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = new DateTime(2026, 7, 1),
      CurrentPeriodEnd = new DateTime(2026, 7, 31),
      Categories = new List<BudgetCategory>
      {
        new() { CategoryId = (int)TransactionCategory.Travel, IsIncome = false, AllocatedAmount = 800m }
      }
    });
    await _context.SaveChangesAsync();

    await _service.UpdateStreaksAsync(userId);

    var family = await _context.Set<UserStreak>()
      .FirstOrDefaultAsync(s => s.UserId == userId && s.StreakType == "family_under_budget");
    family.Should().NotBeNull();
    family!.CurrentCount.Should().Be(1);
  }

  [Fact]
  public async Task IncrementChallengeProgress_ReachingTarget_MarksCompleted()
  {
    var userId = 20;
    _context.Set<UserChallenge>().Add(new UserChallenge
    {
      UserId = userId,
      ChallengeType = "categorize_transactions",
      Title = "Categorization Champ",
      Description = "Categorize 5 transactions",
      TargetValue = 3,
      CurrentValue = 0,
      RewardPoints = 25,
      Status = "active",
      StartDate = DateTime.UtcNow,
      EndDate = DateTime.UtcNow.AddDays(1),
      CreatedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();

    await _service.IncrementChallengeProgressAsync(userId, "categorize_transactions");
    await _service.IncrementChallengeProgressAsync(userId, "categorize_transactions");
    var mid = await _context.Set<UserChallenge>().FirstAsync(c => c.UserId == userId);
    mid.CurrentValue.Should().Be(2);
    mid.Status.Should().Be("active");

    await _service.IncrementChallengeProgressAsync(userId, "categorize_transactions");
    var done = await _context.Set<UserChallenge>().FirstAsync(c => c.UserId == userId);
    done.CurrentValue.Should().Be(3);
    done.Status.Should().Be("completed");
  }

  [Fact]
  public async Task IncrementChallengeProgress_NoActiveChallenge_IsNoOp()
  {
    // No challenge seeded — should not throw.
    await _service.IncrementChallengeProgressAsync(99, "review_budget");
    (await _context.Set<UserChallenge>().AnyAsync()).Should().BeFalse();
  }

  private Task SeedStreakBudgetAsync(int userId) => SeedBudgetWithCategoryAsync(userId,
    new BudgetCategory
    {
      CategoryId = (int)TransactionCategory.Travel,
      IsIncome = false,
      AllocatedAmount = 500m
    });

  // ── No-spend day challenge ────────────────────────────────────────────────

  private static DateTime YesterdayUtc => DateTime.UtcNow.Date.AddDays(-1);

  private async Task<int> SeedAccountAsync(int userId)
  {
    var account = new Account
    {
      UserId = userId,
      Name = "Checking",
      Type = AccountType.Checking,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    };
    _context.Set<Account>().Add(account);
    await _context.SaveChangesAsync();
    return account.Id;
  }

  private async Task<UserChallenge> SeedNoSpendChallengeAsync(int userId, DateTime dayUtc)
  {
    var challenge = new UserChallenge
    {
      UserId = userId,
      ChallengeType = "no_spend_day",
      Title = "No-Spend Day",
      Description = "Get through today without any discretionary spending",
      TargetValue = 1,
      CurrentValue = 0,
      RewardPoints = 30,
      StartDate = dayUtc,
      EndDate = dayUtc.AddDays(1).AddSeconds(-1),
      Status = "active",
      CreatedAt = dayUtc
    };
    _context.Set<UserChallenge>().Add(challenge);
    await _context.SaveChangesAsync();
    return challenge;
  }

  private async Task SeedTransactionAsync(
    int accountId, DateTime date, TransactionCategory? category,
    TransactionType type = TransactionType.Debit)
  {
    _context.Set<Transaction>().Add(new Transaction
    {
      AccountId = accountId,
      PlaidTransactionId = Guid.NewGuid().ToString(),
      Amount = 25m,
      Type = type,
      Date = date,
      CategoryId = category.HasValue ? (int)category.Value : null,
      CreatedAt = date,
      UpdatedAt = date
    });
    await _context.SaveChangesAsync();
  }

  [Fact]
  public async Task UpdateStreaks_NoSpendDayEndedWithOnlyExemptTransactions_CompletesChallenge()
  {
    // Bill-like debits (Rent) and credits (Salary) are not discretionary spending, so a day
    // containing only those still counts as a no-spend day.
    var userId = 60;
    var accountId = await SeedAccountAsync(userId);
    await SeedNoSpendChallengeAsync(userId, YesterdayUtc);
    await SeedTransactionAsync(accountId, YesterdayUtc.AddHours(9), TransactionCategory.Rent);
    await SeedTransactionAsync(accountId, YesterdayUtc.AddHours(12), TransactionCategory.Salary, TransactionType.Credit);

    await _service.UpdateStreaksAsync(userId);

    var challenge = await _context.Set<UserChallenge>().FirstAsync(c => c.UserId == userId);
    challenge.Status.Should().Be("completed");
    challenge.CurrentValue.Should().Be(challenge.TargetValue);
    challenge.CompletedAt.Should().NotBeNull();
  }

  [Fact]
  public async Task UpdateStreaks_NoSpendDayEndedWithDiscretionarySpend_ExpiresChallenge()
  {
    var userId = 61;
    var accountId = await SeedAccountAsync(userId);
    await SeedNoSpendChallengeAsync(userId, YesterdayUtc);
    await SeedTransactionAsync(accountId, YesterdayUtc.AddHours(19), TransactionCategory.DiningOut);

    await _service.UpdateStreaksAsync(userId);

    var challenge = await _context.Set<UserChallenge>().FirstAsync(c => c.UserId == userId);
    challenge.Status.Should().Be("expired");
    challenge.CompletedAt.Should().BeNull();
  }

  [Fact]
  public async Task UpdateStreaks_NoSpendDayEndedWithUncategorizedSpend_ExpiresChallenge()
  {
    // Unknown spend is still spend — an uncategorized debit blocks completion.
    var userId = 62;
    var accountId = await SeedAccountAsync(userId);
    await SeedNoSpendChallengeAsync(userId, YesterdayUtc);
    await SeedTransactionAsync(accountId, YesterdayUtc.AddHours(15), category: null);

    await _service.UpdateStreaksAsync(userId);

    var challenge = await _context.Set<UserChallenge>().FirstAsync(c => c.UserId == userId);
    challenge.Status.Should().Be("expired");
  }

  [Fact]
  public async Task UpdateStreaks_NoSpendDayStillRunning_LeavesChallengeActive()
  {
    // The day isn't over yet, so nothing is settled — even with spend so far today.
    var userId = 63;
    var accountId = await SeedAccountAsync(userId);
    await SeedNoSpendChallengeAsync(userId, DateTime.UtcNow.Date);
    await SeedTransactionAsync(accountId, DateTime.UtcNow, TransactionCategory.DiningOut);

    await _service.UpdateStreaksAsync(userId);

    var challenge = await _context.Set<UserChallenge>().FirstAsync(c => c.UserId == userId);
    challenge.Status.Should().Be("active");
  }

  [Fact]
  public async Task GetDashboardData_EndedCleanNoSpendDay_IsLazilyCompletedOnLoad()
  {
    // The lazy dashboard-load path settles the challenge without the daily job running.
    var userId = 64;
    await SeedAccountAsync(userId);
    await SeedNoSpendChallengeAsync(userId, YesterdayUtc);

    await _service.GetDashboardDataAsync(userId);

    var challenge = await _context.Set<UserChallenge>().FirstAsync(c => c.UserId == userId);
    challenge.Status.Should().Be("completed");
  }

  // ── Activity streaks (login / categorization / savings_goal) ─────────────

  private async Task<UserStreak> GetStreakAsync(int userId, string type) =>
    await _context.Set<UserStreak>().FirstAsync(s => s.UserId == userId && s.StreakType == type);

  [Theory]
  [InlineData("login")]
  [InlineData("categorization")]
  [InlineData("savings_goal")]
  public async Task RecordDailyActivity_SameDayTwice_IncrementsOnlyOnce(string streakType)
  {
    var userId = 70;

    await _service.RecordDailyActivityAsync(userId, streakType);
    await _service.RecordDailyActivityAsync(userId, streakType);

    var streak = await GetStreakAsync(userId, streakType);
    streak.CurrentCount.Should().Be(1);
    streak.BestCount.Should().Be(1);
    streak.LastIncrementDate.Should().Be(DateTime.UtcNow.Date);
  }

  [Fact]
  public async Task RecordDailyActivity_ConsecutiveDay_IncrementsAndUpdatesBestCount()
  {
    var userId = 71;
    var today = DateTime.UtcNow.Date;
    _context.Set<UserStreak>().Add(new UserStreak
    {
      UserId = userId,
      StreakType = "categorization",
      CurrentCount = 5,
      BestCount = 5,
      LastIncrementDate = today.AddDays(-1),
      StreakStartDate = today.AddDays(-5),
      CreatedAt = today.AddDays(-5),
      UpdatedAt = today.AddDays(-1)
    });
    await _context.SaveChangesAsync();

    await _service.RecordDailyActivityAsync(userId, "categorization");

    var streak = await GetStreakAsync(userId, "categorization");
    streak.CurrentCount.Should().Be(6);
    streak.BestCount.Should().Be(6); // new high-water mark
  }

  [Fact]
  public async Task RecordDailyActivity_MissedDayWithFreeze_ConsumesFreezeAndContinues()
  {
    // Activity streaks follow the same freeze conventions as under_budget.
    var userId = 72;
    var today = DateTime.UtcNow.Date;
    _context.Set<UserStreak>().Add(new UserStreak
    {
      UserId = userId,
      StreakType = "savings_goal",
      CurrentCount = 5,
      BestCount = 8,
      StreakFreezes = 1,
      LastIncrementDate = today.AddDays(-2), // one day missed
      StreakStartDate = today.AddDays(-6),
      CreatedAt = today.AddDays(-6),
      UpdatedAt = today.AddDays(-2)
    });
    await _context.SaveChangesAsync();

    await _service.RecordDailyActivityAsync(userId, "savings_goal");

    var streak = await GetStreakAsync(userId, "savings_goal");
    streak.CurrentCount.Should().Be(6);
    streak.StreakFreezes.Should().Be(0);
    streak.BestCount.Should().Be(8); // best not beaten yet
  }

  [Fact]
  public async Task RecordDailyActivity_MissedDayWithoutFreeze_Resets()
  {
    var userId = 73;
    var today = DateTime.UtcNow.Date;
    _context.Set<UserStreak>().Add(new UserStreak
    {
      UserId = userId,
      StreakType = "login",
      CurrentCount = 9,
      BestCount = 9,
      StreakFreezes = 0,
      LastIncrementDate = today.AddDays(-3),
      StreakStartDate = today.AddDays(-11),
      CreatedAt = today.AddDays(-11),
      UpdatedAt = today.AddDays(-3)
    });
    await _context.SaveChangesAsync();

    await _service.RecordDailyActivityAsync(userId, "login");

    var streak = await GetStreakAsync(userId, "login");
    streak.CurrentCount.Should().Be(1);
    streak.BestCount.Should().Be(9); // best survives the break
  }

  [Fact]
  public async Task RecordDailyActivity_UnknownStreakType_IsNoOp()
  {
    await _service.RecordDailyActivityAsync(74, "interpretive_dance");
    (await _context.Set<UserStreak>().AnyAsync(s => s.UserId == 74)).Should().BeFalse();
  }

  [Fact]
  public async Task GetDashboardData_AdvancesLoginStreakOncePerDay()
  {
    var userId = 75;

    var first = await _service.GetDashboardDataAsync(userId);
    var second = await _service.GetDashboardDataAsync(userId);

    var streak = await GetStreakAsync(userId, "login");
    streak.CurrentCount.Should().Be(1); // two loads, one day → one increment
    second.ActiveStreaks.Should().Contain(s => s.StreakType == "login" && s.CurrentCount == 1);
  }
}
