using System;
using System.Threading.Tasks;
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
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Redeeming a promo code against an EXISTING subscription.
///
/// Chosen over extending trial dates because a discount is a native Stripe object: it leaves
/// TrialHistory untouched, so the "one trial per email, ever" gate keeps meaning what it says.
/// These tests pin the parts that could leak information or silently do nothing.
/// </summary>
public class BillingPromoRedemptionTests
{
    private const int UserId = 1;
    private const string SubId = "sub_live_123";

    private static Mock<IStripeService> _stripe = null!;
    private static BuoyAIDbContext _context = null!;

    private static BillingService CreateService(
        string? localSubscriptionId = SubId,
        string? stripeCustomerId = "cus_1")
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new BuoyAIDbContext(options);

        _context.Users.Add(new User
        {
            Id = UserId,
            Email = "u@example.com",
            PasswordHash = "hash",
            StripeCustomerId = stripeCustomerId,
        });

        if (localSubscriptionId != null)
        {
            _context.UserSubscriptions.Add(new UserSubscription
            {
                Id = 1,
                UserId = UserId,
                Tier = SubscriptionTier.Premium,
                BillingCycle = SubscriptionBillingCycle.Monthly,
                Status = UserSubscriptionStatus.Active,
                StripeSubscriptionId = localSubscriptionId,
                CreatedAt = DateTime.UtcNow,
            });
        }
        _context.SaveChanges();

        _stripe = new Mock<IStripeService>();

        return new BillingService(
            _context,
            _stripe.Object,
            new Mock<ISubscriptionSyncService>().Object,
            TestEntitlements.Create(_context, new ConfigurationBuilder().Build()),
            new Mock<IEmailService>().Object,
            new Mock<IFamilyService>().Object,
            new ConfigurationBuilder().Build(),
            new MemoryCache(new MemoryCacheOptions()),
            new Mock<ILogger<BillingService>>().Object,
            new BuoyAiMetrics(new TestBillingMeterFactory()));
    }

    private static SubscriptionDiscountDto Discount() => new()
    {
        Code = "WELCOMEBACK",
        PercentOff = 100m,
        Duration = "once",
        Description = "100% off for one billing period",
    };

    [Fact]
    public async Task RedeemPromoCodeAsync_AppliesToTheUsersSubscription()
    {
        var service = CreateService();
        _stripe.Setup(s => s.ApplyPromotionCodeToSubscriptionAsync(SubId, "WELCOMEBACK"))
            .ReturnsAsync(Discount());

        var result = await service.RedeemPromoCodeAsync(UserId, "WELCOMEBACK");

        result.Should().NotBeNull();
        result!.Description.Should().Be("100% off for one billing period");
        _stripe.Verify(s => s.ApplyPromotionCodeToSubscriptionAsync(SubId, "WELCOMEBACK"), Times.Once);
    }

    [Fact]
    public async Task RedeemPromoCodeAsync_UnknownCode_ReturnsNullWithoutDistinguishingWhy()
    {
        // The controller turns null into a flat 404. An unknown code and a restricted one must
        // look identical, or the form becomes an oracle for which codes exist.
        var service = CreateService();
        _stripe.Setup(s => s.ApplyPromotionCodeToSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((SubscriptionDiscountDto?)null);

        var result = await service.RedeemPromoCodeAsync(UserId, "NOPE");

        result.Should().BeNull();
    }

    [Fact]
    public async Task RedeemPromoCodeAsync_NoSubscriptionAnywhere_ReturnsNullAndNeverCallsStripe()
    {
        var service = CreateService(localSubscriptionId: null, stripeCustomerId: null);

        var result = await service.RedeemPromoCodeAsync(UserId, "WELCOMEBACK");

        result.Should().BeNull();
        _stripe.Verify(
            s => s.ApplyPromotionCodeToSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RedeemPromoCodeAsync_LocalRowMissing_FallsBackToStripe()
    {
        // The drift TrialReconciliationService repairs. Rejecting a valid code because our own
        // row has not caught up yet would be our bug presented as the user's mistake.
        var service = CreateService(localSubscriptionId: null);
        _stripe.Setup(s => s.GetLiveSubscriptionAsync("cus_1"))
            .ReturnsAsync(new StripeSubscriptionSnapshot("sub_from_stripe", "trialing", null, null, false));
        _stripe.Setup(s => s.ApplyPromotionCodeToSubscriptionAsync("sub_from_stripe", "WELCOMEBACK"))
            .ReturnsAsync(Discount());

        var result = await service.RedeemPromoCodeAsync(UserId, "WELCOMEBACK");

        result.Should().NotBeNull();
        _stripe.Verify(s => s.ApplyPromotionCodeToSubscriptionAsync("sub_from_stripe", "WELCOMEBACK"), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task RedeemPromoCodeAsync_BlankCode_ShortCircuits(string? code)
    {
        var service = CreateService();

        var result = await service.RedeemPromoCodeAsync(UserId, code!);

        result.Should().BeNull();
        _stripe.Verify(
            s => s.ApplyPromotionCodeToSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RedeemPromoCodeAsync_StripeFailure_Propagates()
    {
        // Must NOT collapse into "invalid code" — that tells the user their good code is bad.
        var service = CreateService();
        _stripe.Setup(s => s.ApplyPromotionCodeToSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("stripe down"));

        var act = async () => await service.RedeemPromoCodeAsync(UserId, "WELCOMEBACK");

        await act.Should().ThrowAsync<Exception>().WithMessage("stripe down");
    }

    [Fact]
    public async Task GetActiveDiscountAsync_ReadsLiveFromStripe()
    {
        // Deliberately not cached locally: Stripe owns the lifecycle and a discount expires on
        // its own, so a stored copy would go stale with nothing to invalidate it.
        var service = CreateService();
        _stripe.Setup(s => s.GetSubscriptionDiscountAsync(SubId)).ReturnsAsync(Discount());

        var result = await service.GetActiveDiscountAsync(UserId);

        result!.Code.Should().Be("WELCOMEBACK");
        _stripe.Verify(s => s.GetSubscriptionDiscountAsync(SubId), Times.Once);
    }

    [Fact]
    public async Task GetActiveDiscountAsync_NoSubscription_ReturnsNull()
    {
        var service = CreateService(localSubscriptionId: null, stripeCustomerId: null);

        var result = await service.GetActiveDiscountAsync(UserId);

        result.Should().BeNull();
    }
}
