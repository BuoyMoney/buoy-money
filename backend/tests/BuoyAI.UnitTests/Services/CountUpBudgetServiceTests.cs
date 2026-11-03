using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The daily allowance must follow the category's current amount and period length. It used to be
/// frozen (amount / days) the moment count-up was switched on, so a later amount change, or a new
/// period of a different length, never reached it.
/// </summary>
public class CountUpBudgetServiceTests
{
  private readonly Mock<IBudgetRepository> _budgets = new();
  private readonly Mock<ITransactionRepository> _transactions = new();

  private CountUpBudgetService CreateService()
  {
    _transactions.Setup(t => t.GetByUserIdAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .ReturnsAsync(new List<Transaction>());
    return new CountUpBudgetService(_budgets.Object, _transactions.Object, Mock.Of<IFamilyAuthorizationService>(),
      NullLogger<CountUpBudgetService>.Instance, Mock.Of<IHttpContextAccessor>());
  }

  /// <summary>A 30-day period that contains today.</summary>
  private (Budget budget, BudgetCategory category) Arrange(decimal amount, decimal storedAllowance)
  {
    var start = DateTime.UtcNow.Date.AddDays(-5);
    var budget = new Budget { Id = 7, UserId = 3, CurrentPeriodStart = start, CurrentPeriodEnd = start.AddDays(29) };
    var category = new BudgetCategory
    {
      Id = 11, BudgetId = 7, CategoryId = 5, AllocatedAmount = amount,
      DailyAllowance = storedAllowance, IsCountUpEnabled = true,
    };
    _budgets.Setup(b => b.GetBudgetCategoryAsync(11)).ReturnsAsync(category);
    _budgets.Setup(b => b.GetByIdAsync(7)).ReturnsAsync(budget);
    _budgets.Setup(b => b.UpdateBudgetCategoryAsync(It.IsAny<BudgetCategory>())).ReturnsAsync((BudgetCategory c) => c);
    return (budget, category);
  }

  [Fact]
  public async Task EnableCountUpBudgetingAsync_StaleStoredAllowance_IsClearedAndDerivedFromAmount()
  {
    var (_, category) = Arrange(amount: 300m, storedAllowance: 5m);

    var status = await CreateService().EnableCountUpBudgetingAsync(11, true);

    category.DailyAllowance.Should().Be(0);
    status.DailyAllowance.Should().Be(10m); // 300 over 30 days
  }

  [Fact]
  public async Task CalculateCountUpStatusAsync_AfterAmountChange_UsesTheNewAmount()
  {
    var (_, category) = Arrange(amount: 300m, storedAllowance: 0m);
    var service = CreateService();
    (await service.CalculateCountUpStatusAsync(11)).DailyAllowance.Should().Be(10m);

    category.AllocatedAmount = 600m;

    (await service.CalculateCountUpStatusAsync(11)).DailyAllowance.Should().Be(20m);
  }

  [Fact]
  public async Task CalculateCountUpStatusAsync_NewPeriodLength_ChangesTheAllowance()
  {
    var (budget, _) = Arrange(amount: 310m, storedAllowance: 0m);
    budget.CurrentPeriodEnd = budget.CurrentPeriodStart.AddDays(30); // 31-day period

    (await CreateService().CalculateCountUpStatusAsync(11)).DailyAllowance.Should().Be(10m);
  }

  [Fact]
  public async Task CalculateCountUpStatusAsync_ExplicitCustomAllowance_IsKept()
  {
    Arrange(amount: 300m, storedAllowance: 7m);

    (await CreateService().CalculateCountUpStatusAsync(11)).DailyAllowance.Should().Be(7m);
  }

  private static Transaction Spend(decimal amount, int categoryId, int? claimedBy = null, bool pending = false) => new()
  {
    Amount = -amount,
    Type = TransactionType.Debit,
    CategoryId = categoryId,
    ClaimedByUserId = claimedBy,
    IsPending = pending,
    Date = DateTime.UtcNow.Date,
  };

  [Fact]
  public async Task CalculateCountUpStatusAsync_OwnerExcludesPending_LeavesPendingChargesOut()
  {
    // The Budgets screen and the month close leave pending charges out when the owner asks; the
    // card has to show the same number.
    var (budget, category) = Arrange(amount: 300m, storedAllowance: 0m);
    category.CategoryId = (int)TransactionCategory.Groceries;
    budget.User = new User { Id = 3, ExcludePendingFromBudget = true };
    var service = CreateService();
    _transactions.Setup(t => t.GetByUserIdAsync(3, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .ReturnsAsync(new List<Transaction>
      {
        Spend(30m, (int)TransactionCategory.Groceries),
        Spend(50m, (int)TransactionCategory.Groceries, pending: true),
      });

    (await service.CalculateCountUpStatusAsync(11)).ActualSpent.Should().Be(30m);
  }

  [Fact]
  public async Task CalculateCountUpStatusAsync_Allowance_StreaksSeeTheChargesTheBalanceCounts()
  {
    // An allowance counts a claimed charge with no sub-category under Uncategorized. The balance
    // counted it, but the zero-spend days looked at the charge's own category (Groceries) and
    // called today a no-spend day.
    var (budget, category) = Arrange(amount: 300m, storedAllowance: 0m);
    budget.Type = BudgetType.Allowance;
    budget.FamilyId = 9;
    category.CategoryId = (int)TransactionCategory.Uncategorized;
    var service = CreateService();
    _transactions.Setup(t => t.GetByUserOrFamilyAsync(3, 9, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
      .ReturnsAsync(new List<Transaction> { Spend(20m, (int)TransactionCategory.Groceries, claimedBy: 3) });

    var status = await service.CalculateCountUpStatusAsync(11);

    status.ActualSpent.Should().Be(20m);
    status.ZeroSpendingDays.Should().Be(status.DaysElapsed - 1);
  }
}
