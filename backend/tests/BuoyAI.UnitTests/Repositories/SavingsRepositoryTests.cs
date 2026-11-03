using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;

namespace BuoyAI.UnitTests.Repositories;

public class SavingsRepositoryTests
{
    private BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private User CreateTestUser(int id)
    {
        return new User
        {
            Id = id,
            Username = $"user{id}",
            Email = $"user{id}@test.com",
            PasswordHash = "hash"
        };
    }

    private static Budget CreateTestBudget(int id, int userId)
    {
        return new Budget
        {
            Id = id,
            UserId = userId,
            Name = $"Test Budget {id}",
            Period = BudgetPeriod.Monthly,
            CurrentPeriodStart = DateTime.UtcNow.AddDays(-30),
            CurrentPeriodEnd = DateTime.UtcNow,
            LastReset = DateTime.UtcNow.AddDays(-30),
            TotalIncome = 1000m,
            TotalExpenses = 0m,
            IsActive = true
        };
    }

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsGoal_WithAllocations()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var budget = CreateTestBudget(1, 1);
        var goal = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Vacation Fund",
            Description = "Summer vacation",
            TargetAmount = 5000m,
            CurrentAmount = 1500m,
            AllocationPercentage = 20m,
            TargetDate = DateTime.UtcNow.AddMonths(6),
            IsActive = true,
            IsCompleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var allocation = new SavingsAllocation
        {
            Id = 1,
            SavingsGoalId = 1,
            BudgetId = 1,
            AllocatedAmount = 500m,
            LeftoverBudget = 200m,
            AllocationDate = DateTime.UtcNow,
            Source = "budget_surplus",
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Budgets.Add(budget);
        context.SavingsGoals.Add(goal);
        context.SavingsAllocations.Add(allocation);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Vacation Fund");
        result.CurrentAmount.Should().Be(1500m);
        result.Allocations.Should().HaveCount(1);
        result.Allocations.First().AllocatedAmount.Should().Be(500m);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsAllUserGoals_OrderedByCreatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var goal1 = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Emergency Fund",
            TargetAmount = 10000m,
            CurrentAmount = 3000m,
            AllocationPercentage = 30m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow
        };

        var goal2 = new SavingsGoal
        {
            Id = 2,
            UserId = 1,
            Name = "Car Fund",
            TargetAmount = 15000m,
            CurrentAmount = 0m,
            AllocationPercentage = 20m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow
        };

