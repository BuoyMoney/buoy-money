using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class OnboardingQuizServiceTests
{
    private readonly Mock<IOnboardingQuizRepository> _mockQuizRepository;
    private readonly Mock<IUserRepository> _mockUserRepository;
    private readonly Mock<ILogger<OnboardingQuizService>> _mockLogger;
    private readonly OnboardingQuizService _service;

    public OnboardingQuizServiceTests()
    {
        _mockQuizRepository = new Mock<IOnboardingQuizRepository>();
        _mockUserRepository = new Mock<IUserRepository>();
        _mockLogger = new Mock<ILogger<OnboardingQuizService>>();

        _service = new OnboardingQuizService(
            _mockQuizRepository.Object,
            _mockUserRepository.Object,
            _mockLogger.Object
        );
    }

    #region SubmitQuizAsync Tests

    [Fact]
    public async Task SubmitQuizAsync_WithValidSubmission_ShouldCreateQuizAndReturnDto()
    {
        // Arrange
        var userId = 1;
        var submission = new OnboardingQuizSubmissionDto
        {
            PrimaryFinancialGoal = "save_more",
            BiggestChallenge = "sticking_to_budget",
            SpendingPersonality = "impulse_buyer",
            SubscriptionAwareness = "no_idea",
            SavingsBarriers = "[\"bad_habits\", \"low_income\"]",
            PreferredInsightType = "actionable_tips",
            FinancialExperience = "beginner",
            HasUsedBudgetingApps = false,
            AdditionalNotes = "Need help with impulse buying"
        };

        var user = new User { Id = userId, Email = "test@example.com" };
        var createdQuiz = new UserOnboardingQuiz
        {
            Id = 1,
            UserId = userId,
            PrimaryFinancialGoal = submission.PrimaryFinancialGoal,
            BiggestChallenge = submission.BiggestChallenge,
            SpendingPersonality = submission.SpendingPersonality,
            SubscriptionAwareness = submission.SubscriptionAwareness,
            SavingsBarriers = submission.SavingsBarriers,
            PreferredInsightType = submission.PreferredInsightType,
            FinancialExperience = submission.FinancialExperience,
            HasUsedBudgetingApps = submission.HasUsedBudgetingApps,
            AdditionalNotes = submission.AdditionalNotes,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync(user);
        _mockQuizRepository.Setup(x => x.CreateAsync(It.IsAny<UserOnboardingQuiz>())).ReturnsAsync(createdQuiz);

        // Act
        var result = await _service.SubmitQuizAsync(userId, submission);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(userId);
        result.PrimaryFinancialGoal.Should().Be(submission.PrimaryFinancialGoal);
        result.BiggestChallenge.Should().Be(submission.BiggestChallenge);
        result.Version.Should().Be(1);
        _mockQuizRepository.Verify(x => x.CreateAsync(It.Is<UserOnboardingQuiz>(q =>
            q.UserId == userId &&
            q.PrimaryFinancialGoal == submission.PrimaryFinancialGoal &&
            q.BiggestChallenge == submission.BiggestChallenge
        )), Times.Once);
    }

    [Fact]
    public async Task SubmitQuizAsync_WithNonExistentUser_ShouldThrowArgumentException()
    {
        // Arrange
        var userId = 999;
        var submission = new OnboardingQuizSubmissionDto
        {
            PrimaryFinancialGoal = "save_more"
        };

        _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync((User?)null);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.SubmitQuizAsync(userId, submission));
        
        exception.Message.Should().Contain($"User with ID {userId} not found");
        _mockQuizRepository.Verify(x => x.CreateAsync(It.IsAny<UserOnboardingQuiz>()), Times.Never);
    }

    [Fact]
    public async Task SubmitQuizAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        var submission = new OnboardingQuizSubmissionDto { PrimaryFinancialGoal = "save_more" };
        var user = new User { Id = userId, Email = "test@example.com" };

        _mockUserRepository.Setup(x => x.GetByIdAsync(userId)).ReturnsAsync(user);
        _mockQuizRepository.Setup(x => x.CreateAsync(It.IsAny<UserOnboardingQuiz>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.SubmitQuizAsync(userId, submission));
        
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Error submitting onboarding quiz for user {userId}")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region GetLatestQuizAsync Tests

    [Fact]
    public async Task GetLatestQuizAsync_WithExistingQuiz_ShouldReturnDto()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            Id = 1,
            UserId = userId,
            PrimaryFinancialGoal = "save_more",
            Version = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GetLatestQuizAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
        result.PrimaryFinancialGoal.Should().Be("save_more");
        result.Version.Should().Be(2);
    }

    [Fact]
    public async Task GetLatestQuizAsync_WithNoQuiz_ShouldReturnNull()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync((UserOnboardingQuiz?)null);

        // Act
        var result = await _service.GetLatestQuizAsync(userId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestQuizAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.GetLatestQuizAsync(userId));
        
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Error retrieving latest quiz for user {userId}")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region GetQuizHistoryAsync Tests

    [Fact]
    public async Task GetQuizHistoryAsync_WithMultipleVersions_ShouldReturnAllVersions()
    {
        // Arrange
        var userId = 1;
        var quizzes = new List<UserOnboardingQuiz>
        {
            new UserOnboardingQuiz { Id = 1, UserId = userId, Version = 1, CreatedAt = DateTime.UtcNow.AddDays(-30), UpdatedAt = DateTime.UtcNow.AddDays(-30) },
            new UserOnboardingQuiz { Id = 2, UserId = userId, Version = 2, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        };

        _mockQuizRepository.Setup(x => x.GetVersionHistoryByUserIdAsync(userId)).ReturnsAsync(quizzes);

        // Act
        var result = await _service.GetQuizHistoryAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.Should().Contain(q => q.Version == 1);
        result.Should().Contain(q => q.Version == 2);
    }

    [Fact]
    public async Task GetQuizHistoryAsync_WithNoHistory_ShouldReturnEmptyList()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.GetVersionHistoryByUserIdAsync(userId))
            .ReturnsAsync(new List<UserOnboardingQuiz>());

        // Act
        var result = await _service.GetQuizHistoryAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQuizHistoryAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.GetVersionHistoryByUserIdAsync(userId))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.GetQuizHistoryAsync(userId));
        
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Error retrieving quiz history for user {userId}")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region HasCompletedQuizAsync Tests

    [Fact]
    public async Task HasCompletedQuizAsync_WithCompletedQuiz_ShouldReturnTrue()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.HasCompletedQuizAsync(userId)).ReturnsAsync(true);

        // Act
        var result = await _service.HasCompletedQuizAsync(userId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasCompletedQuizAsync_WithoutCompletedQuiz_ShouldReturnFalse()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.HasCompletedQuizAsync(userId)).ReturnsAsync(false);

        // Act
        var result = await _service.HasCompletedQuizAsync(userId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasCompletedQuizAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.HasCompletedQuizAsync(userId))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.HasCompletedQuizAsync(userId));
        
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Error checking quiz completion for user {userId}")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region GeneratePersonalizedInsightsAsync Tests

    [Fact]
    public async Task GeneratePersonalizedInsightsAsync_WithSaveMoreGoal_ShouldReturnSavingsInsights()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            UserId = userId,
            PrimaryFinancialGoal = "save_more",
            BiggestChallenge = "sticking_to_budget",
            SpendingPersonality = "impulse_buyer",
            SubscriptionAwareness = "no_idea",
            SavingsBarriers = "[\"bad_habits\"]"
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GeneratePersonalizedInsightsAsync(userId);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(i => i.InsightType == "savings_strategy");
        result.Should().Contain(i => i.Title == "Boost Your Savings Strategy");
    }

    [Fact]
    public async Task GeneratePersonalizedInsightsAsync_WithBudgetBetterGoal_ShouldReturnBudgetingInsights()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            UserId = userId,
            PrimaryFinancialGoal = "budget_better"
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GeneratePersonalizedInsightsAsync(userId);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(i => i.InsightType == "budgeting_tips");
        result.Should().Contain(i => i.Title == "Master Your Budget");
    }

    [Fact]
    public async Task GeneratePersonalizedInsightsAsync_WithReduceDebtGoal_ShouldReturnDebtInsights()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            UserId = userId,
            PrimaryFinancialGoal = "reduce_debt"
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GeneratePersonalizedInsightsAsync(userId);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().Contain(i => i.InsightType == "debt_management");
        result.Should().Contain(i => i.Title == "Debt Reduction Plan");
    }

    [Fact]
    public async Task GeneratePersonalizedInsightsAsync_WithImpulseBuyerPersonality_ShouldIncludeImpulseControlInsight()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            UserId = userId,
            PrimaryFinancialGoal = "save_more",
            SpendingPersonality = "impulse_buyer"
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GeneratePersonalizedInsightsAsync(userId);

        // Assert
        result.Should().Contain(i => i.InsightType == "impulse_control");
        result.Should().Contain(i => i.RecommendedActions != null && i.RecommendedActions.Contains("Use 24-hour rule"));
    }

    [Fact]
    public async Task GeneratePersonalizedInsightsAsync_WithNoQuiz_ShouldReturnEmptyList()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync((UserOnboardingQuiz?)null);

        // Act
        var result = await _service.GeneratePersonalizedInsightsAsync(userId);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GeneratePersonalizedInsightsAsync_WithSubscriptionAwarenessNoIdea_ShouldIncludeDiscoveryInsight()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            UserId = userId,
            PrimaryFinancialGoal = "save_more",
            SubscriptionAwareness = "no_idea"
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GeneratePersonalizedInsightsAsync(userId);

        // Assert
        result.Should().Contain(i => i.InsightType == "subscription_discovery");
        result.Should().Contain(i => i.Priority == "high");
    }

    [Fact]
    public async Task GeneratePersonalizedInsightsAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.GeneratePersonalizedInsightsAsync(userId));
        
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Error generating personalized insights for user {userId}")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region GetUserProfileForAIAsync Tests

    [Fact]
    public async Task GetUserProfileForAIAsync_WithValidQuiz_ShouldReturnProfileDto()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            UserId = userId,
            PrimaryFinancialGoal = "[\"save_more\", \"budget_better\"]",
            BiggestChallenge = "[\"sticking_to_budget\"]",
            SpendingPersonality = "[\"impulse_buyer\"]",
            SubscriptionAwareness = "no_idea",
            SavingsBarriers = "[\"bad_habits\", \"low_income\"]",
            PreferredInsightType = "[\"actionable_tips\"]",
            FinancialExperience = "beginner",
            HasUsedBudgetingApps = false,
            AdditionalNotes = "Need help",
            CreatedAt = DateTime.UtcNow,
            Version = 1
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GetUserProfileForAIAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
        result.PrimaryFinancialGoals.Should().Contain("save_more");
        result.PrimaryFinancialGoals.Should().Contain("budget_better");
        result.SubscriptionAwareness.Should().Be("no_idea");
        result.FinancialExperience.Should().Be("beginner");
        result.HasUsedBudgetingApps.Should().BeFalse();
        result.QuizVersion.Should().Be(1);
    }

    [Fact]
    public async Task GetUserProfileForAIAsync_WithSingleValueFields_ShouldParseCorrectly()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            UserId = userId,
            PrimaryFinancialGoal = "save_more", // Single value, not array
            BiggestChallenge = "sticking_to_budget",
            SpendingPersonality = "impulse_buyer",
            SubscriptionAwareness = "no_idea",
            SavingsBarriers = "bad_habits",
            PreferredInsightType = "actionable_tips",
            FinancialExperience = "beginner",
            HasUsedBudgetingApps = true,
            CreatedAt = DateTime.UtcNow,
            Version = 1
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GetUserProfileForAIAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.PrimaryFinancialGoals.Should().HaveCount(1);
        result.PrimaryFinancialGoals.Should().Contain("save_more");
        result.HasUsedBudgetingApps.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserProfileForAIAsync_WithNoQuiz_ShouldReturnNull()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync((UserOnboardingQuiz?)null);

        // Act
        var result = await _service.GetUserProfileForAIAsync(userId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUserProfileForAIAsync_WithEmptyFields_ShouldReturnEmptyArrays()
    {
        // Arrange
        var userId = 1;
        var quiz = new UserOnboardingQuiz
        {
            UserId = userId,
            PrimaryFinancialGoal = null,
            BiggestChallenge = "",
            SpendingPersonality = null,
            SubscriptionAwareness = "",
            SavingsBarriers = null,
            PreferredInsightType = "",
            FinancialExperience = "",
            HasUsedBudgetingApps = false,
            CreatedAt = DateTime.UtcNow,
            Version = 1
        };

        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId)).ReturnsAsync(quiz);

        // Act
        var result = await _service.GetUserProfileForAIAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result!.PrimaryFinancialGoals.Should().BeEmpty();
        result.BiggestChallenges.Should().BeEmpty();
        result.SpendingPersonalities.Should().BeEmpty();
        result.SavingsBarriers.Should().BeEmpty();
        result.PreferredInsightTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserProfileForAIAsync_WithRepositoryError_ShouldThrowAndLogError()
    {
        // Arrange
        var userId = 1;
        _mockQuizRepository.Setup(x => x.GetLatestByUserIdAsync(userId))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => _service.GetUserProfileForAIAsync(userId));
        
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Error retrieving user profile for AI for user {userId}")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion
}
