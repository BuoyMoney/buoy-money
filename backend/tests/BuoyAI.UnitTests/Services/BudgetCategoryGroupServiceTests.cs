using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class BudgetCategoryGroupServiceTests : IDisposable
{
  private readonly Mock<IBudgetRepository> _mockBudgetRepository;
  private readonly Mock<IUserRepository> _mockUserRepository;
  private readonly Mock<ILogger<BudgetCategoryGroupService>> _mockLogger;
  private readonly BuoyAIDbContext _context;
  private readonly BudgetCategoryGroupService _service;

  public BudgetCategoryGroupServiceTests()
  {
    _mockBudgetRepository = new Mock<IBudgetRepository>();
    _mockUserRepository = new Mock<IUserRepository>();
    _mockLogger = new Mock<ILogger<BudgetCategoryGroupService>>();
    
    // Setup in-memory database
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
    _context = new BuoyAIDbContext(options);
    
    // Setup default HasAccessAsync to return true (can be overridden in specific tests)
    _mockBudgetRepository.Setup(x => x.HasAccessAsync(It.IsAny<int>(), It.IsAny<int>()))
      .ReturnsAsync(true);
    
    // Mock for budget spending service
    var mockSpendingService = new Mock<IBudgetSpendingService>();
    
    _service = new BudgetCategoryGroupService(
      _context,
      _mockLogger.Object,
      _mockUserRepository.Object,
      _mockBudgetRepository.Object,
      mockSpendingService.Object);
  }

  public void Dispose()
  {
    _context.Dispose();
  }

  #region CreateCategoryGroupAsync Tests

  [Fact]
  public async Task CreateCategoryGroupAsync_WithValidData_ShouldCreateGroup()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId);
    budget.Id = 1;
    
    _context.Budgets.Add(budget);
    await _context.SaveChangesAsync();

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budget.Id))
      .ReturnsAsync(budget);

    var request = new CreateCategoryGroupRequest
    {
      Name = "Housing Expenses",
      Icon = "🏠",
      SortOrder = 1
    };

    // Act
    var result = await _service.CreateCategoryGroupAsync(budget.Id, userId, request);

    // Assert
    result.Should().NotBeNull();
    result.Name.Should().Be("Housing Expenses");
    result.Icon.Should().Be("🏠");
    result.SortOrder.Should().Be(1);
    
    var createdGroup = await _context.CategoryGroups.FirstOrDefaultAsync(g => g.Name == "Housing Expenses");
    createdGroup.Should().NotBeNull();
  }

  [Fact]
  public async Task CreateCategoryGroupAsync_WithUnauthorizedUser_ShouldThrow()
  {
    // Arrange
    var ownerId = 1;
    var otherUserId = 2;
    var budget = CreateTestBudget(ownerId);
    budget.Id = 1;
    
    _context.Budgets.Add(budget);
    await _context.SaveChangesAsync();

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budget.Id))
      .ReturnsAsync(budget);
    
    // Override the default HasAccessAsync to return false for this specific test
    _mockBudgetRepository.Setup(x => x.HasAccessAsync(budget.Id, otherUserId))
      .ReturnsAsync(false);

    var request = new CreateCategoryGroupRequest
    {
      Name = "Housing Expenses",
      Icon = "🏠",
      SortOrder = 1
    };

    // Act & Assert
    var act = () => _service.CreateCategoryGroupAsync(budget.Id, otherUserId, request);
    await act.Should().ThrowAsync<UnauthorizedAccessException>();
  }

  #endregion

  #region UpdateCategoryGroupAsync Tests

  [Fact]
  public async Task UpdateCategoryGroupAsync_WithValidData_ShouldUpdateGroup()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId);
    budget.Id = 1;
    
    var group = new CategoryGroup
    {
      Id = 1,
      UserId = userId, // User-created group
      Name = "Old Name",
      SortOrder = 1
    };

    _context.Budgets.Add(budget);
    _context.CategoryGroups.Add(group);
    await _context.SaveChangesAsync();

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budget.Id))
      .ReturnsAsync(budget);

    var request = new UpdateCategoryGroupRequest
    {
      Name = "New Name",
      Icon = "🌟"
    };

    // Act
    var result = await _service.UpdateCategoryGroupAsync(group.Id, userId, request);

    // Assert
    result.Should().NotBeNull();
    result.Name.Should().Be("New Name");
    result.Icon.Should().Be("🌟");
  }

  [Fact]
  public async Task UpdateCategoryGroupAsync_SystemGroup_ShouldThrow()
  {
    // Arrange
    var userId = 1;
    
    var systemGroup = new CategoryGroup
    {
      Id = 1,
      UserId = null, // System group - no user ownership
      Name = "Necessities",
      SortOrder = 1
    };

    _context.CategoryGroups.Add(systemGroup);
    await _context.SaveChangesAsync();

    var request = new UpdateCategoryGroupRequest
    {
      Name = "Trying to rename system group",
      Icon = "🚫"
    };

    // Act & Assert
    var act = () => _service.UpdateCategoryGroupAsync(systemGroup.Id, userId, request);
    await act.Should().ThrowAsync<InvalidOperationException>();
  }

  #endregion

  #region DeleteCategoryGroupAsync Tests

  [Fact]
  public async Task DeleteCategoryGroupAsync_WithValidGroup_ShouldDeleteAndMoveCategories()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId);
    budget.Id = 1;
    
    var groupToDelete = new CategoryGroup
    {
      Id = 1,
      UserId = userId,
      Name = "Custom Group",
      SortOrder = 1
    };
    
    // "Other" group must have ID matching the constant
    var otherGroup = new CategoryGroup
    {
      Id = CategoryGroup.OtherGroupId,  // Use constant for system "Other" group ID
      UserId = null,
      Name = "Other",
      SortOrder = 99
    };

    // Add a budget category in the group to delete
    var budgetCategory = new BudgetCategory
    {
      Id = 1,
      BudgetId = budget.Id,
      CategoryId = (int)TransactionCategory.Groceries,
      AllocatedAmount = 500,
      GroupId = groupToDelete.Id
    };

    _context.Budgets.Add(budget);
    _context.CategoryGroups.Add(groupToDelete);
    _context.CategoryGroups.Add(otherGroup);
    _context.BudgetCategories.Add(budgetCategory);
    await _context.SaveChangesAsync();

    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budget.Id))
      .ReturnsAsync(budget);

    // Act
    await _service.DeleteCategoryGroupAsync(groupToDelete.Id, userId);

    // Assert
    var deletedGroup = await _context.CategoryGroups.FindAsync(groupToDelete.Id);
    deletedGroup.Should().BeNull();

    var movedCategory = await _context.BudgetCategories.FindAsync(budgetCategory.Id);
    movedCategory.Should().NotBeNull();
    movedCategory!.GroupId.Should().Be(9);  // Moved to "Other" group (ID 9)
  }

  #endregion

  #region GetCategoryGroupsAsync Tests

  [Fact]
  public async Task GetCategoryGroupsAsync_ShouldReturnUserAndSystemGroups()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId);
    budget.Id = 1;
    
    var systemGroup = new CategoryGroup
    {
      Id = 1,
      UserId = null, // System group
      Name = "Necessities",
      SortOrder = 1
    };
    
    var userGroup = new CategoryGroup
    {
      Id = 2,
      UserId = userId, // User's custom group
      Name = "My Custom Group",
      SortOrder = 2
    };
    
    var otherUserGroup = new CategoryGroup
    {
      Id = 3,
      UserId = 999, // Another user's group
      Name = "Someone Else's Group",
      SortOrder = 3
    };

    _context.Budgets.Add(budget);
    _context.CategoryGroups.AddRange(systemGroup, userGroup, otherUserGroup);
    await _context.SaveChangesAsync();

    // Setup mock for authorization check
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budget.Id))
      .ReturnsAsync(budget);

    // Act
    var result = await _service.GetCategoryGroupsAsync(budget.Id, userId);

    // Assert
    result.Should().HaveCount(2);
    result.Should().Contain(g => g.Name == "Necessities");
    result.Should().Contain(g => g.Name == "My Custom Group");
    result.Should().NotContain(g => g.Name == "Someone Else's Group");
  }

  #endregion

  #region ReorderCategoryGroupsAsync Tests

  [Fact]
  public async Task ReorderCategoryGroupsAsync_WithValidOrder_ShouldReorder()
  {
    // Arrange
    var userId = 1;
    var budget = CreateTestBudget(userId);
    budget.Id = 1;
    
    var group1 = new CategoryGroup { Id = 1, UserId = userId, Name = "Group 1", SortOrder = 1 };
    var group2 = new CategoryGroup { Id = 2, UserId = userId, Name = "Group 2", SortOrder = 2 };
    var group3 = new CategoryGroup { Id = 3, UserId = userId, Name = "Group 3", SortOrder = 3 };

    _context.Budgets.Add(budget);
    _context.CategoryGroups.AddRange(group1, group2, group3);
    await _context.SaveChangesAsync();

    // Setup mock for authorization check
    _mockBudgetRepository.Setup(x => x.GetByIdAsync(budget.Id))
      .ReturnsAsync(budget);

    var request = new ReorderCategoryGroupsRequest
    {
      GroupIds = new List<int> { 3, 1, 2 } // Reorder to Group 3, Group 1, Group 2
    };

    // Act
    await _service.ReorderCategoryGroupsAsync(budget.Id, userId, request);

    // Assert
    var reorderedGroups = await _context.CategoryGroups
      .Where(g => g.UserId == userId)
      .OrderBy(g => g.SortOrder)
      .ToListAsync();

    reorderedGroups[0].Id.Should().Be(3);
    reorderedGroups[1].Id.Should().Be(1);
    reorderedGroups[2].Id.Should().Be(2);
  }

  [Fact]
  public async Task ReorderCategoryGroupsAsync_IgnoresSystemAndForeignGroups()
  {
    // Arrange — the caller smuggles a system group and another user's private group into GroupIds.
    var userId = 1;
    var budget = CreateTestBudget(userId);
    budget.Id = 1;

    var ownGroup = new CategoryGroup { Id = 10, UserId = userId, Name = "Mine", SortOrder = 5 };
    var systemGroup = new CategoryGroup { Id = 11, UserId = null, FamilyId = null, Name = "System", SortOrder = 5 };
    var foreignGroup = new CategoryGroup { Id = 12, UserId = 99, Name = "Theirs", SortOrder = 5 };

    _context.Budgets.Add(budget);
    _context.CategoryGroups.AddRange(ownGroup, systemGroup, foreignGroup);
    await _context.SaveChangesAsync();

    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync(new User { Id = userId, FamilyId = null });

    var request = new ReorderCategoryGroupsRequest
    {
      GroupIds = new List<int> { 11, 12, 10 } // attempt to reorder system + foreign to the front
    };

    // Act
    await _service.ReorderCategoryGroupsAsync(budget.Id, userId, request);

    // Assert — only the caller's own group is repositioned; the system and foreign groups are untouched.
    (await _context.CategoryGroups.FindAsync(10))!.SortOrder.Should().Be(2);
    (await _context.CategoryGroups.FindAsync(11))!.SortOrder.Should().Be(5);
    (await _context.CategoryGroups.FindAsync(12))!.SortOrder.Should().Be(5);
  }

  #endregion

  #region MoveCategoryToGroupAsync Tests

  [Fact]
  public async Task MoveCategoryToGroupAsync_ToForeignGroup_ThrowsUnauthorizedAccessException()
  {
    // Arrange — user owns the category's budget but targets another user's private group.
    var userId = 1;
    var budget = CreateTestBudget(userId);
    budget.Id = 1;
    var category = new BudgetCategory
    {
      Id = 50,
      BudgetId = 1,
      CategoryId = (int)TransactionCategory.Groceries,
      AllocatedAmount = 100m
    };
    budget.Categories.Add(category);
    var foreignGroup = new CategoryGroup { Id = 77, UserId = 99, Name = "Secret", SortOrder = 1 };

    _context.Budgets.Add(budget);
    _context.CategoryGroups.Add(foreignGroup);
    await _context.SaveChangesAsync();

    _mockUserRepository.Setup(x => x.GetByIdAsync(userId))
      .ReturnsAsync(new User { Id = userId, FamilyId = null });

    var request = new MoveCategoryToGroupRequest { GroupId = 77 };

    // Act & Assert
    await Assert.ThrowsAsync<UnauthorizedAccessException>(
      () => _service.MoveCategoryToGroupAsync(category.Id, userId, request));
  }

  #endregion

  #region Helper Methods

  private static Budget CreateTestBudget(int userId)
  {
    return new Budget
    {
      UserId = userId,
      Name = "Test Budget",
      IsActive = true,
      Period = BudgetPeriod.Monthly,
      CurrentPeriodStart = new DateTime(2024, 1, 1),
      CurrentPeriodEnd = new DateTime(2024, 1, 31),
      Categories = new List<BudgetCategory>()
    };
  }

  #endregion
}
