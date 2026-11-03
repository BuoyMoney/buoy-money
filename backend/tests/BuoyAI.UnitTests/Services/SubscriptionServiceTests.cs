using Xunit;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using BuoyAI.Core.DTOs.Subscription;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class SubscriptionServiceTests
{
  private readonly Mock<ISubscriptionRepository> _mockSubscriptionRepo;
  private readonly Mock<ISubscriptionTransactionRepository> _mockSubscriptionTransactionRepo;
  private readonly Mock<ITransactionRepository> _mockTransactionRepo;
  private readonly Mock<IDismissedSubscriptionPatternRepository> _mockDismissedPatternRepo;
  private readonly Mock<ISuggestedSubscriptionRepository> _mockSuggestedSubscriptionRepo;
  private readonly Mock<ICrowdsourcedSubscriptionPatternRepository> _mockCrowdsourcedPatternRepo;
  private readonly Mock<IEntitlementService> _mockBillingService;
  private readonly Mock<IUserRepository> _mockUserRepo;
  private readonly Mock<IConfiguration> _mockConfiguration;
  private readonly Mock<ILogger<SubscriptionService>> _mockLogger;
  private readonly SubscriptionService _service;

  public SubscriptionServiceTests()
  {
    _mockSubscriptionRepo = new Mock<ISubscriptionRepository>();
    _mockSubscriptionTransactionRepo = new Mock<ISubscriptionTransactionRepository>();
    _mockTransactionRepo = new Mock<ITransactionRepository>();
    _mockDismissedPatternRepo = new Mock<IDismissedSubscriptionPatternRepository>();
    _mockSuggestedSubscriptionRepo = new Mock<ISuggestedSubscriptionRepository>();
    _mockCrowdsourcedPatternRepo = new Mock<ICrowdsourcedSubscriptionPatternRepository>();
    _mockBillingService = new Mock<IEntitlementService>();
    _mockUserRepo = new Mock<IUserRepository>();
    _mockConfiguration = new Mock<IConfiguration>();
    _mockLogger = new Mock<ILogger<SubscriptionService>>();

    // Setup IConfigurationSection mock for Gemini settings
    var mockGeminiSection = new Mock<IConfigurationSection>();
    mockGeminiSection.Setup(s => s["ApiKey"]).Returns("test-api-key");
    mockGeminiSection.Setup(s => s["Models:Free:Primary"]).Returns("gemini-2.0-flash");
    mockGeminiSection.Setup(s => s["Models:Free:Fallback"]).Returns("gemini-2.0-flash-lite");
    mockGeminiSection.Setup(s => s["Models:Pro:Primary"]).Returns("gemini-2.5-flash");
    mockGeminiSection.Setup(s => s["Models:Pro:Secondary"]).Returns("gemini-2.5-flash");
    _mockConfiguration.Setup(c => c.GetSection("Gemini")).Returns(mockGeminiSection.Object);

    // Setup default billing service (standard tier)
    _mockBillingService.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>()))
      .ReturnsAsync(SubscriptionTier.Standard);

    // Setup default dismissed pattern repo with skipDays parameter
    _mockDismissedPatternRepo.Setup(r => r.IsPatternDismissedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<int>()))
      .ReturnsAsync(false);
    // Detection now batch-loads dismissals/crowdsource up front (N+1 fix) — default to empty.
    _mockDismissedPatternRepo.Setup(r => r.GetActiveDismissedAsync(It.IsAny<int>(), It.IsAny<int>()))
      .ReturnsAsync(new List<DismissedSubscriptionPattern>());
    _mockCrowdsourcedPatternRepo.Setup(r => r.GetByMerchantNamesAsync(It.IsAny<IEnumerable<string>>()))
      .ReturnsAsync(new Dictionary<string, CrowdsourcedSubscriptionPattern>());

    // Setup default suggested subscription repo
    _mockSuggestedSubscriptionRepo.Setup(r => r.GetSuggestionCountSinceAsync(It.IsAny<int>(), It.IsAny<DateTime>()))
      .ReturnsAsync(0);

    _service = new SubscriptionService(
      _mockSubscriptionRepo.Object,
      _mockSubscriptionTransactionRepo.Object,
      _mockTransactionRepo.Object,
      _mockDismissedPatternRepo.Object,
      _mockSuggestedSubscriptionRepo.Object,
      _mockCrowdsourcedPatternRepo.Object,
      _mockBillingService.Object,
      _mockUserRepo.Object,
      _mockConfiguration.Object,
      _mockLogger.Object
    );
  }

  #region GetSubscriptionsAsync

  [Fact]
  public async Task GetSubscriptionsAsync_ReturnsAllUserSubscriptions()
  {
    // Arrange
    var userId = 1;
    var subscriptions = new List<Subscription>
    {
      new() { Id = 1, UserId = userId, Name = "Netflix", Amount = 15.99m },
      new() { Id = 2, UserId = userId, Name = "Spotify", Amount = 9.99m }
    };
    _mockSubscriptionRepo.Setup(r => r.GetByUserIdAsync(userId))
      .ReturnsAsync(subscriptions);

    // Act
    var result = await _service.GetSubscriptionsAsync(userId);

    // Assert
    result.Should().HaveCount(2);
    result.First().Name.Should().Be("Netflix");
  }

  [Fact]
  public async Task GetSubscriptionsAsync_LogsError_OnException()
  {
    // Arrange
    var userId = 1;
    _mockSubscriptionRepo.Setup(r => r.GetByUserIdAsync(userId))
      .ThrowsAsync(new Exception("Database error"));

    // Act & Assert
    await Assert.ThrowsAsync<Exception>(() => _service.GetSubscriptionsAsync(userId));
    _mockLogger.Verify(
      x => x.Log(
        LogLevel.Error,
        It.IsAny<EventId>(),
        It.Is<It.IsAnyType>((v, t) => true),
        It.IsAny<Exception>(),
        It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
      Times.Once);
  }

  #endregion

  #region GetSubscriptionByIdAsync

  [Fact]
  public async Task GetSubscriptionByIdAsync_ExistingSubscription_ReturnsDto()
  {
    // Arrange
    var userId = 1;
    var subscriptionId = 100;
    var subscription = new Subscription
    {
      Id = subscriptionId,
      UserId = userId,
      Name = "Netflix",
      Amount = 15.99m
    };
    _mockSubscriptionRepo.Setup(r => r.GetByIdAsync(subscriptionId))
      .ReturnsAsync(subscription);

    // Act
    var result = await _service.GetSubscriptionByIdAsync(userId, subscriptionId);

    // Assert
    result.Should().NotBeNull();
    result!.Name.Should().Be("Netflix");
  }

  [Fact]
  public async Task GetSubscriptionByIdAsync_WrongUser_ReturnsNull()
  {
    // Arrange
    var userId = 1;
    var subscriptionId = 100;
    var subscription = new Subscription
    {
      Id = subscriptionId,
      UserId = 2, // Different user
      Name = "Netflix"
    };
    _mockSubscriptionRepo.Setup(r => r.GetByIdAsync(subscriptionId))
      .ReturnsAsync(subscription);

    // Act
    var result = await _service.GetSubscriptionByIdAsync(userId, subscriptionId);

    // Assert
    result.Should().BeNull();
  }

  [Fact]
  public async Task GetSubscriptionByIdAsync_NotFound_ReturnsNull()
  {
    // Arrange
    var userId = 1;
    var subscriptionId = 100;
    _mockSubscriptionRepo.Setup(r => r.GetByIdAsync(subscriptionId))
      .ReturnsAsync((Subscription?)null);

    // Act
    var result = await _service.GetSubscriptionByIdAsync(userId, subscriptionId);

    // Assert
    result.Should().BeNull();
  }

  #endregion

  #region CreateSubscriptionAsync

  [Fact]
  public async Task CreateSubscriptionAsync_ValidRequest_CreatesSubscription()
  {
    // Arrange
    var userId = 1;
    var request = new CreateSubscriptionRequest
    {
      Name = "Netflix",
      Description = "Streaming service",
      Amount = 15.99m,
      Frequency = SubscriptionFrequency.Monthly,
      NextDueDate = DateTime.UtcNow.AddDays(30),
      Category = SubscriptionCategory.Streaming,
      MerchantName = "NETFLIX.COM"
    };

    var capturedSubscription = (Subscription?)null;
    _mockSubscriptionRepo.Setup(r => r.CreateAsync(It.IsAny<Subscription>()))
      .Callback<Subscription>(s => capturedSubscription = s)
      .ReturnsAsync((Subscription s) => { s.Id = 100; return s; });

    // Act
    var result = await _service.CreateSubscriptionAsync(userId, request);

    // Assert
    result.Should().NotBeNull();
    result.Name.Should().Be("Netflix");
    result.Amount.Should().Be(15.99m);
    result.Status.Should().Be(SubscriptionStatus.Active);
    result.IsDetected.Should().BeFalse();
    capturedSubscription.Should().NotBeNull();
    capturedSubscription!.UserId.Should().Be(userId);
  }

  [Fact]
  public async Task CreateSubscriptionAsync_NullDescription_DefaultsToEmptyAndCreates()
  {
    // The FE marks Description "(optional)" and sends null when blank. Creating a
    // subscription without a description must succeed (regression: it 400'd before).
    var userId = 1;
    var request = new CreateSubscriptionRequest
    {
      Name = "Spotify",
      Description = null,
      Amount = 9.99m,
      Frequency = SubscriptionFrequency.Monthly,
      NextDueDate = DateTime.UtcNow.AddDays(30),
      Category = SubscriptionCategory.Streaming
    };

    var captured = (Subscription?)null;
    _mockSubscriptionRepo.Setup(r => r.CreateAsync(It.IsAny<Subscription>()))
      .Callback<Subscription>(s => captured = s)
      .ReturnsAsync((Subscription s) => { s.Id = 101; return s; });

    var result = await _service.CreateSubscriptionAsync(userId, request);

    result.Should().NotBeNull();
    result.Name.Should().Be("Spotify");
    captured.Should().NotBeNull();
    captured!.Description.Should().Be(string.Empty); // null coalesced to empty for the non-null entity field
  }

  #endregion

  #region UpdateSubscriptionAsync

  [Fact]
  public async Task UpdateSubscriptionAsync_ExistingSubscription_UpdatesFields()
  {
    // Arrange
    var userId = 1;
    var subscriptionId = 100;
    var existingSubscription = new Subscription
    {
      Id = subscriptionId,
      UserId = userId,
      Name = "Netflix",
      Amount = 15.99m,
      Status = SubscriptionStatus.Active,
      Frequency = SubscriptionFrequency.Monthly
    };

    _mockSubscriptionRepo.Setup(r => r.GetByIdAsync(subscriptionId))
      .ReturnsAsync(existingSubscription);
    _mockSubscriptionRepo.Setup(r => r.UpdateAsync(It.IsAny<Subscription>()))
      .ReturnsAsync((Subscription s) => s);

    var request = new UpdateSubscriptionRequest
    {
      Name = "Netflix Premium",
      Amount = 19.99m,
      Status = SubscriptionStatus.Paused
    };

    // Act
    var result = await _service.UpdateSubscriptionAsync(userId, subscriptionId, request);

    // Assert
    result.Name.Should().Be("Netflix Premium");
    result.Amount.Should().Be(19.99m);
    result.Status.Should().Be(SubscriptionStatus.Paused);
    result.Frequency.Should().Be(SubscriptionFrequency.Monthly); // Unchanged
  }

  [Fact]
  public async Task UpdateSubscriptionAsync_WrongUser_ThrowsException()
  {
    // Arrange
    var userId = 1;
    var subscriptionId = 100;
    var existingSubscription = new Subscription
    {
      Id = subscriptionId,
      UserId = 2, // Different user
      Name = "Netflix"
    };

    _mockSubscriptionRepo.Setup(r => r.GetByIdAsync(subscriptionId))
      .ReturnsAsync(existingSubscription);

    var request = new UpdateSubscriptionRequest { Name = "Updated" };

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => _service.UpdateSubscriptionAsync(userId, subscriptionId, request));
  }

  [Fact]
  public async Task UpdateSubscriptionAsync_NotFound_ThrowsException()
  {
    // Arrange
    var userId = 1;
    var subscriptionId = 100;
    _mockSubscriptionRepo.Setup(r => r.GetByIdAsync(subscriptionId))
      .ReturnsAsync((Subscription?)null);

    var request = new UpdateSubscriptionRequest { Name = "Updated" };

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => _service.UpdateSubscriptionAsync(userId, subscriptionId, request));
  }

  #endregion

  #region DeleteSubscriptionAsync

  [Fact]
  public async Task DeleteSubscriptionAsync_ExistingSubscription_DeletesWithRelationships()
  {
    // Arrange
    var userId = 1;
    var subscriptionId = 100;
    var subscription = new Subscription
    {
      Id = subscriptionId,
      UserId = userId,
      Name = "Netflix"
    };

    _mockSubscriptionRepo.Setup(r => r.GetByIdAsync(subscriptionId))
      .ReturnsAsync(subscription);
    _mockSubscriptionTransactionRepo.Setup(r => r.DeleteBySubscriptionIdAsync(subscriptionId))
      .Returns(Task.CompletedTask);
    _mockSubscriptionRepo.Setup(r => r.DeleteAsync(subscriptionId))
      .Returns(Task.CompletedTask);

    // Act
    await _service.DeleteSubscriptionAsync(userId, subscriptionId);

    // Assert
    _mockSubscriptionTransactionRepo.Verify(r => r.DeleteBySubscriptionIdAsync(subscriptionId), Times.Once);
    _mockSubscriptionRepo.Verify(r => r.DeleteAsync(subscriptionId), Times.Once);
  }

  [Fact]
  public async Task DeleteSubscriptionAsync_WrongUser_ThrowsException()
  {
    // Arrange
    var userId = 1;
    var subscriptionId = 100;
    var subscription = new Subscription
    {
      Id = subscriptionId,
      UserId = 2, // Different user
      Name = "Netflix"
    };

    _mockSubscriptionRepo.Setup(r => r.GetByIdAsync(subscriptionId))
      .ReturnsAsync(subscription);

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => _service.DeleteSubscriptionAsync(userId, subscriptionId));
  }

  #endregion

  #region DetectSubscriptionsAsync

  [Fact]
  public async Task DetectSubscriptionsAsync_MonthlyPattern_DetectsSubscription()
  {
    // Arrange
    var userId = 1;
    var transactions = new List<Transaction>
    {
      new() { Id = 1, AccountId = 1, MerchantName = "NETFLIX.COM", Amount = 15.99m, Date = new DateTime(2024, 1, 15) },
      new() { Id = 2, AccountId = 1, MerchantName = "NETFLIX.COM", Amount = 15.99m, Date = new DateTime(2024, 2, 15) },
      new() { Id = 3, AccountId = 1, MerchantName = "NETFLIX.COM", Amount = 15.99m, Date = new DateTime(2024, 3, 15) }
    };

    _mockTransactionRepo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(transactions);

    var request = new SubscriptionDetectionRequest
    {
      MinOccurrences = 2,
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    };

    // Act
    var result = await _service.DetectSubscriptionsAsync(userId, request);

    // Assert
    result.DetectedSubscriptions.Should().NotBeEmpty();
    var detected = result.DetectedSubscriptions.First();
    detected.MerchantName.Should().Be("NETFLIX.COM");
    detected.Amount.Should().BeApproximately(15.99m, 0.01m);
    detected.Frequency.Should().Be(SubscriptionFrequency.Monthly);
    detected.IsSubscriptionLikely.Should().BeTrue();
    detected.RelatedTransactionIds.Should().HaveCount(3);
  }

  [Fact]
  public async Task DetectSubscriptionsAsync_WeeklyPattern_DetectsSubscription()
  {
    // Arrange
    var userId = 1;
    var transactions = new List<Transaction>
    {
      new() { Id = 1, AccountId = 1, MerchantName = "COFFEE SHOP", Amount = 5.00m, Date = new DateTime(2024, 1, 1) },
      new() { Id = 2, AccountId = 1, MerchantName = "COFFEE SHOP", Amount = 5.00m, Date = new DateTime(2024, 1, 8) },
      new() { Id = 3, AccountId = 1, MerchantName = "COFFEE SHOP", Amount = 5.00m, Date = new DateTime(2024, 1, 15) },
      new() { Id = 4, AccountId = 1, MerchantName = "COFFEE SHOP", Amount = 5.00m, Date = new DateTime(2024, 1, 22) }
    };

    _mockTransactionRepo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(transactions);

    var request = new SubscriptionDetectionRequest { MinOccurrences = 3 };

    // Act
    var result = await _service.DetectSubscriptionsAsync(userId, request);

    // Assert
    result.DetectedSubscriptions.Should().NotBeEmpty();
    var detected = result.DetectedSubscriptions.First();
    detected.Frequency.Should().Be(SubscriptionFrequency.Weekly);
  }

  [Fact]
  public async Task DetectSubscriptionsAsync_IrregularPattern_DoesNotDetect()
  {
    // Arrange
    var userId = 1;
    var transactions = new List<Transaction>
    {
      new() { Id = 1, AccountId = 1, MerchantName = "RANDOM STORE", Amount = 50.00m, Date = new DateTime(2024, 1, 5) },
      new() { Id = 2, AccountId = 1, MerchantName = "RANDOM STORE", Amount = 50.00m, Date = new DateTime(2024, 1, 23) },
      new() { Id = 3, AccountId = 1, MerchantName = "RANDOM STORE", Amount = 50.00m, Date = new DateTime(2024, 3, 10) }
    };

    _mockTransactionRepo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(transactions);

    var request = new SubscriptionDetectionRequest { MinOccurrences = 2 };

    // Act
    var result = await _service.DetectSubscriptionsAsync(userId, request);

    // Assert
    result.DetectedSubscriptions.Should().BeEmpty(); // Too irregular
  }

  [Fact]
  public async Task DetectSubscriptionsAsync_UsesDefaultDates_WhenNotProvided()
  {
    // Arrange
    var userId = 1;
    _mockTransactionRepo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Act
    await _service.DetectSubscriptionsAsync(userId);

    // Assert
    _mockTransactionRepo.Verify(r => r.GetByUserIdAsync(
      userId,
      It.Is<DateTime>(d => d < DateTime.UtcNow.AddYears(-1)), // Approximately 2 years ago
      It.Is<DateTime>(d => d >= DateTime.UtcNow.AddMinutes(-1))
    ), Times.Once);
  }

  #endregion

  #region On-demand cooldown clock

  [Fact]
  public async Task DetectSubscriptionsAsync_WeeklyRun_DoesNotStartOnDemandCooldown()
  {
    // REGRESSION: the weekly background job calls DetectSubscriptionsAsync for every user with
    // isOnDemand=false. It must NOT stamp LastSubscriptionDetectionRunAt, otherwise every user's
    // on-demand "Detect" button would be stuck on cooldown for a week after each weekly sweep.
    var userId = 42;
    var user = new User { Id = userId, Email = "weekly@example.com", LastSubscriptionDetectionRunAt = null };
    _mockUserRepo.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _mockTransactionRepo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    // Weekly path: isOnDemand defaults to false.
    await _service.DetectSubscriptionsAsync(userId, new SubscriptionDetectionRequest());

    user.LastSubscriptionDetectionRunAt.Should().BeNull();
    _mockUserRepo.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
    (await _service.CanRunOnDemandDetectionAsync(userId)).Should().BeTrue();
  }

  [Fact]
  public async Task DetectSubscriptionsAsync_OnDemandRun_StartsCooldown()
  {
    // A user-initiated run (the /detect endpoint / Plaid-sync trigger) DOES stamp the clock, so a
    // subsequent on-demand run is correctly blocked until the cooldown elapses.
    var userId = 42;
    var user = new User { Id = userId, Email = "ondemand@example.com", LastSubscriptionDetectionRunAt = null };
    _mockUserRepo.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
    _mockUserRepo.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync(user);
    _mockTransactionRepo.Setup(r => r.GetByUserIdAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(new List<Transaction>());

    await _service.DetectSubscriptionsAsync(userId, new SubscriptionDetectionRequest(), isOnDemand: true);

    user.LastSubscriptionDetectionRunAt.Should().NotBeNull();
    _mockUserRepo.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Once);
    (await _service.CanRunOnDemandDetectionAsync(userId)).Should().BeFalse();
  }

  #endregion

  #region ConfirmDetectedSubscriptionAsync

  [Fact]
  public async Task ConfirmDetectedSubscriptionAsync_CreatesSubscriptionAndLinksTransactions()
  {
    // Arrange
    var userId = 1;
    var detectedSubscription = new DetectedSubscriptionDto
    {
      MerchantName = "NETFLIX.COM",
      Amount = 15.99m,
      Frequency = SubscriptionFrequency.Monthly,
      EstimatedNextDue = DateTime.UtcNow.AddDays(30),
      RelatedTransactionIds = new List<int> { 1, 2, 3 }
    };

    _mockSubscriptionRepo.Setup(r => r.CreateAsync(It.IsAny<Subscription>()))
      .ReturnsAsync((Subscription s) => 
      {
        s.Id = 100;
        return s;
      });
    _mockSubscriptionTransactionRepo.Setup(r => r.CreateAsync(It.IsAny<SubscriptionTransaction>()))
      .ReturnsAsync((SubscriptionTransaction st) => st);

    // Act
    var result = await _service.ConfirmDetectedSubscriptionAsync(userId, detectedSubscription);

    // Assert
    result.Should().NotBeNull();
    result.IsDetected.Should().BeTrue();
    result.Name.Should().Be("NETFLIX.COM");
    _mockSubscriptionTransactionRepo.Verify(
      r => r.CreateAsync(It.Is<SubscriptionTransaction>(st => st.SubscriptionId == 100)),
      Times.Exactly(3));
  }

  #endregion

  #region DismissDetectedSubscriptionAsync

  [Fact]
  public async Task DismissDetectedSubscriptionAsync_CompletesSuccessfully()
  {
    // Arrange
    var userId = 1;
    var merchantName = "Netflix";

    // Act & Assert (should not throw)
    await _service.DismissDetectedSubscriptionAsync(userId, merchantName);
  }

  #endregion

  #region GetCalendarDataAsync

  [Fact]
  public async Task GetCalendarDataAsync_GroupsSubscriptionsByDate()
  {
    // Arrange
    var userId = 1;
    var date1 = new DateTime(2024, 1, 15);
    var date2 = new DateTime(2024, 1, 20);
    var subscriptions = new List<Subscription>
    {
      new() { Id = 1, UserId = userId, Name = "Netflix", Amount = 15.99m, NextDueDate = date1 },
      new() { Id = 2, UserId = userId, Name = "Spotify", Amount = 9.99m, NextDueDate = date1 },
      new() { Id = 3, UserId = userId, Name = "Gym", Amount = 50.00m, NextDueDate = date2 }
    };

    _mockSubscriptionRepo.Setup(r => r.GetByUserIdAndDateRangeAsync(userId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(subscriptions);

    // Act
    var result = await _service.GetCalendarDataAsync(userId, date1, date2.AddDays(1));

    // Assert
    result.Should().HaveCount(2); // Two distinct dates
    var firstDate = result.First();
    firstDate.Date.Should().Be(date1.Date);
    firstDate.Subscriptions.Should().HaveCount(2);
    firstDate.TotalAmount.Should().Be(25.98m);
  }

  #endregion

  #region GetSubscriptionSummaryAsync

  [Fact]
  public async Task GetSubscriptionSummaryAsync_CalculatesMonthlyTotal()
  {
    // Arrange
    var userId = 1;
    var subscriptions = new List<Subscription>
    {
      new() { Id = 1, UserId = userId, Name = "Netflix", Amount = 15.99m, Frequency = SubscriptionFrequency.Monthly, Category = SubscriptionCategory.Streaming },
      new() { Id = 2, UserId = userId, Name = "Spotify", Amount = 9.99m, Frequency = SubscriptionFrequency.Monthly, Category = SubscriptionCategory.Streaming },
      new() { Id = 3, UserId = userId, Name = "Gym", Amount = 50.00m, Frequency = SubscriptionFrequency.Monthly, Category = SubscriptionCategory.Fitness }
    };

    _mockSubscriptionRepo.Setup(r => r.GetActiveByUserIdAsync(userId))
      .ReturnsAsync(subscriptions);

    // Act
    var result = await _service.GetSubscriptionSummaryAsync(userId);

    // Assert
    result.MonthlyTotal.Should().BeApproximately(75.98m, 0.01m);
    result.YearlyTotal.Should().BeApproximately(911.76m, 0.01m);
    result.ActiveCount.Should().Be(3);
    result.CategoryBreakdown.Should().HaveCount(2);
  }

  [Fact]
  public async Task GetSubscriptionSummaryAsync_ConvertsWeeklyToMonthly()
  {
    // Arrange
    var userId = 1;
    var subscriptions = new List<Subscription>
    {
      new() { Id = 1, UserId = userId, Name = "Weekly Service", Amount = 10.00m, Frequency = SubscriptionFrequency.Weekly, Category = SubscriptionCategory.Other }
    };

    _mockSubscriptionRepo.Setup(r => r.GetActiveByUserIdAsync(userId))
      .ReturnsAsync(subscriptions);

    // Act
    var result = await _service.GetSubscriptionSummaryAsync(userId);

    // Assert
    result.MonthlyTotal.Should().BeApproximately(43.30m, 0.01m); // 10 * 4.33
  }

  [Fact]
  public async Task GetSubscriptionSummaryAsync_ConvertsAnnualToMonthly()
  {
    // Arrange
    var userId = 1;
    var subscriptions = new List<Subscription>
    {
      new() { Id = 1, UserId = userId, Name = "Annual Service", Amount = 120.00m, Frequency = SubscriptionFrequency.Annual, Category = SubscriptionCategory.Software }
    };

    _mockSubscriptionRepo.Setup(r => r.GetActiveByUserIdAsync(userId))
      .ReturnsAsync(subscriptions);

    // Act
    var result = await _service.GetSubscriptionSummaryAsync(userId);

    // Assert
    result.MonthlyTotal.Should().BeApproximately(10.00m, 0.01m); // 120 / 12
  }

  [Fact]
  public async Task GetSubscriptionSummaryAsync_IncludesAllFrequenciesInMonthlyTotal()
  {
    // Regression: BiWeekly/Quarterly/SemiAnnual/Custom subs were previously omitted from the
    // headline MonthlyTotal (only Monthly/Weekly/Annual were summed), so they contributed $0
    // while still appearing (converted) in the CategoryBreakdown — the two figures didn't reconcile.
    var userId = 1;
    var subscriptions = new List<Subscription>
    {
      new() { Id = 1, UserId = userId, Name = "Monthly", Amount = 20.00m, Frequency = SubscriptionFrequency.Monthly, Category = SubscriptionCategory.Software },
      new() { Id = 2, UserId = userId, Name = "BiWeekly", Amount = 50.00m, Frequency = SubscriptionFrequency.BiWeekly, Category = SubscriptionCategory.Other },
      new() { Id = 3, UserId = userId, Name = "Quarterly", Amount = 30.00m, Frequency = SubscriptionFrequency.Quarterly, Category = SubscriptionCategory.Other },
      new() { Id = 4, UserId = userId, Name = "SemiAnnual", Amount = 120.00m, Frequency = SubscriptionFrequency.SemiAnnual, Category = SubscriptionCategory.Fitness }
    };

    _mockSubscriptionRepo.Setup(r => r.GetActiveByUserIdAsync(userId))
      .ReturnsAsync(subscriptions);

    var result = await _service.GetSubscriptionSummaryAsync(userId);

    // 20 (monthly) + 50*2.17 (biweekly=108.50) + 30/3 (quarterly=10) + 120/6 (semiannual=20) = 158.50
    result.MonthlyTotal.Should().BeApproximately(158.50m, 0.01m);
    result.YearlyTotal.Should().BeApproximately(158.50m * 12, 0.01m);

    // The MonthlyTotal must equal the sum of the per-category monthly rows (they now reconcile).
    var categorySum = result.CategoryBreakdown.Sum(c => c.Amount);
    result.MonthlyTotal.Should().BeApproximately(categorySum, 0.01m);
  }

  #endregion
}
