using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class SavingsServiceTests
{
  private readonly Mock<ISavingsRepository> _savingsRepository = new();
  private readonly Mock<IBudgetRepository> _budgetRepository = new();
  private readonly Mock<IUserRepository> _userRepository = new();
  private readonly Mock<IAccountRepository> _accountRepository = new();
  private readonly Mock<IFamilyAuthorizationService> _familyAuthService = new();
  private readonly Mock<IAIInsightGenerationService> _aiInsightService = new();
  private readonly Mock<ILogger<SavingsService>> _logger = new();
  private readonly Mock<IGamificationEvaluationQueue> _gamificationQueue = new();
  private readonly Mock<ITransactionRepository> _transactionRepository = new();

  private SavingsService CreateService()
  {
    return new SavingsService(
      _savingsRepository.Object,
      _budgetRepository.Object,
      _userRepository.Object,
      _accountRepository.Object,
      _familyAuthService.Object,
      _aiInsightService.Object,
      _logger.Object,
      _gamificationQueue.Object,
      Mock.Of<IGamificationService>(),
      _transactionRepository.Object);
  }

  [Fact]
  public async Task GetSavingsOverviewAsync_ShouldAggregateMetrics()
  {
    var service = CreateService();
    var userId = 101;
    var now = DateTime.UtcNow;

    var goals = new List<SavingsGoal>
    {
      new()
      {
        Id = 1,
        UserId = userId,
        Name = "Emergency",
        TargetAmount = 1000m,
        CurrentAmount = 200m,
        AllocationPercentage = 60m,
        TargetDate = null,
        IsActive = true,
        IsCompleted = false,
        CreatedAt = now.AddDays(-10)
      },
      new()
      {
        Id = 2,
        UserId = userId,
        Name = "Vacation",
        TargetAmount = 800m,
        CurrentAmount = 400m,
        AllocationPercentage = 20m,
        TargetDate = now.AddDays(90),
        IsActive = true,
        IsCompleted = false,
        CreatedAt = now.AddDays(-20)
      },
      new()
      {
        Id = 3,
        UserId = userId,
        Name = "Old Goal",
        TargetAmount = 500m,
        CurrentAmount = 500m,
        AllocationPercentage = 20m,
        TargetDate = now.AddDays(-10),
        IsActive = false,
        IsCompleted = true,
        CreatedAt = now.AddMonths(-6)
      }
    };

    var budget = new Budget
    {
      Id = 55,
      UserId = userId,
      TotalIncome = 2000m,
      Categories = new List<BudgetCategory>
      {
        new() { AllocatedAmount = 1200m },
        new() { AllocatedAmount = 300m }
      }
    };

    _savingsRepository.Setup(repo => repo.GetByUserOrFamilyAsync(userId, null)).ReturnsAsync(goals);
    _savingsRepository.Setup(repo => repo.GetTotalSavedAmountAsync(userId)).ReturnsAsync(600m);
    _savingsRepository.Setup(repo => repo.GetMonthlyGrowthAsync(userId)).ReturnsAsync(75m);
    _savingsRepository.Setup(repo => repo.GetActiveGoalsCountAsync(userId)).ReturnsAsync(2);
    _savingsRepository.Setup(repo => repo.GetCompletedGoalsCountAsync(userId)).ReturnsAsync(1);

    _userRepository.Setup(repo => repo.GetByIdAsync(userId)).ReturnsAsync((User?)null);

    _budgetRepository.Setup(repo => repo.GetActivePersonalBudgetAsync(userId)).ReturnsAsync(budget);
    _budgetRepository.Setup(repo => repo.GetByIdAsync(budget.Id)).ReturnsAsync(budget);

    var overview = await service.GetSavingsOverviewAsync(userId);

    overview.TotalSaved.Should().Be(600m);
    overview.MonthlyGrowth.Should().Be(75m);
    overview.CurrentPeriodLeftover.Should().Be(500m);
    overview.ProjectedMonthlyGrowth.Should().Be(400m);
    overview.TotalAllocatedPerPeriod.Should().Be(400m);
    overview.UnallocatedAmount.Should().Be(100m);
    overview.ActiveGoalsCount.Should().Be(2);
    overview.CompletedGoalsCount.Should().Be(1);
    overview.Goals.Should().HaveCount(3);
    // Recent achievements now come from the unified gamification system (mocked empty here).
    overview.RecentAchievements.Should().BeEmpty();

    var emergencyDto = overview.Goals.Single(g => g.Id == 1);
    emergencyDto.ProgressPercentage.Should().Be(20m);
    emergencyDto.RemainingAmount.Should().Be(800m);
    emergencyDto.IsOnTrack.Should().BeTrue();
    emergencyDto.ProgressStatus.Should().Be("on_track");
  }

  [Fact]
  public async Task CreateSavingsGoalAsync_ShouldThrowWhenAllocationExceedsLimit()
  {
    var service = CreateService();
    var userId = 77;
    var existingGoals = new List<SavingsGoal>
    {
      new()
      {
        Id = 1,
        UserId = userId,
        AllocationPercentage = 80m,
        IsActive = true,
        IsCompleted = false
      }
    };

    _savingsRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(existingGoals);

    var request = new CreateSavingsGoalRequest
    {
      Name = "Travel Fund",
      Description = "Trip to the coast",
      TargetAmount = 1500m,
      AllocationPercentage = 30m
    };

    var act = () => service.CreateSavingsGoalAsync(userId, request);

    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("Total allocation percentage for personal goals cannot exceed 100%.");

    _savingsRepository.Verify(repo => repo.CreateAsync(It.IsAny<SavingsGoal>()), Times.Never);
  }

  [Fact]
  public async Task ProcessAutomaticAllocationsAsync_ShouldAllocateToActiveGoals()
  {
    var service = CreateService();
    var budgetId = 15;
    var userId = 42;

    var budget = new Budget
    {
      Id = budgetId,
      UserId = userId,
      TotalIncome = 1000m,
      Categories = new List<BudgetCategory>
      {
        new() { AllocatedAmount = 600m }
      }
    };

    var activeGoal = new SavingsGoal
    {
      Id = 1,
      UserId = userId,
      AllocationPercentage = 50m,
      CurrentAmount = 100m,
      TargetAmount = 500m,
      IsActive = true,
      IsCompleted = false
    };

    var completedGoal = new SavingsGoal
    {
      Id = 2,
      UserId = userId,
      AllocationPercentage = 25m,
      CurrentAmount = 200m,
      TargetAmount = 200m,
      IsActive = true,
      IsCompleted = true
    };

    var inactiveGoal = new SavingsGoal
    {
      Id = 3,
      UserId = userId,
      AllocationPercentage = 25m,
      CurrentAmount = 50m,
      TargetAmount = 300m,
      IsActive = false,
      IsCompleted = false
    };

    var goals = new List<SavingsGoal> { activeGoal, completedGoal, inactiveGoal };
    var allocations = new List<SavingsAllocation>();

    _budgetRepository.Setup(repo => repo.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _savingsRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(goals);
    // Atomic path: each active goal is reloaded by id then persisted via SaveGoalWithAllocationAsync
    // (one ledger row + balance update in a single transaction).
    _savingsRepository.Setup(repo => repo.GetByIdAsync(activeGoal.Id)).ReturnsAsync(activeGoal);
    _savingsRepository
      .Setup(repo => repo.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns((SavingsGoal _, SavingsAllocation allocation) =>
      {
        allocations.Add(allocation);
        return Task.CompletedTask;
      });

    _savingsRepository.Setup(repo => repo.GetActiveGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(repo => repo.GetCompletedGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(repo => repo.GetTotalSavedAmountAsync(userId)).ReturnsAsync(0m);

    await service.ProcessAutomaticAllocationsAsync(budgetId);

    allocations.Should().HaveCount(1);
    allocations[0].SavingsGoalId.Should().Be(activeGoal.Id);
    allocations[0].AllocatedAmount.Should().Be(200m);
    allocations[0].LeftoverBudget.Should().Be(400m);
    allocations[0].Source.Should().Be("budget_surplus");

    activeGoal.CurrentAmount.Should().Be(300m);
    activeGoal.IsCompleted.Should().BeFalse();

    // Only the active goal is persisted; completed/inactive goals are never touched.
    _savingsRepository.Verify(repo => repo.SaveGoalWithAllocationAsync(activeGoal, It.IsAny<SavingsAllocation>()), Times.Once);
    _savingsRepository.Verify(repo => repo.SaveGoalWithAllocationAsync(It.Is<SavingsGoal>(g => g.Id != activeGoal.Id), It.IsAny<SavingsAllocation>()), Times.Never);
  }

  [Fact]
  public async Task ProcessAutomaticAllocationsAsync_PersonalBudget_OnlyFundsPersonalGoals_NoOverAllocation()
  {
    // Regression: a Personal budget's leftover must only fund Personal goals. Personal- and
    // family-goal AllocationPercentages each sum to their own 100% pool, so applying every
    // active goal's percentage to one budget's leftover over-allocates (up to ~200%) and
    // cross-funds the other scope. With 50% personal + 50% family on a $400 leftover, only
    // the personal goal should be funded and total allocated == 50% of leftover ($200).
    var service = CreateService();
    var budgetId = 77;
    var userId = 99;

    var budget = new Budget
    {
      Id = budgetId,
      UserId = userId,
      Type = BudgetType.Personal,
      TotalIncome = 1000m,
      Categories = new List<BudgetCategory>
      {
        new() { AllocatedAmount = 600m } // leftover = 400
      }
    };

    var personalGoal = new SavingsGoal
    {
      Id = 1,
      UserId = userId,
      Type = SavingsGoalType.Personal,
      AllocationPercentage = 50m,
      CurrentAmount = 0m,
      TargetAmount = 1000m,
      IsActive = true,
      IsCompleted = false
    };

    var familyGoal = new SavingsGoal
    {
      Id = 2,
      UserId = userId,
      Type = SavingsGoalType.Family,
      AllocationPercentage = 50m,
      CurrentAmount = 0m,
      TargetAmount = 1000m,
      IsActive = true,
      IsCompleted = false
    };

    var goals = new List<SavingsGoal> { personalGoal, familyGoal };
    var allocations = new List<SavingsAllocation>();

    _budgetRepository.Setup(repo => repo.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _savingsRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(goals);
    _savingsRepository.Setup(repo => repo.GetByIdAsync(personalGoal.Id)).ReturnsAsync(personalGoal);
    _savingsRepository.Setup(repo => repo.GetByIdAsync(familyGoal.Id)).ReturnsAsync(familyGoal);
    _savingsRepository
      .Setup(repo => repo.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns((SavingsGoal _, SavingsAllocation allocation) =>
      {
        allocations.Add(allocation);
        return Task.CompletedTask;
      });

    await service.ProcessAutomaticAllocationsAsync(budgetId);

    // Only the personal goal is funded; the family goal is untouched.
    allocations.Should().HaveCount(1);
    allocations[0].SavingsGoalId.Should().Be(personalGoal.Id);
    allocations[0].AllocatedAmount.Should().Be(200m); // 50% of 400 leftover

    personalGoal.CurrentAmount.Should().Be(200m);
    familyGoal.CurrentAmount.Should().Be(0m);

    // Total allocated == 50% of leftover, NOT 100%+ (the over-allocation bug).
    allocations.Sum(a => a.AllocatedAmount).Should().Be(200m);

    _savingsRepository.Verify(repo => repo.SaveGoalWithAllocationAsync(personalGoal, It.IsAny<SavingsAllocation>()), Times.Once);
    _savingsRepository.Verify(repo => repo.SaveGoalWithAllocationAsync(familyGoal, It.IsAny<SavingsAllocation>()), Times.Never);
  }

  [Fact]
  public async Task ProcessAutomaticAllocationsAsync_FamilyBudget_FundsAllMembersFamilyGoals()
  {
    // Finding 4: a Family budget's leftover must fund EVERY member's family goals, not just the
    // budget owner's. GetByUserIdAsync would return only the owner's goal; the family-aware query
    // returns the co-member's goal too, so both get funded.
    var service = CreateService();
    var budgetId = 55;
    var ownerId = 100;
    var familyId = 7;

    var budget = new Budget
    {
      Id = budgetId,
      UserId = ownerId,
      FamilyId = familyId,
      Type = BudgetType.Family,
      TotalIncome = 1000m,
      CurrentPeriodStart = DateTime.UtcNow.Date.AddDays(-5),
      CurrentPeriodEnd = DateTime.UtcNow.Date.AddDays(25),
      Categories = new List<BudgetCategory> { new() { AllocatedAmount = 600m } } // leftover = 400
    };

    var ownerGoal = new SavingsGoal
    {
      Id = 1, UserId = ownerId, FamilyId = familyId, Type = SavingsGoalType.Family,
      AllocationPercentage = 40m, CurrentAmount = 0m, TargetAmount = 1000m,
      IsActive = true, IsCompleted = false, Allocations = new List<SavingsAllocation>()
    };
    var memberGoal = new SavingsGoal
    {
      Id = 2, UserId = 101, FamilyId = familyId, Type = SavingsGoalType.Family,
      AllocationPercentage = 60m, CurrentAmount = 0m, TargetAmount = 1000m,
      IsActive = true, IsCompleted = false, Allocations = new List<SavingsAllocation>()
    };

    var goals = new List<SavingsGoal> { ownerGoal, memberGoal };
    var allocations = new List<SavingsAllocation>();

    _budgetRepository.Setup(repo => repo.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _savingsRepository.Setup(repo => repo.GetByUserOrFamilyAsync(ownerId, familyId)).ReturnsAsync(goals);
    _savingsRepository.Setup(repo => repo.GetByIdAsync(ownerGoal.Id)).ReturnsAsync(ownerGoal);
    _savingsRepository.Setup(repo => repo.GetByIdAsync(memberGoal.Id)).ReturnsAsync(memberGoal);
    _savingsRepository
      .Setup(repo => repo.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns((SavingsGoal _, SavingsAllocation allocation) => { allocations.Add(allocation); return Task.CompletedTask; });

    await service.ProcessAutomaticAllocationsAsync(budgetId);

    allocations.Should().HaveCount(2);
    ownerGoal.CurrentAmount.Should().Be(160m);   // 40% of 400
    memberGoal.CurrentAmount.Should().Be(240m);  // 60% of 400
    _savingsRepository.Verify(repo => repo.GetByUserIdAsync(It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task ProcessAutomaticAllocationsAsync_SkipsGoalAlreadyAllocatedThisPeriod()
  {
    // Finding 2: the public endpoint has no snapshot gate, so a repeat call must not stack a second
    // budget_surplus allocation for a goal already funded THIS period. A goal funded in a PRIOR
    // period is still eligible.
    var service = CreateService();
    var budgetId = 88;
    var userId = 200;
    var periodStart = DateTime.UtcNow.Date.AddDays(-3);

    var budget = new Budget
    {
      Id = budgetId,
      UserId = userId,
      Type = BudgetType.Personal,
      TotalIncome = 1000m,
      CurrentPeriodStart = periodStart,
      CurrentPeriodEnd = periodStart.AddMonths(1),
      Categories = new List<BudgetCategory> { new() { AllocatedAmount = 600m } } // leftover = 400
    };

    var alreadyFunded = new SavingsGoal
    {
      Id = 1, UserId = userId, Type = SavingsGoalType.Personal,
      AllocationPercentage = 50m, CurrentAmount = 200m, TargetAmount = 1000m,
      IsActive = true, IsCompleted = false,
      Allocations = new List<SavingsAllocation>
      {
        new() { SavingsGoalId = 1, BudgetId = budgetId, Source = "budget_surplus", AllocationDate = periodStart.AddDays(1) }
      }
    };
    var priorPeriodOnly = new SavingsGoal
    {
      Id = 2, UserId = userId, Type = SavingsGoalType.Personal,
      AllocationPercentage = 25m, CurrentAmount = 100m, TargetAmount = 1000m,
      IsActive = true, IsCompleted = false,
      Allocations = new List<SavingsAllocation>
      {
        new() { SavingsGoalId = 2, BudgetId = budgetId, Source = "budget_surplus", AllocationDate = periodStart.AddDays(-40) }
      }
    };

    var goals = new List<SavingsGoal> { alreadyFunded, priorPeriodOnly };
    var allocations = new List<SavingsAllocation>();

    _budgetRepository.Setup(repo => repo.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _savingsRepository.Setup(repo => repo.GetByUserIdAsync(userId)).ReturnsAsync(goals);
    _savingsRepository.Setup(repo => repo.GetByIdAsync(priorPeriodOnly.Id)).ReturnsAsync(priorPeriodOnly);
    _savingsRepository
      .Setup(repo => repo.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns((SavingsGoal _, SavingsAllocation allocation) => { allocations.Add(allocation); return Task.CompletedTask; });

    await service.ProcessAutomaticAllocationsAsync(budgetId);

    // Only the prior-period goal is funded again; the one already funded this period is skipped.
    allocations.Should().ContainSingle();
    allocations[0].SavingsGoalId.Should().Be(priorPeriodOnly.Id);
    _savingsRepository.Verify(repo => repo.SaveGoalWithAllocationAsync(alreadyFunded, It.IsAny<SavingsAllocation>()), Times.Never);
  }

  // ── Initial Amount Tests ──────────────────────────────────────

  [Fact]
  public async Task CreateSavingsGoalAsync_WithInitialAmount_SetsCurrentAmountAndCreatesAllocation()
  {
    var service = CreateService();
    var userId = 10;

    _savingsRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(new List<SavingsGoal>());
    _savingsRepository.Setup(r => r.CreateAsync(It.IsAny<SavingsGoal>()))
      .ReturnsAsync((SavingsGoal g) => { g.Id = 1; return g; });
    _savingsRepository.Setup(r => r.CreateAllocationAsync(It.IsAny<SavingsAllocation>()))
      .ReturnsAsync((SavingsAllocation a) => a);
    _savingsRepository.Setup(r => r.GetActiveGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(r => r.GetCompletedGoalsCountAsync(userId)).ReturnsAsync(0);

    var request = new CreateSavingsGoalRequest
    {
      Name = "Vacation",
      TargetAmount = 1000m,
      InitialAmount = 250m,
      AllocationPercentage = 10m
    };

    var result = await service.CreateSavingsGoalAsync(userId, request);

    result.CurrentAmount.Should().Be(250m);
    result.IsCompleted.Should().BeFalse();

    _savingsRepository.Verify(r => r.CreateAsync(It.Is<SavingsGoal>(g =>
      g.CurrentAmount == 250m && !g.IsCompleted)), Times.Once);
    _savingsRepository.Verify(r => r.CreateAllocationAsync(It.Is<SavingsAllocation>(a =>
      a.AllocatedAmount == 250m && a.Source == "initial_amount")), Times.Once);
  }

  [Fact]
  public async Task CreateSavingsGoalAsync_WithInitialAmountEqualToTarget_MarksCompleted()
  {
    var service = CreateService();
    var userId = 10;

    _savingsRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(new List<SavingsGoal>());
    _savingsRepository.Setup(r => r.CreateAsync(It.IsAny<SavingsGoal>()))
      .ReturnsAsync((SavingsGoal g) => { g.Id = 2; return g; });
    _savingsRepository.Setup(r => r.CreateAllocationAsync(It.IsAny<SavingsAllocation>()))
      .ReturnsAsync((SavingsAllocation a) => a);
    _savingsRepository.Setup(r => r.GetActiveGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(r => r.GetCompletedGoalsCountAsync(userId)).ReturnsAsync(0);

    var request = new CreateSavingsGoalRequest
    {
      Name = "Quick Goal",
      TargetAmount = 500m,
      InitialAmount = 500m,
      AllocationPercentage = 5m
    };

    var result = await service.CreateSavingsGoalAsync(userId, request);

    result.CurrentAmount.Should().Be(500m);
    result.IsCompleted.Should().BeTrue();

    _savingsRepository.Verify(r => r.CreateAsync(It.Is<SavingsGoal>(g =>
      g.CurrentAmount == 500m && g.IsCompleted)), Times.Once);
  }

  [Fact]
  public async Task CreateSavingsGoalAsync_WithZeroInitialAmount_DoesNotCreateAllocation()
  {
    var service = CreateService();
    var userId = 10;

    _savingsRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(new List<SavingsGoal>());
    _savingsRepository.Setup(r => r.CreateAsync(It.IsAny<SavingsGoal>()))
      .ReturnsAsync((SavingsGoal g) => { g.Id = 3; return g; });
    _savingsRepository.Setup(r => r.GetActiveGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(r => r.GetCompletedGoalsCountAsync(userId)).ReturnsAsync(0);

    var request = new CreateSavingsGoalRequest
    {
      Name = "Fresh Start",
      TargetAmount = 2000m,
      InitialAmount = 0m,
      AllocationPercentage = 15m
    };

    await service.CreateSavingsGoalAsync(userId, request);

    _savingsRepository.Verify(r => r.CreateAllocationAsync(It.IsAny<SavingsAllocation>()), Times.Never);
  }

  [Fact]
  public async Task CreateSavingsGoalAsync_WithNegativeInitialAmount_ThrowsArgumentException()
  {
    var service = CreateService();
    var userId = 10;

    _savingsRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(new List<SavingsGoal>());

    var request = new CreateSavingsGoalRequest
    {
      Name = "Bad Goal",
      TargetAmount = 1000m,
      InitialAmount = -50m,
      AllocationPercentage = 10m
    };

    var act = () => service.CreateSavingsGoalAsync(userId, request);

    await act.Should().ThrowAsync<ArgumentException>()
      .WithMessage("*cannot be negative*");

    _savingsRepository.Verify(r => r.CreateAsync(It.IsAny<SavingsGoal>()), Times.Never);
  }

  [Fact]
  public async Task CreateSavingsGoalAsync_WithInitialAmountExceedingTarget_ThrowsArgumentException()
  {
    var service = CreateService();
    var userId = 10;

    _savingsRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(new List<SavingsGoal>());

    var request = new CreateSavingsGoalRequest
    {
      Name = "Over Goal",
      TargetAmount = 500m,
      InitialAmount = 600m,
      AllocationPercentage = 10m
    };

    var act = () => service.CreateSavingsGoalAsync(userId, request);

    await act.Should().ThrowAsync<ArgumentException>()
      .WithMessage("*cannot exceed target*");

    _savingsRepository.Verify(r => r.CreateAsync(It.IsAny<SavingsGoal>()), Times.Never);
  }

  // ── Reset Tests ───────────────────────────────────────────────

  [Fact]
  public async Task ResetSavingsGoalAsync_ZeroesCurrentAmountAndClearsCompleted()
  {
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 42,
      UserId = 10,
      Name = "Emergency Fund",
      CurrentAmount = 2500m,
      TargetAmount = 2500m,
      IsCompleted = true,
      IsActive = true,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>()))
      .ReturnsAsync((SavingsGoal g) => g);

    var result = await service.ResetSavingsGoalAsync(42);

    result.CurrentAmount.Should().Be(0m);
    result.IsCompleted.Should().BeFalse();
    // Preserved fields
    goal.TargetAmount.Should().Be(2500m);
    goal.IsActive.Should().BeTrue();
    goal.Name.Should().Be("Emergency Fund");
    _savingsRepository.Verify(r => r.UpdateAsync(goal), Times.Once);
  }

  [Fact]
  public async Task ResetSavingsGoalAsync_DoesNotTouchAllocationHistory()
  {
    // Reset preserves the audit trail: SavingsAllocation rows are
    // immutable. Verify the service never calls DeleteAllocationAsync.
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 42,
      UserId = 10,
      CurrentAmount = 500m,
      TargetAmount = 2000m,
      IsCompleted = false,
      IsActive = true,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>()))
      .ReturnsAsync((SavingsGoal g) => g);

    await service.ResetSavingsGoalAsync(42);

    _savingsRepository.Verify(r => r.DeleteAllocationAsync(It.IsAny<int>()), Times.Never);
    _savingsRepository.Verify(r => r.CreateAllocationAsync(It.IsAny<SavingsAllocation>()), Times.Never);
  }

  [Fact]
  public async Task ResetSavingsGoalAsync_IsIdempotentWhenAlreadyZero()
  {
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 42,
      UserId = 10,
      CurrentAmount = 0m,
      TargetAmount = 1000m,
      IsCompleted = false,
      IsActive = true,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>()))
      .ReturnsAsync((SavingsGoal g) => g);

    var result = await service.ResetSavingsGoalAsync(42);

    result.CurrentAmount.Should().Be(0m);
    result.IsCompleted.Should().BeFalse();
  }

  [Fact]
  public async Task ResetSavingsGoalAsync_ThrowsWhenGoalNotFound()
  {
    var service = CreateService();
    _savingsRepository.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((SavingsGoal?)null);

    var act = () => service.ResetSavingsGoalAsync(999);

    await act.Should().ThrowAsync<ArgumentException>()
      .WithMessage("*999*");
    _savingsRepository.Verify(r => r.UpdateAsync(It.IsAny<SavingsGoal>()), Times.Never);
  }

  // ── Deposit-from-transaction (income earmark) Tests ───────────

  [Fact]
  public async Task DepositFromTransactionAsync_AddsAmountAndRecordsPositiveAllocation()
  {
    var service = CreateService();
    var userId = 55;
    var goal = new SavingsGoal
    {
      Id = 42,
      UserId = userId,
      CurrentAmount = 1000m,
      TargetAmount = 10000m,
      IsCompleted = false,
    };
    var allocations = new List<SavingsAllocation>();

    _savingsRepository.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns((SavingsGoal _, SavingsAllocation a) => { allocations.Add(a); return Task.CompletedTask; });
    _savingsRepository.Setup(r => r.GetActiveGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(r => r.GetCompletedGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(r => r.GetTotalSavedAmountAsync(userId)).ReturnsAsync(0m);

    var result = await service.DepositFromTransactionAsync(42, 5000m, 777);

    result.CurrentAmount.Should().Be(6000m);
    allocations.Should().HaveCount(1);
    allocations[0].AllocatedAmount.Should().Be(5000m);
    allocations[0].Source.Should().Be("transaction_deposit:777");
  }

  [Fact]
  public async Task DepositFromTransactionAsync_CompletesGoalWhenTargetReached()
  {
    var service = CreateService();
    var userId = 55;
    var goal = new SavingsGoal
    {
      Id = 42,
      UserId = userId,
      CurrentAmount = 9000m,
      TargetAmount = 10000m,
      IsCompleted = false,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns(Task.CompletedTask);
    _savingsRepository.Setup(r => r.GetActiveGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(r => r.GetCompletedGoalsCountAsync(userId)).ReturnsAsync(0);
    _savingsRepository.Setup(r => r.GetTotalSavedAmountAsync(userId)).ReturnsAsync(0m);

    var result = await service.DepositFromTransactionAsync(42, 2000m, 777);

    result.CurrentAmount.Should().Be(11000m);
    result.IsCompleted.Should().BeTrue();
  }

  [Fact]
  public async Task DepositFromTransactionAsync_ThrowsWhenAmountNotPositive()
  {
    var service = CreateService();
    var goal = new SavingsGoal { Id = 42, UserId = 1, CurrentAmount = 100m, TargetAmount = 500m };
    _savingsRepository.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(goal);

    var act = () => service.DepositFromTransactionAsync(42, 0m, 777);

    await act.Should().ThrowAsync<ArgumentException>();
    _savingsRepository.Verify(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()), Times.Never);
  }

  [Fact]
  public async Task UndoDepositAsync_RemovesAmountAndDeletesAllocation()
  {
    var service = CreateService();
    var depositAllocation = new SavingsAllocation
    {
      Id = 900,
      SavingsGoalId = 42,
      AllocatedAmount = 5000m,
      Source = "transaction_deposit:777",
    };
    var goal = new SavingsGoal
    {
      Id = 42,
      UserId = 1,
      CurrentAmount = 6000m,
      TargetAmount = 10000m,
      IsCompleted = false,
      Allocations = new List<SavingsAllocation> { depositAllocation },
    };

    var removedIds = new List<int>();
    _savingsRepository.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.SaveGoalRemovingAllocationsAsync(It.IsAny<SavingsGoal>(), It.IsAny<IEnumerable<int>>()))
      .Returns((SavingsGoal _, IEnumerable<int> ids) => { removedIds.AddRange(ids); return Task.CompletedTask; });

    var result = await service.UndoDepositAsync(42, 777);

    result.CurrentAmount.Should().Be(1000m);
    removedIds.Should().ContainSingle().Which.Should().Be(900);
  }

  [Fact]
  public async Task UndoDepositAsync_ThrowsWhenNoDepositForTransaction()
  {
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 42,
      UserId = 1,
      CurrentAmount = 100m,
      TargetAmount = 500m,
      Allocations = new List<SavingsAllocation>(),
    };
    _savingsRepository.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(goal);

    var act = () => service.UndoDepositAsync(42, 777);

    await act.Should().ThrowAsync<ArgumentException>();
    _savingsRepository.Verify(r => r.SaveGoalRemovingAllocationsAsync(It.IsAny<SavingsGoal>(), It.IsAny<IEnumerable<int>>()), Times.Never);
  }

  // ── Update lifecycle: symmetric completion (R3 #1/#9) ─────────

  [Fact]
  public async Task UpdateSavingsGoalAsync_RaisingTargetAboveBalance_UnCompletesGoal()
  {
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 5,
      UserId = 10,
      Type = SavingsGoalType.Personal,
      Name = "Car",
      CurrentAmount = 100m,
      TargetAmount = 100m,
      AllocationPercentage = 10m,
      IsActive = true,
      IsCompleted = true,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(10)).ReturnsAsync(new List<SavingsGoal> { goal });
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);

    var request = new UpdateSavingsGoalRequest
    {
      Name = "Car",
      TargetAmount = 200m,
      AllocationPercentage = 10m,
      IsActive = true,
    };

    var result = await service.UpdateSavingsGoalAsync(5, request);

    result.IsCompleted.Should().BeFalse();
    goal.IsCompleted.Should().BeFalse();
  }

  [Fact]
  public async Task UpdateSavingsGoalAsync_LoweringTargetToBalance_ReCompletesGoal()
  {
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 6,
      UserId = 10,
      Type = SavingsGoalType.Personal,
      Name = "Trip",
      CurrentAmount = 500m,
      TargetAmount = 1000m,
      AllocationPercentage = 10m,
      IsActive = true,
      IsCompleted = false,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(6)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(10)).ReturnsAsync(new List<SavingsGoal> { goal });
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);

    var request = new UpdateSavingsGoalRequest
    {
      Name = "Trip",
      TargetAmount = 400m,
      AllocationPercentage = 10m,
      IsActive = true,
    };

    var result = await service.UpdateSavingsGoalAsync(6, request);

    result.IsCompleted.Should().BeTrue();
    goal.IsCompleted.Should().BeTrue();
  }

  [Fact]
  public async Task UpdateSavingsGoalAsync_OmittingIsActive_PreservesActiveState()
  {
    // The Flutter edit sheet changes only name/target and omits isActive. With a non-nullable bool
    // that bound to false and silently deactivated the goal; nullable IsActive must preserve state.
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 9,
      UserId = 10,
      Type = SavingsGoalType.Personal,
      Name = "Emergency",
      CurrentAmount = 200m,
      TargetAmount = 1000m,
      AllocationPercentage = 10m,
      IsActive = true,
      IsCompleted = false,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(10)).ReturnsAsync(new List<SavingsGoal> { goal });
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);

    var request = new UpdateSavingsGoalRequest
    {
      Name = "Emergency",
      TargetAmount = 1500m,
      AllocationPercentage = 10m,
      // IsActive intentionally omitted (null)
    };

    var result = await service.UpdateSavingsGoalAsync(9, request);

    result.IsActive.Should().BeTrue();
    goal.IsActive.Should().BeTrue();
  }

  [Fact]
  public async Task UpdateSavingsGoalAsync_ExplicitIsActiveFalse_Deactivates()
  {
    // When the client DOES supply isActive:false, it must still take effect.
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 11,
      UserId = 10,
      Type = SavingsGoalType.Personal,
      Name = "Paused",
      CurrentAmount = 200m,
      TargetAmount = 1000m,
      AllocationPercentage = 10m,
      IsActive = true,
      IsCompleted = false,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(11)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(10)).ReturnsAsync(new List<SavingsGoal> { goal });
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);

    var request = new UpdateSavingsGoalRequest
    {
      Name = "Paused",
      TargetAmount = 1000m,
      AllocationPercentage = 10m,
      IsActive = false,
    };

    var result = await service.UpdateSavingsGoalAsync(11, request);

    result.IsActive.Should().BeFalse();
    goal.IsActive.Should().BeFalse();
  }

  // ── Update allocation % is type-scoped (R3 #7/#10/#12) ────────

  [Fact]
  public async Task UpdateSavingsGoalAsync_PersonalEdit_IgnoresFamilyGoalAllocation()
  {
    // Personal 50% + Family 60% are each valid (separate pools). Editing the personal goal must
    // validate against personal goals ONLY, so 50% is fine even though 50 + 60 > 100 combined.
    var service = CreateService();
    var personal = new SavingsGoal
    {
      Id = 7,
      UserId = 10,
      Type = SavingsGoalType.Personal,
      Name = "Personal",
      CurrentAmount = 0m,
      TargetAmount = 1000m,
      AllocationPercentage = 50m,
      IsActive = true,
      IsCompleted = false,
    };
    var family = new SavingsGoal
    {
      Id = 8,
      UserId = 10,
      Type = SavingsGoalType.Family,
      Name = "Family",
      AllocationPercentage = 60m,
      IsActive = true,
      IsCompleted = false,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(personal);
    _savingsRepository.Setup(r => r.GetByUserIdAsync(10)).ReturnsAsync(new List<SavingsGoal> { personal, family });
    _savingsRepository.Setup(r => r.UpdateAsync(It.IsAny<SavingsGoal>())).ReturnsAsync((SavingsGoal g) => g);

    var request = new UpdateSavingsGoalRequest
    {
      Name = "Personal",
      TargetAmount = 1000m,
      AllocationPercentage = 50m,
      IsActive = true,
    };

    var act = () => service.UpdateSavingsGoalAsync(7, request);

    await act.Should().NotThrowAsync();
    _savingsRepository.Verify(r => r.UpdateAsync(personal), Times.Once);
  }

  // ── Negative allocation % rejected (R3 #14) ───────────────────

  [Fact]
  public async Task CreateSavingsGoalAsync_WithNegativeAllocationPercentage_Throws()
  {
    var service = CreateService();
    _savingsRepository.Setup(r => r.GetByUserIdAsync(10)).ReturnsAsync(new List<SavingsGoal>());

    var request = new CreateSavingsGoalRequest
    {
      Name = "Bad",
      TargetAmount = 1000m,
      AllocationPercentage = -50m,
    };

    var act = () => service.CreateSavingsGoalAsync(10, request);

    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("*between 0 and 100*");
    _savingsRepository.Verify(r => r.CreateAsync(It.IsAny<SavingsGoal>()), Times.Never);
  }

  // ── Allocate response carries owner flags (R3 #16) ────────────

  [Fact]
  public async Task AllocateToGoalAsync_WithCurrentUser_SetsIsOwnerAndCanContribute()
  {
    var service = CreateService();
    var goal = new SavingsGoal
    {
      Id = 9,
      UserId = 10,
      Type = SavingsGoalType.Personal,
      Name = "Personal",
      CurrentAmount = 100m,
      TargetAmount = 1000m,
      IsActive = true,
      IsCompleted = false,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Returns(Task.CompletedTask);
    _familyAuthService.Setup(a => a.GetUserRoleAsync(10)).ReturnsAsync((FamilyMemberRole?)null);

    var result = await service.AllocateToGoalAsync(9, new ManualAllocationRequest { Amount = 50m, Source = "manual" }, currentUserId: 10);

    result.CurrentAmount.Should().Be(150m);
    result.IsOwner.Should().BeTrue();
    result.CanContribute.Should().BeTrue();
  }

  // ── On-track is pace-based, not a flat short-circuit (R3 #15) ──

  [Fact]
  public async Task GetSavingsGoalAsync_ZeroFundedGoalDueSoon_ReportsBehind()
  {
    var service = CreateService();
    var now = DateTime.UtcNow;
    var goal = new SavingsGoal
    {
      Id = 11,
      UserId = 10,
      Type = SavingsGoalType.Personal,
      Name = "Ambitious",
      CurrentAmount = 0m,
      TargetAmount = 10000m,
      TargetDate = now.AddDays(60),
      CreatedAt = now.AddDays(-30),
      IsActive = true,
      IsCompleted = false,
    };

    _savingsRepository.Setup(r => r.GetByIdAsync(11)).ReturnsAsync(goal);

    var result = await service.GetSavingsGoalAsync(11);

    result!.IsOnTrack.Should().BeFalse();
    result.ProgressStatus.Should().Be("behind");
  }

  // ── Optional account link (reconciliation) ──────────────────────────────

  [Fact]
  public async Task GetSavingsGoalsAsync_LinkedGoals_ReportDriftAgainstTheAccountBalance()
  {
    var service = CreateService();
    var userId = 501;
    var account = new Account { Id = 9, UserId = userId, Name = "Ally Savings", CurrentBalance = 2180m };

    // Two goals share ONE account: the honest comparison is the balance against their SUM,
    // never a single goal against the whole balance.
    var goals = new List<SavingsGoal>
    {
      new() { Id = 1, UserId = userId, Name = "Emergency", TargetAmount = 5000m, CurrentAmount = 2000m,
              AccountId = 9, Account = account, IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-5) },
      new() { Id = 2, UserId = userId, Name = "Car", TargetAmount = 1000m, CurrentAmount = 400m,
              AccountId = 9, Account = account, IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-5) },
    };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(new User { Id = userId });
    _savingsRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null)).ReturnsAsync(goals);
    _savingsRepository.Setup(r => r.GetGoalTotalsByAccountAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(new Dictionary<int, decimal> { [9] = 2400m });

    var result = (await service.GetSavingsGoalsAsync(userId)).ToList();

    var emergency = result.Single(g => g.Id == 1);
    emergency.AccountName.Should().Be("Ally Savings");
    emergency.AccountBalance.Should().Be(2180m);
    // 2000 + 400 promised, not just this goal's 2000.
    emergency.AccountAllocatedToGoals.Should().Be(2400m);
    // Negative = the goals promise more than the account actually holds.
    emergency.AccountUnallocated.Should().Be(-220m);
  }

  [Fact]
  public async Task GetSavingsGoalsAsync_UnlinkedGoal_LeavesReconciliationNull()
  {
    var service = CreateService();
    var userId = 502;
    var goals = new List<SavingsGoal>
    {
      new() { Id = 1, UserId = userId, Name = "Cash under mattress", TargetAmount = 100m,
              CurrentAmount = 50m, AccountId = null, IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-1) },
    };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(new User { Id = userId });
    _savingsRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null)).ReturnsAsync(goals);

    var result = (await service.GetSavingsGoalsAsync(userId)).ToList();

    // Unlinked is first-class, not an error state: nothing to reconcile against.
    result.Single().AccountId.Should().BeNull();
    result.Single().AccountBalance.Should().BeNull();
    result.Single().AccountUnallocated.Should().BeNull();
    _savingsRepository.Verify(r => r.GetGoalTotalsByAccountAsync(It.IsAny<IEnumerable<int>>()), Times.Never);
  }

  [Fact]
  public async Task CreateSavingsGoalAsync_ShouldRejectAnAccountTheUserDoesNotOwn()
  {
    var service = CreateService();
    var userId = 503;

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(new User { Id = userId });
    // The caller owns account 1; account 42 belongs to someone else.
    _accountRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null))
      .ReturnsAsync(new List<Account> { new() { Id = 1, UserId = userId, Name = "Mine" } });
    _savingsRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null)).ReturnsAsync(new List<SavingsGoal>());

    var act = () => service.CreateSavingsGoalAsync(userId, new CreateSavingsGoalRequest
    {
      Name = "Sneaky", TargetAmount = 100m, AccountId = 42
    });

    // Otherwise the reconciliation fields would echo back a stranger's balance.
    await act.Should().ThrowAsync<UnauthorizedAccessException>();
  }

  [Fact]
  public async Task CreateSavingsGoalAsync_ShouldRejectACreditCardAccount()
  {
    var service = CreateService();
    var userId = 504;

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(new User { Id = userId });
    _accountRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null))
      .ReturnsAsync(new List<Account>
      {
        new() { Id = 7, UserId = userId, Name = "Visa", Type = AccountType.CreditCard },
      });
    _savingsRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null)).ReturnsAsync(new List<SavingsGoal>());

    var act = () => service.CreateSavingsGoalAsync(userId, new CreateSavingsGoalRequest
    {
      Name = "Emergency", TargetAmount = 100m, AccountId = 7
    });

    // A card balance is money OWED. Reconciling an envelope against it would read debt
    // as savings, and syncing would set the goal to a negative number.
    await act.Should().ThrowAsync<InvalidOperationException>();
  }

  // ── Sync + reconcile ───────────────────────────────────────────────────

  private void SetupLinkedAccount(int userId, int accountId, decimal balance)
  {
    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(new User { Id = userId });
    _accountRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null))
      .ReturnsAsync(new List<Account>
      {
        new() { Id = accountId, UserId = userId, Name = "Ally Savings",
                Type = AccountType.Savings, CurrentBalance = balance },
      });
  }

  [Fact]
  public async Task SyncGoalToAccountAsync_ShouldRefuseWhenTheAccountHoldsMoreThanOneGoal()
  {
    var service = CreateService();
    var userId = 601;
    SetupLinkedAccount(userId, accountId: 9, balance: 2180m);

    var goal = new SavingsGoal { Id = 1, UserId = userId, Name = "Emergency", TargetAmount = 5000m,
                                 CurrentAmount = 2000m, AccountId = 9 };
    _savingsRepository.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.GetGoalCountByAccountAsync(9)).ReturnsAsync(2);

    var act = () => service.SyncGoalToAccountAsync(1, userId);

    // Setting this goal to the whole balance would claim its sibling's money.
    await act.Should().ThrowAsync<InvalidOperationException>();
    _savingsRepository.Verify(
      r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()),
      Times.Never);
  }

  [Fact]
  public async Task SyncGoalToAccountAsync_SoleGoal_AdoptsTheBalanceAndRecordsASignedLedgerRow()
  {
    var service = CreateService();
    var userId = 602;
    SetupLinkedAccount(userId, accountId: 9, balance: 1800m);

    // Goal currently claims MORE than the account holds, so the sync moves it DOWN.
    var goal = new SavingsGoal { Id = 1, UserId = userId, Name = "Emergency", TargetAmount = 5000m,
                                 CurrentAmount = 2000m, AccountId = 9, IsCompleted = false };
    _savingsRepository.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(goal);
    _savingsRepository.Setup(r => r.GetGoalCountByAccountAsync(9)).ReturnsAsync(1);

    SavingsAllocation? recorded = null;
    _savingsRepository
      .Setup(r => r.SaveGoalWithAllocationAsync(It.IsAny<SavingsGoal>(), It.IsAny<SavingsAllocation>()))
      .Callback<SavingsGoal, SavingsAllocation>((_, a) => recorded = a)
      .Returns(Task.CompletedTask);

    await service.SyncGoalToAccountAsync(1, userId);

    goal.CurrentAmount.Should().Be(1800m);
    // Negative, because the ledger has to explain a balance going DOWN too.
    recorded!.AllocatedAmount.Should().Be(-200m);
    recorded.Source.Should().Be("account_sync");
    // A correction is not an achievement — otherwise sync/unsync farms badges.
    _gamificationQueue.Verify(q => q.Enqueue(It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task ReconcileAccountAsync_ShouldCommitEveryGoalInOneTransaction()
  {
    var service = CreateService();
    var userId = 603;
    SetupLinkedAccount(userId, accountId: 9, balance: 2180m);

    var goals = new List<SavingsGoal>
    {
      new() { Id = 1, UserId = userId, Name = "Emergency", TargetAmount = 5000m, CurrentAmount = 2000m, AccountId = 9 },
      new() { Id = 2, UserId = userId, Name = "Car", TargetAmount = 1000m, CurrentAmount = 400m, AccountId = 9 },
    };
    _savingsRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null)).ReturnsAsync(goals);
    _savingsRepository.Setup(r => r.GetGoalTotalsByAccountAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(new Dictionary<int, decimal> { [9] = 2180m });

    List<(SavingsGoal Goal, SavingsAllocation Allocation)>? batch = null;
    _savingsRepository
      .Setup(r => r.SaveGoalsWithAllocationsAsync(It.IsAny<IEnumerable<(SavingsGoal, SavingsAllocation)>>()))
      .Callback<IEnumerable<(SavingsGoal, SavingsAllocation)>>(c => batch = c.ToList())
      .Returns(Task.CompletedTask);

    var result = await service.ReconcileAccountAsync(9, new ReconcileAccountRequest
    {
      Adjustments = new List<GoalReconcileAdjustment>
      {
        new() { GoalId = 1, NewAmount = 1780m },
        new() { GoalId = 2, NewAmount = 400m },   // unchanged — must not produce a ledger row
      }
    }, userId);

    // ONE call carrying every change: a half-applied reconcile leaves the account further out
    // of step than before the user tried to fix it.
    _savingsRepository.Verify(
      r => r.SaveGoalsWithAllocationsAsync(It.IsAny<IEnumerable<(SavingsGoal, SavingsAllocation)>>()),
      Times.Once);
    batch!.Should().HaveCount(1);
    batch![0].Allocation.AllocatedAmount.Should().Be(-220m);
    batch![0].Allocation.Source.Should().Be("account_reconcile");
    result.Unallocated.Should().Be(0m);
  }

  [Fact]
  public async Task ReconcileAccountAsync_ShouldAllowASurplusToStandUnassigned()
  {
    var service = CreateService();
    var userId = 604;
    SetupLinkedAccount(userId, accountId: 9, balance: 3000m);

    var goals = new List<SavingsGoal>
    {
      new() { Id = 1, UserId = userId, Name = "Emergency", TargetAmount = 5000m, CurrentAmount = 2000m, AccountId = 9 },
    };
    _savingsRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null)).ReturnsAsync(goals);
    _savingsRepository.Setup(r => r.GetGoalTotalsByAccountAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(new Dictionary<int, decimal> { [9] = 2500m });
    _savingsRepository
      .Setup(r => r.SaveGoalsWithAllocationsAsync(It.IsAny<IEnumerable<(SavingsGoal, SavingsAllocation)>>()))
      .Returns(Task.CompletedTask);

    var result = await service.ReconcileAccountAsync(9, new ReconcileAccountRequest
    {
      Adjustments = new List<GoalReconcileAdjustment> { new() { GoalId = 1, NewAmount = 2500m } }
    }, userId);

    // Money no goal claims is a legitimate end state, NOT an unfinished reconcile.
    result.Unallocated.Should().Be(500m);
  }

  [Fact]
  public async Task ReconcileAccountAsync_ShouldRejectAGoalHeldInADifferentAccount()
  {
    var service = CreateService();
    var userId = 605;
    SetupLinkedAccount(userId, accountId: 9, balance: 1000m);

    _savingsRepository.Setup(r => r.GetByUserOrFamilyAsync(userId, null))
      .ReturnsAsync(new List<SavingsGoal>
      {
        new() { Id = 1, UserId = userId, Name = "Elsewhere", TargetAmount = 100m, CurrentAmount = 50m, AccountId = 77 },
      });

    var act = () => service.ReconcileAccountAsync(9, new ReconcileAccountRequest
    {
      Adjustments = new List<GoalReconcileAdjustment> { new() { GoalId = 1, NewAmount = 900m } }
    }, userId);

    // Otherwise reconciling one account could rewrite balances on goals held in another.
    await act.Should().ThrowAsync<ArgumentException>();
  }
}
