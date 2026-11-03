using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class BudgetServiceTests : IDisposable
{
  private readonly Mock<IBudgetRepository> _mockBudgetRepository;
  private readonly Mock<ITransactionRepository> _mockTransactionRepository;
  private readonly Mock<IUserRepository> _mockUserRepository;
  private readonly Mock<ICategoryRepository> _mockCategoryRepository;
  private readonly Mock<IBudgetPeriodService> _mockBudgetPeriodService;
  private readonly Mock<ISavingsRepository> _mockSavingsRepository;
  private readonly Mock<IFamilyAuthorizationService> _mockAuthService;
  private readonly Mock<ILogger<BudgetService>> _mockLogger;
  private readonly BuoyAIDbContext _context;
  private readonly BudgetService _budgetService;

  public BudgetServiceTests()
  {
    _mockBudgetRepository = new Mock<IBudgetRepository>();
    _mockTransactionRepository = new Mock<ITransactionRepository>();
    _mockUserRepository = new Mock<IUserRepository>();
    _mockCategoryRepository = new Mock<ICategoryRepository>();
    _mockBudgetPeriodService = new Mock<IBudgetPeriodService>();
    _mockSavingsRepository = new Mock<ISavingsRepository>();
    _mockAuthService = new Mock<IFamilyAuthorizationService>();
    _mockLogger = new Mock<ILogger<BudgetService>>();
    
    // Setup in-memory database for BudgetTransfer operations
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);
    
    // Mock for budget spending service
    var mockSpendingService = new Mock<IBudgetSpendingService>();
    
    // Setup category repository to return empty list by default
    _mockCategoryRepository.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(new List<Category>());
    
    var mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
    mockHttpContextAccessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

    _budgetService = new BudgetService(
      _mockBudgetRepository.Object,
      _mockTransactionRepository.Object,
      _mockUserRepository.Object,
      _mockCategoryRepository.Object,
      _mockBudgetPeriodService.Object,
      _mockSavingsRepository.Object,
      mockSpendingService.Object,
      _mockAuthService.Object,
      _context,
      _mockLogger.Object,
      mockHttpContextAccessor.Object,
      Mock.Of<IGamificationEvaluationQueue>(),
      new BuoyAI.Infrastructure.Telemetry.BuoyAiMetrics(new TestMeterFactory()));
  }

  public void Dispose()
  {
    _context.Dispose();
  }

  [Fact]
  public async Task GetActiveBudgetAsync_WithActiveBudget_ShouldReturnBudgetDto()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId);
    var transactions = CreateTestTransactions();

    _mockBudgetRepository.Setup(x => x.GetActivePersonalBudgetAsync(userId))
      .ReturnsAsync(budget);
    
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(transactions);

    // Act
    var result = await _budgetService.GetActiveBudgetAsync(userId);

    // Assert
    result.Should().NotBeNull();
    result!.Id.Should().Be(budget.Id);
    result.Name.Should().Be(budget.Name);
    result.IsActive.Should().BeTrue();
    result.Categories.Should().HaveCount(2);
  }

  [Fact]
  public async Task GetActiveBudgetAsync_WithNoActiveBudget_ShouldReturnNull()
  {
    // Arrange
    var userId = 1;
    _mockBudgetRepository.Setup(x => x.GetActiveByUserIdAsync(userId))
      .ReturnsAsync((Budget?)null);

    // Act
    var result = await _budgetService.GetActiveBudgetAsync(userId);

    // Assert
    result.Should().BeNull();
  }

  [Fact]
  public async Task CreateBudgetAsync_WithValidRequest_ShouldCreateBudgetAndReturnDto()
  {
    // Arrange
    var userId = 1;
    var request = new CreateBudgetRequest
    {
      Name = "Monthly Budget",
      Period = BudgetPeriod.Monthly,
      TotalIncome = 5000m,
      Type = BudgetType.Personal, // Specify type for proper deactivation logic
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 500m },
        new() { CategoryId = (int)TransactionCategory.DiningOut, AllocatedAmount = 200m }
      }
    };

    var existingBudget = CreateTestBudget(userId);
    existingBudget.IsActive = true;
    existingBudget.Type = BudgetType.Personal; // Same type as request

    var createdBudget = CreateTestBudget(userId);
    createdBudget.Id = 2;
    createdBudget.Name = request.Name;

    // Setup GetByUserIdAsync to return existing budgets (used for type-based deactivation)
    _mockBudgetRepository.Setup(x => x.GetByUserIdAsync(userId))
      .ReturnsAsync(new List<Budget> { existingBudget });
    
    _mockBudgetRepository.Setup(x => x.UpdateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget b) => b);

    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .ReturnsAsync(createdBudget);

    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    var result = await _budgetService.CreateBudgetAsync(userId, request);

    // Assert
    result.Should().NotBeNull();
    result.Name.Should().Be(request.Name);
    result.TotalIncome.Should().Be(request.TotalIncome);
    result.Categories.Should().HaveCount(2);
    result.IsActive.Should().BeTrue();

    // Verify existing budget of same type was deactivated
    _mockBudgetRepository.Verify(x => x.UpdateAsync(It.Is<Budget>(b => !b.IsActive)), Times.Once);
    
    // Verify new budget was created
    _mockBudgetRepository.Verify(x => x.CreateAsync(It.IsAny<Budget>()), Times.Once);
  }

  [Fact]
  public async Task CreateBudgetAsync_PersonalBudgetForFamilyMember_LinksUnderFamilyBudget()
  {
    var userId = 1;
    var request = new CreateBudgetRequest
    {
      Name = "My Budget",
      Period = BudgetPeriod.Monthly,
      TotalIncome = 2000m,
      Type = BudgetType.Personal,
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.DiningOut, AllocatedAmount = 200m }
      }
    };

    _mockBudgetRepository.Setup(x => x.GetByUserIdAsync(userId))
      .ReturnsAsync(new List<Budget>());
    // Caller is in family 4...
    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync(new User { Id = userId, FamilyId = 4, Email = "m@f.com", Username = "m", FirstName = "M", LastName = "Ember" });
    // ...which has an active family budget (id 10)
    _mockBudgetRepository.Setup(x => x.GetByFamilyIdAsync(4))
      .ReturnsAsync(new List<Budget> { new Budget { Id = 10, Type = BudgetType.Family, IsActive = true, FamilyId = 4 } });

    var createdBudget = CreateTestBudget(userId);
    createdBudget.Id = 11;
    _mockBudgetRepository.Setup(x => x.UpdateAsync(It.IsAny<Budget>())).ReturnsAsync((Budget b) => b);
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>())).ReturnsAsync(createdBudget);
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    await _budgetService.CreateBudgetAsync(userId, request);

    // The personal budget is created as a live child of the family budget (10), tagged to family 4
    _mockBudgetRepository.Verify(x => x.CreateAsync(
      It.Is<Budget>(b => b.ParentBudgetId == 10 && b.FamilyId == 4 && b.Type == BudgetType.Personal)), Times.Once);
  }

  [Fact]
  public async Task GetBudgetsAsync_ShouldReturnAllBudgetsForUser()
  {
    // Arrange
    var userId = 1;
    var budgets = new List<Budget>
    {
      CreateTestBudget(userId),
      CreateTestBudget(userId, 2, "Previous Budget", false)
    };

    _mockBudgetRepository.Setup(x => x.GetByUserIdAsync(userId))
      .ReturnsAsync(budgets);

    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    var result = await _budgetService.GetBudgetsAsync(userId);

    // Assert
    result.Should().HaveCount(2);
    result.First().IsActive.Should().BeTrue();
    result.Skip(1).First().IsActive.Should().BeFalse();
  }

  [Fact]
  public async Task DeleteBudgetAsync_ShouldCallRepositoryDelete()
  {
    // Arrange
    var budgetId = 1;
    var userId = 1;
    var budget = CreateTestBudget(userId, budgetId);
    budget.IsPrimary = false; // Non-primary budget can be deleted
    
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budgetId))
      .ReturnsAsync(budget);

    // Act
    await _budgetService.DeleteBudgetAsync(budgetId, userId);

    // Assert
    _mockBudgetRepository.Verify(x => x.DeleteAsync(budgetId), Times.Once);
  }

  [Fact]
  public async Task DeleteBudgetAsync_WithPrimaryBudget_ShouldThrowInvalidOperationException()
  {
    // Arrange
    var budgetId = 1;
    var userId = 1;
    var budget = CreateTestBudget(userId, budgetId);
    budget.IsPrimary = true;
    
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budgetId))
      .ReturnsAsync(budget);

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(() => 
      _budgetService.DeleteBudgetAsync(budgetId, userId));
  }

  [Fact]
  public async Task DeleteBudgetAsync_WithUnauthorizedUser_ShouldThrowUnauthorizedAccessException()
  {
    // Arrange
    var budgetId = 1;
    var ownerId = 1;
    var unauthorizedUserId = 2;
    var budget = CreateTestBudget(ownerId, budgetId);
    
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budgetId))
      .ReturnsAsync(budget);

    // Act & Assert
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
      _budgetService.DeleteBudgetAsync(budgetId, unauthorizedUserId));
  }

  private static Budget CreateTestBudget(int userId, int budgetId = 1, string name = "Test Budget", bool isActive = true)
  {
    return new Budget
    {
      Id = budgetId,
      UserId = userId,
      Name = name,
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = DateTime.UtcNow.Date,
      CurrentPeriodEnd = DateTime.UtcNow.Date.AddMonths(1),
      TotalIncome = 5000m,
      TotalExpenses = 700m,
      IsActive = isActive,
      Categories = new List<BudgetCategory>
      {
        new()
        {
          Id = 1,
          BudgetId = budgetId,
          CategoryId = (int)TransactionCategory.Groceries,
          AllocatedAmount = 500m
        },
        new()
        {
          Id = 2,
          BudgetId = budgetId,
          CategoryId = (int)TransactionCategory.DiningOut,
          AllocatedAmount = 200m
        }
      }
    };
  }

  private static List<Transaction> CreateTestTransactions()
  {
    return new List<Transaction>
    {
      new()
      {
        Id = 1,
        Amount = 50m,
        Type = TransactionType.Debit,
        CategoryId = (int)TransactionCategory.Groceries,
        Date = DateTime.UtcNow.Date.AddDays(-1),
        Account = new Account { Name = "Test Account" }
      },
      new()
      {
        Id = 2,
        Amount = 25m,
        Type = TransactionType.Debit,
        CategoryId = (int)TransactionCategory.DiningOut,
        Date = DateTime.UtcNow.Date.AddDays(-2),
        Account = new Account { Name = "Test Account" }
      }
    };
  }

  [Fact]
  public async Task GetActiveBudgetAsync_WithEarmarkedThenSplitCharge_ShouldCountSavingsTransferExactlyOnce()
  {
    // Regression for the earmarked-then-split double-count: splitting a charge that is
    // earmarked to a savings goal copies the parent's SavingsGoalId onto each child (so the
    // earmark survives the split). The parent AND both children therefore carry the goal id.
    // The savings-transfers section must count the CHILDREN only (parent is IsSplit=true), so
    // a $60 charge split $40 + $20 renders as $60 total — not $120 across three rows.
    var userId = 1;
    var budget = CreateTestBudget(userId);

    var goal = new SavingsGoal { Id = 5, Name = "Bali Fund", CountWithdrawalsTowardBudget = false };

    var parent = new Transaction
    {
      Id = 10,
      Amount = 60m,
      Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.Groceries,
      Date = DateTime.UtcNow.Date.AddDays(-1),
      IsSplit = true,
      SavingsGoalId = 5,
      SavingsGoal = goal,
      Account = new Account { Name = "Test Account" }
    };
    var child1 = new Transaction
    {
      Id = 11,
      Amount = 40m,
      Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.Groceries,
      Date = DateTime.UtcNow.Date.AddDays(-1),
      IsSplit = false,
      ParentTransactionId = 10,
      SavingsGoalId = 5,
      SavingsGoal = goal,
      Account = new Account { Name = "Test Account" }
    };
    var child2 = new Transaction
    {
      Id = 12,
      Amount = 20m,
      Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.DiningOut,
      Date = DateTime.UtcNow.Date.AddDays(-1),
      IsSplit = false,
      ParentTransactionId = 10,
      SavingsGoalId = 5,
      SavingsGoal = goal,
      Account = new Account { Name = "Test Account" }
    };

    var transactions = new List<Transaction> { parent, child1, child2 };

    _mockBudgetRepository.Setup(x => x.GetActivePersonalBudgetAsync(userId))
      .ReturnsAsync(budget);
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(transactions);

    // Act
    var result = await _budgetService.GetActiveBudgetAsync(userId);

    // Assert — the split parent (id 10) must be excluded; only the children carry the amount.
    result.Should().NotBeNull();
    result!.SavingsTransfers.Select(s => s.TransactionId).Should().NotContain(10);
    result.SavingsTransfers.Should().HaveCount(2);
    result.SavingsTransfers.Sum(s => s.Amount).Should().Be(60m);
    result.TotalSavingsWithdrawals.Should().Be(60m);
  }

  #region Family Budget Tests

  [Fact]
  public async Task GetFamilyBudgetsAsync_WithFamilyId_ShouldReturnFamilyBudgets()
  {
    // Arrange
    var userId = 1;
    var familyId = 1;
    var familyBudget = CreateTestBudget(userId);
    familyBudget.FamilyId = familyId;
    familyBudget.Type = BudgetType.Family;
    familyBudget.IsPrimary = true;

    var budgets = new List<Budget> { familyBudget };
    var transactions = CreateTestTransactions();

    // User must be in the same family
    var user = new User { Id = userId, FirstName = "Test", LastName = "User", FamilyId = familyId };

    _mockBudgetRepository.Setup(x => x.GetByFamilyIdAsync(familyId))
      .ReturnsAsync(budgets);
    
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(transactions);

    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync(user);

    _mockBudgetPeriodService.Setup(x => x.ShouldResetBudget(It.IsAny<Budget>(), It.IsAny<DateTime>()))
      .Returns(false);

    // Act
    var result = await _budgetService.GetFamilyBudgetsAsync(familyId, userId);

    // Assert
    result.Should().NotBeNull();
    result.Should().HaveCount(1);
    result.First().Type.Should().Be(BudgetType.Family);
    result.First().IsPrimary.Should().BeTrue();
  }

  [Fact]
  public async Task ClaimTransactionAsync_WithValidData_ShouldUpdateTransaction()
  {
    // Arrange
    var userId = 1;
    var transactionId = 1;
    
    var user = new User { Id = userId, FirstName = "Test", LastName = "User", FamilyId = 1 };
    var accountOwner = new User { Id = userId, FamilyId = 1 };
    
    var transaction = new Transaction
    {
      Id = transactionId,
      Amount = 50m,
      Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.Groceries,
      Date = DateTime.UtcNow,
      AccountId = 1,
      Account = new Account { Id = 1, UserId = userId, User = accountOwner }
    };

    _mockTransactionRepository.Setup(x => x.GetByIdAsync(transactionId))
      .ReturnsAsync(transaction);
    
    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync(user);
    
    _mockTransactionRepository.Setup(x => x.UpdateAsync(It.IsAny<Transaction>()))
      .ReturnsAsync(transaction);

    // Act
    await _budgetService.ClaimTransactionAsync(transactionId, userId);

    // Assert
    _mockTransactionRepository.Verify(x => x.UpdateAsync(It.Is<Transaction>(t => t.ClaimedByUserId == userId)), Times.Once);
  }

  [Fact]
  public async Task ClaimTransactionAsync_WithInvalidTransaction_ShouldThrowArgumentException()
  {
    // Arrange
    var userId = 1;
    var transactionId = 999;

    _mockTransactionRepository.Setup(x => x.GetByIdAsync(transactionId))
      .ReturnsAsync((Transaction)null!);

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(() => 
      _budgetService.ClaimTransactionAsync(transactionId, userId));
  }

  [Fact]
  public async Task ClaimTransactionAsync_WithUnauthorizedUser_ShouldThrowUnauthorizedAccessException()
  {
    // Arrange
    var ownerId = 1;
    var unauthorizedUserId = 2;
    var transactionId = 1;
    
    // Owner is in family 1, unauthorized user is in family 2
    var owner = new User { Id = ownerId, FamilyId = 1 };
    var unauthorizedUser = new User { Id = unauthorizedUserId, FamilyId = 2 };
    
    var transaction = new Transaction
    {
      Id = transactionId,
      Amount = 50m,
      Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.Groceries,
      Date = DateTime.UtcNow,
      AccountId = 1,
      Account = new Account { Id = 1, UserId = ownerId, User = owner }
    };

    _mockTransactionRepository.Setup(x => x.GetByIdAsync(transactionId))
      .ReturnsAsync(transaction);

    _mockUserRepository.Setup(x => x.GetByIdAsync(unauthorizedUserId))
      .ReturnsAsync(unauthorizedUser);

    // The role-aware authorization service rejects a cross-family claim (Edit permission denied).
    _mockAuthService
      .Setup(x => x.ValidateTransactionAccessOrThrowAsync(unauthorizedUserId, transactionId, ResourcePermission.Edit))
      .ThrowsAsync(new UnauthorizedAccessException("Transaction does not belong to your family"));

    // Act & Assert
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
      _budgetService.ClaimTransactionAsync(transactionId, unauthorizedUserId));
  }

  [Fact]
  public async Task DeleteBudgetAsync_WithPrimaryFamilyBudget_ShouldThrowInvalidOperationException()
  {
    // Arrange
    var userId = 1;
    var budgetId = 1;
    var budget = CreateTestBudget(userId, budgetId);
    budget.Type = BudgetType.Family;
    budget.IsPrimary = true;

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budgetId))
      .ReturnsAsync(budget);

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(() => 
      _budgetService.DeleteBudgetAsync(budgetId, userId));
  }

  [Fact]
  public async Task CreateAllowanceBudgetAsync_ShouldCreateBudgetAndLinkedCategoryInParent()
  {
    // Arrange
    var userId = 1;
    var assignedUserId = 2;
    var parentBudgetId = 1;
    var familyId = 1;
    
    var parentBudget = CreateTestBudget(userId, parentBudgetId);
    parentBudget.FamilyId = familyId;
    parentBudget.Type = BudgetType.Family;
    
    var assignedUser = new User { Id = assignedUserId, FamilyId = familyId };
    
    var request = new CreateBudgetRequest
    {
      Name = "Mo's Allowance",
      Period = BudgetPeriod.Monthly,
      AllocatedAmount = 200m,
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.Uncategorized, AllocatedAmount = 200m }
      }
    };

    var createdBudget = new Budget
    {
      Id = 10,
      UserId = assignedUserId,
      FamilyId = familyId,
      Name = request.Name,
      Type = BudgetType.Allowance,
      AllocatedAmount = 200m,
      ParentBudgetId = parentBudgetId,
      Categories = new List<BudgetCategory>
      {
        new() { Id = 1, CategoryId = (int)TransactionCategory.Uncategorized, AllocatedAmount = 200m }
      }
    };

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(parentBudgetId))
      .ReturnsAsync(parentBudget);
    
    _mockUserRepository.Setup(x => x.GetByIdAsync(assignedUserId))
      .ReturnsAsync(assignedUser);
    
    _mockBudgetPeriodService.Setup(x => x.CalculateCurrentPeriod(It.IsAny<BudgetPeriod>(), It.IsAny<DateTime?>()))
      .Returns((DateTime.UtcNow, DateTime.UtcNow.AddMonths(1)));
    
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .ReturnsAsync(createdBudget);
    
    _mockBudgetRepository.Setup(x => x.CreateBudgetCategoryAsync(It.IsAny<BudgetCategory>()))
      .ReturnsAsync((BudgetCategory c) => { c.Id = 100; return c; });
    
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(assignedUserId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    var result = await _budgetService.CreateAllowanceBudgetAsync(userId, parentBudgetId, assignedUserId, request);

    // Assert
    result.Should().NotBeNull();
    result.Name.Should().Be("Mo's Allowance");
    result.Type.Should().Be(BudgetType.Allowance);
    result.ParentBudgetId.Should().Be(parentBudgetId);
    
    // Verify the allowance budget was created
    _mockBudgetRepository.Verify(x => x.CreateAsync(It.Is<Budget>(b => 
      b.Type == BudgetType.Allowance && 
      b.ParentBudgetId == parentBudgetId &&
      b.UserId == assignedUserId)), Times.Once);
    
    // Verify a linked category was created in the parent budget
    _mockBudgetRepository.Verify(x => x.CreateBudgetCategoryAsync(It.Is<BudgetCategory>(c =>
      c.BudgetId == parentBudgetId &&
      c.IsAllowance == true &&
      c.LinkedBudgetId == createdBudget.Id &&
      c.AssignedUserId == assignedUserId)), Times.Once);
  }

  [Fact]
  public async Task CreateBudgetAsync_WithDifferentType_ShouldNotDeactivateExistingBudget()
  {
    // Arrange
    var userId = 1;
    var familyId = 1;
    var request = new CreateBudgetRequest
    {
      Name = "Family Budget",
      Period = BudgetPeriod.Monthly,
      TotalIncome = 10000m,
      Type = BudgetType.Family, // Creating a Family budget
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 1000m }
      }
    };

    // User must be in a family to create a family budget
    var testUser = new User
    {
      Id = userId,
      Username = "testuser",
      Email = "test@example.com",
      PasswordHash = "hash",
      FamilyId = familyId
    };
    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync(testUser);

    // Existing PERSONAL budget should NOT be deactivated
    var existingPersonalBudget = CreateTestBudget(userId);
    existingPersonalBudget.IsActive = true;
    existingPersonalBudget.Type = BudgetType.Personal;

    var createdBudget = CreateTestBudget(userId);
    createdBudget.Id = 2;
    createdBudget.Name = request.Name;
    createdBudget.Type = BudgetType.Family;
    createdBudget.FamilyId = familyId;

    _mockBudgetRepository.Setup(x => x.GetByUserIdAsync(userId))
      .ReturnsAsync(new List<Budget> { existingPersonalBudget });
    
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .ReturnsAsync(createdBudget);

    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    var result = await _budgetService.CreateBudgetAsync(userId, request);

    // Assert
    result.Should().NotBeNull();
    result.Type.Should().Be(BudgetType.Family);
    
    // Verify existing personal budget was NOT deactivated (UpdateAsync should not be called)
    _mockBudgetRepository.Verify(x => x.UpdateAsync(It.IsAny<Budget>()), Times.Never);
    
    // Verify new family budget was created with FamilyId
    _mockBudgetRepository.Verify(x => x.CreateAsync(It.Is<Budget>(b => b.Type == BudgetType.Family && b.FamilyId == familyId)), Times.Once);
  }

  #endregion

  // NOTE: Category Group tests have been moved to BudgetCategoryGroupServiceTests.cs
  // as part of service extraction for better separation of concerns.

  #region Child Budget Tests (Budget Redesign)

  [Fact]
  public async Task CreateChildBudgetAsync_WithValidData_ShouldCreateChildBudget()
  {
    // Arrange
    var userId = 1;
    var parentBudget = CreateTestBudget(userId);
    parentBudget.Id = 1;
    parentBudget.FamilyId = 1;

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(parentBudget.Id))
      .ReturnsAsync(parentBudget);
    
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget b) => { 
        b.Id = 2; 
        b.User = new User { Id = userId, FirstName = "Test" };
        return b; 
      });

    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(
        It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    var request = new CreateChildBudgetRequest
    {
      Name = "Entertainment Fund",
      FundingAmount = 200m,
      IsSharedWithFamily = true,
      RolloverMode = ChildBudgetRolloverMode.AllocateToSavings,
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.Entertainment, AllocatedAmount = 100m, AllowRollover = true },
        new() { CategoryId = (int)TransactionCategory.DiningOut, AllocatedAmount = 100m, AllowRollover = false }
      }
    };

    // Act
    var result = await _budgetService.CreateChildBudgetAsync(userId, parentBudget.Id, request);

    // Assert
    result.Should().NotBeNull();
    result.Name.Should().Be("Entertainment Fund");
    result.RolloverMode.Should().Be(ChildBudgetRolloverMode.AllocateToSavings);
    result.IsSharedWithFamily.Should().BeTrue();
    
    _mockBudgetRepository.Verify(x => x.CreateAsync(It.Is<Budget>(b => 
      b.Name == "Entertainment Fund" && 
      b.ParentBudgetId == parentBudget.Id &&
      b.RolloverMode == ChildBudgetRolloverMode.AllocateToSavings &&
      b.Type == BudgetType.Family)), Times.Once); // Type=Family indicates shared with family
  }

  [Fact]
  public async Task UpdateChildBudgetAsync_WithRolloverModeChange_ShouldUpdateSettings()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId);
    budget.Id = 1;
    budget.ParentBudgetId = 100;
    budget.RolloverMode = ChildBudgetRolloverMode.RolloverToSelf;
    budget.Type = BudgetType.Personal; // Not shared with family

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budget.Id))
      .ReturnsAsync(budget);
    
    _mockBudgetRepository.Setup(x => x.UpdateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget b) => b);

    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(
        It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    var request = new UpdateChildBudgetRequest
    {
      RolloverMode = ChildBudgetRolloverMode.PromptUser,
      IsSharedWithFamily = true
    };

    // Act
    var result = await _budgetService.UpdateChildBudgetAsync(budget.Id, userId, request);

    // Assert
    result.Should().NotBeNull();
    result.RolloverMode.Should().Be(ChildBudgetRolloverMode.PromptUser);
    result.IsSharedWithFamily.Should().BeTrue();
  }

  [Fact]
  public async Task UpdateBudgetAsync_WithNegativeCategoryAllocation_ThrowsArgumentException()
  {
    // Arrange — the edit path must enforce the same allocation bound the create path does, so a negative
    // allocation can't be stored (it would understate TotalExpenses and corrupt remaining/deficit math).
    var userId = 1;
    var request = new CreateBudgetRequest
    {
      Name = "Edited Budget",
      Period = BudgetPeriod.Monthly,
      Type = BudgetType.Personal,
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = -500_000m }
      }
    };

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => _budgetService.UpdateBudgetAsync(1, userId, request));
  }

  #endregion

  #region Period History Tests

  // ── Helper ──────────────────────────────────────────────────────────────

  private BudgetPeriodSnapshot CreateTestSnapshot(int budgetId, int snapshotId = 1,
    DateTime? periodStart = null, DateTime? periodEnd = null)
  {
    var start = periodStart ?? DateTime.UtcNow.Date.AddMonths(-1);
    var snapshot = new BudgetPeriodSnapshot
    {
      Id = snapshotId,
      BudgetId = budgetId,
      PeriodStart = start,
      PeriodEnd = periodEnd ?? start.AddMonths(1).AddDays(-1),
      TotalAllocated = 700m,
      TotalSpent = 550m,
      TotalIncome = 5000m,
    };
    snapshot.CategorySnapshots = new List<BudgetPeriodCategorySnapshot>
    {
      new()
      {
        Id = snapshotId * 10 + 1,
        BudgetPeriodSnapshotId = snapshotId,
        CategoryId = (int)TransactionCategory.Groceries,
        CategoryName = "Groceries",
        AllocatedAmount = 500m,
        SpentAmount = 420m,
        IsIncome = false,
        AllowRollover = false,
        BudgetPeriodSnapshot = snapshot,
      },
      new()
      {
        Id = snapshotId * 10 + 2,
        BudgetPeriodSnapshotId = snapshotId,
        CategoryId = (int)TransactionCategory.DiningOut,
        CategoryName = "Dining Out",
        AllocatedAmount = 200m,
        SpentAmount = 130m,
        IsIncome = false,
        AllowRollover = false,
        BudgetPeriodSnapshot = snapshot,
      },
    };
    return snapshot;
  }

  // ── GetBudgetPeriodsAsync ───────────────────────────────────────────────

  [Fact]
  public async Task GetBudgetPeriodsAsync_WithOwnerUserId_ReturnsSnapshotsNewestFirst()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId, budgetId: 1);
    _mockBudgetRepository.Setup(r => r.GetByIdAsync(budget.Id)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(budget.Id, userId)).ReturnsAsync((BudgetShare?)null);

    var older = CreateTestSnapshot(budget.Id, snapshotId: 1,
      periodStart: DateTime.UtcNow.Date.AddMonths(-2));
    var newer = CreateTestSnapshot(budget.Id, snapshotId: 2,
      periodStart: DateTime.UtcNow.Date.AddMonths(-1));
    _context.BudgetPeriodSnapshots.AddRange(older, newer);
    await _context.SaveChangesAsync();

    // Act
    var result = (await _budgetService.GetBudgetPeriodsAsync(budget.Id, userId)).ToList();

    // Assert
    result.Should().HaveCount(2);
    result[0].PeriodStart.Should().BeAfter(result[1].PeriodStart);
  }

  [Fact]
  public async Task GetBudgetPeriodsAsync_WithFamilyMember_ReturnsSnapshots()
  {
    // Arrange
    var ownerId = 1;
    var memberId = 2;
    var familyId = 10;
    var budget = CreateTestBudget(ownerId, budgetId: 1);
    budget.Type = BudgetType.Family;
    budget.FamilyId = familyId;

    _mockBudgetRepository.Setup(r => r.GetByIdAsync(budget.Id)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(budget.Id, memberId)).ReturnsAsync((BudgetShare?)null);
    _mockUserRepository.Setup(r => r.GetByIdAsync(memberId))
      .ReturnsAsync(new User { Id = memberId, FamilyId = familyId });

    var snapshot = CreateTestSnapshot(budget.Id, snapshotId: 1);
    _context.BudgetPeriodSnapshots.Add(snapshot);
    await _context.SaveChangesAsync();

    // Act
    var result = await _budgetService.GetBudgetPeriodsAsync(budget.Id, memberId);

    // Assert
    result.Should().HaveCount(1);
  }

  [Fact]
  public async Task GetBudgetPeriodsAsync_WithUnauthorizedUser_ThrowsUnauthorizedAccessException()
  {
    // Arrange
    var ownerId = 1;
    var attackerId = 99;
    var budget = CreateTestBudget(ownerId, budgetId: 1);

    _mockBudgetRepository.Setup(r => r.GetByIdAsync(budget.Id)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(budget.Id, attackerId)).ReturnsAsync((BudgetShare?)null);
    _mockUserRepository.Setup(r => r.GetByIdAsync(attackerId))
      .ReturnsAsync(new User { Id = attackerId, FamilyId = null });

    // Act & Assert
    await Assert.ThrowsAsync<UnauthorizedAccessException>(
      () => _budgetService.GetBudgetPeriodsAsync(budget.Id, attackerId));
  }

  // ── GetBudgetPeriodDetailAsync ──────────────────────────────────────────

  [Fact]
  public async Task GetBudgetPeriodDetailAsync_WithValidSnapshot_ReturnsDetailWithCategories()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId, budgetId: 1);
    _mockBudgetRepository.Setup(r => r.GetByIdAsync(budget.Id)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(budget.Id, userId)).ReturnsAsync((BudgetShare?)null);

    var snapshot = CreateTestSnapshot(budget.Id, snapshotId: 5);
    _context.BudgetPeriodSnapshots.Add(snapshot);
    await _context.SaveChangesAsync();

    // Act
    var result = await _budgetService.GetBudgetPeriodDetailAsync(budget.Id, 5, userId);

    // Assert
    result.Should().NotBeNull();
    result!.Id.Should().Be(5);
    result.Categories.Should().HaveCount(2);
  }

  [Fact]
  public async Task GetBudgetPeriodDetailAsync_WithSnapshotFromDifferentBudget_ReturnsNull()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId, budgetId: 1);
    _mockBudgetRepository.Setup(r => r.GetByIdAsync(budget.Id)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(budget.Id, userId)).ReturnsAsync((BudgetShare?)null);

    // Snapshot belongs to budgetId=99, not budgetId=1
    var otherSnapshot = CreateTestSnapshot(budgetId: 99, snapshotId: 7);
    _context.BudgetPeriodSnapshots.Add(otherSnapshot);
    await _context.SaveChangesAsync();

    // Act
    var result = await _budgetService.GetBudgetPeriodDetailAsync(budget.Id, 7, userId);

    // Assert
    result.Should().BeNull();
  }

  [Fact]
  public async Task GetBudgetPeriodDetailAsync_WithUnauthorizedUser_ThrowsUnauthorizedAccessException()
  {
    // Arrange
    var ownerId = 1;
    var attackerId = 99;
    var budget = CreateTestBudget(ownerId, budgetId: 1);

    _mockBudgetRepository.Setup(r => r.GetByIdAsync(budget.Id)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(budget.Id, attackerId)).ReturnsAsync((BudgetShare?)null);
    _mockUserRepository.Setup(r => r.GetByIdAsync(attackerId))
      .ReturnsAsync(new User { Id = attackerId, FamilyId = null });

    // Act & Assert
    await Assert.ThrowsAsync<UnauthorizedAccessException>(
      () => _budgetService.GetBudgetPeriodDetailAsync(budget.Id, 1, attackerId));
  }

  // ── UpdatePeriodCategoryAmountAsync ────────────────────────────────────

  [Fact]
  public async Task UpdatePeriodCategoryAmountAsync_WithValidData_UpdatesAmountAndRecalculatesParentTotal()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId, budgetId: 1);
    _mockBudgetRepository.Setup(r => r.GetByIdAsync(budget.Id)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(budget.Id, userId)).ReturnsAsync((BudgetShare?)null);

    var snapshot = CreateTestSnapshot(budget.Id, snapshotId: 3);
    _context.BudgetPeriodSnapshots.Add(snapshot);
    await _context.SaveChangesAsync();

    var categorySnapshotId = snapshot.CategorySnapshots.First().Id;

    // Act
    var result = await _budgetService.UpdatePeriodCategoryAmountAsync(
      budget.Id, 3, categorySnapshotId, 600m, userId);

    // Assert
    result.AllocatedAmount.Should().Be(600m);

    var updatedSnapshot = await _context.BudgetPeriodSnapshots
      .Include(s => s.CategorySnapshots)
      .FirstAsync(s => s.Id == 3);
    // TotalAllocated = 600 (groceries updated) + 200 (dining) = 800
    updatedSnapshot.TotalAllocated.Should().Be(800m);
  }

  [Fact]
  public async Task UpdatePeriodCategoryAmountAsync_WithCategoryFromDifferentBudget_ThrowsKeyNotFoundException()
  {
    // Arrange — attacker owns budget 1, but the snapshot belongs to budget 2
    var attackerId = 1;
    var attackerBudget = CreateTestBudget(attackerId, budgetId: 1);
    _mockBudgetRepository.Setup(r => r.GetByIdAsync(attackerBudget.Id)).ReturnsAsync(attackerBudget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(attackerBudget.Id, attackerId)).ReturnsAsync((BudgetShare?)null);

    // Victim's snapshot is in budget 2
    var victimSnapshot = CreateTestSnapshot(budgetId: 2, snapshotId: 10);
    _context.BudgetPeriodSnapshots.Add(victimSnapshot);
    await _context.SaveChangesAsync();

    var victimCategoryId = victimSnapshot.CategorySnapshots.First().Id;

    // Act & Assert — must not find it because snapshotId=10 does not belong to budgetId=1
    await Assert.ThrowsAsync<KeyNotFoundException>(
      () => _budgetService.UpdatePeriodCategoryAmountAsync(
        attackerBudget.Id, 10, victimCategoryId, 999_999m, attackerId));
  }

  [Fact]
  public async Task UpdatePeriodCategoryAmountAsync_WithUnauthorizedUser_ThrowsUnauthorizedAccessException()
  {
    // Arrange
    var ownerId = 1;
    var attackerId = 99;
    var budget = CreateTestBudget(ownerId, budgetId: 1);

    _mockBudgetRepository.Setup(r => r.GetByIdAsync(budget.Id)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(r => r.GetBudgetShareAsync(budget.Id, attackerId)).ReturnsAsync((BudgetShare?)null);
    _mockUserRepository.Setup(r => r.GetByIdAsync(attackerId))
      .ReturnsAsync(new User { Id = attackerId, FamilyId = null });

    // The role-aware authorization service denies Edit to a non-owner / non-family user.
    _mockAuthService
      .Setup(x => x.ValidateBudgetAccessOrThrowAsync(attackerId, budget.Id, ResourcePermission.Edit))
      .ThrowsAsync(new UnauthorizedAccessException("Budget does not belong to your family"));

    // Act & Assert
    await Assert.ThrowsAsync<UnauthorizedAccessException>(
      () => _budgetService.UpdatePeriodCategoryAmountAsync(budget.Id, 1, 1, 100m, attackerId));
  }

  #endregion

  #region Duplicate-category integrity (PR5)

  [Fact]
  public async Task CreateBudgetAsync_WithDuplicateCategory_CollapsesToSingleRow()
  {
    // Arrange — the request carries the SAME category twice (e.g. a malformed / AI-driven POST).
    var userId = 1;
    var request = new CreateBudgetRequest
    {
      Name = "Dup Budget",
      Period = BudgetPeriod.Monthly,
      TotalIncome = 5000m,
      Type = BudgetType.Personal,
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 500m },
        new() { CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 300m }, // duplicate
        new() { CategoryId = (int)TransactionCategory.DiningOut, AllocatedAmount = 200m }
      }
    };

    Budget? captured = null;
    _mockBudgetRepository.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Budget>());
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .Callback<Budget>(b => captured = b)
      .ReturnsAsync((Budget b) => { b.Id = 2; return b; });
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    await _budgetService.CreateBudgetAsync(userId, request);

    // Assert — the duplicate Groceries row is dropped (first occurrence wins), so the persisted
    // budget has exactly two categories and the DB unique index is never violated.
    captured.Should().NotBeNull();
    captured!.Categories.Should().HaveCount(2);
    captured.Categories.Count(c => c.CategoryId == (int)TransactionCategory.Groceries).Should().Be(1);
    captured.Categories.Single(c => c.CategoryId == (int)TransactionCategory.Groceries)
      .AllocatedAmount.Should().Be(500m); // first wins
  }

  [Fact]
  public async Task UpdateBudgetAsync_WhenExistingBudgetHasDuplicateCategoryRows_DoesNotThrow()
  {
    // Arrange — legacy data: the stored budget already holds two rows for the same CategoryId
    // (nullable AssignedUserId let them slip past the old index). The old ToDictionary threw
    // "An item with the same key has already been added" here (unhandled 500, uneditable budget).
    var userId = 1;
    var budget = CreateTestBudget(userId, budgetId: 5);
    budget.Categories = new List<BudgetCategory>
    {
      new() { Id = 1, BudgetId = 5, CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 500m, RolloverBalance = 10m },
      new() { Id = 2, BudgetId = 5, CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 300m, RolloverBalance = 20m }
    };

    var request = new CreateBudgetRequest
    {
      Name = "Edited",
      Period = BudgetPeriod.Monthly,
      TotalIncome = 4000m,
      Type = BudgetType.Personal,
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 450m }
      }
    };

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(5)).ReturnsAsync(budget);
    _mockBudgetRepository.Setup(x => x.UpdateAsync(It.IsAny<Budget>())).ReturnsAsync((Budget b) => b);
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    var act = async () => await _budgetService.UpdateBudgetAsync(5, userId, request);

    // Assert — the GroupBy-based existing-category lookup tolerates the duplicate instead of throwing.
    await act.Should().NotThrowAsync();
    _mockBudgetRepository.Verify(x => x.UpdateAsync(It.IsAny<Budget>()), Times.Once);
  }

  #endregion

  #region RFC #793 — Family personal budgets (spawn / fund-from / binding / additive income / bucketing)

  private (Budget parent, BudgetCategory category, User member) ArrangeSpawnContext(
    int adminUserId, int memberId, int familyId, int parentBudgetId, int categoryId,
    decimal categoryAmount, string memberFirstName = "Mo")
  {
    var parent = CreateTestBudget(adminUserId, parentBudgetId);
    parent.Type = BudgetType.Family;
    parent.FamilyId = familyId;

    var category = new BudgetCategory
    {
      Id = categoryId,
      BudgetId = parentBudgetId,
      CategoryId = (int)TransactionCategory.Groceries,
      AllocatedAmount = categoryAmount,
      Budget = parent
    };

    var member = new User { Id = memberId, FamilyId = familyId, FirstName = memberFirstName };

    _mockBudgetRepository.Setup(x => x.GetBudgetCategoryAsync(categoryId)).ReturnsAsync(category);
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(parentBudgetId)).ReturnsAsync(parent);
    _mockUserRepository.Setup(x => x.GetByIdAsync(memberId)).ReturnsAsync(member);
    _mockAuthService.Setup(x => x.CanCreateFamilyBudgetAsync(adminUserId))
      .ReturnsAsync(AuthorizationResult.Allowed(FamilyMemberRole.Owner));
    _mockTransactionRepository.Setup(x => x.GetByUserOrFamilyAsync(
        It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(
        It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());
    return (parent, category, member);
  }

  [Fact]
  public async Task SpawnPersonalBudgetFromCategoryAsync_WithNoOverrides_DefaultsAmountToCategoryAndNamesAfterMember()
  {
    // Arrange
    var (_, _, _) = ArrangeSpawnContext(adminUserId: 1, memberId: 2, familyId: 7, parentBudgetId: 100, categoryId: 55, categoryAmount: 300m, memberFirstName: "Mo");
    Budget? captured = null;
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget b) => { b.Id = 200; captured = b; return b; });
    _mockBudgetRepository.Setup(x => x.UpdateBudgetCategoryAsync(It.IsAny<BudgetCategory>()))
      .ReturnsAsync((BudgetCategory c) => c);
    _mockBudgetRepository.Setup(x => x.GetLinkedCategoryForChildAsync(200))
      .ReturnsAsync(new BudgetCategory { IsAllowance = true, LinkedBudgetId = 200 });

    // Act
    var result = await _budgetService.SpawnPersonalBudgetFromCategoryAsync(
      1, 55, new SpawnPersonalBudgetRequest { AssignedUserId = 2 });

    // Assert
    captured.Should().NotBeNull();
    captured!.AllocatedAmount.Should().Be(300m);          // defaults to the category's current amount
    captured.Name.Should().Be("Mo's Budget");             // defaults to "{first name}'s Budget"
    captured.Type.Should().Be(BudgetType.Personal);
    captured.ParentBudgetId.Should().Be(100);
    captured.UserId.Should().Be(2);
    result.IsFamilyFunded.Should().BeTrue();
    result.FamilyAllocationAmount.Should().Be(300m);
  }

  [Fact]
  public async Task SpawnPersonalBudgetFromCategoryAsync_LinksExistingCategory_KeepingRealCategoryId()
  {
    // Arrange
    ArrangeSpawnContext(adminUserId: 1, memberId: 2, familyId: 7, parentBudgetId: 100, categoryId: 55, categoryAmount: 300m);
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget b) => { b.Id = 200; return b; });
    BudgetCategory? capturedCat = null;
    _mockBudgetRepository.Setup(x => x.UpdateBudgetCategoryAsync(It.IsAny<BudgetCategory>()))
      .ReturnsAsync((BudgetCategory c) => { capturedCat = c; return c; });
    _mockBudgetRepository.Setup(x => x.GetLinkedCategoryForChildAsync(200))
      .ReturnsAsync(new BudgetCategory { IsAllowance = true, LinkedBudgetId = 200 });

    // Act — existing transactions on the category are untouched; we only re-purpose it as the link.
    await _budgetService.SpawnPersonalBudgetFromCategoryAsync(1, 55, new SpawnPersonalBudgetRequest { AssignedUserId = 2 });

    // Assert
    capturedCat.Should().NotBeNull();
    capturedCat!.IsAllowance.Should().BeTrue();
    capturedCat.LinkedBudgetId.Should().Be(200);
    capturedCat.AssignedUserId.Should().Be(2);
    // Keeps its real CategoryId (NOT moved to Uncategorized/99) so existing transactions stay put.
    capturedCat.CategoryId.Should().Be((int)TransactionCategory.Groceries);
  }

  [Fact]
  public async Task SpawnPersonalBudgetFromCategoryAsync_WhenMemberNotAdmin_ThrowsUnauthorized()
  {
    // Arrange — a plain member (denied by CanCreateFamilyBudgetAsync) cannot spawn (controller returns 403).
    ArrangeSpawnContext(adminUserId: 3, memberId: 3, familyId: 7, parentBudgetId: 100, categoryId: 55, categoryAmount: 300m);
    _mockAuthService.Setup(x => x.CanCreateFamilyBudgetAsync(3))
      .ReturnsAsync(AuthorizationResult.Denied("Only Owner and BudgetAdmin can create family budgets", FamilyMemberRole.Member));

    // Act
    var act = () => _budgetService.SpawnPersonalBudgetFromCategoryAsync(3, 55, new SpawnPersonalBudgetRequest());

    // Assert
    await act.Should().ThrowAsync<UnauthorizedAccessException>();
    _mockBudgetRepository.Verify(x => x.CreateAsync(It.IsAny<Budget>()), Times.Never);
  }

  [Fact]
  public async Task SpawnPersonalBudgetFromCategoryAsync_WhenCategoryAlreadyLinked_ThrowsInvalidOperation()
  {
    // Arrange
    var (_, category, _) = ArrangeSpawnContext(adminUserId: 1, memberId: 2, familyId: 7, parentBudgetId: 100, categoryId: 55, categoryAmount: 300m);
    category.IsAllowance = true;      // already a funding link
    category.LinkedBudgetId = 999;

    // Act
    var act = () => _budgetService.SpawnPersonalBudgetFromCategoryAsync(1, 55, new SpawnPersonalBudgetRequest { AssignedUserId = 2 });

    // Assert — controller maps InvalidOperationException to 400.
    await act.Should().ThrowAsync<InvalidOperationException>();
    _mockBudgetRepository.Verify(x => x.CreateAsync(It.IsAny<Budget>()), Times.Never);
  }

  [Fact]
  public async Task SpawnPersonalBudgetFromCategoryAsync_WhenNotAFamilyCategory_ThrowsInvalidOperation()
  {
    // Arrange — the owning budget is a personal budget, not a family budget.
    var parent = CreateTestBudget(1, 100);
    parent.Type = BudgetType.Personal;
    parent.FamilyId = null;
    var category = new BudgetCategory { Id = 55, BudgetId = 100, CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 100m, Budget = parent };
    _mockBudgetRepository.Setup(x => x.GetBudgetCategoryAsync(55)).ReturnsAsync(category);

    // Act
    var act = () => _budgetService.SpawnPersonalBudgetFromCategoryAsync(1, 55, new SpawnPersonalBudgetRequest());

    // Assert
    await act.Should().ThrowAsync<InvalidOperationException>();
  }

  [Fact]
  public async Task CreateBudgetAsync_WithFundFromCategory_LinksCategoryToNewPersonalChild()
  {
    // Arrange — an admin creates a Personal budget in a family, funded from an existing family category.
    var userId = 1;
    var familyId = 7;
    var parentBudgetId = 100;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyBudget = CreateTestBudget(userId, parentBudgetId);
    familyBudget.Type = BudgetType.Family;
    familyBudget.FamilyId = familyId;
    var fundCategory = new BudgetCategory { Id = 55, BudgetId = parentBudgetId, CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 250m, Budget = familyBudget };

    _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync(user);
    _mockBudgetRepository.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Budget>());
    _mockBudgetRepository.Setup(x => x.GetByFamilyIdAsync(familyId)).ReturnsAsync(new List<Budget> { familyBudget });
    _mockBudgetPeriodService.Setup(x => x.CalculateCurrentPeriod(It.IsAny<BudgetPeriod>(), It.IsAny<DateTime?>()))
      .Returns((DateTime.UtcNow, DateTime.UtcNow.AddMonths(1)));
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget b) => { b.Id = 200; return b; });
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(parentBudgetId)).ReturnsAsync(familyBudget);
    _mockBudgetRepository.Setup(x => x.GetBudgetCategoryAsync(55)).ReturnsAsync(fundCategory);
    _mockAuthService.Setup(x => x.CanCreateFamilyBudgetAsync(userId))
      .ReturnsAsync(AuthorizationResult.Allowed(FamilyMemberRole.Owner));
    BudgetCategory? capturedCat = null;
    _mockBudgetRepository.Setup(x => x.UpdateBudgetCategoryAsync(It.IsAny<BudgetCategory>()))
      .ReturnsAsync((BudgetCategory c) => { capturedCat = c; return c; });
    _mockBudgetRepository.Setup(x => x.UpdateAsync(It.IsAny<Budget>())).ReturnsAsync((Budget b) => b);
    _mockTransactionRepository.Setup(x => x.GetByUserOrFamilyAsync(
        It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());
    _mockBudgetRepository.Setup(x => x.GetLinkedCategoryForChildAsync(200))
      .ReturnsAsync(new BudgetCategory { IsAllowance = true, LinkedBudgetId = 200 });

    var request = new CreateBudgetRequest
    {
      Name = "My Personal Budget",
      Period = BudgetPeriod.Monthly,
      Type = BudgetType.Personal,
      FundFromCategoryId = 55,
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.DiningOut, AllocatedAmount = 0m }
      }
    };

    // Act
    var result = await _budgetService.CreateBudgetAsync(userId, request);

    // Assert
    capturedCat.Should().NotBeNull();
    capturedCat!.IsAllowance.Should().BeTrue();
    capturedCat.LinkedBudgetId.Should().Be(200);
    capturedCat.AssignedUserId.Should().Be(userId);
    capturedCat.AllocatedAmount.Should().Be(250m);       // seeded from the family category's amount
    result.IsFamilyFunded.Should().BeTrue();
  }

  [Fact]
  public async Task CreateBudgetAsync_WithFundFromCategory_WhenMemberNotAdmin_ThrowsUnauthorized()
  {
    // Arrange — a plain member cannot fund a personal budget from the family pool.
    var userId = 3;
    var familyId = 7;
    var parentBudgetId = 100;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyBudget = CreateTestBudget(1, parentBudgetId);
    familyBudget.Type = BudgetType.Family;
    familyBudget.FamilyId = familyId;

    _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync(user);
    _mockBudgetRepository.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Budget>());
    _mockBudgetRepository.Setup(x => x.GetByFamilyIdAsync(familyId)).ReturnsAsync(new List<Budget> { familyBudget });
    _mockBudgetPeriodService.Setup(x => x.CalculateCurrentPeriod(It.IsAny<BudgetPeriod>(), It.IsAny<DateTime?>()))
      .Returns((DateTime.UtcNow, DateTime.UtcNow.AddMonths(1)));
    _mockBudgetRepository.Setup(x => x.CreateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget b) => { b.Id = 200; return b; });
    _mockAuthService.Setup(x => x.CanCreateFamilyBudgetAsync(userId))
      .ReturnsAsync(AuthorizationResult.Denied("Only Owner and BudgetAdmin can create family budgets", FamilyMemberRole.Member));

    var request = new CreateBudgetRequest
    {
      Name = "My Personal Budget",
      Period = BudgetPeriod.Monthly,
      Type = BudgetType.Personal,
      FundFromCategoryId = 55,
      Categories = new List<CreateBudgetCategoryRequest> { new() { CategoryId = (int)TransactionCategory.DiningOut, AllocatedAmount = 0m } }
    };

    // Act
    var act = () => _budgetService.CreateBudgetAsync(userId, request);

    // Assert
    await act.Should().ThrowAsync<UnauthorizedAccessException>();
  }

  [Fact]
  public async Task UpdateBudgetCategoryAsync_WhenCategoryLinked_PropagatesAmountToChildAllocation()
  {
    // Arrange — B3: family category is the source of truth; editing its amount rebinds the child.
    var category = new BudgetCategory
    {
      Id = 55, BudgetId = 100, CategoryId = (int)TransactionCategory.Groceries,
      AllocatedAmount = 100m, IsAllowance = true, LinkedBudgetId = 200
    };
    var parentBudget = CreateTestBudget(1, 100);
    var linkedChild = CreateTestBudget(2, 200);
    linkedChild.Type = BudgetType.Personal;
    linkedChild.AllocatedAmount = 100m;

    _mockBudgetRepository.Setup(x => x.GetBudgetCategoryAsync(55)).ReturnsAsync(category);
    _mockBudgetRepository.Setup(x => x.UpdateBudgetCategoryAsync(It.IsAny<BudgetCategory>()))
      .ReturnsAsync((BudgetCategory c) => c);
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(100)).ReturnsAsync(parentBudget);
    Budget? capturedLinked = null;
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(200)).ReturnsAsync(linkedChild);
    _mockBudgetRepository.Setup(x => x.UpdateAsync(It.IsAny<Budget>()))
      .ReturnsAsync((Budget b) => { if (b.Id == 200) capturedLinked = b; return b; });
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    await _budgetService.UpdateBudgetCategoryAsync(55, new UpdateBudgetCategoryRequest { AllocatedAmount = 250m, AllowRollover = true });

    // Assert
    capturedLinked.Should().NotBeNull();
    capturedLinked!.AllocatedAmount.Should().Be(250m);
  }

  [Fact]
  public async Task UpdateChildBudgetAsync_WhenLinkedChildAndAdmin_PropagatesFundingBackToCategory()
  {
    // Arrange — B3 reverse: an admin changing the child's funding rebinds the family category.
    var userId = 1;
    var child = CreateTestBudget(2, 200);
    child.ParentBudgetId = 100;
    child.Type = BudgetType.Personal;
    child.AllocatedAmount = 100m;
    var linkedCat = new BudgetCategory { Id = 55, BudgetId = 100, CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 100m, IsAllowance = true, LinkedBudgetId = 200 };

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(200)).ReturnsAsync(child);
    _mockBudgetRepository.Setup(x => x.GetLinkedCategoryForChildAsync(200)).ReturnsAsync(linkedCat);
    _mockAuthService.Setup(x => x.CanCreateFamilyBudgetAsync(userId))
      .ReturnsAsync(AuthorizationResult.Allowed(FamilyMemberRole.Owner));
    BudgetCategory? capturedCat = null;
    _mockBudgetRepository.Setup(x => x.UpdateBudgetCategoryAsync(It.IsAny<BudgetCategory>()))
      .ReturnsAsync((BudgetCategory c) => { capturedCat = c; return c; });
    _mockBudgetRepository.Setup(x => x.UpdateAsync(It.IsAny<Budget>())).ReturnsAsync((Budget b) => b);
    _mockTransactionRepository.Setup(x => x.GetByUserIdAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    await _budgetService.UpdateChildBudgetAsync(200, userId, new UpdateChildBudgetRequest { FundingAmount = 400m });

    // Assert
    capturedCat.Should().NotBeNull();
    capturedCat!.AllocatedAmount.Should().Be(400m);
  }

  [Fact]
  public async Task UpdateChildBudgetAsync_WhenLinkedChildAndMember_ChangingFunding_ThrowsUnauthorized()
  {
    // Arrange — B4: the assigned member may NOT change the allocation of a family-funded budget.
    var userId = 3;
    var child = CreateTestBudget(3, 200);
    child.ParentBudgetId = 100;
    child.Type = BudgetType.Personal;
    child.AllocatedAmount = 100m;
    var linkedCat = new BudgetCategory { Id = 55, BudgetId = 100, IsAllowance = true, LinkedBudgetId = 200 };

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(200)).ReturnsAsync(child);
    _mockBudgetRepository.Setup(x => x.GetLinkedCategoryForChildAsync(200)).ReturnsAsync(linkedCat);
    _mockAuthService.Setup(x => x.CanCreateFamilyBudgetAsync(userId))
      .ReturnsAsync(AuthorizationResult.Denied("Only Owner and BudgetAdmin can create family budgets", FamilyMemberRole.Member));

    // Act
    var act = () => _budgetService.UpdateChildBudgetAsync(200, userId, new UpdateChildBudgetRequest { FundingAmount = 400m });

    // Assert
    await act.Should().ThrowAsync<UnauthorizedAccessException>();
  }

  [Fact]
  public async Task GetBudgetByIdAsync_LinkedPersonalChild_TotalIncomeIsAllocationPlusOwnIncome()
  {
    // Arrange — B5: a linked Personal child's spendable = family allocation + the member's own income.
    var ownerId = 2;
    var child = new Budget
    {
      Id = 200, UserId = ownerId, FamilyId = 7, ParentBudgetId = 100, Type = BudgetType.Personal,
      Name = "Personal", Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = DateTime.UtcNow.Date, CurrentPeriodEnd = DateTime.UtcNow.Date.AddMonths(1),
      AllocatedAmount = 300m,
      Categories = new List<BudgetCategory>
      {
        new() { Id = 1, BudgetId = 200, CategoryId = (int)TransactionCategory.Salary, AllocatedAmount = 0m, IsIncome = true }
      }
    };
    var ownIncome = new Transaction
    {
      Id = 1, Amount = 500m, Type = TransactionType.Credit,
      CategoryId = (int)TransactionCategory.Salary, ClaimedByUserId = ownerId,
      Date = DateTime.UtcNow.Date.AddDays(-1), Account = new Account { Name = "Acct" }
    };

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(200)).ReturnsAsync(child);
    _mockBudgetRepository.Setup(x => x.GetLinkedCategoryForChildAsync(200))
      .ReturnsAsync(new BudgetCategory { IsAllowance = true, LinkedBudgetId = 200 });
    _mockBudgetPeriodService.Setup(x => x.ShouldResetBudget(It.IsAny<Budget>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
      .Returns(false);
    _mockTransactionRepository.Setup(x => x.GetByUserOrFamilyAsync(
        ownerId, 7, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction> { ownIncome });

    // Act
    var result = await _budgetService.GetBudgetByIdAsync(200, ownerId);

    // Assert
    result.Should().NotBeNull();
    result!.IsFamilyFunded.Should().BeTrue();
    result.TotalIncome.Should().Be(800m);   // 300 allocation + 500 own income
  }

  [Fact]
  public async Task GetBudgetByIdAsync_LinkedPersonalChild_BucketsSpendByPersonalCategory()
  {
    // Arrange — B6: a linked Personal child buckets claimed spend by PersonalUserCategoryId
    // (like an Allowance), not by the transaction's own CategoryId.
    var ownerId = 2;
    const int personalCatId = 5;                 // unified id = 1000 + 5 = 1005
    var child = new Budget
    {
      Id = 200, UserId = ownerId, FamilyId = 7, ParentBudgetId = 100, Type = BudgetType.Personal,
      Name = "Personal", Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = DateTime.UtcNow.Date, CurrentPeriodEnd = DateTime.UtcNow.Date.AddMonths(1),
      AllocatedAmount = 300m,
      Categories = new List<BudgetCategory>
      {
        new() { Id = 1, BudgetId = 200, CategoryId = 1000 + personalCatId, AllocatedAmount = 100m }
      }
    };
    var claimed = new Transaction
    {
      Id = 1, Amount = -80m, Type = TransactionType.Debit,
      CategoryId = (int)TransactionCategory.Groceries,       // family category, NOT the personal one
      ClaimedByUserId = ownerId, PersonalUserCategoryId = personalCatId,
      Date = DateTime.UtcNow.Date.AddDays(-1), Account = new Account { Name = "Acct" }
    };

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(200)).ReturnsAsync(child);
    _mockBudgetRepository.Setup(x => x.GetLinkedCategoryForChildAsync(200))
      .ReturnsAsync(new BudgetCategory { IsAllowance = true, LinkedBudgetId = 200 });
    _mockBudgetPeriodService.Setup(x => x.ShouldResetBudget(It.IsAny<Budget>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
      .Returns(false);
    _mockCategoryRepository.Setup(x => x.GetByIdsAsync(It.IsAny<IEnumerable<int>>()))
      .ReturnsAsync(new List<Category> { new() { Id = 1000 + personalCatId, Name = "Fun Money" } });
    _mockTransactionRepository.Setup(x => x.GetByUserOrFamilyAsync(
        ownerId, 7, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction> { claimed });

    // Act
    var result = await _budgetService.GetBudgetByIdAsync(200, ownerId);

    // Assert — the spend lands on the personal sub-category (1005), proving personal-key bucketing.
    result.Should().NotBeNull();
    var personalCategory = result!.Categories.Single(c => c.CategoryId == 1000 + personalCatId);
    personalCategory.SpentAmount.Should().Be(80m);
  }

  #endregion
}
