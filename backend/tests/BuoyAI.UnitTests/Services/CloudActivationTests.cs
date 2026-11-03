using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The first local row for a subscription bought through Checkout. Cloud Checkouts say what they sold
/// ("entitlements", "interval") instead of a tier number, and the row must come out right from the
/// first moment; hosted Checkouts keep using the tier number they always did.
/// </summary>
public class CloudActivationTests
{
  private readonly BuoyAIDbContext _context = new(new DbContextOptionsBuilder<BuoyAIDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
  private readonly Mock<IFamilyService> _family = new();

  private readonly Mock<IStripeService> _stripeMock = new();

  private BillingService Service()
  {
    var config = new ConfigurationBuilder().Build();
    var entitlements = TestEntitlements.Create(_context, config);
    return new BillingService(
      _context, _stripeMock.Object, Mock.Of<ISubscriptionSyncService>(), entitlements, Mock.Of<IEmailService>(),
      _family.Object, config, new MemoryCache(new MemoryCacheOptions()),
      NullLogger<BillingService>.Instance, new BuoyAiMetrics(new TestBillingMeterFactory()));
  }

  private async Task<User> UserAsync()
  {
    var user = new User { Email = "u@example.com", Username = "u", PasswordHash = "x", FirstName = "U", LastName = "S", StripeCustomerId = "cus_1" };
    _context.Users.Add(user);
    await _context.SaveChangesAsync();
    return user;
  }

  [Theory]
  [InlineData("bank_sync", SubscriptionTier.Standard)]
  [InlineData("bank_sync,instant_refresh", SubscriptionTier.Standard)]
  [InlineData("bank_sync,ai", SubscriptionTier.Premium)]
  [InlineData("ai", SubscriptionTier.Premium)]
  [InlineData("instant_refresh", SubscriptionTier.Standard)]   // refresh alone is nothing: falls to the floor
  public async Task ACloudCheckout_DerivesTheTierFromTheProductsSold(string sold, SubscriptionTier expected)
  {
    var user = await UserAsync();
    var metadata = new Dictionary<string, string> { ["userId"] = user.Id.ToString(), ["entitlements"] = sold, ["interval"] = "year" };

    await Service().ActivateSubscriptionFromCheckoutAsync(null, "sub_1", "cus_1", metadata, "active", 38.98m);

    var row = await _context.UserSubscriptions.SingleAsync();
    row.Tier.Should().Be(expected);
    row.BillingCycle.Should().Be(SubscriptionBillingCycle.Yearly);
    row.Amount.Should().Be(38.98m);
    row.Status.Should().Be(UserSubscriptionStatus.Active);
    (await _context.Users.FindAsync(user.Id))!.SubscriptionTier.Should().Be(expected);
  }

  [Fact]
  public async Task ACloudCheckout_CancelsTheHostedTrial_SoItCannotConvertIntoASecondBill()
  {
    var user = await UserAsync();
    _context.UserSubscriptions.Add(new UserSubscription
    {
      UserId = user.Id, Tier = SubscriptionTier.Premium, BillingCycle = SubscriptionBillingCycle.Monthly,
      Status = UserSubscriptionStatus.Trialing, StripeSubscriptionId = "sub_trial",
      StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();
    var metadata = new Dictionary<string, string> { ["userId"] = user.Id.ToString(), ["entitlements"] = "bank_sync", ["interval"] = "year" };

    await Service().ActivateSubscriptionFromCheckoutAsync(null, "sub_cloud", "cus_1", metadata, "active", 10m);

    _stripeMock.Verify(s => s.CancelSubscriptionAsync("sub_trial", true), Times.Once);
    (await _context.UserSubscriptions.SingleAsync(s => s.StripeSubscriptionId == "sub_trial")).Status
      .Should().Be(UserSubscriptionStatus.Canceled);
    (await _context.UserSubscriptions.SingleAsync(s => s.StripeSubscriptionId == "sub_cloud")).Status
      .Should().Be(UserSubscriptionStatus.Active);
  }

  [Fact]
  public async Task TheOldTrialBeingDeleted_KeepsTheCloudPlansTier()
  {
    var user = await UserAsync();
    user.SubscriptionTier = SubscriptionTier.Standard;
    var now = DateTime.UtcNow;
    _context.UserSubscriptions.AddRange(
      new UserSubscription { UserId = user.Id, Tier = SubscriptionTier.Premium, BillingCycle = SubscriptionBillingCycle.Monthly,
        Status = UserSubscriptionStatus.Trialing, StripeSubscriptionId = "sub_trial", StartDate = now, NextBillingDate = now, CreatedAt = now, UpdatedAt = now },
      new UserSubscription { UserId = user.Id, Tier = SubscriptionTier.Standard, BillingCycle = SubscriptionBillingCycle.Yearly,
        Status = UserSubscriptionStatus.Active, StripeSubscriptionId = "sub_cloud", StartDate = now, NextBillingDate = now, CreatedAt = now, UpdatedAt = now });
    await _context.SaveChangesAsync();

    await Service().CancelSubscriptionByStripeIdAsync("sub_trial");

    (await _context.Users.FindAsync(user.Id))!.SubscriptionTier.Should().Be(SubscriptionTier.Standard);
    _family.Verify(f => f.UpdateFamilyMembersTierAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier?>()), Times.Never);
  }

  [Fact]
  public async Task AMonthlyCloudCheckout_IsRecordedMonthly()
  {
    var user = await UserAsync();
    var metadata = new Dictionary<string, string> { ["userId"] = user.Id.ToString(), ["entitlements"] = "bank_sync", ["interval"] = "month" };
    await Service().ActivateSubscriptionFromCheckoutAsync(null, "sub_1", "cus_1", metadata, "active", 2.79m);
    (await _context.UserSubscriptions.SingleAsync()).BillingCycle.Should().Be(SubscriptionBillingCycle.Monthly);
  }

  [Fact]
  public async Task ACloudCheckout_CannotSmuggleAnAdminTier()
  {
    var user = await UserAsync();
    var metadata = new Dictionary<string, string>
    {
      ["userId"] = user.Id.ToString(), ["entitlements"] = "bank_sync", ["tier"] = "99"   // ignored: products decide
    };
    await Service().ActivateSubscriptionFromCheckoutAsync(null, "sub_1", "cus_1", metadata, "active", 1m);
    (await _context.UserSubscriptions.SingleAsync()).Tier.Should().Be(SubscriptionTier.Standard);
  }

  [Fact]
  public async Task ACloudCheckout_WithUnknownProducts_GrantsOnlyTheFloor()
  {
    var user = await UserAsync();
    var metadata = new Dictionary<string, string> { ["userId"] = user.Id.ToString(), ["entitlements"] = "admin,root" };
    await Service().ActivateSubscriptionFromCheckoutAsync(null, "sub_1", "cus_1", metadata, "active", 1m);
    (await _context.UserSubscriptions.SingleAsync()).Tier.Should().Be(SubscriptionTier.Standard);
  }

  [Fact]
  public async Task AHostedCheckout_StillUsesItsTierAndCycleNumbers()
  {
    var user = await UserAsync();
    var metadata = new Dictionary<string, string>
    {
      ["userId"] = user.Id.ToString(),
      ["tier"] = ((int)SubscriptionTier.Premium).ToString(),
      ["billingCycle"] = ((int)SubscriptionBillingCycle.Yearly).ToString()
    };

    await Service().ActivateSubscriptionFromCheckoutAsync(null, "sub_h", "cus_1", metadata, "active", 99m);

    var row = await _context.UserSubscriptions.SingleAsync();
    row.Tier.Should().Be(SubscriptionTier.Premium);
    row.BillingCycle.Should().Be(SubscriptionBillingCycle.Yearly);
    row.Amount.Should().Be(99m);
  }

  [Fact]
  public async Task ACloudCheckout_WithAnIncompletePayment_ActivatesNothing()
  {
    var user = await UserAsync();
    var metadata = new Dictionary<string, string> { ["userId"] = user.Id.ToString(), ["entitlements"] = "bank_sync" };
    await Service().ActivateSubscriptionFromCheckoutAsync(null, "sub_1", "cus_1", metadata, "incomplete", 1m);
    (await _context.UserSubscriptions.CountAsync()).Should().Be(0);
  }
}
