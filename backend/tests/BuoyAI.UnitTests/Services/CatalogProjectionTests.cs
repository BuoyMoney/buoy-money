using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The Cloud half of THE mapping: a subscription with several priced products becomes rows in
/// UserEntitlements, a legacy tier the older gates still read, and a local plan row. Hosted
/// Standard and Premium subscriptions must come out exactly as they did before.
/// </summary>
public class CatalogProjectionTests
{
  private readonly BuoyAIDbContext _context;
  private readonly Mock<IStripeService> _stripe = new();
  private readonly SubscriptionSyncService _service;
  private static readonly DateTime PeriodEnd = DateTime.UtcNow.AddDays(200);

  public CatalogProjectionTests()
  {
    _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["Stripe:Products:Premium:Monthly"] = "price_prem_m",
      ["Stripe:Products:Standard:Yearly"] = "price_std_y"
    }).Build();
    _service = new SubscriptionSyncService(_context, _stripe.Object, config, Mock.Of<ILogger<SubscriptionSyncService>>());
  }

  private async Task<User> SeedUserAsync(string email = "u@example.com", bool trialing = false)
  {
    var user = new User
    {
      Email = email, Username = email, PasswordHash = "x", FirstName = "U", LastName = "S",
      StripeCustomerId = "cus_1", IsTrialActive = trialing,
      TrialEndDate = trialing ? DateTime.UtcNow.AddDays(10) : null
    };
    _context.Users.Add(user);
    await _context.SaveChangesAsync();
    return user;
  }

  private static StripeSnapshotItem Item(string entitlements, long qty = 1, bool founding = false, string interval = "year", long cents = 2399) =>
    new("price_" + entitlements.Replace(',', '_'), entitlements + "_" + interval, entitlements, qty, founding, interval, cents);

  private static StripeSubscriptionSnapshot Snap(
    string status, string id = "sub_1", bool cancel = false, Dictionary<string, string>? metadata = null,
    params StripeSnapshotItem[] items) =>
    new(id, status, null, items.FirstOrDefault()?.PriceId, cancel, items, PeriodEnd, metadata);

  private Task<List<UserEntitlement>> RowsAsync(int userId) =>
    _context.UserEntitlements.Where(e => e.UserId == userId).OrderBy(e => e.Entitlement).ToListAsync();

  // ── a new Cloud subscription ──

  [Fact]
  public async Task AnActiveCloudSubscription_BecomesOneRowPerProduct()
  {
    var user = await SeedUserAsync();

    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync", founding: true), Item("ai", founding: true, cents: 1499)]));

    var rows = await RowsAsync(user.Id);
    rows.Select(r => r.Entitlement).Should().Equal("ai", "bank_sync");
    rows.Should().OnlyContain(r => r.ExternalId == "sub_1" && r.Provider == SubscriptionProvider.Stripe
      && r.Status == "active" && r.Founding && r.PaidThrough == PeriodEnd && r.EndsAt == null && r.Quantity == 1);
  }

  [Fact]
  public async Task TheLegacyTier_FollowsTheProducts_SoTheOlderGatesKeepWorking()
  {
    var user = await SeedUserAsync();

    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync")]));
    (await _context.Users.FindAsync(user.Id))!.SubscriptionTier.Should().Be(SubscriptionTier.Standard);

    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync"), Item("ai")]));
    (await _context.Users.FindAsync(user.Id))!.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
  }

  [Fact]
  public async Task TheLocalPlanRow_MirrorsTheAmountAndInterval()
  {
    var user = await SeedUserAsync();

    await _service.ProjectAsync(user.Id, Snap("active",
      items: [Item("bank_sync", cents: 2399), Item("extra_logins", qty: 3, cents: 600), Item("ai", cents: 1499)]));

    var row = await _context.UserSubscriptions.SingleAsync(s => s.UserId == user.Id);
    row.StripeSubscriptionId.Should().Be("sub_1");
    row.Amount.Should().Be((2399 + 3 * 600 + 1499) / 100m);
    row.BillingCycle.Should().Be(SubscriptionBillingCycle.Yearly);
    row.Tier.Should().Be(SubscriptionTier.Premium);
    row.NextBillingDate.Should().Be(PeriodEnd);
    row.Status.Should().Be(UserSubscriptionStatus.Active);
  }

  [Fact]
  public async Task AMonthlyPlan_IsRecordedAsMonthly()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync", interval: "month", cents: 279)]));
    (await _context.UserSubscriptions.SingleAsync()).BillingCycle.Should().Be(SubscriptionBillingCycle.Monthly);
  }

  [Fact]
  public async Task ProjectingTwice_ChangesNothing()
  {
    var user = await SeedUserAsync();
    var snapshot = Snap("active", items: [Item("bank_sync"), Item("ai")]);

    await _service.ProjectAsync(user.Id, snapshot);
    await _service.ProjectAsync(user.Id, snapshot);

    (await RowsAsync(user.Id)).Should().HaveCount(2);
    (await _context.UserSubscriptions.CountAsync()).Should().Be(1);
  }

  // ── changes ──

  [Fact]
  public async Task AddingAProduct_AddsARow_AndRaisesTheTier()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync")]));

    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync"), Item("instant_refresh")]));

    (await RowsAsync(user.Id)).Select(r => r.Entitlement).Should().Equal("bank_sync", "instant_refresh");
  }

  [Fact]
  public async Task ARemovedProduct_LosesItsRow()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync"), Item("ai")]));

    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync")]));

    (await RowsAsync(user.Id)).Select(r => r.Entitlement).Should().Equal("bank_sync");
  }

  [Fact]
  public async Task ChangingTheExtraLoginsQuantity_UpdatesTheRow()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync"), Item("extra_logins", qty: 2)]));
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync"), Item("extra_logins", qty: 6)]));

    (await RowsAsync(user.Id)).Single(r => r.Entitlement == "extra_logins").Quantity.Should().Be(6);
    (await RowsAsync(user.Id)).Should().HaveCount(2);
  }

  [Fact]
  public async Task AScheduledRemoval_ShowsAsAnEndDate_UntilItHappens()
  {
    var user = await SeedUserAsync();
    var end = DateTime.UtcNow.AddDays(40);
    var metadata = new Dictionary<string, string> { ["pending_end_ai"] = end.ToString("o") };

    await _service.ProjectAsync(user.Id, Snap("active", metadata: metadata, items: [Item("bank_sync"), Item("ai")]));
    (await RowsAsync(user.Id)).Single(r => r.Entitlement == "ai").EndsAt.Should().BeCloseTo(end, TimeSpan.FromSeconds(1));

    // The renewal comes: Stripe drops the item and the row goes with it.
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync")]));
    (await RowsAsync(user.Id)).Select(r => r.Entitlement).Should().Equal("bank_sync");
  }

  [Fact]
  public async Task ABoughtAgainProduct_IsNotCutOffByAStaleEndDate()
  {
    var user = await SeedUserAsync();
    var stale = new Dictionary<string, string> { ["pending_end_ai"] = DateTime.UtcNow.AddDays(-30).ToString("o") };
    await _service.ProjectAsync(user.Id, Snap("active", metadata: stale, items: [Item("bank_sync"), Item("ai")]));
    (await RowsAsync(user.Id)).Single(r => r.Entitlement == "ai").EndsAt.Should().BeNull();
  }

  // ── the lifecycle ──

  [Theory]
  [InlineData("past_due")]
  [InlineData("unpaid")]
  public async Task AFailingCard_KeepsTheProducts_WhileStripeRetries(string status)
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync")]));

    await _service.ProjectAsync(user.Id, Snap(status, items: [Item("bank_sync")]));

    var rows = await RowsAsync(user.Id);
    rows.Should().ContainSingle().Which.Status.Should().Be("past_due");
    (await _context.Users.FindAsync(user.Id))!.RequiresPaidPlan.Should().BeFalse();
  }

  [Theory]
  [InlineData("canceled")]
  [InlineData("paused")]
  [InlineData("incomplete_expired")]
  public async Task AnEndedSubscription_RemovesTheProducts_AndTheTier(string status)
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync"), Item("ai")]));

    await _service.ProjectAsync(user.Id, Snap(status, items: [Item("bank_sync"), Item("ai")]));

    (await RowsAsync(user.Id)).Should().BeEmpty();
    var after = await _context.Users.FindAsync(user.Id);
    after!.SubscriptionTier.Should().BeNull();
    after.RequiresPaidPlan.Should().BeTrue();
  }

  [Theory]
  [InlineData("incomplete")]
  [InlineData("brand_new_status")]
  public async Task APendingOrUnknownStatus_LeavesTheProductsAlone(string status)
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync")]));

    await _service.ProjectAsync(user.Id, Snap(status, items: [Item("bank_sync"), Item("ai")]));

    (await RowsAsync(user.Id)).Select(r => r.Entitlement).Should().Equal("bank_sync");
  }

  [Fact]
  public async Task ASnapshotThatDidNotCarryItsItems_NeverDeletesProducts()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("bank_sync")]));

    // A partial snapshot (items null) is "I do not know", not "there are none".
    await _service.ProjectAsync(user.Id, new StripeSubscriptionSnapshot("sub_1", "active", null, "price_x", false));

    (await RowsAsync(user.Id)).Should().ContainSingle();
  }

  [Fact]
  public async Task ACancelAtPeriodEnd_ShowsEveryProductAsEnding_ButKeepsThem()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", cancel: true, items: [Item("bank_sync"), Item("ai")]));
    (await RowsAsync(user.Id)).Should().OnlyContain(r => r.EndsAt == PeriodEnd);
  }

  [Fact]
  public async Task AnInstantRefresh_WithoutBankSync_IsNotStored()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", items: [Item("ai"), Item("instant_refresh")]));
    (await RowsAsync(user.Id)).Select(r => r.Entitlement).Should().Equal("ai");
  }

  // ── trial and other subscriptions ──

  [Fact]
  public async Task ACloudPlanInItsTrial_GrantsExactlyItsProducts()
  {
    var user = await SeedUserAsync();
    var trialEnd = DateTime.UtcNow.AddDays(14);

    await _service.ProjectAsync(user.Id, new StripeSubscriptionSnapshot(
      "sub_1", "trialing", trialEnd, "p", false, [Item("bank_sync")], PeriodEnd, null));

    var after = await _context.Users.FindAsync(user.Id);
    after!.IsTrialActive.Should().BeTrue();
    after.SubscriptionTier.Should().Be(SubscriptionTier.Standard);
    (await RowsAsync(user.Id)).Should().ContainSingle().Which.Entitlement.Should().Be("bank_sync");
  }

  [Fact]
  public async Task AnOldTrialPausing_DoesNotRevokeACloudPlanTheUserBoughtSince()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, Snap("active", id: "sub_cloud", items: [Item("bank_sync"), Item("ai")]));

    // The cardless trial subscription from before they bought now pauses.
    await _service.ProjectAsync(user.Id, new StripeSubscriptionSnapshot("sub_old_trial", "paused", null, "price_prem_m", false));

    var after = await _context.Users.FindAsync(user.Id);
    after!.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
    after.RequiresPaidPlan.Should().BeFalse();
    (await RowsAsync(user.Id)).Should().HaveCount(2);
  }

  [Fact]
  public async Task AnAppStorePlan_IsNotTouchedByACloudSubscriptionEnding()
  {
    var user = await SeedUserAsync();
    _context.UserSubscriptions.Add(new UserSubscription
    {
      UserId = user.Id, Tier = SubscriptionTier.Premium, BillingCycle = SubscriptionBillingCycle.Yearly,
      Status = UserSubscriptionStatus.Active, Provider = SubscriptionProvider.AppStore, ExternalId = "app_store:premium",
      StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow.AddDays(300),
      CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });
    user.SubscriptionTier = SubscriptionTier.Premium;
    await _context.SaveChangesAsync();

    await _service.ProjectAsync(user.Id, Snap("canceled", items: [Item("bank_sync")]));

    (await _context.Users.FindAsync(user.Id))!.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
  }

  // ── hosted Standard and Premium: unchanged ──

  [Fact]
  public async Task AHostedPremiumSubscription_ProjectsAsBefore_WithNoProductRows()
  {
    var user = await SeedUserAsync();

    await _service.ProjectAsync(user.Id, new StripeSubscriptionSnapshot(
      "sub_h", "active", null, "price_prem_m", false,
      [new StripeSnapshotItem("price_prem_m", null, null, 1, false, "month", 1499)], PeriodEnd, null));

    var after = await _context.Users.FindAsync(user.Id);
    after!.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
    (await RowsAsync(user.Id)).Should().BeEmpty();
    var row = await _context.UserSubscriptions.SingleAsync();
    row.Amount.Should().Be(0m, "the local amount of a hosted row is set by its own flow, not by projection");
    row.BillingCycle.Should().Be(SubscriptionBillingCycle.Monthly);
  }

  [Fact]
  public async Task AHostedSubscription_WithItemsNull_IsAlsoUnchanged()
  {
    var user = await SeedUserAsync();
    await _service.ProjectAsync(user.Id, new StripeSubscriptionSnapshot("sub_h", "active", null, "price_std_y", false));
    (await _context.Users.FindAsync(user.Id))!.SubscriptionTier.Should().Be(SubscriptionTier.Standard);
    (await RowsAsync(user.Id)).Should().BeEmpty();
  }

  [Fact]
  public async Task AHostedSubscription_NeverGainsProductRows_EvenIfOneAlreadyExistedForThatId()
  {
    var user = await SeedUserAsync();
    _context.UserEntitlements.Add(new UserEntitlement
    {
      UserId = user.Id, Entitlement = "ai", ExternalId = "sub_h", Provider = SubscriptionProvider.Stripe
    });
    await _context.SaveChangesAsync();

    await _service.ProjectAsync(user.Id, new StripeSubscriptionSnapshot(
      "sub_h", "active", null, "price_prem_m", false,
      [new StripeSnapshotItem("price_prem_m", null, null, 1, false, "month", 1499)], PeriodEnd, null));

    (await RowsAsync(user.Id)).Should().BeEmpty("a subscription that is not a Cloud plan holds no product rows");
  }

  [Fact]
  public async Task AnotherUsersProducts_AreNeverTouched()
  {
    var a = await SeedUserAsync("a@example.com");
    var b = await SeedUserAsync("b@example.com");
    await _service.ProjectAsync(a.Id, Snap("active", id: "sub_a", items: [Item("bank_sync"), Item("ai")]));

    await _service.ProjectAsync(b.Id, Snap("canceled", id: "sub_b", items: [Item("bank_sync")]));

    (await RowsAsync(a.Id)).Should().HaveCount(2);
    (await RowsAsync(b.Id)).Should().BeEmpty();
  }

  // ── pulling from Stripe ──

  [Fact]
  public async Task RefreshFromStripe_ProjectsTheLiveSubscriptionsItems()
  {
    var user = await SeedUserAsync();
    _stripe.Setup(s => s.GetLiveSubscriptionAsync("cus_1"))
      .ReturnsAsync(Snap("active", items: [Item("bank_sync"), Item("instant_refresh")]));

    (await _service.RefreshFromStripeAsync(user.Id, "test", TimeSpan.Zero)).Should().BeTrue();

    (await RowsAsync(user.Id)).Select(r => r.Entitlement).Should().Equal("bank_sync", "instant_refresh");
  }
}
