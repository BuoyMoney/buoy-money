using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The auto-allocation pool (sum of AllocationPercentage over a scope's active, uncompleted goals)
/// is only capped at 100% when a goal is created or edited. A completed goal drops out of the pool,
/// so its share can be handed to other goals; when that goal is later reopened (reset to zero, or a
/// withdrawal pulls it back under target) it rejoins with its old share and the pool exceeds 100%.
/// </summary>
public class SavingsAllocationPoolTests
{
  private readonly Mock<ISavingsRepository> _savingsRepository = new();
  private readonly Mock<IBudgetRepository> _budgetRepository = new();
  private readonly Mock<IUserRepository> _userRepository = new();

  private SavingsService CreateService() => new(
    _savingsRepository.Object,
    _budgetRepository.Object,
    _userRepository.Object,
    Mock.Of<IAccountRepository>(),
    Mock.Of<IFamilyAuthorizationService>(),
    Mock.Of<IAIInsightGenerationService>(),
    Mock.Of<ILogger<SavingsService>>(),
    Mock.Of<IGamificationEvaluationQueue>(),
    Mock.Of<IGamificationService>(),
    Mock.Of<ITransactionRepository>());

  private static SavingsGoal Goal(int id, decimal pct, bool completed = false) => new()
  {
    Id = id,
    UserId = 7,
    Type = SavingsGoalType.Personal,
    Name = $"Goal {id}",
    AllocationPercentage = pct,
    CurrentAmount = completed ? 1000m : 0m,
    TargetAmount = 1000m,
    IsActive = true,
    IsCompleted = completed,
  };

  [Fact]
  public async Task ProcessAutomaticAllocations_PoolOver100Percent_NeverHandsOutMoreThanTheLeftover()
  {
    // 60% + 40% + 50% = 150%: the state a reopened goal leaves behind.
    var goals = new List<SavingsGoal> { Goal(1, 60m), Goal(2, 40m), Goal(3, 50m) };
    var budget = new Budget
    {
      Id = 5,
      UserId = 7,
      Type = BudgetType.Personal,
      TotalIncome = 1000m,
      Categories = new List<BudgetCategory> { new() { AllocatedAmount = 700m } }, // leftover 300
    };
    var allocations = new List<SavingsAllocation>();

    _budgetRepository.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(budget);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(7)).ReturnsAsync(goals);
    foreach (var g in goals)
      _savingsRepository.Setup(r => r.GetByIdAsync(g.Id)).ReturnsAsync(g);
    _savingsRepository
      .Setup(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns((SavingsGoal _, SavingsAllocation a) => { allocations.Add(a); return Task.CompletedTask; });

    await CreateService().ProcessAutomaticAllocationsAsync(5);

    // Before the fix: 180 + 120 + 150 = 450 out of a 300 leftover.
    allocations.Sum(a => a.AllocatedAmount).Should().BeLessThanOrEqualTo(300m);
    // Shares stay proportional to what the user chose.
    allocations.Single(a => a.SavingsGoalId == 1).AllocatedAmount.Should().Be(120m);
    allocations.Single(a => a.SavingsGoalId == 2).AllocatedAmount.Should().Be(80m);
    allocations.Single(a => a.SavingsGoalId == 3).AllocatedAmount.Should().Be(100m);
  }

  [Fact]
  public async Task ProcessAutomaticAllocations_PoolAtOrUnder100Percent_IsUnchanged()
  {
    var goals = new List<SavingsGoal> { Goal(1, 60m), Goal(2, 30m) };
    var budget = new Budget
    {
      Id = 5,
      UserId = 7,
      Type = BudgetType.Personal,
      TotalIncome = 1000m,
      Categories = new List<BudgetCategory> { new() { AllocatedAmount = 700m } },
    };
    var allocations = new List<SavingsAllocation>();

    _budgetRepository.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(budget);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(7)).ReturnsAsync(goals);
    foreach (var g in goals)
      _savingsRepository.Setup(r => r.GetByIdAsync(g.Id)).ReturnsAsync(g);
    _savingsRepository
      .Setup(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns((SavingsGoal _, SavingsAllocation a) => { allocations.Add(a); return Task.CompletedTask; });

    await CreateService().ProcessAutomaticAllocationsAsync(5);

    allocations.Single(a => a.SavingsGoalId == 1).AllocatedAmount.Should().Be(180m);
    allocations.Single(a => a.SavingsGoalId == 2).AllocatedAmount.Should().Be(90m);
  }

  private List<SavingsAllocation> PersonalBudget(List<SavingsGoal> goals, decimal leftover)
  {
    var budget = new Budget
    {
      Id = 5,
      UserId = 7,
      Type = BudgetType.Personal,
      TotalIncome = 1000m,
      CurrentPeriodStart = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
      Categories = new List<BudgetCategory> { new() { AllocatedAmount = 1000m - leftover } },
    };
    var allocations = new List<SavingsAllocation>();
    _budgetRepository.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(budget);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(7)).ReturnsAsync(goals);
    foreach (var g in goals)
      _savingsRepository.Setup(r => r.GetByIdAsync(g.Id)).ReturnsAsync(g);
    _savingsRepository
      .Setup(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns((SavingsGoal _, SavingsAllocation a) => { allocations.Add(a); return Task.CompletedTask; });
    return allocations;
  }

  [Fact]
  public async Task ProcessAutomaticAllocations_RetryAfterAGoalWasPaidAndFinished_KeepsTheSameDivisor()
  {
    // 60% + 40% + 50% = 150% on a 300 leftover. Goal 1 was paid 120 this period, which finished
    // it, and then the run stopped. The retry must still divide by 150: 80 + 100, not 120 + 150.
    var paid = Goal(1, 60m, completed: true);
    paid.Allocations = new List<SavingsAllocation>
    {
      new() { SavingsGoalId = 1, BudgetId = 5, Source = "budget_surplus", AllocatedAmount = 120m,
              AllocationDate = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc) },
    };
    var allocations = PersonalBudget(new List<SavingsGoal> { paid, Goal(2, 40m), Goal(3, 50m) }, 300m);

    await CreateService().ProcessAutomaticAllocationsAsync(5);

    allocations.Single(a => a.SavingsGoalId == 2).AllocatedAmount.Should().Be(80m);
    allocations.Single(a => a.SavingsGoalId == 3).AllocatedAmount.Should().Be(100m);
    (120m + allocations.Sum(a => a.AllocatedAmount)).Should().BeLessThanOrEqualTo(300m);
  }

  [Fact]
  public async Task ProcessAutomaticAllocations_SharesRoundDownToTheCent_SoTheTotalNeverPassesTheLeftover()
  {
    // Three 50% goals on 100.01: each share is 33.3366..., which the database would store as 33.34.
    var allocations = PersonalBudget(new List<SavingsGoal> { Goal(1, 50m), Goal(2, 50m), Goal(3, 50m) }, 100.01m);

    await CreateService().ProcessAutomaticAllocationsAsync(5);

    allocations.Should().HaveCount(3).And.OnlyContain(a => a.AllocatedAmount == 33.33m);
    allocations.Sum(a => a.AllocatedAmount).Should().BeLessThanOrEqualTo(100.01m);
  }

  [Fact]
  public async Task ResetSavingsGoal_ReopeningACompletedGoal_DoesNotPushThePoolOver100Percent()
  {
    // Goal 3 finished at 50%. The user then gave its share away: goals 1 and 2 now hold 100%.
    var reopened = Goal(3, 50m, completed: true);
    var goals = new List<SavingsGoal> { Goal(1, 60m), Goal(2, 40m), reopened };

    _savingsRepository.Setup(r => r.GetByIdAsync(3)).ReturnsAsync(reopened);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(7)).ReturnsAsync(goals);
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);

    var result = await CreateService().ResetSavingsGoalAsync(3);

    result.CurrentAmount.Should().Be(0m);
    result.IsCompleted.Should().BeFalse();
    // Before the fix the pool became 150% and every later edit in it was refused.
    goals.Where(g => g.IsActive && !g.IsCompleted).Sum(g => g.AllocationPercentage)
      .Should().BeLessThanOrEqualTo(100m);
    reopened.AllocationPercentage.Should().Be(0m);
  }

  [Fact]
  public async Task ResetSavingsGoal_ReopeningWithRoomLeft_KeepsAsMuchShareAsFits()
  {
    var reopened = Goal(3, 50m, completed: true);
    var goals = new List<SavingsGoal> { Goal(1, 70m), reopened };

    _savingsRepository.Setup(r => r.GetByIdAsync(3)).ReturnsAsync(reopened);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(7)).ReturnsAsync(goals);
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);

    await CreateService().ResetSavingsGoalAsync(3);

    reopened.AllocationPercentage.Should().Be(30m);
  }

  // A purchase or an undone deposit can also take a finished goal back under target, and it
  // rejoins at its old share: goals 1-3 below hold 150%. Payouts scale that down, but every edit
  // used to be refused ("cannot exceed 100%"), locking the user out of their own goals.
  private List<SavingsGoal> PoolOver100()
  {
    var goals = new List<SavingsGoal> { Goal(1, 60m), Goal(2, 40m), Goal(3, 50m) };
    foreach (var g in goals)
      _savingsRepository.Setup(r => r.GetByIdAsync(g.Id)).ReturnsAsync(g);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(7)).ReturnsAsync(goals);
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);
    return goals;
  }

  private static UpdateSavingsGoalRequest Edit(string name, decimal pct) =>
    new() { Name = name, TargetAmount = 1000m, AllocationPercentage = pct };

  [Fact]
  public async Task UpdateSavingsGoal_PoolOver100_EditKeepingTheShare_IsAllowed()
  {
    PoolOver100();

    var result = await CreateService().UpdateSavingsGoalAsync(1, Edit("Emergency fund", 60m));

    result.Name.Should().Be("Emergency fund");
  }

  [Fact]
  public async Task UpdateSavingsGoal_PoolOver100_LoweringTheShare_IsAllowed()
  {
    var goals = PoolOver100();

    // Still over 100% afterwards (110%), but closer: allowed, since it doesn't raise anything.
    await CreateService().UpdateSavingsGoalAsync(3, Edit("Goal 3", 10m));

    goals.Sum(g => g.AllocationPercentage).Should().Be(110m);
  }

  [Fact]
  public async Task UpdateSavingsGoal_PoolOver100_RaisingTheShare_IsStillRefused()
  {
    PoolOver100();

    var raise = () => CreateService().UpdateSavingsGoalAsync(2, Edit("Goal 2", 41m));

    await raise.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot exceed 100%*");
  }

  [Fact]
  public async Task UpdateSavingsGoal_ReactivatingAGoalIntoAFullPool_IsRefused()
  {
    // Goals 1 and 2 hold 100%. Goal 3 is paused at 50%: switching it back on at the same share
    // would make 150%, so it's checked like a new share.
    var paused = Goal(3, 50m);
    paused.IsActive = false;
    var goals = new List<SavingsGoal> { Goal(1, 60m), Goal(2, 40m), paused };
    _savingsRepository.Setup(r => r.GetByIdAsync(3)).ReturnsAsync(paused);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(7)).ReturnsAsync(goals);
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);
    var request = Edit("Goal 3", 50m);
    request.IsActive = true;

    var reactivate = () => CreateService().UpdateSavingsGoalAsync(3, request);

    await reactivate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot exceed 100%*");
    paused.IsActive.Should().BeFalse();
  }

  [Fact]
  public async Task UpdateSavingsGoal_RaisingAFinishedGoalsTargetIntoAFullPool_IsRefused()
  {
    // Goal 3 finished at 50% (1000 of 1000) and its share went to goals 1 and 2. Raising its target
    // to 2000 reopens it, which would make the pool 150%.
    var finished = Goal(3, 50m, completed: true);
    var goals = new List<SavingsGoal> { Goal(1, 60m), Goal(2, 40m), finished };
    _savingsRepository.Setup(r => r.GetByIdAsync(3)).ReturnsAsync(finished);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(7)).ReturnsAsync(goals);
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);
    var request = Edit("Goal 3", 50m);
    request.TargetAmount = 2000m;

    var reopen = () => CreateService().UpdateSavingsGoalAsync(3, request);

    await reopen.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot exceed 100%*");
  }
}
