using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs.Subscription;
using BuoyAI.Core.Enums;
using System.Security.Claims;

namespace BuoyAI.UnitTests.Controllers;

public class SubscriptionsControllerTests
{
    private readonly Mock<ISubscriptionService> _mockSubscriptionService;
    private readonly Mock<ILogger<SubscriptionsController>> _mockLogger;
    private readonly SubscriptionsController _controller;

    public SubscriptionsControllerTests()
    {
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockLogger = new Mock<ILogger<SubscriptionsController>>();

        _controller = new SubscriptionsController(
            _mockSubscriptionService.Object,
            _mockLogger.Object);

        // Setup authenticated user context
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "123")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = claimsPrincipal
            }
        };
    }

    #region GetSubscriptions Tests

    [Fact]
    public async Task GetSubscriptions_HasSubscriptions_ReturnsOkWithSubscriptions()
    {
        // Arrange
        var subscriptions = new List<SubscriptionDto>
        {
            new SubscriptionDto { Id = 1, Name = "Netflix", Amount = 15.99m },
            new SubscriptionDto { Id = 2, Name = "Spotify", Amount = 9.99m }
        };
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionsAsync(123))
            .ReturnsAsync(subscriptions);

        // Act
        var result = await _controller.GetSubscriptions();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedSubscriptions = okResult!.Value as IEnumerable<SubscriptionDto>;
        returnedSubscriptions.Should().HaveCount(2);
        _mockSubscriptionService.Verify(s => s.GetSubscriptionsAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetSubscriptions_NoSubscriptions_ReturnsOkWithEmptyList()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionsAsync(123))
            .ReturnsAsync(new List<SubscriptionDto>());

        // Act
        var result = await _controller.GetSubscriptions();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedSubscriptions = okResult!.Value as IEnumerable<SubscriptionDto>;
        returnedSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSubscriptions_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionsAsync(123))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetSubscriptions());
    }

    #endregion

    #region GetSubscription Tests

    [Fact]
    public async Task GetSubscription_SubscriptionExists_ReturnsOkWithSubscription()
    {
        // Arrange
        var subscription = new SubscriptionDto { Id = 1, Name = "Netflix", Amount = 15.99m };
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionByIdAsync(123, 1))
            .ReturnsAsync(subscription);

        // Act
        var result = await _controller.GetSubscription(1);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedSubscription = okResult!.Value as SubscriptionDto;
        returnedSubscription.Should().NotBeNull();
        returnedSubscription!.Id.Should().Be(1);
        _mockSubscriptionService.Verify(s => s.GetSubscriptionByIdAsync(123, 1), Times.Once);
    }

    [Fact]
    public async Task GetSubscription_SubscriptionNotFound_ReturnsNotFound()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionByIdAsync(123, 999))
            .ReturnsAsync((SubscriptionDto?)null);

        // Act
        var result = await _controller.GetSubscription(999);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
        var notFoundResult = result as NotFoundObjectResult;
        var response = notFoundResult!.Value;
        var messageProperty = response!.GetType().GetProperty("message");
        var message = messageProperty!.GetValue(response) as string;
        message.Should().Be("Subscription not found");
    }

    [Fact]
    public async Task GetSubscription_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionByIdAsync(123, 1))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetSubscription(1));
    }

    #endregion

    #region CreateSubscription Tests

    [Fact]
    public async Task CreateSubscription_ValidRequest_ReturnsCreatedAtAction()
    {
        // Arrange
        var request = new CreateSubscriptionRequest
        {
            Name = "Netflix",
            Amount = 15.99m,
            Frequency = SubscriptionFrequency.Monthly,
            Category = SubscriptionCategory.Streaming,
            NextDueDate = DateTime.UtcNow.AddDays(30)
        };
        var createdSubscription = new SubscriptionDto { Id = 1, Name = "Netflix", Amount = 15.99m };
        _mockSubscriptionService
            .Setup(s => s.CreateSubscriptionAsync(123, request))
            .ReturnsAsync(createdSubscription);

        // Act
        var result = await _controller.CreateSubscription(request);

        // Assert
        result.Should().BeOfType<CreatedAtActionResult>();
        var createdResult = result as CreatedAtActionResult;
        createdResult!.ActionName.Should().Be(nameof(SubscriptionsController.GetSubscription));
        var returnedSubscription = createdResult.Value as SubscriptionDto;
        returnedSubscription.Should().NotBeNull();
        returnedSubscription!.Id.Should().Be(1);
        _mockSubscriptionService.Verify(s => s.CreateSubscriptionAsync(123, request), Times.Once);
    }

    [Fact]
    public async Task CreateSubscription_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        var request = new CreateSubscriptionRequest { Name = "Netflix" };
        _mockSubscriptionService
            .Setup(s => s.CreateSubscriptionAsync(123, request))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.CreateSubscription(request));
    }

    #endregion

    #region UpdateSubscription Tests

    [Fact]
    public async Task UpdateSubscription_ValidRequest_ReturnsOkWithUpdatedSubscription()
    {
        // Arrange
        var request = new UpdateSubscriptionRequest { Name = "Netflix Premium", Amount = 19.99m };
        var updatedSubscription = new SubscriptionDto { Id = 1, Name = "Netflix Premium", Amount = 19.99m };
        _mockSubscriptionService
            .Setup(s => s.UpdateSubscriptionAsync(123, 1, request))
            .ReturnsAsync(updatedSubscription);

        // Act
        var result = await _controller.UpdateSubscription(1, request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedSubscription = okResult!.Value as SubscriptionDto;
        returnedSubscription.Should().NotBeNull();
        returnedSubscription!.Name.Should().Be("Netflix Premium");
        _mockSubscriptionService.Verify(s => s.UpdateSubscriptionAsync(123, 1, request), Times.Once);
    }

    [Fact]
    public async Task UpdateSubscription_SubscriptionNotFound_ReturnsNotFound()
    {
        // Arrange
        var request = new UpdateSubscriptionRequest { Name = "Netflix" };
        _mockSubscriptionService
            .Setup(s => s.UpdateSubscriptionAsync(123, 999, request))
            .ThrowsAsync(new ArgumentException("Subscription not found"));

        // Act
        var result = await _controller.UpdateSubscription(999, request);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task UpdateSubscription_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        var request = new UpdateSubscriptionRequest { Name = "Netflix" };
        _mockSubscriptionService
            .Setup(s => s.UpdateSubscriptionAsync(123, 1, request))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.UpdateSubscription(1, request));
    }

    #endregion

    #region DeleteSubscription Tests

    [Fact]
    public async Task DeleteSubscription_ValidRequest_ReturnsNoContent()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.DeleteSubscriptionAsync(123, 1))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteSubscription(1);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _mockSubscriptionService.Verify(s => s.DeleteSubscriptionAsync(123, 1), Times.Once);
    }

    [Fact]
    public async Task DeleteSubscription_SubscriptionNotFound_ReturnsNotFound()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.DeleteSubscriptionAsync(123, 999))
            .ThrowsAsync(new ArgumentException("Subscription not found"));

        // Act
        var result = await _controller.DeleteSubscription(999);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task DeleteSubscription_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.DeleteSubscriptionAsync(123, 1))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.DeleteSubscription(1));
    }

    #endregion

    #region DetectSubscriptions Tests

    [Fact]
    public async Task DetectSubscriptions_ValidRequest_ReturnsOkWithDetectedSubscriptions()
    {
        // Arrange
        var request = new SubscriptionDetectionRequest { MinOccurrences = 3 };
        var response = new SubscriptionDetectionResponse
        {
            DetectedSubscriptions = new List<DetectedSubscriptionDto>
            {
                new DetectedSubscriptionDto { MerchantName = "Netflix", Amount = 15.99m, Confidence = 0.95 }
            },
            TotalDetected = 1,
            HighConfidenceCount = 1,
            PendingReviewCount = 0,
            IsAIEnhanced = false,
            DetectionDate = DateTime.UtcNow
        };
        _mockSubscriptionService
            .Setup(s => s.CanRunOnDemandDetectionAsync(123))
            .ReturnsAsync(true);
        _mockSubscriptionService
            .Setup(s => s.DetectSubscriptionsAsync(123, request, true))
            .ReturnsAsync(response);

        // Act
        var result = await _controller.DetectSubscriptions(request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedResponse = okResult!.Value as SubscriptionDetectionResponse;
        returnedResponse!.DetectedSubscriptions.Should().HaveCount(1);
        _mockSubscriptionService.Verify(s => s.DetectSubscriptionsAsync(123, request, true), Times.Once);
    }

    [Fact]
    public async Task DetectSubscriptions_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.CanRunOnDemandDetectionAsync(123))
            .ReturnsAsync(true);
        _mockSubscriptionService
            .Setup(s => s.DetectSubscriptionsAsync(123, null, true))
            .ThrowsAsync(new Exception("Detection error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.DetectSubscriptions(null));
    }

    #endregion

    #region ConfirmDetectedSubscription Tests

    [Fact]
    public async Task ConfirmDetectedSubscription_ValidRequest_ReturnsOkWithSubscription()
    {
        // Arrange
        var detectedSubscription = new DetectedSubscriptionDto
        {
            MerchantName = "Netflix",
            Amount = 15.99m,
            Frequency = SubscriptionFrequency.Monthly
        };
        var confirmedSubscription = new SubscriptionDto { Id = 1, Name = "Netflix", Amount = 15.99m };
        _mockSubscriptionService
            .Setup(s => s.ConfirmDetectedSubscriptionAsync(123, detectedSubscription))
            .ReturnsAsync(confirmedSubscription);

        // Act
        var result = await _controller.ConfirmDetectedSubscription(detectedSubscription);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedSubscription = okResult!.Value as SubscriptionDto;
        returnedSubscription.Should().NotBeNull();
        _mockSubscriptionService.Verify(s => s.ConfirmDetectedSubscriptionAsync(123, detectedSubscription), Times.Once);
    }

    [Fact]
    public async Task ConfirmDetectedSubscription_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        var detectedSubscription = new DetectedSubscriptionDto { MerchantName = "Netflix" };
        _mockSubscriptionService
            .Setup(s => s.ConfirmDetectedSubscriptionAsync(123, detectedSubscription))
            .ThrowsAsync(new Exception("Confirmation error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.ConfirmDetectedSubscription(detectedSubscription));
    }

    #endregion

    #region DismissDetectedSubscription Tests

    [Fact]
    public async Task DismissDetectedSubscription_ValidRequest_ReturnsOkWithMessage()
    {
        // Arrange
        var request = new DismissSubscriptionRequest { MerchantName = "Netflix", Amount = 15.99m };
        _mockSubscriptionService
            .Setup(s => s.DismissDetectedSubscriptionAsync(123, "Netflix", 15.99m, null))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DismissDetectedSubscription(1, request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var response = okResult!.Value;
        var messageProperty = response!.GetType().GetProperty("message");
        var message = messageProperty!.GetValue(response) as string;
        message.Should().Be("Detected subscription dismissed");
        _mockSubscriptionService.Verify(s => s.DismissDetectedSubscriptionAsync(123, "Netflix", 15.99m, null), Times.Once);
    }

    [Fact]
    public async Task DismissDetectedSubscription_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        var request = new DismissSubscriptionRequest { MerchantName = "Netflix" };
        _mockSubscriptionService
            .Setup(s => s.DismissDetectedSubscriptionAsync(123, "Netflix", null, null))
            .ThrowsAsync(new Exception("Dismiss error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.DismissDetectedSubscription(1, request));
    }

    #endregion

    #region GetCalendarData Tests

    [Fact]
    public async Task GetCalendarData_ValidDateRange_ReturnsOkWithCalendarData()
    {
        // Arrange
        var startDate = DateTime.UtcNow;
        var endDate = DateTime.UtcNow.AddMonths(1);
        var calendarData = new List<CalendarSubscriptionDto>
        {
            new CalendarSubscriptionDto
            {
                Date = startDate.AddDays(1),
                TotalAmount = 25.98m,
                Subscriptions = new List<SubscriptionDto>
                {
                    new SubscriptionDto { Name = "Netflix", Amount = 15.99m },
                    new SubscriptionDto { Name = "Spotify", Amount = 9.99m }
                }
            }
        };
        _mockSubscriptionService
            .Setup(s => s.GetCalendarDataAsync(123, startDate, endDate))
            .ReturnsAsync(calendarData);

        // Act
        var result = await _controller.GetCalendarData(startDate, endDate);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedData = okResult!.Value as IEnumerable<CalendarSubscriptionDto>;
        returnedData.Should().HaveCount(1);
        _mockSubscriptionService.Verify(s => s.GetCalendarDataAsync(123, startDate, endDate), Times.Once);
    }

    [Fact]
    public async Task GetCalendarData_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        var startDate = DateTime.UtcNow;
        var endDate = DateTime.UtcNow.AddMonths(1);
        _mockSubscriptionService
            .Setup(s => s.GetCalendarDataAsync(123, startDate, endDate))
            .ThrowsAsync(new Exception("Calendar error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetCalendarData(startDate, endDate));
    }

    #endregion

    #region GetSummary Tests

    [Fact]
    public async Task GetSummary_ValidRequest_ReturnsOkWithSummary()
    {
        // Arrange
        var summary = new SubscriptionSummaryDto
        {
            MonthlyTotal = 50.00m,
            YearlyTotal = 600.00m,
            ActiveCount = 5
        };
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionSummaryAsync(123))
            .ReturnsAsync(summary);

        // Act
        var result = await _controller.GetSummary();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var returnedSummary = okResult!.Value as SubscriptionSummaryDto;
        returnedSummary.Should().NotBeNull();
        returnedSummary!.MonthlyTotal.Should().Be(50.00m);
        _mockSubscriptionService.Verify(s => s.GetSubscriptionSummaryAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetSummary_ServiceThrowsException_PropagatesException()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.GetSubscriptionSummaryAsync(123))
            .ThrowsAsync(new Exception("Summary error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetSummary());
    }

    #endregion

    #region GetDetectionCooldown Tests

    [Fact]
    public async Task GetDetectionCooldown_CanRun_ReturnsOkWithCanRunTrue()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.CanRunOnDemandDetectionAsync(123))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.GetDetectionCooldown();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var json = System.Text.Json.JsonSerializer.Serialize(okResult!.Value);
        json.Should().Contain("\"canRun\":true");
        _mockSubscriptionService.Verify(s => s.CanRunOnDemandDetectionAsync(123), Times.Once);
    }

    [Fact]
    public async Task GetDetectionCooldown_CannotRun_ReturnsOkWithCanRunFalse()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.CanRunOnDemandDetectionAsync(123))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.GetDetectionCooldown();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        var json = System.Text.Json.JsonSerializer.Serialize(okResult!.Value);
        json.Should().Contain("\"canRun\":false");
    }

    [Fact]
    public async Task GetDetectionCooldown_ServiceThrows_PropagatesException()
    {
        // Arrange
        _mockSubscriptionService
            .Setup(s => s.CanRunOnDemandDetectionAsync(123))
            .ThrowsAsync(new Exception("Cooldown check error"));

        // Act & Assert — exception propagates to middleware; controller does not catch generic exceptions
        await Assert.ThrowsAsync<Exception>(() => _controller.GetDetectionCooldown());
    }

    #endregion
}
