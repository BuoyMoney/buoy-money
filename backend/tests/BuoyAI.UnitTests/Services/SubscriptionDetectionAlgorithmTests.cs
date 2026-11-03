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

/// <summary>
/// Focused tests for the subscription-detection algorithm overhaul: the >=3-occurrence cadence
/// guard, the subscription-candidate filter (rent/transfers excluded), amount clustering across a
/// price change, and pattern detection running without a Gemini key.
/// </summary>
public class SubscriptionDetectionAlgorithmTests
{
  private readonly Mock<ISubscriptionRepository> _mockSubscriptionRepo = new();
  private readonly Mock<ISubscriptionTransactionRepository> _mockSubscriptionTransactionRepo = new();
  private readonly Mock<ITransactionRepository> _mockTransactionRepo = new();
  private readonly Mock<IDismissedSubscriptionPatternRepository> _mockDismissedPatternRepo = new();
  private readonly Mock<ISuggestedSubscriptionRepository> _mockSuggestedSubscriptionRepo = new();
  private readonly Mock<ICrowdsourcedSubscriptionPatternRepository> _mockCrowdsourcedPatternRepo = new();
  private readonly Mock<IEntitlementService> _mockBillingService = new();
  private readonly Mock<IUserRepository> _mockUserRepo = new();
  private readonly Mock<ILogger<SubscriptionService>> _mockLogger = new();

