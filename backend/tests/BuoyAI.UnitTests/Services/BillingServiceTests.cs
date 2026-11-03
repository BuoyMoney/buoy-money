using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics.Metrics;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class BillingServiceTests
{
    private readonly Mock<IStripeService> _mockStripeService;
    private readonly Mock<ISubscriptionSyncService> _mockSyncService = new();
    private readonly Mock<IEmailService> _mockEmailService;
    private readonly Mock<IFamilyService> _mockFamilyService;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger<BillingService>> _mockLogger;
    private readonly IMemoryCache _memoryCache;
    private readonly BuoyAIDbContext _context;
    private readonly BuoyAiMetrics _metrics;
    private readonly BillingService _billingService;

    public BillingServiceTests()
    {
        // Setup in-memory database
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new BuoyAIDbContext(options);

        _mockStripeService = new Mock<IStripeService>();
        // Default: the customer belongs to this environment, so Stripe actually deletes it.
        // The false case (another environment owns it) has its own test below.
        _mockStripeService
            .Setup(s => s.DeleteCustomerAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockEmailService = new Mock<IEmailService>();
        _mockFamilyService = new Mock<IFamilyService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<BillingService>>();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());

        // Create metrics using a test meter factory
        var meterFactory = new TestBillingMeterFactory();
        _metrics = new BuoyAiMetrics(meterFactory);

        // Mock Stripe price IDs configuration
        _mockConfiguration.Setup(c => c["Stripe:Products:Standard:Monthly"]).Returns("price_standard_monthly");
        _mockConfiguration.Setup(c => c["Stripe:Products:Standard:Yearly"]).Returns("price_standard_yearly");
        _mockConfiguration.Setup(c => c["Stripe:Products:Premium:Monthly"]).Returns("price_premium_monthly");
        _mockConfiguration.Setup(c => c["Stripe:Products:Premium:Yearly"]).Returns("price_premium_yearly");

        // Default: the deny-only Stripe refresh finds nothing new, so these tests exercise
        // the local projection exactly as before.
        _mockSyncService
            .Setup(x => x.RefreshFromStripeAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _billingService = new BillingService(
            _context,
            _mockStripeService.Object,
            _mockSyncService.Object,
            TestEntitlements.Create(_context, _mockConfiguration.Object, _mockSyncService.Object),
            _mockEmailService.Object,
            _mockFamilyService.Object,
            _mockConfiguration.Object,
            _memoryCache,
            _mockLogger.Object,
            _metrics
        );
    }

    #region Trial Management Tests

    [Fact]
    public async Task IsTrialActiveAsync_WithActiveTrial_ReturnsTrue()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            IsTrialActive = true,
            TrialEndDate = DateTime.UtcNow.AddDays(30)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.IsTrialActiveAsync(user.Id);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsTrialActiveAsync_WithExpiredTrial_ReturnsFalse()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            IsTrialActive = true,
            TrialEndDate = DateTime.UtcNow.AddDays(-1)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.IsTrialActiveAsync(user.Id);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetTrialDaysRemainingAsync_WithActiveTrial_ReturnsCorrectDays()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            IsTrialActive = true,
            TrialEndDate = DateTime.UtcNow.AddDays(15)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.GetTrialDaysRemainingAsync(user.Id);

        // Assert
        result.Should().BeInRange(14, 15); // Account for timing
    }

    [Fact]
    public async Task CanStartTrialAsync_WithNewUser_ReturnsTrue()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            HasUsedTrial = false
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.CanStartTrialAsync(user.Id);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanStartTrialAsync_EmailHadATrialOnADeletedAccount_ReturnsFalse()
    {
        // Arrange: this account never trialled, but its email did (delete + re-register).
        // StartTrialAsync refuses that, so the billing page must not offer the trial card.
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "returning@example.com",
            PasswordHash = "hash",
            HasUsedTrial = false
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        await TrialHistoryRecorder.RecordGrantAsync(_context, "returning@example.com", 999, "no-card-trial");

        // Act
        var result = await _billingService.CanStartTrialAsync(user.Id);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanStartTrialAsync_WithUserWhoUsedTrial_ReturnsFalse()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            HasUsedTrial = true
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.CanStartTrialAsync(user.Id);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Subscription Management Tests

    [Fact]
    public async Task CreateSubscriptionAsync_WithValidRequest_CreatesSubscription()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var request = new CreateBillingSubscriptionRequest
        {
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            PromoCode = null
        };

        _mockStripeService.Setup(x => x.CreateSubscriptionAsync(
                It.IsAny<string>(), It.IsAny<string>(), null, false))
            .ReturnsAsync("sub_test123");

        // Act
        var result = await _billingService.CreateSubscriptionAsync(user.Id, request);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(user.Id);
        result.Tier.Should().Be(SubscriptionTier.Standard);
        result.BillingCycle.Should().Be(SubscriptionBillingCycle.Monthly);
        result.Status.Should().Be(UserSubscriptionStatus.Active);
        result.StripeSubscriptionId.Should().Be("sub_test123");
        result.Amount.Should().BeGreaterThan(0);

        // Verify user was updated
        var updatedUser = await _context.Users.FindAsync(user.Id);
        updatedUser!.SubscriptionTier.Should().Be(SubscriptionTier.Standard);
        updatedUser.IsTrialActive.Should().BeFalse();
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithPromoCode_AppliesPromoCode()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var request = new CreateBillingSubscriptionRequest
        {
            Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Yearly,
            PromoCode = "SAVE20"
        };

        _mockStripeService.Setup(x => x.CreateSubscriptionAsync(
                It.IsAny<string>(), It.IsAny<string>(), "SAVE20", false))
            .ReturnsAsync("sub_test123");

        // Act
        var result = await _billingService.CreateSubscriptionAsync(user.Id, request);

        // Assert
        result.PromoCode.Should().Be("SAVE20");
        _mockStripeService.Verify(x => x.CreateSubscriptionAsync(
            It.IsAny<string>(), It.IsAny<string>(), "SAVE20", false), Times.Once);
    }

    [Fact]
    public async Task CreateSubscriptionAsync_WithActiveSubscription_ThrowsException()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123"
        };
        _context.Users.Add(user);

        var existingSubscription = new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1)
        };
        _context.UserSubscriptions.Add(existingSubscription);
        await _context.SaveChangesAsync();

        var request = new CreateBillingSubscriptionRequest
        {
            Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _billingService.CreateSubscriptionAsync(user.Id, request));
    }

    [Fact]
    public async Task ChangeSubscriptionAsync_WithValidRequest_ChangesSubscription()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123",
            SubscriptionTier = SubscriptionTier.Standard
        };
        _context.Users.Add(user);

        var subscription = new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_test123"
        };
        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        var request = new ChangeSubscriptionRequest
        {
            NewTier = SubscriptionTier.Premium,
            NewBillingCycle = null
        };

        _mockStripeService.Setup(x => x.UpdateSubscriptionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _billingService.ChangeSubscriptionAsync(user.Id, request);

        // Assert
        result.Tier.Should().Be(SubscriptionTier.Premium);
        result.BillingCycle.Should().Be(SubscriptionBillingCycle.Monthly); // Unchanged

        var updatedUser = await _context.Users.FindAsync(user.Id);
        updatedUser!.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
    }

    [Fact]
    public async Task ChangeSubscriptionAsync_WithoutActiveSubscription_ThrowsException()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var request = new ChangeSubscriptionRequest
        {
            NewTier = SubscriptionTier.Premium
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _billingService.ChangeSubscriptionAsync(user.Id, request));
    }

    [Fact]
    public async Task CancelSubscriptionAsync_WithImmediateCancellation_CancelsSubscription()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123",
            SubscriptionTier = SubscriptionTier.Standard
        };
        _context.Users.Add(user);

        var subscription = new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_test123"
        };
        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        var request = new CancelSubscriptionRequest
        {
            CancelImmediately = true,
            CancellationReason = "Testing"
        };

        _mockStripeService.Setup(x => x.CancelSubscriptionAsync(
                It.IsAny<string>(), true))
            .Returns(Task.CompletedTask);

        // Act
        await _billingService.CancelSubscriptionAsync(user.Id, request);

        // Assert
        var updatedSubscription = await _context.UserSubscriptions
            .FirstOrDefaultAsync(s => s.Id == subscription.Id);
        updatedSubscription!.Status.Should().Be(UserSubscriptionStatus.Canceled);

        _mockStripeService.Verify(x => x.CancelSubscriptionAsync(
            "sub_test123", true), Times.Once);
    }

    [Fact]
    public async Task GetUserSubscriptionAsync_WithActiveSubscription_ReturnsSubscription()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _context.Users.Add(user);

        var subscription = new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1)
        };
        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.GetUserSubscriptionAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result!.UserId.Should().Be(user.Id);
        result.Status.Should().Be(UserSubscriptionStatus.Active);
    }

    [Fact]
    public async Task GetUserSubscriptionAsync_WithoutSubscription_ReturnsNull()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.GetUserSubscriptionAsync(user.Id);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Feature Access Tests

    [Fact]
    public async Task CanAccessFeatureAsync_WithRequiredTier_ReturnsTrue()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            IsTrialActive = true,
            TrialStartDate = DateTime.UtcNow,
            TrialEndDate = DateTime.UtcNow.AddMonths(2),
            SubscriptionTier = SubscriptionTier.Standard
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.CanAccessFeatureAsync(user.Id, "plaid_connections");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessFeatureAsync_WithoutRequiredTier_ReturnsFalse()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            SubscriptionTier = SubscriptionTier.Standard
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act - Premium feature with Standard tier
        var result = await _billingService.CanAccessFeatureAsync(user.Id, "ai_chat_bot");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessFeatureAsync_ExportData_IsAvailableToStandardUsers()
    {
        // Data export is free for every plan and must never be gated.
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            SubscriptionTier = SubscriptionTier.Standard
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var result = await _billingService.CanAccessFeatureAsync(user.Id, "export_data");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessFeatureAsync_WithUnknownFeature_ReturnsTrue()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act - Unknown feature allows access for backward compatibility
        var result = await _billingService.CanAccessFeatureAsync(user.Id, "unknown_feature");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserTierAsync_WithActiveSubscription_ReturnsSubscriptionTier()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            SubscriptionTier = SubscriptionTier.Premium,
            SubscriptionStartDate = DateTime.UtcNow
        };
        _context.Users.Add(user);

        var subscription = new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 15.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1)
        };
        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.GetUserTierAsync(user.Id);

        // Assert
        result.Should().Be(SubscriptionTier.Premium);
    }

    [Fact]
    public async Task HasActiveSubscriptionOrTrialAsync_WithActiveTrial_ReturnsTrue()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            IsTrialActive = true,
            TrialEndDate = DateTime.UtcNow.AddDays(30)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.HasActiveSubscriptionOrTrialAsync(user.Id);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasActiveSubscriptionOrTrialAsync_WithActiveSubscription_ReturnsTrue()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            IsTrialActive = false
        };
        _context.Users.Add(user);

        var subscription = new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1)
        };
        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.HasActiveSubscriptionOrTrialAsync(user.Id);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region Promo Code Tests

    [Fact]
    public async Task ValidatePromoCodeAsync_WithValidCode_ReturnsTrue()
    {
        // Arrange
        _mockStripeService.Setup(x => x.ValidatePromoCodeAsync("SAVE20"))
            .ReturnsAsync(true);

        // Act
        var result = await _billingService.ValidatePromoCodeAsync("SAVE20");

        // Assert
        result.Should().BeTrue();
        _mockStripeService.Verify(x => x.ValidatePromoCodeAsync("SAVE20"), Times.Once);
    }

    [Fact]
    public async Task ValidatePromoCodeAsync_WithInvalidCode_ReturnsFalse()
    {
        // Arrange
        _mockStripeService.Setup(x => x.ValidatePromoCodeAsync("INVALID"))
            .ReturnsAsync(false);

        // Act
        var result = await _billingService.ValidatePromoCodeAsync("INVALID");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidatePromoCodeAsync_WithPriceId_ReturnsValidationResult()
    {
        // Arrange
        var validationResult = new PromoCodeValidationResult
        {
            IsValid = true,
            PromoCode = "SAVE20",
            DiscountPercent = 20,
            OriginalPrice = 10000, // $100 in cents
            FinalPrice = 8000, // $80 in cents
            DiscountAmount = 2000,
            Description = "20% off"
        };

        _mockStripeService.Setup(x => x.ValidatePromoCodeWithPriceAsync("SAVE20", "price_test123"))
            .ReturnsAsync(validationResult);

        // Act
        var result = await _billingService.ValidatePromoCodeAsync("SAVE20", "price_test123");

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeTrue();
        result.DiscountPercent.Should().Be(20);
        result.OriginalPrice.Should().Be(10000);
        result.FinalPrice.Should().Be(8000);
    }

    #endregion

    #region Stripe Customer Deletion Tests

    [Fact]
    public async Task DeleteStripeCustomerAsync_WithCustomerId_DeletesAtStripeAndClearsId()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_abc123"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _billingService.DeleteStripeCustomerAsync(user.Id);

        // Assert — customer deleted at Stripe and the local reference cleared
        _mockStripeService.Verify(s => s.DeleteCustomerAsync("cus_abc123"), Times.Once);
        var reloaded = await _context.Users.FindAsync(user.Id);
        reloaded!.StripeCustomerId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteStripeCustomerAsync_WhenCustomerBelongsToAnotherEnvironment_KeepsLocalReference()
    {
        // Staging and production share one live Stripe account. When the delete is declined
        // nothing changed at Stripe, so the local id still points at a customer that really
        // exists. Clearing it would silently mint a duplicate on the next provisioning pass.
        _mockStripeService
            .Setup(s => s.DeleteCustomerAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        var user = new User
        {
            Id = 3,
            Username = "foreign",
            Email = "foreign@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_owned_by_prod"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _billingService.DeleteStripeCustomerAsync(user.Id);

        // Assert — we asked, Stripe declined, and the reference survives
        _mockStripeService.Verify(s => s.DeleteCustomerAsync("cus_owned_by_prod"), Times.Once);
        var reloaded = await _context.Users.FindAsync(user.Id);
        reloaded!.StripeCustomerId.Should().Be("cus_owned_by_prod");
    }

    [Fact]
    public async Task DeleteStripeCustomerAsync_WithNoCustomerId_DoesNothing()
    {
        // Arrange
        var user = new User
        {
            Id = 2,
            Username = "nocustomer",
            Email = "nocustomer@example.com",
            PasswordHash = "hash",
            StripeCustomerId = null
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _billingService.DeleteStripeCustomerAsync(user.Id);

        // Assert — never calls Stripe when there's no customer to delete
        _mockStripeService.Verify(s => s.DeleteCustomerAsync(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region Billing Portal Tests

    [Fact]
    public async Task CreateBillingPortalSessionAsync_WithCustomer_ReturnsPortalUrl()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "portaluser",
            Email = "portal@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_portal1"
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        _mockConfiguration.Setup(c => c["App:BaseUrl"]).Returns("https://buoy-ai.com");
        _mockStripeService
            .Setup(s => s.CreateBillingPortalSessionAsync("cus_portal1", It.IsAny<string>()))
            .ReturnsAsync("https://billing.stripe.com/session/abc");

        // Act
        var url = await _billingService.CreateBillingPortalSessionAsync(user.Id);

        // Assert
        url.Should().Be("https://billing.stripe.com/session/abc");
        _mockStripeService.Verify(s => s.CreateBillingPortalSessionAsync("cus_portal1", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CreateBillingPortalSessionAsync_WithNoCustomer_Throws()
    {
        // Arrange
        var user = new User
        {
            Id = 2,
            Username = "nocustomer",
            Email = "noc@example.com",
            PasswordHash = "hash",
            StripeCustomerId = null
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act / Assert
        var act = () => _billingService.CreateBillingPortalSessionAsync(user.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region Stripe Webhook Idempotency Tests

    [Fact]
    public async Task HasProcessedStripeEventAsync_UnseenEvent_ReturnsFalse()
    {
        var result = await _billingService.HasProcessedStripeEventAsync("evt_never_seen");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task MarkStripeEventProcessedAsync_RecordsEvent_SoItIsThenSeen()
    {
        await _billingService.MarkStripeEventProcessedAsync("evt_123", "invoice.payment_succeeded");

        (await _billingService.HasProcessedStripeEventAsync("evt_123")).Should().BeTrue();
        _context.ProcessedStripeEvents.Count(e => e.EventId == "evt_123").Should().Be(1);
    }

    [Fact]
    public async Task MarkStripeEventProcessedAsync_CalledTwice_IsIdempotent()
    {
        await _billingService.MarkStripeEventProcessedAsync("evt_dup", "invoice.payment_succeeded");
        await _billingService.MarkStripeEventProcessedAsync("evt_dup", "invoice.payment_succeeded");

        // The second call must not add a duplicate row (retries are no-ops).
        _context.ProcessedStripeEvents.Count(e => e.EventId == "evt_dup").Should().Be(1);
    }

    [Fact]
    public async Task MarkStripeEventProcessedAsync_EmptyEventId_IsNoOp()
    {
        await _billingService.MarkStripeEventProcessedAsync("", "invoice.payment_succeeded");

        _context.ProcessedStripeEvents.Should().BeEmpty();
        (await _billingService.HasProcessedStripeEventAsync("")).Should().BeFalse();
    }

    #endregion

    #region Family Inheritance Revocation Tests (webhook cancellations)

    [Fact]
    public async Task CancelSubscriptionByStripeIdAsync_RevokesFamilyMembersInheritedTier()
    {
        // Arrange — an owner on Premium whose subscription is canceled by Stripe
        // (portal / dunning / period-end all arrive as customer.subscription.deleted).
        _mockFamilyService
            .Setup(f => f.UpdateFamilyMembersTierAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier?>()))
            .Returns(Task.CompletedTask);

        var user = new User
        {
            Id = 1,
            Username = "owner",
            Email = "owner@example.com",
            PasswordHash = "hash",
            SubscriptionTier = SubscriptionTier.Premium
        };
        _context.Users.Add(user);
        _context.UserSubscriptions.Add(new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 15.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_owner"
        });
        await _context.SaveChangesAsync();

        // Act
        await _billingService.CancelSubscriptionByStripeIdAsync("sub_owner");

        // Assert — owner tier cleared AND family members stripped of inherited access
        var updatedUser = await _context.Users.FindAsync(user.Id);
        updatedUser!.SubscriptionTier.Should().BeNull();
        _mockFamilyService.Verify(f => f.UpdateFamilyMembersTierAsync(user.Id, null), Times.Once);
    }

    [Fact]
    public async Task HandleSubscriptionStatusChangeAsync_Canceled_RevokesFamilyMembersInheritedTier()
    {
        // Arrange
        _mockFamilyService
            .Setup(f => f.UpdateFamilyMembersTierAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier?>()))
            .Returns(Task.CompletedTask);

        var user = new User
        {
            Id = 1,
            Username = "owner",
            Email = "owner@example.com",
            PasswordHash = "hash",
            SubscriptionTier = SubscriptionTier.Premium
        };
        _context.Users.Add(user);
        _context.UserSubscriptions.Add(new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 15.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_owner"
        });
        await _context.SaveChangesAsync();

        // Act
        await _billingService.HandleSubscriptionStatusChangeAsync("sub_owner", "canceled");

        // Assert
        var updatedUser = await _context.Users.FindAsync(user.Id);
        updatedUser!.SubscriptionTier.Should().BeNull();
        _mockFamilyService.Verify(f => f.UpdateFamilyMembersTierAsync(user.Id, null), Times.Once);
    }

    [Fact]
    public async Task HandleSubscriptionStatusChangeAsync_PastDue_DoesNotRevokeFamilyMembers()
    {
        // Arrange — a past_due status must NOT strip inherited access (subscription can recover)
        _mockFamilyService
            .Setup(f => f.UpdateFamilyMembersTierAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier?>()))
            .Returns(Task.CompletedTask);

        var user = new User
        {
            Id = 1,
            Username = "owner",
            Email = "owner@example.com",
            PasswordHash = "hash",
            SubscriptionTier = SubscriptionTier.Premium
        };
        _context.Users.Add(user);
        _context.UserSubscriptions.Add(new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 15.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_owner"
        });
        await _context.SaveChangesAsync();

        // Act
        await _billingService.HandleSubscriptionStatusChangeAsync("sub_owner", "past_due");

        // Assert
        _mockFamilyService.Verify(
            f => f.UpdateFamilyMembersTierAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier?>()),
            Times.Never);
    }

    #endregion

    #region No-Card Trial Tests

    [Fact]
    public async Task StartTrialAsync_NewUser_ActivatesTrialWithoutSettingPersistedTier()
    {
        // Arrange
        var trialDaysSection = new Mock<IConfigurationSection>();
        trialDaysSection.Setup(s => s.Value).Returns((string?)null); // fall back to default 30
        _mockConfiguration
            .Setup(c => c.GetSection("Subscription:TrialPeriodDays"))
            .Returns(trialDaysSection.Object);

        var user = new User
        {
            Id = 1,
            Username = "newuser",
            Email = "newuser@example.com",
            PasswordHash = "hash",
            HasUsedTrial = false
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _billingService.StartTrialAsync(user.Id);

        // Assert
        result.IsTrialActive.Should().BeTrue();
        result.HasUsedTrial.Should().BeTrue();
        result.TrialEndDate.Should().NotBeNull();
        result.TrialEndDate.Should().BeAfter(DateTime.UtcNow);
        // Persisted tier must stay null so trial access expires automatically via
        // GetUserTierAsync's TrialEndDate check (no scheduled job to reset it otherwise).
        result.SubscriptionTier.Should().BeNull();

        // Trial grants Premium during the window, and expires on its own.
        (await _billingService.GetUserTierAsync(user.Id)).Should().Be(SubscriptionTier.Premium);
    }

    [Fact]
    public async Task StartTrialAsync_UserWhoAlreadyUsedTrial_Throws()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "used",
            Email = "used@example.com",
            PasswordHash = "hash",
            HasUsedTrial = true
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _billingService.StartTrialAsync(user.Id));
    }

    #endregion

    #region Duplicate-Subscription Guard Tests (double-billing)

    [Fact]
    public async Task CreateSubscriptionAsync_WithPastDueSubscription_ThrowsException()
    {
        // Arrange — a past-due subscription still represents a live billing relationship,
        // so a second subscribe must be blocked (else the user is billed twice).
        var user = new User
        {
            Id = 1,
            Username = "pastdue",
            Email = "pastdue@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123"
        };
        _context.Users.Add(user);
        _context.UserSubscriptions.Add(new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.PastDue,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_existing"
        });
        await _context.SaveChangesAsync();

        var request = new CreateBillingSubscriptionRequest
        {
            Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _billingService.CreateSubscriptionAsync(user.Id, request));
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_WithPastDueSubscription_ThrowsException()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Username = "pastdue",
            Email = "pastdue@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123"
        };
        _context.Users.Add(user);
        _context.UserSubscriptions.Add(new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.PastDue,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_existing"
        });
        await _context.SaveChangesAsync();
        _mockConfiguration.Setup(c => c["App:BaseUrl"]).Returns("https://buoy-ai.com");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _billingService.CreateCheckoutSessionAsync(
                user.Id, SubscriptionTier.Premium, SubscriptionBillingCycle.Monthly));
    }

    [Fact]
    public async Task ActivateSubscriptionFromCheckoutAsync_WithExistingLiveSubscription_DoesNotCreateDuplicate()
    {
        // Arrange — user already has a past-due subscription; a checkout for a *different*
        // Stripe subscription must not insert a second active row.
        _mockFamilyService
            .Setup(f => f.UpdateFamilyMembersTierAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier?>()))
            .Returns(Task.CompletedTask);

        var user = new User
        {
            Id = 1,
            Username = "pastdue",
            Email = "pastdue@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123"
        };
        _context.Users.Add(user);
        _context.UserSubscriptions.Add(new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.PastDue,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_original"
        });
        await _context.SaveChangesAsync();

        var metadata = new Dictionary<string, string>
        {
            ["userId"] = user.Id.ToString(),
            ["tier"] = ((int)SubscriptionTier.Premium).ToString(),
            ["billingCycle"] = ((int)SubscriptionBillingCycle.Monthly).ToString()
        };

        // Act — a second, different Stripe subscription tries to activate via checkout
        await _billingService.ActivateSubscriptionFromCheckoutAsync(
            user.Id.ToString(), "sub_second", "cus_test123", metadata, "active");

        // Assert — still only the original row; no duplicate was inserted
        var subs = await _context.UserSubscriptions.Where(s => s.UserId == user.Id).ToListAsync();
        subs.Should().HaveCount(1);
        subs[0].StripeSubscriptionId.Should().Be("sub_original");
    }

    [Fact]
    public async Task ActivateSubscriptionFromCheckoutAsync_WithIncompleteStatus_DoesNotActivate()
    {
        // Arrange — checkout.session.completed can arrive before payment settles. A status
        // that is not active/trialing must NOT grant access (finding #5 guard).
        var user = new User
        {
            Id = 1,
            Username = "pending",
            Email = "pending@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123",
            RequiresPaidPlan = true
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var metadata = new Dictionary<string, string>
        {
            ["userId"] = user.Id.ToString(),
            ["tier"] = ((int)SubscriptionTier.Premium).ToString(),
            ["billingCycle"] = ((int)SubscriptionBillingCycle.Monthly).ToString()
        };

        // Act
        await _billingService.ActivateSubscriptionFromCheckoutAsync(
            user.Id.ToString(), "sub_incomplete", "cus_test123", metadata, "incomplete");

        // Assert — no subscription row created, paywall still up
        (await _context.UserSubscriptions.AnyAsync(s => s.UserId == user.Id)).Should().BeFalse();
        var updated = await _context.Users.FindAsync(user.Id);
        updated!.SubscriptionTier.Should().BeNull();
        updated.RequiresPaidPlan.Should().BeTrue();
    }

    #endregion

    #region Change-Subscription Wire Contract Tests

    [Fact]
    public void ChangeSubscriptionRequest_BindsTierAndBillingCycleJsonKeys()
    {
        // The client PUTs {"tier":2,"billingCycle":12}. Ensure the JsonPropertyName mapping
        // matches so the plan change is not a silent no-op (finding #2).
        var json = "{\"tier\":2,\"billingCycle\":12}";

        var request = System.Text.Json.JsonSerializer
            .Deserialize<BuoyAI.Core.DTOs.Billing.ChangeSubscriptionRequest>(json);

        request.Should().NotBeNull();
        request!.NewTier.Should().Be(SubscriptionTier.Premium);
        request.NewBillingCycle.Should().Be(SubscriptionBillingCycle.Yearly);
    }

    [Fact]
    public void ChangeSubscriptionRequest_DoesNotBindLegacyNewTierKeys()
    {
        // Guards against regressing to the old mismatched "newTier"/"newBillingCycle" wire
        // contract, which bound to null and made the change a no-op.
        var json = "{\"newTier\":2,\"newBillingCycle\":12}";

        var request = System.Text.Json.JsonSerializer
            .Deserialize<BuoyAI.Core.DTOs.Billing.ChangeSubscriptionRequest>(json);

        request.Should().NotBeNull();
        request!.NewTier.Should().BeNull();
        request.NewBillingCycle.Should().BeNull();
    }

    #endregion

    #region Payment Idempotency Tests (webhook retry safety)

    [Fact]
    public async Task HandleSuccessfulPaymentAsync_DuplicateInvoice_DoesNotDoubleInsert()
    {
        // Arrange: a subscription with one already-recorded succeeded payment for an invoice.
        var user = new User { Email = "pay@test.com", FirstName = "Pay", SubscriptionTier = SubscriptionTier.Standard };
        _context.Users.Add(user);
        var subscription = new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_dup"
        };
        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        // First delivery records the payment.
        await _billingService.HandleSuccessfulPaymentAsync("sub_dup", 10.00m, "in_123");
        // Second delivery (Stripe retry of the same invoice event) must be a no-op.
        await _billingService.HandleSuccessfulPaymentAsync("sub_dup", 10.00m, "in_123");

        // Assert: exactly one succeeded payment row for this invoice.
        var count = await _context.SubscriptionPayments
            .CountAsync(p => p.StripeInvoiceId == "in_123" && p.Status == PaymentStatus.Succeeded);
        count.Should().Be(1);
    }

    [Fact]
    public async Task HandleFailedPaymentAsync_DuplicateInvoice_DoesNotDoubleInsertOrInflateRetryAttempt()
    {
        // Arrange
        var user = new User { Email = "fail@test.com", FirstName = "Fail", SubscriptionTier = SubscriptionTier.Standard };
        _context.Users.Add(user);
        var subscription = new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Standard,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 10.00m,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            NextBillingDate = DateTime.UtcNow.AddMonths(1),
            StripeSubscriptionId = "sub_dupfail"
        };
        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        await _billingService.HandleFailedPaymentAsync("sub_dupfail", "card_declined", "in_fail_1");
        // Retry of the same failed-invoice event.
        await _billingService.HandleFailedPaymentAsync("sub_dupfail", "card_declined", "in_fail_1");

        var count = await _context.SubscriptionPayments
            .CountAsync(p => p.StripeInvoiceId == "in_fail_1" && p.Status == PaymentStatus.Failed);
        count.Should().Be(1);
    }

    [Fact]
    public async Task HandleTrialLapsedAsync_StripsFamilyMembersInheritedTier()
    {
        var user = new User
        {
            Email = "lapse-family@example.com",
            Username = "lapse-family",
            PasswordHash = "x",
            Currency = "USD",
            IsTrialActive = true,
            HasUsedTrial = true,
            TrialEndDate = DateTime.UtcNow.AddDays(-1),
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        _context.UserSubscriptions.Add(new UserSubscription
        {
            UserId = user.Id,
            Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 0m,
            StartDate = DateTime.UtcNow.AddDays(-30),
            Status = UserSubscriptionStatus.Trialing,
            StripeSubscriptionId = "sub_lapse_family",
        });
        await _context.SaveChangesAsync();

        await _billingService.HandleTrialLapsedAsync("sub_lapse_family");

        // A cardless trial PAUSES rather than deletes, so this handler is the
        // only chance to revoke the invitees' PremiumInherited.
        _mockFamilyService.Verify(
            f => f.UpdateFamilyMembersTierAsync(user.Id, null), Times.Once);
    }

    #endregion
}

/// <summary>
/// Simple meter factory for unit tests
/// </summary>
internal class TestBillingMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new Meter(options.Name, options.Version);
    public void Dispose() { }
}
