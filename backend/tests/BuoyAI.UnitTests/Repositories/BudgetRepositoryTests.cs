using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class BudgetRepositoryTests
{
    private static BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private static User CreateTestUser(int id, string username)
    {
        return new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static Budget CreateTestBudget(int id, int userId, string name, bool isActive = true)
    {
        return new Budget
        {
            Id = id,
            UserId = userId,
            Name = name,
            Period = BudgetPeriod.Monthly,
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1),
            LastReset = DateTime.UtcNow,
            AutoReset = true,
            TotalIncome = 5000m,
            TotalExpenses = 0m,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10)
        };
    }

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsUserBudgets_OrderedByCreatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget1 = CreateTestBudget(1, 1, "January Budget");
        budget1.CreatedAt = DateTime.UtcNow.AddDays(-20);
        var budget2 = CreateTestBudget(2, 1, "February Budget");
        budget2.CreatedAt = DateTime.UtcNow.AddDays(-10);

        context.Budgets.AddRange(budget1, budget2);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        var budgets = result.ToList();
        budgets.Should().HaveCount(2);
        budgets[0].Name.Should().Be("February Budget"); // Most recent first
        budgets[1].Name.Should().Be("January Budget");
    }

    [Fact]
    public async Task GetByUserIdAsync_IncludesCategories()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        budget.Categories = new List<BudgetCategory>
        {
            new BudgetCategory
            {
                Id = 1,
                BudgetId = 1,
                CategoryId = (int)TransactionCategory.Groceries,
                AllocatedAmount = 500m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        var budgets = result.ToList();
        budgets.Should().HaveCount(1);
        budgets[0].Categories.Should().HaveCount(1);
        budgets[0].Categories.First().CategoryId.Should().Be((int)TransactionCategory.Groceries);
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsEmpty_WhenNoUserBudgets()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(999);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsBudget_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Name.Should().Be("Test Budget");
        result.Period.Should().Be(BudgetPeriod.Monthly);
    }

    [Fact]
    public async Task GetByIdAsync_IncludesUserAndCategories()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        budget.Categories = new List<BudgetCategory>
        {
            new BudgetCategory
            {
                Id = 1,
                BudgetId = 1,
                CategoryId = (int)TransactionCategory.Shopping,
                AllocatedAmount = 300m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.User.Should().NotBeNull();
        result.User.Username.Should().Be("testuser");
        result.Categories.Should().HaveCount(1);
        result.Categories.First().CategoryId.Should().Be((int)TransactionCategory.Shopping);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetActiveByUserIdAsync Tests

    [Fact]
    public async Task GetActiveByUserIdAsync_ReturnsActiveBudget()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var activeBudget = CreateTestBudget(1, 1, "Active Budget", isActive: true);
        var inactiveBudget = CreateTestBudget(2, 1, "Inactive Budget", isActive: false);

        context.Budgets.AddRange(activeBudget, inactiveBudget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetActiveByUserIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Active Budget");
        result.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetActiveByUserIdAsync_IncludesCategories()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Active Budget", isActive: true);
        budget.Categories = new List<BudgetCategory>
        {
            new BudgetCategory
            {
                Id = 1,
                BudgetId = 1,
                CategoryId = (int)TransactionCategory.Transportation,
                AllocatedAmount = 200m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetActiveByUserIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Categories.Should().HaveCount(1);
        result.Categories.First().CategoryId.Should().Be((int)TransactionCategory.Transportation);
    }

    [Fact]
    public async Task GetActiveByUserIdAsync_ReturnsNull_WhenNoActiveBudget()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var inactiveBudget = CreateTestBudget(1, 1, "Inactive Budget", isActive: false);
        context.Budgets.Add(inactiveBudget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetActiveByUserIdAsync(1);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsBudget_AndSetsTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var budget = new Budget
        {
            UserId = 1,
            Name = "New Budget",
            Period = BudgetPeriod.Monthly,
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1),
            LastReset = DateTime.UtcNow,
            TotalIncome = 6000m,
            TotalExpenses = 0m,
            IsActive = true
        };

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.CreateAsync(budget);

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));

        var saved = await context.Budgets.FindAsync(result.Id);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("New Budget");
    }

    [Fact]
    public async Task CreateAsync_SetsCategoryTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var budget = new Budget
        {
            UserId = 1,
            Name = "Budget with Categories",
            Period = BudgetPeriod.Monthly,
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1),
            LastReset = DateTime.UtcNow,
            TotalIncome = 5000m,
            IsActive = true,
            Categories = new List<BudgetCategory>
            {
                new BudgetCategory
                {
                    CategoryId = (int)TransactionCategory.Groceries,
                    AllocatedAmount = 500m
                },
                new BudgetCategory
                {
                    CategoryId = (int)TransactionCategory.Entertainment,
                    AllocatedAmount = 200m
                }
            }
        };

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.CreateAsync(budget);

        // Assert
        result.Categories.Should().HaveCount(2);
        foreach (var category in result.Categories)
        {
            category.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
            category.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        }
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesBudget_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Original Name");
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        // Detach to avoid tracking conflicts
        context.Entry(budget).State = EntityState.Detached;

        var repository = new BudgetRepository(context);

        // Act
        await Task.Delay(100); // Ensure UpdatedAt changes
        budget.Name = "Updated Name";
        budget.TotalIncome = 7000m;
        var result = await repository.UpdateAsync(budget);

        // Assert
        result.Name.Should().Be("Updated Name");
        result.TotalIncome.Should().Be(7000m);
        result.UpdatedAt.Should().BeAfter(result.CreatedAt);

        var updated = await context.Budgets.FindAsync(1);
        updated!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateAsync_UpdatesBudgetPeriod()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        budget.Period = BudgetPeriod.Monthly;
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        context.Entry(budget).State = EntityState.Detached;

        var repository = new BudgetRepository(context);

        // Act
        budget.Period = BudgetPeriod.Weekly;
        budget.CurrentPeriodStart = DateTime.UtcNow;
        budget.CurrentPeriodEnd = DateTime.UtcNow.AddDays(7);
        await repository.UpdateAsync(budget);

        // Assert
        var updated = await context.Budgets.FindAsync(1);
        updated!.Period.Should().Be(BudgetPeriod.Weekly);
        updated.CurrentPeriodEnd.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromSeconds(1));
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesBudget_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        await repository.DeleteAsync(1);

        // Assert
        var deleted = await context.Budgets.FindAsync(1);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenBudgetNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new BudgetRepository(context);

        // Act & Assert
        await repository.Invoking(r => r.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    #endregion

    #region GetBudgetCategoryAsync Tests

    [Fact]
    public async Task GetBudgetCategoryAsync_ReturnsCategory_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        budget.Categories = new List<BudgetCategory>
        {
            new BudgetCategory
            {
                Id = 1,
                BudgetId = 1,
                CategoryId = (int)TransactionCategory.Healthcare,
                AllocatedAmount = 400m,
                AllowRollover = true,
                RolloverBalance = 50m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetBudgetCategoryAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.CategoryId.Should().Be((int)TransactionCategory.Healthcare);
        result.AllocatedAmount.Should().Be(400m);
        result.AllowRollover.Should().BeTrue();
        result.RolloverBalance.Should().Be(50m);
    }

    [Fact]
    public async Task GetBudgetCategoryAsync_IncludesBudget()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        budget.Categories = new List<BudgetCategory>
        {
            new BudgetCategory
            {
                Id = 1,
                BudgetId = 1,
                CategoryId = (int)TransactionCategory.Utilities,
                AllocatedAmount = 250m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetBudgetCategoryAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Budget.Should().NotBeNull();
        result.Budget.Name.Should().Be("Test Budget");
    }

    [Fact]
    public async Task GetBudgetCategoryAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new BudgetRepository(context);

        // Act
        var result = await repository.GetBudgetCategoryAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region UpdateBudgetCategoryAsync Tests

    [Fact]
    public async Task UpdateBudgetCategoryAsync_UpdatesCategory_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        var category = new BudgetCategory
        {
            Id = 1,
            BudgetId = 1,
            CategoryId = (int)TransactionCategory.Groceries,
            AllocatedAmount = 500m,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };
        budget.Categories = new List<BudgetCategory> { category };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        context.Entry(category).State = EntityState.Detached;

        var repository = new BudgetRepository(context);

        // Act
        await Task.Delay(100); // Ensure UpdatedAt changes
        category.AllocatedAmount = 600m;
        var result = await repository.UpdateBudgetCategoryAsync(category);

        // Assert
        result.AllocatedAmount.Should().Be(600m);
        result.UpdatedAt.Should().BeAfter(result.CreatedAt);

        var updated = await context.BudgetCategories.FindAsync(1);
        updated!.AllocatedAmount.Should().Be(600m);
    }

    [Fact]
    public async Task UpdateBudgetCategoryAsync_UpdatesRolloverFields()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        var category = new BudgetCategory
        {
            Id = 1,
            BudgetId = 1,
            CategoryId = (int)TransactionCategory.Shopping,
            AllocatedAmount = 300m,
            AllowRollover = false,
            RolloverBalance = 0m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        budget.Categories = new List<BudgetCategory> { category };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        context.Entry(category).State = EntityState.Detached;

        var repository = new BudgetRepository(context);

        // Act
        category.AllowRollover = true;
        category.RolloverBalance = 100m;
        await repository.UpdateBudgetCategoryAsync(category);

        // Assert
        var updated = await context.BudgetCategories.FindAsync(1);
        updated!.AllowRollover.Should().BeTrue();
        updated.RolloverBalance.Should().Be(100m);
    }

    [Fact]
    public async Task UpdateBudgetCategoryAsync_UpdatesCountUpFields()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var budget = CreateTestBudget(1, 1, "Test Budget");
        var category = new BudgetCategory
        {
            Id = 1,
            BudgetId = 1,
            CategoryId = (int)TransactionCategory.Entertainment,
            AllocatedAmount = 200m,
            IsCountUpEnabled = false,
            DailyAllowance = 0m,
            EarnedToDate = 0m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        budget.Categories = new List<BudgetCategory> { category };

        context.Budgets.Add(budget);
        await context.SaveChangesAsync();

        context.Entry(category).State = EntityState.Detached;

        var repository = new BudgetRepository(context);

        // Act
        category.IsCountUpEnabled = true;
        category.DailyAllowance = 10m;
        category.EarnedToDate = 150m;
        category.LastAllowanceCalculation = DateTime.UtcNow;
        await repository.UpdateBudgetCategoryAsync(category);

        // Assert
        var updated = await context.BudgetCategories.FindAsync(1);
        updated!.IsCountUpEnabled.Should().BeTrue();
        updated.DailyAllowance.Should().Be(10m);
        updated.EarnedToDate.Should().Be(150m);
    }

    #endregion
}
