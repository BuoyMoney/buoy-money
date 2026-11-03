using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Focused coverage for deficit-resolution amount validation (round-2 budget hardening).
/// A category with a real deficit is set up (spent 1100 vs allocated 100 = 1000 deficit); the tests
/// assert the service rejects unsigned/oversized resolution amounts instead of trusting the request.
/// </summary>
public class BudgetTransferServiceTests : IDisposable
{
  private readonly Mock<IBudgetRepository> _mockBudgetRepository;
  private readonly Mock<IUserRepository> _mockUserRepository;
  private readonly Mock<ISavingsRepository> _mockSavingsRepository;
  private readonly Mock<IBudgetSpendingService> _mockSpendingService;
  private readonly Mock<IFamilyAuthorizationService> _mockFamilyAuthService;
  private readonly Mock<IGamificationService> _mockGamificationService;
  private readonly BuoyAIDbContext _context;
  private readonly BudgetTransferService _service;

  private const int UserId = 1;
  private const int BudgetId = 1;
  private const int DeficitCategoryId = 5;

  public BudgetTransferServiceTests()
  {
    _mockBudgetRepository = new Mock<IBudgetRepository>();
    _mockUserRepository = new Mock<IUserRepository>();
    _mockSavingsRepository = new Mock<ISavingsRepository>();
    _mockSpendingService = new Mock<IBudgetSpendingService>();
    _mockFamilyAuthService = new Mock<IFamilyAuthorizationService>();
    _mockGamificationService = new Mock<IGamificationService>();

    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);

