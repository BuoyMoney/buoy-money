using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Claims;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class OnboardingQuizControllerTests
{
    private readonly Mock<IOnboardingQuizService> _mockQuizService;
    private readonly Mock<ILogger<OnboardingQuizController>> _mockLogger;
    private readonly OnboardingQuizController _controller;

    public OnboardingQuizControllerTests()
    {
        _mockQuizService = new Mock<IOnboardingQuizService>();
        _mockLogger = new Mock<ILogger<OnboardingQuizController>>();
        
        _controller = new OnboardingQuizController(
            _mockQuizService.Object,
            _mockLogger.Object);

        // Setup authenticated user context
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, "123")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };
    }

    #region SubmitQuiz

    [Fact]
    public async Task SubmitQuiz_ValidSubmission_ReturnsOkWithResponse()
    {
        // Arrange
        var submission = new OnboardingQuizSubmissionDto
        {
            PrimaryFinancialGoal = "save_for_house",
            BiggestChallenge = "controlling_spending",
            SpendingPersonality = "balanced_spender",
            SubscriptionAwareness = "aware_need_help",
            PreferredInsightType = "actionable_tips",
            FinancialExperience = "intermediate"
        };

        var expectedResponse = new OnboardingQuizResponseDto
        {
            Id = 1,
            UserId = 123,
            PrimaryFinancialGoal = submission.PrimaryFinancialGoal,
            BiggestChallenge = submission.BiggestChallenge,
            SpendingPersonality = submission.SpendingPersonality,
            CreatedAt = DateTime.UtcNow,
            Version = 1
        };

        _mockQuizService.Setup(s => s.SubmitQuizAsync(123, submission))
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _controller.SubmitQuiz(submission);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(expectedResponse);
        _mockQuizService.Verify(s => s.SubmitQuizAsync(123, submission), Times.Once);
    }

    [Fact]
    public async Task SubmitQuiz_InvalidSubmission_ReturnsBadRequest()
    {
        // Arrange
        var submission = new OnboardingQuizSubmissionDto();

        _mockQuizService.Setup(s => s.SubmitQuizAsync(123, submission))
            .ThrowsAsync(new ArgumentException("Invalid submission data"));

        // Act
        var result = await _controller.SubmitQuiz(submission);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SubmitQuiz_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        var submission = new OnboardingQuizSubmissionDto
        {
            PrimaryFinancialGoal = "save_for_house",
            BiggestChallenge = "controlling_spending",
            SpendingPersonality = "balanced_spender",
            SubscriptionAwareness = "aware_need_help",
            PreferredInsightType = "actionable_tips",
            FinancialExperience = "intermediate"
        };

        _mockQuizService.Setup(s => s.SubmitQuizAsync(123, submission))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.SubmitQuiz(submission));
    }

    #endregion

    #region GetLatestQuiz

    [Fact]
    public async Task GetLatestQuiz_QuizExists_ReturnsOkWithQuiz()
    {
        // Arrange
        var expectedQuiz = new OnboardingQuizResponseDto
        {
            Id = 1,
            UserId = 123,
            PrimaryFinancialGoal = "save_for_house",
            CreatedAt = DateTime.UtcNow,
            Version = 1
        };

        _mockQuizService.Setup(s => s.GetLatestQuizAsync(123))
            .ReturnsAsync(expectedQuiz);

        // Act
        var result = await _controller.GetLatestQuiz();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult!.Value.Should().BeEquivalentTo(expectedQuiz);
        _mockQuizService.Verify(s => s.GetLatestQuizAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetLatestQuiz_NoQuizFound_ReturnsNotFound()
    {
        // Arrange
        _mockQuizService.Setup(s => s.GetLatestQuizAsync(123))
            .ReturnsAsync((OnboardingQuizResponseDto?)null);

        // Act
        var result = await _controller.GetLatestQuiz();

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
        _mockQuizService.Verify(s => s.GetLatestQuizAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetLatestQuiz_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockQuizService.Setup(s => s.GetLatestQuizAsync(123))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetLatestQuiz());
    }

    #endregion

    #region GetQuizHistory

    [Fact]
    public async Task GetQuizHistory_HasHistory_ReturnsOkWithList()
    {
        // Arrange
        var historyList = new List<OnboardingQuizResponseDto>
        {
            new OnboardingQuizResponseDto
            {
                Id = 1,
                UserId = 123,
                PrimaryFinancialGoal = "save_for_house",
                CreatedAt = DateTime.UtcNow.AddMonths(-2),
                Version = 1
            },
            new OnboardingQuizResponseDto
            {
                Id = 2,
                UserId = 123,
                PrimaryFinancialGoal = "build_emergency_fund",
                CreatedAt = DateTime.UtcNow,
                Version = 2
            }
        };

        _mockQuizService.Setup(s => s.GetQuizHistoryAsync(123))
            .ReturnsAsync(historyList);

        // Act
        var result = await _controller.GetQuizHistory();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedList = okResult!.Value as IEnumerable<OnboardingQuizResponseDto>;
        returnedList.Should().HaveCount(2);
        _mockQuizService.Verify(s => s.GetQuizHistoryAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetQuizHistory_NoHistory_ReturnsOkWithEmptyList()
    {
        // Arrange
        var emptyList = new List<OnboardingQuizResponseDto>();

        _mockQuizService.Setup(s => s.GetQuizHistoryAsync(123))
            .ReturnsAsync(emptyList);

        // Act
        var result = await _controller.GetQuizHistory();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as ObjectResult;
        var returnedList = okResult!.Value as IEnumerable<OnboardingQuizResponseDto>;
        returnedList.Should().BeEmpty();
        _mockQuizService.Verify(s => s.GetQuizHistoryAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetQuizHistory_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockQuizService.Setup(s => s.GetQuizHistoryAsync(123))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetQuizHistory());
    }

    #endregion

    #region HasCompletedQuiz

    [Fact]
    public async Task HasCompletedQuiz_UserHasCompleted_ReturnsOkWithTrue()
    {
        // Arrange
        _mockQuizService.Setup(s => s.HasCompletedQuizAsync(123))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.HasCompletedQuiz();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var hasCompletedValue = okResult!.Value?.GetType().GetProperty("hasCompleted")?.GetValue(okResult.Value);
        hasCompletedValue.Should().Be(true);
        _mockQuizService.Verify(s => s.HasCompletedQuizAsync(123), Times.Once);
    }

    [Fact]
    public async Task HasCompletedQuiz_UserHasNotCompleted_ReturnsOkWithFalse()
    {
        // Arrange
        _mockQuizService.Setup(s => s.HasCompletedQuizAsync(123))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.HasCompletedQuiz();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var hasCompletedValue = okResult!.Value?.GetType().GetProperty("hasCompleted")?.GetValue(okResult.Value);
        hasCompletedValue.Should().Be(false);
        _mockQuizService.Verify(s => s.HasCompletedQuizAsync(123), Times.Once);
    }

    [Fact]
    public async Task HasCompletedQuiz_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockQuizService.Setup(s => s.HasCompletedQuizAsync(123))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.HasCompletedQuiz());
    }

    #endregion
}
