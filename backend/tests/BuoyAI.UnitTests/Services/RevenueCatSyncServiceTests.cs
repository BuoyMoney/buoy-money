using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The projection from RevenueCat webhook events (App Store / Play) onto UserSubscriptions and
/// the user's entitlement flags. Store subscriptions and Stripe rows share one entitlement, so
/// the important cases are the ones where they interact.
/// </summary>
public class RevenueCatSyncServiceTests
{
  private readonly BuoyAIDbContext _db;
  private readonly Dictionary<string, string?> _settings = new()
  {
    ["RevenueCat:WebhookAuthorization"] = "shared-secret-value",
  };

  public RevenueCatSyncServiceTests()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    _db = new BuoyAIDbContext(options);
  }

  private RevenueCatSyncService CreateService()
  {
    var config = new ConfigurationBuilder().AddInMemoryCollection(_settings).Build();
    var factory = new Mock<IHttpClientFactory>();
    factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
    return new RevenueCatSyncService(_db, factory.Object, config, NullLogger<RevenueCatSyncService>.Instance);
  }

  private async Task<User> SeedUserAsync(int id = 4, bool trialActive = false)
  {
    var user = new User
    {
      Id = id, Email = $"u{id}@example.com", Username = $"u{id}", PasswordHash = "x",
      IsTrialActive = trialActive,
      TrialEndDate = trialActive ? DateTime.UtcNow.AddDays(20) : null,
      RequiresPaidPlan = !trialActive,
    };
    _db.Users.Add(user);
    await _db.SaveChangesAsync();
    return user;
  }

  private static RevenueCatEvent Event(string type, int userId = 4, string product = StoreProducts.PremiumMonthly,
    double expiresInDays = 30, string store = "APP_STORE", string environment = "PRODUCTION", string? newProduct = null) => new()
  {
    Type = type, Id = Guid.NewGuid().ToString("N"), AppUserId = userId.ToString(), ProductId = product,
    NewProductId = newProduct, Store = store, Environment = environment,
    PurchasedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    ExpirationAtMs = DateTimeOffset.UtcNow.AddDays(expiresInDays).ToUnixTimeMilliseconds(),
    Price = 17.99m, Currency = "USD",
  };

  [Fact]
  public async Task InitialPurchase_CreatesAnActiveStoreRowAndGrantsTheTier()
  {
    var user = await SeedUserAsync();
    var service = CreateService();

    var result = await service.ApplyEventAsync(Event("INITIAL_PURCHASE"));

    result.Applied.Should().BeTrue();
    var row = _db.UserSubscriptions.Single();
    row.Provider.Should().Be(SubscriptionProvider.AppStore);
    row.ExternalId.Should().Be("appstore:buoyai_premium_monthly");
    row.Tier.Should().Be(SubscriptionTier.Premium);
    row.BillingCycle.Should().Be(SubscriptionBillingCycle.Monthly);
    row.Status.Should().Be(UserSubscriptionStatus.Active);
    row.StripeSubscriptionId.Should().BeNull();
    row.Amount.Should().Be(17.99m);
    user.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
    user.RequiresPaidPlan.Should().BeFalse();
  }

  [Fact]
  public async Task Renewal_UpdatesTheSameRowInsteadOfAddingOne()
  {
    await SeedUserAsync();
    var service = CreateService();
    await service.ApplyEventAsync(Event("INITIAL_PURCHASE", expiresInDays: 30));

    await service.ApplyEventAsync(Event("RENEWAL", expiresInDays: 60));

    _db.UserSubscriptions.Should().HaveCount(1);
    _db.UserSubscriptions.Single().NextBillingDate.Should().BeCloseTo(DateTime.UtcNow.AddDays(60), TimeSpan.FromMinutes(1));
  }

  [Fact]
  public async Task Expiration_RevokesWhenNothingElseIsLive()
  {
    var user = await SeedUserAsync();
    var service = CreateService();
    await service.ApplyEventAsync(Event("INITIAL_PURCHASE"));

    await service.ApplyEventAsync(Event("EXPIRATION", expiresInDays: -0.01));

    _db.UserSubscriptions.Single().Status.Should().Be(UserSubscriptionStatus.Expired);
    user.SubscriptionTier.Should().BeNull();
    user.RequiresPaidPlan.Should().BeTrue();
  }

  [Fact]
  public async Task Expiration_KeepsAccessWhileAStripeSubscriptionIsLive()
  {
    var user = await SeedUserAsync();
    _db.UserSubscriptions.Add(new UserSubscription
    {
      UserId = user.Id, Provider = SubscriptionProvider.Stripe, Tier = SubscriptionTier.Standard,
      BillingCycle = SubscriptionBillingCycle.Monthly, Status = UserSubscriptionStatus.Active,
      StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow.AddDays(10), StripeSubscriptionId = "sub_1",
    });
    await _db.SaveChangesAsync();
    var service = CreateService();
    await service.ApplyEventAsync(Event("INITIAL_PURCHASE"));
    user.SubscriptionTier.Should().Be(SubscriptionTier.Premium, "the higher of the two live rows wins");

    await service.ApplyEventAsync(Event("EXPIRATION", expiresInDays: -0.01));

    user.SubscriptionTier.Should().Be(SubscriptionTier.Standard, "the Stripe row is still live");
    user.RequiresPaidPlan.Should().BeFalse();
  }

  [Fact]
  public async Task Expiration_KeepsARunningTrialAlone()
  {
    var user = await SeedUserAsync(trialActive: true);
    user.SubscriptionTier = SubscriptionTier.Premium;
    await _db.SaveChangesAsync();
    var service = CreateService();
    await service.ApplyEventAsync(Event("INITIAL_PURCHASE"));

    await service.ApplyEventAsync(Event("EXPIRATION", expiresInDays: -0.01));

    user.RequiresPaidPlan.Should().BeFalse("the cardless trial is still running");
    user.IsTrialActive.Should().BeTrue();
  }

  [Fact]
  public async Task Cancellation_BeforeExpiry_KeepsAccessUntilTheEndDate()
  {
    var user = await SeedUserAsync();
    var service = CreateService();
    await service.ApplyEventAsync(Event("INITIAL_PURCHASE"));

    await service.ApplyEventAsync(Event("CANCELLATION", expiresInDays: 12));

    var row = _db.UserSubscriptions.Single();
    row.Status.Should().Be(UserSubscriptionStatus.Active);
    row.EndDate.Should().BeCloseTo(DateTime.UtcNow.AddDays(12), TimeSpan.FromMinutes(1));
    user.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
  }

  [Fact]
  public async Task Cancellation_WithPastExpiry_IsARefundAndRevokesNow()
  {
    var user = await SeedUserAsync();
    var service = CreateService();
    await service.ApplyEventAsync(Event("INITIAL_PURCHASE"));

    await service.ApplyEventAsync(Event("CANCELLATION", expiresInDays: -1));

    _db.UserSubscriptions.Single().Status.Should().Be(UserSubscriptionStatus.Canceled);
    user.RequiresPaidPlan.Should().BeTrue();
  }

  [Fact]
  public async Task BillingIssue_KeepsAccessAsPastDue()
  {
    var user = await SeedUserAsync();
    var service = CreateService();
    await service.ApplyEventAsync(Event("INITIAL_PURCHASE"));

    await service.ApplyEventAsync(Event("BILLING_ISSUE"));

    _db.UserSubscriptions.Single().Status.Should().Be(UserSubscriptionStatus.PastDue);
    user.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
    user.RequiresPaidPlan.Should().BeFalse();
  }

  [Fact]
  public async Task ProductChange_RetiresThePreviousStoreRow()
  {
    var user = await SeedUserAsync();
    var service = CreateService();
    await service.ApplyEventAsync(Event("INITIAL_PURCHASE", product: StoreProducts.StandardMonthly));

    await service.ApplyEventAsync(Event("PRODUCT_CHANGE", product: StoreProducts.StandardMonthly, newProduct: StoreProducts.PremiumYearly));

    var rows = _db.UserSubscriptions.OrderBy(r => r.Id).ToList();
    rows.Should().HaveCount(2);
    rows[0].Status.Should().Be(UserSubscriptionStatus.Canceled);
    rows[1].Tier.Should().Be(SubscriptionTier.Premium);
    rows[1].BillingCycle.Should().Be(SubscriptionBillingCycle.Yearly);
    rows[1].Status.Should().Be(UserSubscriptionStatus.Active);
    user.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
  }

  [Fact]
  public async Task SandboxEvent_IsIgnoredWhenAcceptSandboxIsFalse()
  {
    await SeedUserAsync();
    _settings["RevenueCat:AcceptSandbox"] = "false";
    var service = CreateService();

    var result = await service.ApplyEventAsync(Event("INITIAL_PURCHASE", environment: "SANDBOX"));

    result.Applied.Should().BeFalse();
    _db.UserSubscriptions.Should().BeEmpty();
  }

  [Fact]
  public async Task SandboxEvent_IsAcceptedByDefault()
  {
    await SeedUserAsync();
    var service = CreateService();

    (await service.ApplyEventAsync(Event("INITIAL_PURCHASE", environment: "SANDBOX"))).Applied.Should().BeTrue();
  }

  [Fact]
  public async Task UnknownUserUnknownProductAndForeignStore_AreIgnoredNotThrown()
  {
    await SeedUserAsync();
    var service = CreateService();

    (await service.ApplyEventAsync(Event("INITIAL_PURCHASE", userId: 999))).Applied.Should().BeFalse();
    (await service.ApplyEventAsync(Event("INITIAL_PURCHASE", product: "someone_elses_product"))).Applied.Should().BeFalse();
    (await service.ApplyEventAsync(Event("INITIAL_PURCHASE", store: "STRIPE"))).Applied.Should().BeFalse("web billing stays with our own Stripe projection");
    (await service.ApplyEventAsync(new RevenueCatEvent { Type = "INITIAL_PURCHASE", AppUserId = "$RCAnonymousID:abc" })).Applied.Should().BeFalse();
    _db.UserSubscriptions.Should().BeEmpty();
  }

  [Fact]
  public async Task TestEvent_IsAcknowledgedWithoutChanges()
  {
    var service = CreateService();
    (await service.ApplyEventAsync(new RevenueCatEvent { Type = "TEST", Id = "t1" })).Applied.Should().BeFalse();
  }

  [Fact]
  public void WebhookAuthorization_IsAnExactMatchOnly()
  {
    var service = CreateService();
    service.IsConfigured.Should().BeTrue();
    service.IsWebhookAuthorized("shared-secret-value").Should().BeTrue();
    service.IsWebhookAuthorized(" shared-secret-value ").Should().BeTrue("RevenueCat sends the value verbatim; whitespace is not a secret");
    service.IsWebhookAuthorized("shared-secret-valu").Should().BeFalse();
    service.IsWebhookAuthorized("").Should().BeFalse();
    service.IsWebhookAuthorized(null).Should().BeFalse();
  }

  [Fact]
  public void WebhookAuthorization_FailsClosedWhenNothingIsConfigured()
  {
    _settings.Remove("RevenueCat:WebhookAuthorization");
    var service = CreateService();
    service.IsConfigured.Should().BeFalse();
    service.IsWebhookAuthorized("anything").Should().BeFalse();
  }

  [Fact]
  public async Task ProcessedEvents_AreRememberedOnce()
  {
    var service = CreateService();
    (await service.HasProcessedEventAsync("e1")).Should().BeFalse();
    await service.MarkEventProcessedAsync("e1", "RENEWAL");
    await service.MarkEventProcessedAsync("e1", "RENEWAL");
    (await service.HasProcessedEventAsync("e1")).Should().BeTrue();
    _db.ProcessedRevenueCatEvents.Should().HaveCount(1);
  }

  [Fact]
  public async Task RefreshFromRevenueCat_WithoutASecretKey_IsANoOp()
  {
    await SeedUserAsync();
    var service = CreateService();
    (await service.RefreshFromRevenueCatAsync(4, "test")).Should().BeFalse();
  }

  [Fact]
  public void StoreProducts_ResolveHonoursConfigOverridesAndPlayBasePlans()
  {
    Func<string, string?> none = _ => null;
    StoreProducts.Resolve("buoyai_premium_yearly", none).Should().Be((SubscriptionTier.Premium, SubscriptionBillingCycle.Yearly));
    StoreProducts.Resolve("buoyai_standard_monthly:monthly-base", none).Should().Be((SubscriptionTier.Standard, SubscriptionBillingCycle.Monthly));
    StoreProducts.Resolve("nope", none).Should().BeNull();
    Func<string, string?> overridden = k => k == StoreProducts.ConfigKey(SubscriptionTier.Premium, SubscriptionBillingCycle.Monthly) ? "com.buoyai.premium.m" : null;
    StoreProducts.Resolve("com.buoyai.premium.m", overridden).Should().Be((SubscriptionTier.Premium, SubscriptionBillingCycle.Monthly));
    StoreProducts.Resolve("buoyai_premium_monthly", overridden).Should().BeNull("the override replaces the default id");
  }
}

