using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// "What may this user use?" answered from the database. Hosted Standard and Premium keep working
/// (mapped to products), Cloud plans work from their projected rows, and the two never double up.
/// </summary>
public class ProductAccessTests
{
  private readonly BuoyAIDbContext _context = new(new DbContextOptionsBuilder<BuoyAIDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

  private EntitlementService Service(Dictionary<string, string?>? settings = null) =>
    TestEntitlements.Create(_context, new ConfigurationBuilder()
      .AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());

  private async Task<User> UserAsync(
    string email = "u@example.com", bool trialing = false, SubscriptionTier? tier = null, bool instanceAdmin = false)
  {
    var user = new User
    {
      Email = email, Username = email, PasswordHash = "x", FirstName = "U", LastName = "S",
      IsTrialActive = trialing, TrialStartDate = trialing ? DateTime.UtcNow.AddDays(-2) : null,
      TrialEndDate = trialing ? DateTime.UtcNow.AddDays(10) : null,
      SubscriptionTier = tier, IsInstanceAdmin = instanceAdmin
    };
    _context.Users.Add(user);
    await _context.SaveChangesAsync();
    return user;
  }

  private async Task LegacyPlanAsync(
    int userId, SubscriptionTier tier, SubscriptionProvider provider = SubscriptionProvider.Stripe,
    UserSubscriptionStatus status = UserSubscriptionStatus.Active, string? id = null)
  {
    _context.UserSubscriptions.Add(new UserSubscription
    {
      UserId = userId, Tier = tier, BillingCycle = SubscriptionBillingCycle.Monthly, Status = status,
      Provider = provider,
      StripeSubscriptionId = provider == SubscriptionProvider.Stripe ? id ?? "sub_legacy" : null,
      ExternalId = provider == SubscriptionProvider.Stripe ? null : id ?? "app_store:premium",
      StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow.AddDays(30),
      CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();
  }

  private async Task RowAsync(
    int userId, string entitlement, int quantity = 1, string externalId = "sub_cloud",
    string status = "active", DateTime? endsAt = null, bool founding = false)
  {
    _context.UserEntitlements.Add(new UserEntitlement
    {
      UserId = userId, Entitlement = entitlement, Quantity = quantity, ExternalId = externalId,
      Provider = SubscriptionProvider.Stripe, Status = status, EndsAt = endsAt, Founding = founding
    });
    await _context.SaveChangesAsync();
  }

  private async Task CloudPlanRowAsync(int userId, string id = "sub_cloud", UserSubscriptionStatus status = UserSubscriptionStatus.Active, SubscriptionTier tier = SubscriptionTier.Standard) =>
    await LegacyPlanAsync(userId, tier, SubscriptionProvider.Stripe, status, id);

  // ── hosted plans keep working ──

  [Theory]
  [InlineData(SubscriptionTier.Standard)]
  [InlineData(SubscriptionTier.Premium)]
  public async Task AHostedPlan_GetsBankSyncAndAi_AndNeverInstantRefresh(SubscriptionTier tier)
  {
    var user = await UserAsync();
    await LegacyPlanAsync(user.Id, tier);

    var access = await Service().GetProductAccessAsync(user.Id);

    access.BankSync.Should().BeTrue();
    access.Ai.Should().BeTrue();
    access.InstantRefresh.Should().BeFalse();
    access.ExtraLogins.Should().Be(0);
  }

  [Fact]
  public async Task AnActiveTrial_GetsTheTrialTiersProducts()
  {
    var user = await UserAsync(trialing: true);
    var access = await Service().GetProductAccessAsync(user.Id);
    access.BankSync.Should().BeTrue();
    access.Ai.Should().BeTrue();
  }

  [Fact]
  public async Task AnExpiredTrial_GetsNothing()
  {
    var user = await UserAsync(trialing: true);
    user.TrialEndDate = DateTime.UtcNow.AddDays(-1);
    await _context.SaveChangesAsync();
    (await Service().GetProductAccessAsync(user.Id)).IsEmpty.Should().BeTrue();
  }

  [Fact]
  public async Task AStorePlan_GetsProducts()
  {
    var user = await UserAsync();
    await LegacyPlanAsync(user.Id, SubscriptionTier.Premium, SubscriptionProvider.AppStore);
    (await Service().GetProductAccessAsync(user.Id)).BankSync.Should().BeTrue();
  }

  [Theory]
  [InlineData("ai_insights")]
  [InlineData("automatic_categorization")]
  [InlineData("ai_chat_bot")]
  public async Task ACloudCustomerWithOnlyBankSync_IsRefusedTheAiFeatures(string feature)
  {
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id, "sub_cloud", UserSubscriptionStatus.Active, SubscriptionTier.Standard);
    await RowAsync(user.Id, ProductEntitlements.BankSync);

    var service = Service();
    (await service.HasFeatureAsync(user.Id, feature)).Should().BeFalse();
    (await service.HasFeatureAsync(user.Id, "plaid_connections")).Should().BeTrue();
  }

  [Fact]
  public async Task NoPlanAtAll_GetsNothing()
  {
    var user = await UserAsync();
    (await Service().GetProductAccessAsync(user.Id)).Should().Be(ProductAccess.None);
  }

  [Fact]
  public async Task AnUnknownUser_GetsNothing_AndDoesNotThrow() =>
    (await Service().GetProductAccessAsync(12345)).Should().Be(ProductAccess.None);

  [Fact]
  public async Task AnOperator_GetsEverything()
  {
    var user = await UserAsync("boss@example.com");
    var access = await Service(new() { ["Admin:Emails:0"] = "boss@example.com" }).GetProductAccessAsync(user.Id);
    access.Should().BeSameAs(ProductAccess.All);
  }

  [Fact]
  public async Task AFamilyMemberInheritingPremium_GetsTheProducts()
  {
    var owner = await UserAsync("owner@example.com");
    await LegacyPlanAsync(owner.Id, SubscriptionTier.Premium);
    var member = await UserAsync("member@example.com", tier: SubscriptionTier.PremiumInherited);
    _context.UserSubscriptions.Add(new UserSubscription
    {
      UserId = member.Id, Tier = SubscriptionTier.PremiumInherited, BillingCycle = SubscriptionBillingCycle.Monthly,
      Status = UserSubscriptionStatus.Active, StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow.AddDays(30),
      CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();

    var access = await Service().GetProductAccessAsync(member.Id);
    access.BankSync.Should().BeTrue();
    access.Ai.Should().BeTrue();
  }

  [Fact]
  public async Task TheLegacyMap_CanBeChanged_ByConfiguration()
  {
    var user = await UserAsync();
    await LegacyPlanAsync(user.Id, SubscriptionTier.Premium);
    var access = await Service(new() { ["Entitlements:LegacyTierProducts:Premium"] = "bank_sync,ai,instant_refresh" })
      .GetProductAccessAsync(user.Id);
    access.InstantRefresh.Should().BeTrue();
  }

  // ── Cloud plans ──

  [Fact]
  public async Task ACloudPlan_GetsExactlyItsProducts_NotTheTierItImplies()
  {
    var user = await UserAsync(tier: SubscriptionTier.Standard);
    await CloudPlanRowAsync(user.Id);
    await RowAsync(user.Id, "bank_sync");

    var access = await Service().GetProductAccessAsync(user.Id);

    access.BankSync.Should().BeTrue();
    access.Ai.Should().BeFalse("the Standard tier a Bank-sync-only plan maps to must not also hand out AI");
    access.InstantRefresh.Should().BeFalse();
  }

  [Fact]
  public async Task ACloudPlan_WithEveryProduct_AndExtraLogins()
  {
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id, tier: SubscriptionTier.Premium);
    await RowAsync(user.Id, "bank_sync", founding: true);
    await RowAsync(user.Id, "ai", founding: true);
    await RowAsync(user.Id, "instant_refresh");
    await RowAsync(user.Id, "extra_logins", quantity: 4);

    var access = await Service().GetProductAccessAsync(user.Id);

    access.Should().BeEquivalentTo(new { BankSync = true, Ai = true, InstantRefresh = true, ExtraLogins = 4, Founding = true });
  }

  [Fact]
  public async Task InstantRefresh_WithoutBankSync_IsNotGranted_EvenIfARowExists()
  {
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id, tier: SubscriptionTier.Premium);
    await RowAsync(user.Id, "ai");
    await RowAsync(user.Id, "instant_refresh");
    await RowAsync(user.Id, "extra_logins", quantity: 3);

    var access = await Service().GetProductAccessAsync(user.Id);

    access.Ai.Should().BeTrue();
    access.InstantRefresh.Should().BeFalse();
    access.ExtraLogins.Should().Be(0);
  }

  [Fact]
  public async Task APastDuePlan_KeepsItsProducts()
  {
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id);
    await RowAsync(user.Id, "bank_sync", status: "past_due");
    (await Service().GetProductAccessAsync(user.Id)).BankSync.Should().BeTrue();
  }

  [Fact]
  public async Task AProductPastItsEndDate_IsGone_EvenBeforeTheWebhookArrives()
  {
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id, tier: SubscriptionTier.Premium);
    await RowAsync(user.Id, "bank_sync");
    await RowAsync(user.Id, "ai", endsAt: DateTime.UtcNow.AddMinutes(-5));

    var access = await Service().GetProductAccessAsync(user.Id);

    access.BankSync.Should().BeTrue();
    access.Ai.Should().BeFalse();
  }