    _service = new BudgetTransferService(
      _mockBudgetRepository.Object,
      _mockUserRepository.Object,
      _mockSavingsRepository.Object,
      _mockSpendingService.Object,
      _mockFamilyAuthService.Object,
      _context,
      Mock.Of<ILogger<BudgetTransferService>>(),
      _mockGamificationService.Object);
  }

  public void Dispose() => _context.Dispose();

  private async Task<BudgetCategory> SeedDeficitBudgetAsync()
  {
    var category = new BudgetCategory
    {
      Id = DeficitCategoryId,
      BudgetId = BudgetId,
      CategoryId = (int)TransactionCategory.Groceries,
      AllocatedAmount = 100m,
      RolloverBalance = 0m
    };
    var budget = new Budget
    {
      Id = BudgetId,
      UserId = UserId,
      Name = "Test Budget",
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = DateTime.UtcNow.Date,
      CurrentPeriodEnd = DateTime.UtcNow.Date.AddMonths(1),
      IsActive = true,
      Categories = new List<BudgetCategory> { category }
    };
    _context.Budgets.Add(budget);
    await _context.SaveChangesAsync();

    _mockBudgetRepository.Setup(r => r.HasAccessAsync(BudgetId, UserId)).ReturnsAsync(true);
    // Spent 1100 against 100 allocated + 0 rollover => a real deficit of 1000.
    _mockSpendingService.Setup(s => s.GetCategorySpentAmountAsync(It.IsAny<BudgetCategory>()))
      .ReturnsAsync(1100m);

    return category;
  }

  [Fact]
  public async Task ResolveDeficitAsync_WithNegativeActionAmount_ThrowsArgumentException()
  {
    // Arrange — the classic exploit: a negative PullFromSavings that would fabricate savings balance,
    // paired with a positive rollover so the naive sum still equals the reported DeficitAmount.
    await SeedDeficitBudgetAsync();

    var request = new ResolveDeficitRequest
    {
      CategoryId = DeficitCategoryId,
      DeficitAmount = 1m,
      Actions = new List<DeficitResolutionAction>
      {
        new() { Strategy = DeficitResolutionStrategy.PullFromSavings, Amount = -1000m, SavingsGoalId = 42 },
        new() { Strategy = DeficitResolutionStrategy.CarryAsNegativeRollover, Amount = 1001m }
      }
    };

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => _service.ResolveDeficitAsync(BudgetId, UserId, request));
  }

  [Fact]
  public async Task ResolveDeficitAsync_WithTotalExceedingActualDeficit_ThrowsInvalidOperationException()
  {
    // Arrange — request claims a 5000 deficit but the server-computed deficit is only 1000.
    await SeedDeficitBudgetAsync();

    var request = new ResolveDeficitRequest
    {
      CategoryId = DeficitCategoryId,
      DeficitAmount = 5000m,
      Actions = new List<DeficitResolutionAction>
      {
        new() { Strategy = DeficitResolutionStrategy.CarryAsNegativeRollover, Amount = 5000m }
      }
    };

    // Act & Assert — DomainValidationException derives from InvalidOperationException.
    await Assert.ThrowsAsync<DomainValidationException>(
      () => _service.ResolveDeficitAsync(BudgetId, UserId, request));
  }

  [Fact]
  public async Task ResolveDeficitAsync_WithinDeficit_Succeeds()
  {
    // Arrange — a legitimate positive resolution at/under the real deficit is accepted.
    await SeedDeficitBudgetAsync();

    var request = new ResolveDeficitRequest
    {
      CategoryId = DeficitCategoryId,
      DeficitAmount = 1000m,
      Actions = new List<DeficitResolutionAction>
      {
        new() { Strategy = DeficitResolutionStrategy.CarryAsNegativeRollover, Amount = 1000m }
      }
    };

    // Act
    var result = await _service.ResolveDeficitAsync(BudgetId, UserId, request);

    // Assert
    result.Should().NotBeNull();
    result.TotalResolved.Should().Be(1000m);
  }

  [Fact]
  public async Task ResolveDeficitAsync_PullFromAnotherUsersSavingsGoal_IsRejectedAndLeavesBalanceIntact()
  {
    // Arrange — the IDOR: attacker owns budget B (UserId) but targets a PERSONAL savings goal owned
    // by a DIFFERENT user. Pulling from it must be rejected and the victim's balance untouched.
    await SeedDeficitBudgetAsync();

    const int victimGoalId = 42;
    var victimGoal = new SavingsGoal
    {
      Id = victimGoalId,
      UserId = 999, // NOT the caller
      Name = "Victim Emergency Fund",
      Type = SavingsGoalType.Personal,
      TargetAmount = 5000m,
      CurrentAmount = 5000m,
      IsCompleted = true
    };
    _context.SavingsGoals.Add(victimGoal);
    await _context.SaveChangesAsync();

    var request = new ResolveDeficitRequest
    {
      CategoryId = DeficitCategoryId,
      DeficitAmount = 500m,
      Actions = new List<DeficitResolutionAction>
      {
        new() { Strategy = DeficitResolutionStrategy.PullFromSavings, Amount = 500m, SavingsGoalId = victimGoalId }
      }
    };

    // Act
    var result = await _service.ResolveDeficitAsync(BudgetId, UserId, request);

    // Assert — action failed, nothing resolved, victim balance and completion untouched.
    result.TotalResolved.Should().Be(0m);
    result.ActionResults.Should().ContainSingle(r => !r.Success);

    var reloaded = await _context.SavingsGoals.FindAsync(victimGoalId);
    reloaded!.CurrentAmount.Should().Be(5000m);
    reloaded.IsCompleted.Should().BeTrue();
  }

  [Fact]
  public async Task ResolveDeficitAsync_PullFromOwnCompletedGoal_UnflagsCompletionWhenBelowTarget()
  {
    // Arrange — caller owns a fully-funded (completed) personal goal; pulling below target must
    // re-open it so leftover auto-allocation can refill it (finding 5).
    await SeedDeficitBudgetAsync();

    const int goalId = 77;
    var goal = new SavingsGoal
    {
      Id = goalId,
      UserId = UserId, // the caller / budget owner
      Name = "My Vacation",
      Type = SavingsGoalType.Personal,
      TargetAmount = 1000m,
      CurrentAmount = 1000m,
      IsCompleted = true
    };
    _context.SavingsGoals.Add(goal);
    await _context.SaveChangesAsync();

    var request = new ResolveDeficitRequest
    {
      CategoryId = DeficitCategoryId,
      DeficitAmount = 300m,
      Actions = new List<DeficitResolutionAction>
      {
        new() { Strategy = DeficitResolutionStrategy.PullFromSavings, Amount = 300m, SavingsGoalId = goalId }
      }
    };

    // Act
    var result = await _service.ResolveDeficitAsync(BudgetId, UserId, request);

    // Assert
    result.TotalResolved.Should().Be(300m);
    var reloaded = await _context.SavingsGoals.FindAsync(goalId);
    reloaded!.CurrentAmount.Should().Be(700m);
    reloaded.IsCompleted.Should().BeFalse();
  }
}
