using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class OnboardingQuizRepositoryTests
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
            PasswordHash = "hashedpassword"
        };
    }

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsQuiz_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var quiz = new UserOnboardingQuiz
        {
            UserId = user.Id,
            PrimaryFinancialGoal = "save_more",
            BiggestChallenge = "sticking_to_budget",
            SpendingPersonality = "impulse_buyer",
            SubscriptionAwareness = "know_most",
            SavingsBarriers = "[\"bad_habits\",\"lifestyle_inflation\"]",
            PreferredInsightType = "spending_alerts",
            FinancialExperience = "beginner",
            HasUsedBudgetingApps = false,
            AdditionalNotes = "Need help tracking subscriptions",
            Version = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };
        context.UserOnboardingQuizzes.Add(quiz);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByUserIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(quiz.Id);
        result.UserId.Should().Be(user.Id);
        result.PrimaryFinancialGoal.Should().Be("save_more");
        result.BiggestChallenge.Should().Be("sticking_to_budget");
        result.SpendingPersonality.Should().Be("impulse_buyer");
        result.SubscriptionAwareness.Should().Be("know_most");
        result.SavingsBarriers.Should().Be("[\"bad_habits\",\"lifestyle_inflation\"]");
        result.PreferredInsightType.Should().Be("spending_alerts");
        result.FinancialExperience.Should().Be("beginner");
        result.HasUsedBudgetingApps.Should().BeFalse();
        result.AdditionalNotes.Should().Be("Need help tracking subscriptions");
        result.Version.Should().Be(1);
        result.User.Should().NotBeNull();
        result.User.Email.Should().Be("user1@test.com");
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetLatestByUserIdAsync Tests

    [Fact]
    public async Task GetLatestByUserIdAsync_ReturnsLatestVersion()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var oldQuiz = new UserOnboardingQuiz
        {
            UserId = user.Id,
            PrimaryFinancialGoal = "save_more",
            Version = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            UpdatedAt = DateTime.UtcNow.AddDays(-30)
        };

        var latestQuiz = new UserOnboardingQuiz
        {
            UserId = user.Id,
            PrimaryFinancialGoal = "budget_better",
            Version = 2,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        };

        context.UserOnboardingQuizzes.AddRange(oldQuiz, latestQuiz);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetLatestByUserIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Version.Should().Be(2);
        result.PrimaryFinancialGoal.Should().Be("budget_better");
    }

    [Fact]
    public async Task GetLatestByUserIdAsync_ReturnsNull_WhenNoQuizzes()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        // Act
        var result = await repository.GetLatestByUserIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestByUserIdAsync_FiltersCorrectlyByUser()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        context.Users.AddRange(user1, user2);

        var user1Quiz = new UserOnboardingQuiz
        {
            UserId = user1.Id,
            PrimaryFinancialGoal = "save_more",
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var user2Quiz = new UserOnboardingQuiz
        {
            UserId = user2.Id,
            PrimaryFinancialGoal = "budget_better",
            Version = 3,
            CreatedAt = DateTime.UtcNow.AddDays(1), // More recent and higher version
            UpdatedAt = DateTime.UtcNow.AddDays(1)
        };

        context.UserOnboardingQuizzes.AddRange(user1Quiz, user2Quiz);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetLatestByUserIdAsync(user1.Id);

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(user1.Id);
        result.PrimaryFinancialGoal.Should().Be("save_more");
        result.Version.Should().Be(1);
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsQuiz_AndSetsVersionToOne()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var quiz = new UserOnboardingQuiz
        {
            UserId = user.Id,
            PrimaryFinancialGoal = "reduce_debt",
            BiggestChallenge = "overspending",
            SpendingPersonality = "careful_planner",
            SubscriptionAwareness = "know_all",
            SavingsBarriers = "[\"irregular_income\"]",
            PreferredInsightType = "budget_optimization",
            FinancialExperience = "intermediate",
            HasUsedBudgetingApps = true,
            AdditionalNotes = "Looking for debt reduction strategies"
        };

        // Act
        var result = await repository.CreateAsync(quiz);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.Version.Should().Be(1);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));

        var savedQuiz = await context.UserOnboardingQuizzes.FindAsync(result.Id);
        savedQuiz.Should().NotBeNull();
        savedQuiz!.PrimaryFinancialGoal.Should().Be("reduce_debt");
        savedQuiz.AdditionalNotes.Should().Be("Looking for debt reduction strategies");
    }

    [Fact]
    public async Task CreateAsync_IncrementsVersion_WhenQuizAlreadyExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var existingQuiz = new UserOnboardingQuiz
        {
            UserId = user.Id,
            PrimaryFinancialGoal = "save_more",
            Version = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10)
        };
        context.UserOnboardingQuizzes.Add(existingQuiz);
        await context.SaveChangesAsync();

        var newQuiz = new UserOnboardingQuiz
        {
            UserId = user.Id,
            PrimaryFinancialGoal = "budget_better"
        };

        // Act
        var result = await repository.CreateAsync(newQuiz);

        // Assert
        result.Should().NotBeNull();
        result.Version.Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_VersionIsPerUser_NotGlobal()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        context.Users.AddRange(user1, user2);

        var user1Quiz = new UserOnboardingQuiz
        {
            UserId = user1.Id,
            PrimaryFinancialGoal = "save_more",
            Version = 5,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10)
        };
        context.UserOnboardingQuizzes.Add(user1Quiz);
        await context.SaveChangesAsync();

        var user2Quiz = new UserOnboardingQuiz
        {
            UserId = user2.Id,
            PrimaryFinancialGoal = "budget_better"
        };

        // Act
        var result = await repository.CreateAsync(user2Quiz);

        // Assert
        result.Should().NotBeNull();
        result.Version.Should().Be(1); // User 2's first quiz should be version 1, not 6
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesQuiz_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var originalCreatedAt = DateTime.UtcNow.AddDays(-5);
        var quiz = new UserOnboardingQuiz
        {
            UserId = user.Id,
            PrimaryFinancialGoal = "save_more",
            BiggestChallenge = "sticking_to_budget",
            Version = 1,
            CreatedAt = originalCreatedAt,
            UpdatedAt = originalCreatedAt
        };
        context.UserOnboardingQuizzes.Add(quiz);
        await context.SaveChangesAsync();

        // Detach to simulate fresh retrieval
        context.Entry(quiz).State = EntityState.Detached;

        // Act
        quiz.BiggestChallenge = "overspending";
        quiz.AdditionalNotes = "Updated challenge";
        var result = await repository.UpdateAsync(quiz);

        // Assert
        result.Should().NotBeNull();
        result.BiggestChallenge.Should().Be("overspending");
        result.AdditionalNotes.Should().Be("Updated challenge");
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        result.CreatedAt.Should().Be(originalCreatedAt); // CreatedAt should not change
    }

    #endregion

    #region GetVersionHistoryByUserIdAsync Tests

    [Fact]
    public async Task GetVersionHistoryByUserIdAsync_ReturnsAllVersions_OrderedByVersionDescending()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var quizzes = new[]
        {
            new UserOnboardingQuiz
            {
                UserId = user.Id,
                PrimaryFinancialGoal = "save_more",
                Version = 1,
                CreatedAt = DateTime.UtcNow.AddDays(-30),
                UpdatedAt = DateTime.UtcNow.AddDays(-30)
            },
            new UserOnboardingQuiz
            {
                UserId = user.Id,
                PrimaryFinancialGoal = "budget_better",
                Version = 2,
                CreatedAt = DateTime.UtcNow.AddDays(-15),
                UpdatedAt = DateTime.UtcNow.AddDays(-15)
            },
            new UserOnboardingQuiz
            {
                UserId = user.Id,
                PrimaryFinancialGoal = "reduce_debt",
                Version = 3,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };
        context.UserOnboardingQuizzes.AddRange(quizzes);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetVersionHistoryByUserIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(3);
        result.ElementAt(0).Version.Should().Be(3);
        result.ElementAt(0).PrimaryFinancialGoal.Should().Be("reduce_debt");
        result.ElementAt(1).Version.Should().Be(2);
        result.ElementAt(1).PrimaryFinancialGoal.Should().Be("budget_better");
        result.ElementAt(2).Version.Should().Be(1);
        result.ElementAt(2).PrimaryFinancialGoal.Should().Be("save_more");
    }

    [Fact]
    public async Task GetVersionHistoryByUserIdAsync_ReturnsEmptyList_WhenNoQuizzes()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        // Act
        var result = await repository.GetVersionHistoryByUserIdAsync(999);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVersionHistoryByUserIdAsync_FiltersCorrectlyByUser()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        context.Users.AddRange(user1, user2);

        var user1Quizzes = new[]
        {
            new UserOnboardingQuiz
            {
                UserId = user1.Id,
                PrimaryFinancialGoal = "save_more",
                Version = 1,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                UpdatedAt = DateTime.UtcNow.AddDays(-10)
            },
            new UserOnboardingQuiz
            {
                UserId = user1.Id,
                PrimaryFinancialGoal = "budget_better",
                Version = 2,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        var user2Quiz = new UserOnboardingQuiz
        {
            UserId = user2.Id,
            PrimaryFinancialGoal = "reduce_debt",
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.UserOnboardingQuizzes.AddRange(user1Quizzes);
        context.UserOnboardingQuizzes.Add(user2Quiz);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetVersionHistoryByUserIdAsync(user1.Id);

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(q => q.UserId == user1.Id);
    }

    #endregion

    #region HasCompletedQuizAsync Tests

    [Fact]
    public async Task HasCompletedQuizAsync_ReturnsTrue_WhenQuizExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var quiz = new UserOnboardingQuiz
        {
            UserId = user.Id,
            PrimaryFinancialGoal = "save_more",
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.UserOnboardingQuizzes.Add(quiz);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.HasCompletedQuizAsync(user.Id);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasCompletedQuizAsync_ReturnsFalse_WhenNoQuiz()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new OnboardingQuizRepository(context);

        // Act
        var result = await repository.HasCompletedQuizAsync(999);

        // Assert
        result.Should().BeFalse();
    }

    #endregion
}