  [Fact]
  public async Task AProductEndingLater_IsStillOn_AndReportsWhen()
  {
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id, tier: SubscriptionTier.Premium);
    await RowAsync(user.Id, "bank_sync");
    var end = DateTime.UtcNow.AddDays(20);
    await RowAsync(user.Id, "ai", endsAt: end);

    var access = await Service().GetProductAccessAsync(user.Id);

    access.Ai.Should().BeTrue();
    access.EndsAt["ai"].Should().Be(end);
    access.EndsAt.Should().NotContainKey("bank_sync");
  }

  [Fact]
  public async Task AnotherUsersRows_AreInvisible()
  {
    var a = await UserAsync("a@example.com");
    var b = await UserAsync("b@example.com");
    await CloudPlanRowAsync(a.Id);
    await RowAsync(a.Id, "bank_sync");

    (await Service().GetProductAccessAsync(b.Id)).IsEmpty.Should().BeTrue();
  }

  [Fact]
  public async Task ACloudPlansTrial_IsNotWidenedByTheTrialTier()
  {
    var user = await UserAsync(trialing: true, tier: SubscriptionTier.Standard);
    _context.UserSubscriptions.Add(new UserSubscription
    {
      UserId = user.Id, Tier = SubscriptionTier.Standard, BillingCycle = SubscriptionBillingCycle.Yearly,
      Status = UserSubscriptionStatus.Trialing, Provider = SubscriptionProvider.Stripe, StripeSubscriptionId = "sub_cloud",
      StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow.AddDays(10),
      CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();
    await RowAsync(user.Id, "bank_sync");

    var access = await Service().GetProductAccessAsync(user.Id);

    access.BankSync.Should().BeTrue();
    access.Ai.Should().BeFalse("a trial of Bank sync is a trial of Bank sync, not of the whole Premium trial");
  }

  [Fact]
  public async Task ALegacyTrial_PlusACloudPlan_BothCount()
  {
    var user = await UserAsync(trialing: true);   // a cardless trial with its own subscription
    await CloudPlanRowAsync(user.Id);
    await RowAsync(user.Id, "bank_sync");

    var access = await Service().GetProductAccessAsync(user.Id);

    access.BankSync.Should().BeTrue();
    access.Ai.Should().BeTrue("the trial that has not ended is still a trial");
  }

  [Fact]
  public async Task ACloudPlan_PlusAnAppStorePlan_Combine()
  {
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id, tier: SubscriptionTier.Premium);
    await RowAsync(user.Id, "ai");
    await LegacyPlanAsync(user.Id, SubscriptionTier.Standard, SubscriptionProvider.AppStore, id: "app_store:standard");

    var access = await Service(new() { ["Entitlements:LegacyTierProducts:Standard"] = "bank_sync" })
      .GetProductAccessAsync(user.Id);

    access.BankSync.Should().BeTrue("from the store plan");
    access.Ai.Should().BeTrue("from the Cloud plan");
  }

  [Fact]
  public async Task ACloudRefresh_NeedsBankSyncOnTheSameCloudPlan_NotOnAStorePlan()
  {
    // Conservative on purpose: a refresh costs money per call, and Cloud will not sell one without
    // bank sync on the same subscription, so a row like this only comes from bad data.
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id);
    await RowAsync(user.Id, "instant_refresh");
    await LegacyPlanAsync(user.Id, SubscriptionTier.Standard, SubscriptionProvider.AppStore, id: "app_store:standard");

    var access = await Service().GetProductAccessAsync(user.Id);

    access.BankSync.Should().BeTrue();
    access.InstantRefresh.Should().BeFalse();
  }

  [Fact]
  public async Task TheCloudPlansOwnRow_IsNotCountedAsALegacyPlan()
  {
    // Active UserSubscriptions row for the Cloud subscription itself, tier Premium (it has AI).
    var user = await UserAsync();
    await CloudPlanRowAsync(user.Id, tier: SubscriptionTier.Premium);
    await RowAsync(user.Id, "ai");

    var access = await Service().GetProductAccessAsync(user.Id);

    access.Ai.Should().BeTrue();
    access.BankSync.Should().BeFalse("an AI-only plan must not unlock bank sync through the Premium tier it maps to");
  }

  // ── the bank-connection gate ──

  [Fact]
  public async Task Plaid_IsAllowedForAHostedPlan()
  {
    var user = await UserAsync();
    await LegacyPlanAsync(user.Id, SubscriptionTier.Standard);
    (await Service().HasFeatureAsync(user.Id, "plaid_connections")).Should().BeTrue();
  }

  [Fact]
  public async Task Plaid_IsDeniedForAnAiOnlyCloudPlan_EvenThoughItsTierIsPremium()
  {
    var user = await UserAsync(tier: SubscriptionTier.Premium);
    await CloudPlanRowAsync(user.Id, tier: SubscriptionTier.Premium);
    await RowAsync(user.Id, "ai");

    var service = Service();
    (await service.GetEffectiveTierAsync(user.Id)).Should().Be(SubscriptionTier.Premium);
    (await service.HasFeatureAsync(user.Id, "plaid_connections")).Should().BeFalse();
    (await service.HasFeatureAsync(user.Id, "ai_chat_bot")).Should().BeTrue("the AI product still gets the AI features");
  }

  [Fact]
  public async Task Plaid_IsAllowedForABankSyncCloudPlan()
  {
    var user = await UserAsync(tier: SubscriptionTier.Standard);
    await CloudPlanRowAsync(user.Id);
    await RowAsync(user.Id, "bank_sync");
    (await Service().HasFeatureAsync(user.Id, "plaid_connections")).Should().BeTrue();
  }

  [Fact]
  public async Task Plaid_IsDeniedWithNoPlan()
  {
    var user = await UserAsync();
    (await Service().HasFeatureAsync(user.Id, "plaid_connections")).Should().BeFalse();
  }

  [Fact]
  public async Task Plaid_IsAllowedForAnOperator()
  {
    var user = await UserAsync("boss@example.com");
    (await Service(new() { ["Admin:Emails:0"] = "boss@example.com" }).HasFeatureAsync(user.Id, "plaid_connections")).Should().BeTrue();
  }

  [Fact]
  public async Task OtherFeatures_AreUnchanged_ByTheProducts()
  {
    var user = await UserAsync();
    await LegacyPlanAsync(user.Id, SubscriptionTier.Standard);
    var service = Service();
    (await service.HasFeatureAsync(user.Id, "budgeting_tools")).Should().BeTrue();
    (await service.HasFeatureAsync(user.Id, "ai_chat_bot")).Should().BeFalse();
    (await service.HasFeatureAsync(user.Id, "a_feature_nobody_gated")).Should().BeTrue();
  }

  // ── self-hosted ──

  [Fact]
  public async Task SelfHost_EveryRealAccountHasEveryProduct()
  {
    var user = await UserAsync();
    var selfHost = new SelfHostEntitlementService(_context, new ConfigurationBuilder().Build());
    (await selfHost.GetProductAccessAsync(user.Id)).Should().Be(ProductAccess.All);
    (await selfHost.GetProductAccessAsync(999)).Should().Be(ProductAccess.None);
  }
}