        var goal3 = new SavingsGoal
        {
            Id = 3,
            UserId = 1,
            Name = "Completed Goal",
            TargetAmount = 1000m,
            CurrentAmount = 1000m,
            AllocationPercentage = 0m,
            IsActive = false,
            IsCompleted = true,
            CreatedAt = DateTime.UtcNow.AddDays(-20),
            UpdatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.SavingsGoals.AddRange(goal1, goal2, goal3);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        result.Should().HaveCount(3);
        result.First().Name.Should().Be("Completed Goal"); // Oldest first
        result.Last().Name.Should().Be("Car Fund"); // Newest last
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsEmptyList_WhenNoGoals()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsGoal_AndSetsTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var goal = new SavingsGoal
        {
            UserId = 1,
            Name = "New Goal",
            TargetAmount = 2000m,
            CurrentAmount = 0m,
            AllocationPercentage = 15m,
            IsActive = true
        };

        // Act
        var result = await repository.CreateAsync(goal);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));

        var saved = await context.SavingsGoals.FindAsync(result.Id);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("New Goal");
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesGoal_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var goal = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Original Name",
            TargetAmount = 1000m,
            CurrentAmount = 500m,
            AllocationPercentage = 10m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };

        context.Users.Add(user);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        // Modify goal
        goal.Name = "Updated Name";
        goal.CurrentAmount = 750m;

        await Task.Delay(100); // Small delay to ensure UpdatedAt changes

        // Act
        var result = await repository.UpdateAsync(goal);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Updated Name");
        result.CurrentAmount.Should().Be(750m);
        result.UpdatedAt.Should().BeAfter(result.CreatedAt);

        var updated = await context.SavingsGoals.FindAsync(1);
        updated!.Name.Should().Be("Updated Name");
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesGoal_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var goal = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "To Delete",
            TargetAmount = 1000m,
            CurrentAmount = 0m,
            AllocationPercentage = 10m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        // Act
        await repository.DeleteAsync(1);

        // Assert
        var deleted = await context.SavingsGoals.FindAsync(1);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_DoesNothing_WhenGoalNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        // Act & Assert
        await repository.DeleteAsync(999); // Should not throw
    }

    #endregion

    #region CreateAllocationAsync Tests

    [Fact]
    public async Task CreateAllocationAsync_AddsAllocation_AndSetsCreatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var budget = CreateTestBudget(1, 1);
        var goal = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Goal",
            TargetAmount = 1000m,
            CurrentAmount = 0m,
            AllocationPercentage = 10m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Budgets.Add(budget);
        context.SavingsGoals.Add(goal);
        await context.SaveChangesAsync();

        var allocation = new SavingsAllocation
        {
            SavingsGoalId = 1,
            BudgetId = 1,
            AllocatedAmount = 250m,
            LeftoverBudget = 150m,
            AllocationDate = DateTime.UtcNow,
            Source = "budget_surplus"
        };

        // Act
        var result = await repository.CreateAllocationAsync(allocation);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        result.AllocatedAmount.Should().Be(250m);
    }

    #endregion

    #region GetAllocationsByGoalIdAsync Tests

    [Fact]
    public async Task GetAllocationsByGoalIdAsync_ReturnsAllocations_OrderedByDate()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var budget = CreateTestBudget(1, 1);
        var goal = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Goal",
            TargetAmount = 1000m,
            CurrentAmount = 0m,
            AllocationPercentage = 10m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var allocation1 = new SavingsAllocation
        {
            Id = 1,
            SavingsGoalId = 1,
            BudgetId = 1,
            AllocatedAmount = 100m,
            LeftoverBudget = 50m,
            AllocationDate = DateTime.UtcNow.AddDays(-5),
            Source = "budget_surplus",
            CreatedAt = DateTime.UtcNow
        };

        var allocation2 = new SavingsAllocation
        {
            Id = 2,
            SavingsGoalId = 1,
            BudgetId = 1,
            AllocatedAmount = 150m,
            LeftoverBudget = 75m,
            AllocationDate = DateTime.UtcNow.AddDays(-2),
            Source = "budget_surplus",
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Budgets.Add(budget);
        context.SavingsGoals.Add(goal);
        context.SavingsAllocations.AddRange(allocation1, allocation2);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetAllocationsByGoalIdAsync(1);

        // Assert
        result.Should().HaveCount(2);
        result.First().AllocatedAmount.Should().Be(100m); // Oldest first
        result.Last().AllocatedAmount.Should().Be(150m); // Newest last
    }

    #endregion

    #region GetAllocationsByUserIdAsync Tests

    [Fact]
    public async Task GetAllocationsByUserIdAsync_ReturnsAllUserAllocations()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var budget = CreateTestBudget(1, 1);
        var goal1 = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Goal 1",
            TargetAmount = 1000m,
            CurrentAmount = 0m,
            AllocationPercentage = 10m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var goal2 = new SavingsGoal
        {
            Id = 2,
            UserId = 1,
            Name = "Goal 2",
            TargetAmount = 2000m,
            CurrentAmount = 0m,
            AllocationPercentage = 15m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var allocation1 = new SavingsAllocation
        {
            Id = 1,
            SavingsGoalId = 1,
            BudgetId = 1,
            AllocatedAmount = 100m,
            LeftoverBudget = 50m,
            AllocationDate = DateTime.UtcNow.AddDays(-3),
            Source = "budget_surplus",
            CreatedAt = DateTime.UtcNow
        };

        var allocation2 = new SavingsAllocation
        {
            Id = 2,
            SavingsGoalId = 2,
            BudgetId = 1,
            AllocatedAmount = 200m,
            LeftoverBudget = 100m,
            AllocationDate = DateTime.UtcNow.AddDays(-1),
            Source = "budget_surplus",
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Budgets.Add(budget);
        context.SavingsGoals.AddRange(goal1, goal2);
        context.SavingsAllocations.AddRange(allocation1, allocation2);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetAllocationsByUserIdAsync(1);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(a => a.AllocatedAmount == 100m);
        result.Should().Contain(a => a.AllocatedAmount == 200m);
        result.All(a => a.SavingsGoal != null).Should().BeTrue(); // Navigation loaded
    }

    #endregion


    #region GetTotalSavedAmountAsync Tests

    [Fact]
    public async Task GetTotalSavedAmountAsync_ReturnsSumOfCurrentAmounts()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var goal1 = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Goal 1",
            TargetAmount = 5000m,
            CurrentAmount = 1500m,
            AllocationPercentage = 20m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var goal2 = new SavingsGoal
        {
            Id = 2,
            UserId = 1,
            Name = "Goal 2",
            TargetAmount = 3000m,
            CurrentAmount = 2000m,
            AllocationPercentage = 15m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.SavingsGoals.AddRange(goal1, goal2);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetTotalSavedAmountAsync(1);

        // Assert
        result.Should().Be(3500m); // 1500 + 2000
    }

    [Fact]
    public async Task GetTotalSavedAmountAsync_ReturnsZero_WhenNoGoals()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetTotalSavedAmountAsync(1);

        // Assert
        result.Should().Be(0m);
    }

    #endregion

    #region GetMonthlyGrowthAsync Tests

    [Fact]
    public async Task GetMonthlyGrowthAsync_ReturnsSumOfAllocationsFromLastMonth()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var budget = CreateTestBudget(1, 1);
        var goal = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Goal",
            TargetAmount = 5000m,
            CurrentAmount = 1000m,
            AllocationPercentage = 20m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var recentAllocation1 = new SavingsAllocation
        {
            Id = 1,
            SavingsGoalId = 1,
            BudgetId = 1,
            AllocatedAmount = 200m,
            LeftoverBudget = 100m,
            AllocationDate = DateTime.UtcNow.AddDays(-15), // Within last month
            Source = "budget_surplus",
            CreatedAt = DateTime.UtcNow
        };

        var recentAllocation2 = new SavingsAllocation
        {
            Id = 2,
            SavingsGoalId = 1,
            BudgetId = 1,
            AllocatedAmount = 300m,
            LeftoverBudget = 150m,
            AllocationDate = DateTime.UtcNow.AddDays(-5), // Within last month
            Source = "budget_surplus",
            CreatedAt = DateTime.UtcNow
        };

        var oldAllocation = new SavingsAllocation
        {
            Id = 3,
            SavingsGoalId = 1,
            BudgetId = 1,
            AllocatedAmount = 500m,
            LeftoverBudget = 200m,
            AllocationDate = DateTime.UtcNow.AddDays(-35), // Older than 1 month
            Source = "budget_surplus",
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.Budgets.Add(budget);
        context.SavingsGoals.Add(goal);
        context.SavingsAllocations.AddRange(recentAllocation1, recentAllocation2, oldAllocation);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetMonthlyGrowthAsync(1);

        // Assert
        result.Should().Be(500m); // 200 + 300, not including old allocation
    }

    #endregion

    #region GetActiveGoalsCountAsync Tests

    [Fact]
    public async Task GetActiveGoalsCountAsync_ReturnsCountOfActiveNotCompleted()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var activeGoal1 = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Active 1",
            TargetAmount = 1000m,
            CurrentAmount = 500m,
            AllocationPercentage = 10m,
            IsActive = true,
            IsCompleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var activeGoal2 = new SavingsGoal
        {
            Id = 2,
            UserId = 1,
            Name = "Active 2",
            TargetAmount = 2000m,
            CurrentAmount = 1000m,
            AllocationPercentage = 15m,
            IsActive = true,
            IsCompleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var completedGoal = new SavingsGoal
        {
            Id = 3,
            UserId = 1,
            Name = "Completed",
            TargetAmount = 1000m,
            CurrentAmount = 1000m,
            AllocationPercentage = 0m,
            IsActive = false,
            IsCompleted = true, // Should not be counted
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var inactiveGoal = new SavingsGoal
        {
            Id = 4,
            UserId = 1,
            Name = "Inactive",
            TargetAmount = 500m,
            CurrentAmount = 100m,
            AllocationPercentage = 0m,
            IsActive = false, // Should not be counted
            IsCompleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.SavingsGoals.AddRange(activeGoal1, activeGoal2, completedGoal, inactiveGoal);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetActiveGoalsCountAsync(1);

        // Assert
        result.Should().Be(2); // Only activeGoal1 and activeGoal2
    }

    #endregion

    #region GetCompletedGoalsCountAsync Tests

    [Fact]
    public async Task GetCompletedGoalsCountAsync_ReturnsCountOfCompletedGoals()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new SavingsRepository(context);

        var user = CreateTestUser(1);
        var completedGoal1 = new SavingsGoal
        {
            Id = 1,
            UserId = 1,
            Name = "Completed 1",
            TargetAmount = 1000m,
            CurrentAmount = 1000m,
            AllocationPercentage = 0m,
            IsActive = false,
            IsCompleted = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var completedGoal2 = new SavingsGoal
        {
            Id = 2,
            UserId = 1,
            Name = "Completed 2",
            TargetAmount = 2000m,
            CurrentAmount = 2000m,
            AllocationPercentage = 0m,
            IsActive = false,
            IsCompleted = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var activeGoal = new SavingsGoal
        {
            Id = 3,
            UserId = 1,
            Name = "Active",
            TargetAmount = 500m,
            CurrentAmount = 100m,
            AllocationPercentage = 10m,
            IsActive = true,
            IsCompleted = false, // Should not be counted
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Users.Add(user);
        context.SavingsGoals.AddRange(completedGoal1, completedGoal2, activeGoal);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetCompletedGoalsCountAsync(1);

        // Assert
        result.Should().Be(2); // Only completedGoal1 and completedGoal2
    }

    #endregion
}