  private SubscriptionService CreateService(string apiKey = "test-api-key")
  {
    var mockGeminiSection = new Mock<IConfigurationSection>();
    mockGeminiSection.Setup(s => s["ApiKey"]).Returns(apiKey);
    mockGeminiSection.Setup(s => s["Models:Free:Primary"]).Returns("gemini-2.0-flash");
    mockGeminiSection.Setup(s => s["Models:Free:Fallback"]).Returns("gemini-2.0-flash-lite");
    mockGeminiSection.Setup(s => s["Models:Pro:Primary"]).Returns("gemini-2.5-flash");
    mockGeminiSection.Setup(s => s["Models:Pro:Secondary"]).Returns("gemini-2.5-flash");

    var mockConfig = new Mock<IConfiguration>();
    mockConfig.Setup(c => c.GetSection("Gemini")).Returns(mockGeminiSection.Object);

    _mockBillingService.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>()))
      .ReturnsAsync(SubscriptionTier.Standard);
    _mockDismissedPatternRepo
      .Setup(r => r.IsPatternDismissedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<int>()))
      .ReturnsAsync(false);
    // Detection now batch-loads dismissals/crowdsource up front (N+1 fix) — default to empty.
    _mockDismissedPatternRepo
      .Setup(r => r.GetActiveDismissedAsync(It.IsAny<int>(), It.IsAny<int>()))
      .ReturnsAsync(new List<DismissedSubscriptionPattern>());
    _mockCrowdsourcedPatternRepo
      .Setup(r => r.GetByMerchantNamesAsync(It.IsAny<IEnumerable<string>>()))
      .ReturnsAsync(new Dictionary<string, CrowdsourcedSubscriptionPattern>());

    // GOOGLE_API_KEY env var would override the config key; clear it so the empty-key test is honest.
    Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);

    return new SubscriptionService(
      _mockSubscriptionRepo.Object,
      _mockSubscriptionTransactionRepo.Object,
      _mockTransactionRepo.Object,
      _mockDismissedPatternRepo.Object,
      _mockSuggestedSubscriptionRepo.Object,
      _mockCrowdsourcedPatternRepo.Object,
      _mockBillingService.Object,
      _mockUserRepo.Object,
      mockConfig.Object,
      _mockLogger.Object);
  }

  private void SetupTransactions(IEnumerable<Transaction> transactions)
  {
    _mockTransactionRepo
      .Setup(r => r.GetByUserIdAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
      .ReturnsAsync(transactions.ToList());
  }

  private static Transaction Txn(int id, string merchant, decimal amount, DateTime date, int? categoryId = null) =>
    new()
    {
      Id = id,
      AccountId = 1,
      MerchantName = merchant,
      Amount = amount,
      Type = TransactionType.Debit,
      Date = date,
      CategoryId = categoryId
    };

  [Fact]
  public async Task TwoSameMerchantCharges_DoNotProduceSuggestion()
  {
    // A 2-transaction group has a single interval that is trivially "perfectly regular".
    // Even with a permissive MinOccurrences=2, the >=3-occurrence cadence guard must block it.
    var service = CreateService();
    SetupTransactions(new[]
    {
      Txn(1, "SOME SHOP", 12.34m, new DateTime(2024, 1, 10)),
      Txn(2, "SOME SHOP", 12.34m, new DateTime(2024, 2, 10)),
    });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      MinOccurrences = 2,
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().BeEmpty();
  }

  [Fact]
  public async Task ThreeRegularMonthlyCharges_ProduceSuggestion()
  {
    // Uses the on-demand default request (MinOccurrences now 3). Three regular monthly charges
    // are the minimum that can establish a cadence.
    var service = CreateService();
    SetupTransactions(new[]
    {
      Txn(1, "NETFLIX.COM", 15.99m, new DateTime(2024, 1, 15)),
      Txn(2, "NETFLIX.COM", 15.99m, new DateTime(2024, 2, 15)),
      Txn(3, "NETFLIX.COM", 15.99m, new DateTime(2024, 3, 15)),
    });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().ContainSingle();
    var detected = result.DetectedSubscriptions.Single();
    detected.MerchantName.Should().Be("NETFLIX.COM");
    detected.Frequency.Should().Be(SubscriptionFrequency.Monthly);
    detected.IsSubscriptionLikely.Should().BeTrue();
    detected.RelatedTransactionIds.Should().HaveCount(3);
  }

  [Fact]
  public async Task RentAndTransfers_AreExcluded()
  {
    // Rent and transfers are the MOST regular monthly debits, but they are never subscriptions.
    // The candidate filter must drop them even though they form a perfect monthly cadence.
    var service = CreateService();
    SetupTransactions(new[]
    {
      // Perfectly regular monthly rent
      Txn(1, "GREENFIELD APARTMENTS", 1800m, new DateTime(2024, 1, 1), (int)TransactionCategory.Rent),
      Txn(2, "GREENFIELD APARTMENTS", 1800m, new DateTime(2024, 2, 1), (int)TransactionCategory.Rent),
      Txn(3, "GREENFIELD APARTMENTS", 1800m, new DateTime(2024, 3, 1), (int)TransactionCategory.Rent),
      // Perfectly regular monthly transfer
      Txn(4, "TRANSFER TO SAVINGS", 500m, new DateTime(2024, 1, 5), (int)TransactionCategory.Transfer),
      Txn(5, "TRANSFER TO SAVINGS", 500m, new DateTime(2024, 2, 5), (int)TransactionCategory.Transfer),
      Txn(6, "TRANSFER TO SAVINGS", 500m, new DateTime(2024, 3, 5), (int)TransactionCategory.Transfer),
    });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().BeEmpty();
  }

  [Fact]
  public async Task PriceChange_StaysOneGroup()
  {
    // A subscription whose price steps up part-way through must stay a SINGLE detected group.
    // The old fixed-bucket approach shattered it into two "subs"; amount clustering keeps it whole.
    var service = CreateService();
    SetupTransactions(new[]
    {
      Txn(1, "STREAMPLUS", 9.99m, new DateTime(2024, 1, 15)),
      Txn(2, "STREAMPLUS", 9.99m, new DateTime(2024, 2, 15)),
      Txn(3, "STREAMPLUS", 9.99m, new DateTime(2024, 3, 15)),
      Txn(4, "STREAMPLUS", 11.49m, new DateTime(2024, 4, 15)), // price increase
      Txn(5, "STREAMPLUS", 11.49m, new DateTime(2024, 5, 15)),
    });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().ContainSingle();
    result.DetectedSubscriptions.Single().RelatedTransactionIds.Should().HaveCount(5);
  }

  [Fact]
  public async Task AnnualTwoCharges_ProduceAnnualSuggestion()
  {
    // An annual subscription bills once a year, so a 2-year window holds at most 2-3 charges and
    // often exactly 2. The >=3-occurrence floor would make it undetectable; the annual-only
    // 2-occurrence path (two charges ~365 days apart) rescues it.
    var service = CreateService();
    SetupTransactions(new[]
    {
      Txn(1, "ANNUAL DOMAIN CO", 48.00m, new DateTime(2023, 3, 1)),
      Txn(2, "ANNUAL DOMAIN CO", 48.00m, new DateTime(2024, 3, 1)), // ~366 days later
    });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      StartDate = new DateTime(2023, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().ContainSingle();
    var detected = result.DetectedSubscriptions.Single();
    detected.MerchantName.Should().Be("ANNUAL DOMAIN CO");
    detected.Frequency.Should().Be(SubscriptionFrequency.Annual);
    detected.IsSubscriptionLikely.Should().BeTrue();
    detected.RelatedTransactionIds.Should().HaveCount(2);
  }

  [Fact]
  public async Task TwoChargesNotAYearApart_DoNotProduceSuggestion()
  {
    // Two charges ~6 months apart are NOT annual. The 2-occurrence path is annual-only (interval
    // must be near a year), so the >=3-occurrence guard still blocks them — this is what keeps the
    // relaxed path from resurrecting the "two random purchases = subscription" false positive.
    var service = CreateService();
    SetupTransactions(new[]
    {
      Txn(1, "SOME MERCHANT", 60.00m, new DateTime(2024, 1, 10)),
      Txn(2, "SOME MERCHANT", 60.00m, new DateTime(2024, 7, 10)), // ~182 days apart
    });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      MinOccurrences = 2, // even a permissive floor must not let a non-annual pair through
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().BeEmpty();
  }

  [Fact]
  public async Task PatternDetection_WorksWithNoGeminiKey()
  {
    // Removing the Gemini hard-gate: detection must run (and succeed) with no API key configured.
    // Only the optional AI enhancement is skipped, so IsAIEnhanced stays false.
    var service = CreateService(apiKey: "");
    SetupTransactions(new[]
    {
      Txn(1, "SPOTIFY", 9.99m, new DateTime(2024, 1, 20)),
      Txn(2, "SPOTIFY", 9.99m, new DateTime(2024, 2, 20)),
      Txn(3, "SPOTIFY", 9.99m, new DateTime(2024, 3, 20)),
    });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().ContainSingle();
    result.IsAIEnhanced.Should().BeFalse();
  }

  [Fact]
  public async Task DismissedPattern_IsSkipped_ViaBatchLookup()
  {
    // Detection now batch-loads dismissals ONCE (GetActiveDismissedAsync) rather than calling
    // IsPatternDismissedAsync per merchant group (the N+1). A dismissed merchant must still be
    // excluded, and the per-group query must no longer be issued.
    var service = CreateService();
    SetupTransactions(new[]
    {
      Txn(1, "NETFLIX", 15.99m, new DateTime(2024, 1, 15)),
      Txn(2, "NETFLIX", 15.99m, new DateTime(2024, 2, 15)),
      Txn(3, "NETFLIX", 15.99m, new DateTime(2024, 3, 15)),
    });
    _mockDismissedPatternRepo
      .Setup(r => r.GetActiveDismissedAsync(It.IsAny<int>(), It.IsAny<int>()))
      .ReturnsAsync(new List<DismissedSubscriptionPattern>
      {
        new()
        {
          UserId = 1,
          MerchantName = "netflix", // stored lower-cased/trimmed
          AmountMin = null,          // no amount constraint => matches any charge
          IsPermanent = true,
          DismissedAt = DateTime.UtcNow,
        }
      });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().BeEmpty();
    _mockDismissedPatternRepo.Verify(
      r => r.GetActiveDismissedAsync(1, It.IsAny<int>()), Times.Once);
    _mockDismissedPatternRepo.Verify(
      r => r.IsPatternDismissedAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<int>()),
      Times.Never);
  }

  [Fact]
  public async Task CrowdsourceBoost_IsRecorded_ViaBatchLookup()
  {
    // The crowdsource boost is looked up via a single batched GetByMerchantNamesAsync rather than
    // one GetByMerchantNameAsync per merchant group.
    var service = CreateService();
    SetupTransactions(new[]
    {
      Txn(1, "NETFLIX", 15.99m, new DateTime(2024, 1, 15)),
      Txn(2, "NETFLIX", 15.99m, new DateTime(2024, 2, 15)),
      Txn(3, "NETFLIX", 15.99m, new DateTime(2024, 3, 15)),
    });
    _mockCrowdsourcedPatternRepo
      .Setup(r => r.GetByMerchantNamesAsync(It.IsAny<IEnumerable<string>>()))
      .ReturnsAsync(new Dictionary<string, CrowdsourcedSubscriptionPattern>
      {
        ["netflix"] = new() { NormalizedMerchantName = "netflix", ConfidenceBoost = 0.15 }
      });

    var result = await service.DetectSubscriptionsAsync(1, new SubscriptionDetectionRequest
    {
      StartDate = new DateTime(2024, 1, 1),
      EndDate = new DateTime(2024, 12, 31)
    });

    result.DetectedSubscriptions.Should().ContainSingle();
    result.DetectedSubscriptions.Single().DetectionMethod.Should().Contain("crowdsource");
    _mockCrowdsourcedPatternRepo.Verify(
      r => r.GetByMerchantNamesAsync(It.IsAny<IEnumerable<string>>()), Times.Once);
  }
}
