using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class BudgetPeriodServiceTests : IDisposable
{
  private readonly Mock<ILogger<BudgetPeriodService>> _logger = new();
  private readonly Mock<IBudgetRepository> _budgetRepository = new();
  private readonly Mock<ITransactionRepository> _transactionRepository = new();
  private readonly Mock<ISavingsService> _savingsService = new();
  private readonly BuoyAIDbContext _dbContext;

  public BudgetPeriodServiceTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _dbContext = new BuoyAIDbContext(options);
  }

  public void Dispose()
  {
    _dbContext.Dispose();
  }

  private BudgetPeriodService CreateService()
  {
    return new BudgetPeriodService(
      _logger.Object,
      _budgetRepository.Object,
      _transactionRepository.Object,
      new Lazy<ISavingsService>(() => _savingsService.Object),
      _dbContext);
  }

  [Fact]
  public void CalculateCurrentPeriod_Weekly_ReturnsSundayThroughSaturday()
  {
    var service = CreateService();
    var referenceDate = new DateTime(2024, 7, 3, 0, 0, 0, DateTimeKind.Utc);

    var (start, end) = service.CalculateCurrentPeriod(BudgetPeriod.Weekly, referenceDate);

    start.Should().Be(new DateTime(2024, 6, 30, 0, 0, 0, DateTimeKind.Utc));
    end.Should().Be(new DateTime(2024, 7, 6, 0, 0, 0, DateTimeKind.Utc));
  }

  [Fact]
  public void CalculateCurrentPeriod_BiWeekly_AnchorsToReferenceMonday()
  {
    var service = CreateService();
    var referenceDate = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    var (start, end) = service.CalculateCurrentPeriod(BudgetPeriod.BiWeekly, referenceDate);

    start.Should().Be(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    end.Should().Be(new DateTime(2024, 1, 14, 0, 0, 0, DateTimeKind.Utc));
  }

  // #941 week-start setting: null keeps LEGACY behavior byte-identical (the two
  // tests above); a set value anchors BOTH weekly and bi-weekly to that day.
  [Fact]
  public void CalculateCurrentPeriod_Weekly_MondayStart_ReturnsMondayThroughSunday()
  {
    var service = CreateService();
    // Wed 2024-07-03 → Monday-anchored week is Mon 07-01 .. Sun 07-07.
    var referenceDate = new DateTime(2024, 7, 3, 0, 0, 0, DateTimeKind.Utc);

    var (start, end) = service.CalculateCurrentPeriod(BudgetPeriod.Weekly, referenceDate, weekStart: DayOfWeek.Monday);

    start.Should().Be(new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc));
    end.Should().Be(new DateTime(2024, 7, 7, 0, 0, 0, DateTimeKind.Utc));
  }

  [Fact]
  public void CalculateCurrentPeriod_Weekly_FridayStart_AnchorsToPayday()
  {
    var service = CreateService();
    // Wed 2024-07-03 → Friday-anchored week began Fri 06-28.
    var referenceDate = new DateTime(2024, 7, 3, 0, 0, 0, DateTimeKind.Utc);

    var (start, end) = service.CalculateCurrentPeriod(BudgetPeriod.Weekly, referenceDate, weekStart: DayOfWeek.Friday);

    start.Should().Be(new DateTime(2024, 6, 28, 0, 0, 0, DateTimeKind.Utc));
    end.Should().Be(new DateTime(2024, 7, 4, 0, 0, 0, DateTimeKind.Utc));
  }

  [Fact]
  public void CalculateCurrentPeriod_BiWeekly_SundayStart_UsesShiftedEpochGrid()
  {
    var service = CreateService();
    // Epoch shifts from Mon 2024-01-01 to the first Sunday on/after it (2024-01-07).
    // Wed 2024-01-10 falls in the 14-day period starting Sun 2024-01-07.
    var referenceDate = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    var (start, end) = service.CalculateCurrentPeriod(BudgetPeriod.BiWeekly, referenceDate, weekStart: DayOfWeek.Sunday);

    start.Should().Be(new DateTime(2024, 1, 7, 0, 0, 0, DateTimeKind.Utc));
    end.Should().Be(new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc));
  }

  [Fact]
  public void CalculateCurrentPeriod_BiWeekly_ExplicitMonday_MatchesLegacyAnchor()
  {
    var service = CreateService();
    var referenceDate = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    var legacy = service.CalculateCurrentPeriod(BudgetPeriod.BiWeekly, referenceDate);
    var explicitMonday = service.CalculateCurrentPeriod(BudgetPeriod.BiWeekly, referenceDate, weekStart: DayOfWeek.Monday);

    explicitMonday.Should().Be(legacy);
  }

  [Fact]
  public void CalculateNextPeriod_MonthlyMovesToFollowingMonth()
  {
    var service = CreateService();
    var currentPeriodEnd = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Utc);

    var (start, end) = service.CalculateNextPeriod(BudgetPeriod.Monthly, currentPeriodEnd);

    start.Should().Be(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));
    end.Should().Be(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc));
  }

  [Fact]
  public void GenerateEncouragementMessage_AllCategoriesUnderBudgetRewardsChampionMessage()
  {
    var service = CreateService();
    var summary = new BudgetPeriodSummaryDto
    {
      TotalSavings = 125m,
      CategorySummaries =
      {
        new BudgetCategorySummaryDto { WasUnderBudget = true },
        new BudgetCategorySummaryDto { WasUnderBudget = true }
      }
    };

    var message = service.GenerateEncouragementMessage(summary);

    message.Should().Contain("BUDGET CHAMPION");
  }

  [Fact]
  public void GenerateEncouragementMessage_NoSavingsEncouragesLearning()
  {
    var service = CreateService();
    var summary = new BudgetPeriodSummaryDto
    {
      TotalSavings = 0,
      CategorySummaries =
      {
        new BudgetCategorySummaryDto { WasUnderBudget = false },
        new BudgetCategorySummaryDto { WasUnderBudget = true }
      }
    };

    var message = service.GenerateEncouragementMessage(summary);

    message.Should().Contain("Every dollar has a purpose");
  }

  [Fact]
  public void ShouldResetBudget_WithoutOffset_UsesUtcDate()
  {
    var service = CreateService();
    var budget = new Budget
    {
      AutoReset = true,
      CurrentPeriodEnd = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc)
    };

    // UTC midnight May 1 — should reset with no offset
    service.ShouldResetBudget(budget, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)).Should().BeTrue();
    // April 30 UTC — should not reset
    service.ShouldResetBudget(budget, new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc)).Should().BeFalse();
  }

  [Fact]
  public void ShouldResetBudget_WithNegativeOffset_HoldsUntilLocalMidnight()
  {
    var service = CreateService();
    var budget = new Budget
    {
      AutoReset = true,
      CurrentPeriodEnd = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc)
    };

    // UTC-6 user: UTC 02:00 May 1 = April 30 at 20:00 local — still April, no reset yet
    service.ShouldResetBudget(budget, new DateTime(2026, 5, 1, 2, 0, 0, DateTimeKind.Utc), utcOffsetMinutes: -360)
      .Should().BeFalse();
    // UTC-6 user: UTC 06:00 May 1 = midnight May 1 local — reset fires
    service.ShouldResetBudget(budget, new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc), utcOffsetMinutes: -360)
      .Should().BeTrue();
  }

  [Fact]
  public void ShouldResetBudget_WithPositiveOffset_ResetsEarlierInUtc()
  {
    var service = CreateService();
    var budget = new Budget
    {
      AutoReset = true,
      CurrentPeriodEnd = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc)
    };

    // UTC+12 user: UTC 12:00 April 30 = midnight May 1 local — reset fires 12h before UTC midnight
    service.ShouldResetBudget(budget, new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc), utcOffsetMinutes: 720)
      .Should().BeTrue();
    // UTC+12 user: UTC 11:59 April 30 = April 30 at 23:59 local — no reset yet
    service.ShouldResetBudget(budget, new DateTime(2026, 4, 30, 11, 59, 0, DateTimeKind.Utc), utcOffsetMinutes: 720)
      .Should().BeFalse();
  }

  [Fact]
  public void CalculateCurrentPeriod_Monthly_WithNegativeOffset_UsesLocalDate()
  {
    var service = CreateService();
    // UTC-6 user at UTC 02:00 May 1 = April 30 at 20:00 local — should land in April's period
    var utcNow = new DateTime(2026, 5, 1, 2, 0, 0, DateTimeKind.Utc);

    var (start, end) = service.CalculateCurrentPeriod(BudgetPeriod.Monthly, utcNow, utcOffsetMinutes: -360);

    start.Should().Be(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
    end.Should().Be(new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc));
  }

  [Fact]
  public void CalculateCurrentPeriod_Monthly_WithPositiveOffset_WrapsIntoNextMonth()
  {
    var service = CreateService();
    // UTC+12 user at UTC 12:00 April 30 = midnight May 1 local — should land in May's period
    var utcNow = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

    var (start, end) = service.CalculateCurrentPeriod(BudgetPeriod.Monthly, utcNow, utcOffsetMinutes: 720);

    start.Should().Be(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
    end.Should().Be(new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc));
  }

  [Fact]
  public async Task ResetBudgetAsync_WithMixedCategories_UpdatesBudgetAndRollovers()
  {
    var service = CreateService();

    var budget = new Budget
    {
      Id = 42,
      UserId = 17,
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
      CurrentPeriodEnd = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Utc),
      AutoReset = true,
      Categories = new List<BudgetCategory>
      {
        new()
        {
          Id = 1,
          CategoryId = (int)TransactionCategory.Groceries,
          AllocatedAmount = 500m,
          AllowRollover = true,
          RolloverBalance = 50m,
          CategoryNav = new Category { Id = (int)TransactionCategory.Groceries, Name = "Groceries" }
        },
        new()
        {
          Id = 2,
          CategoryId = (int)TransactionCategory.Entertainment,
          AllocatedAmount = 200m,
          AllowRollover = false,
          RolloverBalance = 0m,
          CategoryNav = new Category { Id = (int)TransactionCategory.Entertainment, Name = "Entertainment" }
        }
      }
    };

    var transactions = new List<Transaction>
    {
      new()
      {
        Amount = -350m,
        Type = TransactionType.Debit,
        CategoryId = (int)TransactionCategory.Groceries,
        Date = new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc)
      },
      new()
      {
        Amount = -150m,
        Type = TransactionType.Debit,
        CategoryId = (int)TransactionCategory.Entertainment,
        Date = new DateTime(2024, 1, 21, 0, 0, 0, DateTimeKind.Utc)
      },
      new()
      {
        Amount = 1000m,
        Type = TransactionType.Credit,
        CategoryId = (int)TransactionCategory.OtherIncome,
        Date = new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc)
      }
    };

    _transactionRepository
      .Setup(repo => repo.GetByUserIdAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .ReturnsAsync(transactions);

    _budgetRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget updated) => updated);

    var summary = await service.ResetBudgetAsync(budget);

    summary.TotalSavings.Should().Be(50m);
    summary.TotalRollover.Should().Be(200m);
    summary.CategorySummaries.Should().HaveCount(2);
    summary.CategorySummaries.Should().ContainSingle(c => c.CategoryName == "Groceries" && c.RolledOver == 200m);
    summary.EncouragementMessage.Should().Contain("BUDGET CHAMPION");

    budget.CurrentPeriodStart.Should().Be(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));
    budget.CurrentPeriodEnd.Should().Be(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc));
    budget.LastReset.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

    budget.Categories.Should().ContainSingle(c => c.Id == 1 && c.RolloverBalance == 200m);
    budget.Categories.Should().ContainSingle(c => c.Id == 2 && c.RolloverBalance == 0m);

    _budgetRepository.Verify(repo => repo.UpdateAsync(It.Is<Budget>(b => b.Id == budget.Id)), Times.Once);
  }

  // A family budget's live screen counts every family member's spending
  // (BudgetSpendingService uses GetByUserOrFamilyAsync). The period-end reset used to
  // count only the owner's accounts, so a member's spending vanished from the period
  // summary and the rollover carried forward money that had already been spent.
  // The close counts what the Budgets screen counts (BudgetSpending): loaded across the family,
  // then the claim rule. A Family budget gets the unclaimed charges; a member's or child's budget
  // only the ones claimed to its owner, so no charge counts twice and nobody's spending lands on
  // someone else's budget.
  private Budget FamilyScopedBudget(BudgetType type, int categoryId, decimal allocated) => new()
  {
    Id = 77,
    UserId = 5,
    FamilyId = 9,
    Type = type,
    Period = BudgetPeriod.Monthly,
    CurrentPeriodStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    CurrentPeriodEnd = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Utc),
    AutoReset = true,
    Categories = new List<BudgetCategory>
    {
      new()
      {
        Id = 1,
        CategoryId = categoryId,
        AllocatedAmount = allocated,
        AllowRollover = true,
        RolloverBalance = 0m,
        CategoryNav = new Category { Id = categoryId, Name = "Spending" }
      }
    }
  };

  private static Transaction Charge(decimal amount, int? claimedBy, int day) => new()
  {
    Amount = -amount,
    Type = TransactionType.Debit,
    CategoryId = (int)TransactionCategory.Groceries,
    ClaimedByUserId = claimedBy,
    Date = new DateTime(2024, 1, day, 0, 0, 0, DateTimeKind.Utc)
  };

  private void FamilyCharges(params Transaction[] charges)
  {
    // The owner's own accounts alone (the old query) see only the first charge.
    _transactionRepository
      .Setup(repo => repo.GetByUserIdAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .ReturnsAsync(new List<Transaction> { charges[0] });
    _transactionRepository
      .Setup(repo => repo.GetByUserOrFamilyAsync(5, 9, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .ReturnsAsync(charges.ToList());
    _budgetRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget updated) => updated);
  }

  [Fact]
  public async Task ResetBudgetAsync_FamilyBudget_CountsUnclaimedSpendingAcrossTheFamily()
  {
    var budget = FamilyScopedBudget(BudgetType.Family, (int)TransactionCategory.Groceries, 500m);
    // The owner's $100 and another member's $300 are shared spend; $50 claimed to a child is theirs.
    FamilyCharges(Charge(100m, null, 10), Charge(300m, null, 12), Charge(50m, 6, 14));

    var summary = await CreateService().ResetBudgetAsync(budget);

    summary.CategorySummaries.Should().ContainSingle(c => c.Spent == 400m);
    budget.Categories.Should().ContainSingle(c => c.Id == 1 && c.RolloverBalance == 100m);
  }

  [Fact]
  public async Task ResetBudgetAsync_MembersOwnBudgetInAFamily_CountsOnlyChargesClaimedToThem()
  {
    var budget = FamilyScopedBudget(BudgetType.Personal, (int)TransactionCategory.Groceries, 150m);
    // $45 claimed to this member; the parent's unclaimed $150 and another child's $30 are not theirs.
    FamilyCharges(Charge(45m, 5, 10), Charge(150m, null, 12), Charge(30m, 6, 14));

    var summary = await CreateService().ResetBudgetAsync(budget);

    summary.CategorySummaries.Should().ContainSingle(c => c.Spent == 45m);
    budget.Categories.Should().ContainSingle(c => c.Id == 1 && c.RolloverBalance == 105m);
  }

  [Fact]
  public async Task ResetBudgetAsync_LoadsThePeriodsTransactionsOnce_NotOncePerCategory()
  {
    var budget = FamilyScopedBudget(BudgetType.Family, (int)TransactionCategory.Groceries, 500m);
    budget.Categories.Add(new BudgetCategory
    {
      Id = 2, CategoryId = (int)TransactionCategory.DiningOut, AllocatedAmount = 200m, AllowRollover = true,
      CategoryNav = new Category { Id = (int)TransactionCategory.DiningOut, Name = "Dining" }
    });
    budget.Categories.Add(new BudgetCategory
    {
      Id = 3, CategoryId = (int)TransactionCategory.Shopping, AllocatedAmount = 100m, AllowRollover = true,
      CategoryNav = new Category { Id = (int)TransactionCategory.Shopping, Name = "Shopping" }
    });
    FamilyCharges(Charge(100m, null, 10));

    await CreateService().ResetBudgetAsync(budget);

    _transactionRepository.Verify(repo => repo.GetByUserOrFamilyAsync(
      5, 9, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()), Times.Once);
  }

  [Fact]
  public async Task ResetBudgetAsync_Allowance_CountsTheChildsClaimedChargeUnderUncategorized()
  {
    // The shape found in production: an allowance with one Uncategorized line, a $45 charge claimed
    // to the child (not yet sub-categorized), and the parent's unclaimed $150 in the family pool.
    // The Budgets screen shows $105 left; the close used to carry $150 (missing the $45), and
    // counting the whole family would have carried -$45.
    var budget = FamilyScopedBudget(BudgetType.Allowance, (int)TransactionCategory.Uncategorized, 150m);
    FamilyCharges(Charge(150m, null, 10), Charge(45m, 5, 12));

    await CreateService().ResetBudgetAsync(budget);

    budget.Categories.Should().ContainSingle(c => c.Id == 1 && c.RolloverBalance == 105m);
  }

  // ── Regression: the "$11,105.82 saved" bug ─────────────────────────────
  //
  // A real household budget reported saving $11,105.82 in a month it overspent by
  // $2,246.05 — off by ~$13k, in the direction a budgeting app can least afford.
  // Two causes: the salary row counted as an $8,535 "saving", and every overspent
  // category was discarded because only positive variances were summed.

  [Fact]
  public void GenerateEncouragementMessage_Overspend_SaysSoInsteadOfCelebrating()
  {
    var service = CreateService();
    var summary = new BudgetPeriodSummaryDto
    {
      PeriodStart = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
      TotalSavings = -2246.05m,
      CategorySummaries =
      {
        new BudgetCategorySummaryDto { WasUnderBudget = false },
        new BudgetCategorySummaryDto { WasUnderBudget = true }
      }
    };

    var message = service.GenerateEncouragementMessage(summary);

    message.Should().Contain("$2,246.05");
    message.Should().Contain("more than you budgeted");
    // The old copy congratulated the user in exactly this situation.
    message.Should().NotContain("saved");
    message.Should().NotContain("CHAMPION");
  }

  [Fact]
  public void GenerateEncouragementMessage_NamesThePeriodItActuallyDescribes()
  {
    var service = CreateService();
    var summary = new BudgetPeriodSummaryDto
    {
      PeriodStart = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
      TotalSavings = 250m,
      CategorySummaries = { new BudgetCategorySummaryDto { WasUnderBudget = true } }
    };

    var message = service.GenerateEncouragementMessage(summary);

    // These summaries describe a COMPLETED period, so "this period" pointed at the month
    // on screen while quoting the previous one's numbers.
    message.Should().Contain("July");
    message.Should().NotContain("this period");
  }

  [Fact]
  public void GenerateEncouragementMessage_IncomeCategoriesDoNotCountTowardTheUnderBudgetTally()
  {
    var service = CreateService();
    var summary = new BudgetPeriodSummaryDto
    {
      PeriodStart = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
      TotalSavings = 100m,
      CategorySummaries =
      {
        // Income always looks "under budget" — nothing is ever spent from it.
        new BudgetCategorySummaryDto { CategoryName = "Salary", IsIncome = true, WasUnderBudget = true },
        new BudgetCategorySummaryDto { CategoryName = "Groceries", WasUnderBudget = true },
        new BudgetCategorySummaryDto { CategoryName = "Dining Out", WasUnderBudget = false }
      }
    };

    var message = service.GenerateEncouragementMessage(summary);

    // 1 of 2 SPENDING categories under budget — not 2 of 3, and nowhere near a champion month.
    message.Should().NotContain("CHAMPION");
    message.Should().NotContain("of 3");
  }

  [Fact]
  public void GenerateEncouragementMessage_IncomeOnlyBudget_DoesNotClaimAllCategoriesUnderBudget()
  {
    var service = CreateService();
    var summary = new BudgetPeriodSummaryDto
    {
      PeriodStart = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
      TotalSavings = 500m,
      CategorySummaries =
      {
        new BudgetCategorySummaryDto { CategoryName = "Salary", IsIncome = true, WasUnderBudget = true }
      }
    };

    var message = service.GenerateEncouragementMessage(summary);

    // With zero spending categories, 0 == 0 used to satisfy "under budget in ALL categories".
    message.Should().NotContain("ALL categories");
  }
}
