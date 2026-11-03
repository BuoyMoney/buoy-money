using FluentAssertions;
using Moq;
using Xunit;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class FamilyAuthorizationServiceTests
{
  private readonly Mock<IUserRepository> _userRepository = new();
  private readonly Mock<IBudgetRepository> _budgetRepository = new();
  private readonly Mock<ISavingsRepository> _savingsRepository = new();
  private readonly Mock<ITransactionRepository> _transactionRepository = new();
  private readonly Mock<IFamilyMemberRepository> _familyMemberRepository = new();
  private readonly Mock<IAccountRepository> _accountRepository = new();
  private readonly Mock<ILogger<FamilyAuthorizationService>> _logger = new();

  private FamilyAuthorizationService CreateService()
  {
    return new FamilyAuthorizationService(
      _userRepository.Object,
      _familyMemberRepository.Object,
      _budgetRepository.Object,
      _savingsRepository.Object,
      _transactionRepository.Object,
      _accountRepository.Object,
      _logger.Object);
  }

  #region CanAccessBudgetAsync Tests

  [Fact]
  public async Task CanAccessBudgetAsync_WhenUserOwnsBudget_ShouldReturnAuthorized()
  {
    // Arrange
    var userId = 1;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = null };
    var budget = new Budget { Id = budgetId, UserId = userId, FamilyId = null };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);

    var service = CreateService();

    // Act
    var result = await service.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Edit);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.Reason.Should().BeNull();
  }

  [Fact]
  public async Task CanAccessBudgetAsync_WhenOwnerEditsFamilyBudget_ShouldReturnAuthorized()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.Owner };
    var budget = new Budget { Id = budgetId, UserId = 2, FamilyId = familyId, Type = BudgetType.Family }; // Different user, same family

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Edit);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.UserRole.Should().Be(FamilyMemberRole.Owner);
  }

  [Fact]
  public async Task CanAccessBudgetAsync_WhenBudgetAdminEditsFamilyBudget_ShouldReturnAuthorized()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.BudgetAdmin };
    var budget = new Budget { Id = budgetId, UserId = 2, FamilyId = familyId, Type = BudgetType.Family };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Edit);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.UserRole.Should().Be(FamilyMemberRole.BudgetAdmin);
  }

  [Fact]
  public async Task CanAccessBudgetAsync_WhenMemberTriesToEditFamilyBudget_ShouldReturnDenied()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.Member };
    var budget = new Budget { Id = budgetId, UserId = 2, FamilyId = familyId, Type = BudgetType.Family }; // Family budget not owned by requesting user

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Edit);

    // Assert
    result.IsAuthorized.Should().BeFalse();
    result.UserRole.Should().Be(FamilyMemberRole.Member);
    result.Reason.Should().Match(r => r.Contains("Owner") || r.Contains("BudgetAdmin"));
  }

  [Fact]
  public async Task CanAccessBudgetAsync_WhenReadOnlyTriesToReadFamilyBudget_ShouldReturnAuthorized()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.ReadOnly };
    var budget = new Budget { Id = budgetId, UserId = 2, FamilyId = familyId };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Read);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.UserRole.Should().Be(FamilyMemberRole.ReadOnly);
  }

  [Fact]
  public async Task CanAccessBudgetAsync_WhenReadOnlyTriesToEditFamilyBudget_ShouldReturnDenied()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.ReadOnly };
    var budget = new Budget { Id = budgetId, UserId = 2, FamilyId = familyId };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Edit);

    // Assert
    result.IsAuthorized.Should().BeFalse();
    result.UserRole.Should().Be(FamilyMemberRole.ReadOnly);
  }

  [Fact]
  public async Task CanAccessBudgetAsync_WhenUserNotInFamily_ShouldReturnDenied()
  {
    // Arrange
    var userId = 1;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = null };
    var budget = new Budget { Id = budgetId, UserId = 2, FamilyId = 10 }; // Belongs to another user's family

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _budgetRepository.Setup(r => r.GetBudgetShareAsync(budgetId, userId)).ReturnsAsync((BudgetShare?)null);

    var service = CreateService();

    // Act
    var result = await service.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Read);

    // Assert
    result.IsAuthorized.Should().BeFalse();
    result.Reason.Should().Contain("family");
  }

  #endregion

  #region CanAccessGoalAsync Tests

  [Fact]
  public async Task CanAccessGoalAsync_WhenMemberAllocatesToFamilyGoal_ShouldReturnAuthorized()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var goalId = 50;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.Member };
    var goal = new SavingsGoal { Id = goalId, UserId = 2, FamilyId = familyId, Type = SavingsGoalType.Family };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _savingsRepository.Setup(r => r.GetByIdAsync(goalId)).ReturnsAsync(goal);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessGoalAsync(userId, goalId, ResourcePermission.Allocate);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.UserRole.Should().Be(FamilyMemberRole.Member);
  }

  [Fact]
  public async Task CanAccessGoalAsync_WhenMemberTriesToDeleteFamilyGoal_ShouldReturnDenied()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var goalId = 50;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.Member };
    var goal = new SavingsGoal { Id = goalId, UserId = 2, FamilyId = familyId, Type = SavingsGoalType.Family };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _savingsRepository.Setup(r => r.GetByIdAsync(goalId)).ReturnsAsync(goal);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessGoalAsync(userId, goalId, ResourcePermission.Delete);

    // Assert
    result.IsAuthorized.Should().BeFalse();
    result.Reason.Should().Match(r => r.Contains("Owner") || r.Contains("BudgetAdmin"));
  }

  [Fact]
  public async Task CanAccessGoalAsync_WhenReadOnlyTriesToAllocate_ShouldReturnDenied()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var goalId = 50;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.ReadOnly };
    var goal = new SavingsGoal { Id = goalId, UserId = 2, FamilyId = familyId, Type = SavingsGoalType.Family };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _savingsRepository.Setup(r => r.GetByIdAsync(goalId)).ReturnsAsync(goal);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessGoalAsync(userId, goalId, ResourcePermission.Allocate);

    // Assert
    result.IsAuthorized.Should().BeFalse();
    result.UserRole.Should().Be(FamilyMemberRole.ReadOnly);
  }

  #endregion

  #region CanAccessTransactionAsync Tests

  [Fact]
  public async Task CanAccessTransactionAsync_WhenMemberCategorizesFamilyTransaction_ShouldReturnAuthorized()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var transactionId = 200;
    var accountId = 500;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.Member };
    var account = new Account { Id = accountId, UserId = 2, FamilyId = familyId };
    var transaction = new Transaction { Id = transactionId, AccountId = accountId };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _transactionRepository.Setup(r => r.GetByIdAsync(transactionId)).ReturnsAsync(transaction);
    _accountRepository.Setup(r => r.GetByIdAsync(accountId)).ReturnsAsync(account);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessTransactionAsync(userId, transactionId, ResourcePermission.Edit);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.UserRole.Should().Be(FamilyMemberRole.Member);
  }

  [Fact]
  public async Task CanAccessTransactionAsync_WhenReadOnlyTriesToEdit_ShouldReturnDenied()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var transactionId = 200;
    var accountId = 500;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.ReadOnly };
    var account = new Account { Id = accountId, UserId = 2, FamilyId = familyId };
    var transaction = new Transaction { Id = transactionId, AccountId = accountId };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _transactionRepository.Setup(r => r.GetByIdAsync(transactionId)).ReturnsAsync(transaction);
    _accountRepository.Setup(r => r.GetByIdAsync(accountId)).ReturnsAsync(account);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanAccessTransactionAsync(userId, transactionId, ResourcePermission.Edit);

    // Assert
    result.IsAuthorized.Should().BeFalse();
    result.UserRole.Should().Be(FamilyMemberRole.ReadOnly);
  }

  #endregion

  #region CanManageBankConnectionsAsync Tests

  [Fact]
  public async Task CanManageBankConnectionsAsync_WhenOwner_ShouldReturnAuthorized()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.Owner };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanManageBankConnectionsAsync(userId);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.UserRole.Should().Be(FamilyMemberRole.Owner);
  }

  [Fact]
  public async Task CanManageBankConnectionsAsync_WhenBudgetAdmin_ShouldReturnAuthorized()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.BudgetAdmin };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanManageBankConnectionsAsync(userId);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.UserRole.Should().Be(FamilyMemberRole.BudgetAdmin);
  }

  [Fact]
  public async Task CanManageBankConnectionsAsync_WhenMember_ShouldReturnDenied()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.Member };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanManageBankConnectionsAsync(userId);

    // Assert
    result.IsAuthorized.Should().BeFalse();
    result.UserRole.Should().Be(FamilyMemberRole.Member);
    result.Reason.Should().Match(r => r.Contains("Budget Admin") || r.Contains("Owner"));
  }

  [Fact]
  public async Task CanManageBankConnectionsAsync_WhenReadOnly_ShouldReturnDenied()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.ReadOnly };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act
    var result = await service.CanManageBankConnectionsAsync(userId);

    // Assert
    result.IsAuthorized.Should().BeFalse();
    result.UserRole.Should().Be(FamilyMemberRole.ReadOnly);
  }

  [Fact]
  public async Task CanManageBankConnectionsAsync_WhenNotInFamily_ShouldReturnAuthorized()
  {
    // Arrange - User not in any family has full control of their own account
    var userId = 1;
    var user = new User { Id = userId, FamilyId = null };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);

    var service = CreateService();

    // Act
    var result = await service.CanManageBankConnectionsAsync(userId);

    // Assert
    result.IsAuthorized.Should().BeTrue();
    result.UserRole.Should().BeNull();
  }

  #endregion

  #region ValidateOrThrow Tests

  [Fact]
  public async Task ValidateBudgetAccessOrThrowAsync_WhenAuthorized_ShouldNotThrow()
  {
    // Arrange
    var userId = 1;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = null };
    var budget = new Budget { Id = budgetId, UserId = userId, FamilyId = null };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);

    var service = CreateService();

    // Act & Assert
    var exception = await Record.ExceptionAsync(() => 
      service.ValidateBudgetAccessOrThrowAsync(userId, budgetId, ResourcePermission.Edit));
    
    exception.Should().BeNull();
  }

  [Fact]
  public async Task ValidateBudgetAccessOrThrowAsync_WhenUnauthorized_ShouldThrowUnauthorizedAccessException()
  {
    // Arrange
    var userId = 1;
    var budgetId = 100;
    var user = new User { Id = userId, FamilyId = null };
    var budget = new Budget { Id = budgetId, UserId = 2, FamilyId = null }; // Belongs to different user

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _budgetRepository.Setup(r => r.GetByIdAsync(budgetId)).ReturnsAsync(budget);
    _budgetRepository.Setup(r => r.GetBudgetShareAsync(budgetId, userId)).ReturnsAsync((BudgetShare?)null);

    var service = CreateService();

    // Act & Assert
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
      service.ValidateBudgetAccessOrThrowAsync(userId, budgetId, ResourcePermission.Edit));
  }

  [Fact]
  public async Task ValidateBankConnectionAccessOrThrowAsync_WhenMember_ShouldThrowUnauthorizedAccessException()
  {
    // Arrange
    var userId = 1;
    var familyId = 10;
    var user = new User { Id = userId, FamilyId = familyId };
    var familyMember = new FamilyMember { UserId = userId, FamilyId = familyId, Role = FamilyMemberRole.Member };

    _userRepository.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _familyMemberRepository.Setup(r => r.GetByUserIdAsync(userId)).ReturnsAsync(familyMember);

    var service = CreateService();

    // Act & Assert
    var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
      service.ValidateBankConnectionAccessOrThrowAsync(userId));
    
    exception.Message.Should().Match(m => m.Contains("Budget Admin") || m.Contains("Owner"));
  }

  #endregion
}