/// <summary>A live store subscription must survive the Stripe trial it was bought during ending.</summary>
public class StripeProjectionStoreGuardTests
{
  [Fact]
  public async Task PausedStripeTrial_DoesNotRevokeALiveAppStoreSubscription()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    await using var db = new BuoyAIDbContext(options);
    var user = new User
    {
      Id = 7, Email = "u7@example.com", Username = "u7", PasswordHash = "x", StripeCustomerId = "cus_7",
      IsTrialActive = true, TrialEndDate = DateTime.UtcNow.AddDays(-1), SubscriptionTier = SubscriptionTier.Premium,
    };
    db.Users.Add(user);
    db.UserSubscriptions.Add(new UserSubscription
    {
      UserId = 7, Provider = SubscriptionProvider.AppStore, ExternalId = "appstore:buoyai_premium_monthly",
      Tier = SubscriptionTier.Premium, BillingCycle = SubscriptionBillingCycle.Monthly, Status = UserSubscriptionStatus.Active,
      StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow.AddDays(20),
    });
    await db.SaveChangesAsync();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
    var service = new SubscriptionSyncService(db, Mock.Of<IStripeService>(), config, NullLogger<SubscriptionSyncService>.Instance);

    await service.ProjectAsync(7, new StripeSubscriptionSnapshot("sub_trial", "paused", null, null, false));

    user.IsTrialActive.Should().BeFalse();
    user.SubscriptionTier.Should().Be(SubscriptionTier.Premium, "the App Store subscription is live");
    user.RequiresPaidPlan.Should().BeFalse();
    db.UserSubscriptions.Single(s => s.StripeSubscriptionId == "sub_trial").Status.Should().Be(UserSubscriptionStatus.TrialExpired);
  }
}
