using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class ProductEntitlementNamesTests
{
  [Theory]
  [InlineData("bank_sync", new[] { "bank_sync" })]
  [InlineData("bank_sync,ai", new[] { "bank_sync", "ai" })]
  [InlineData(" Bank_Sync , AI ", new[] { "bank_sync", "ai" })]
  [InlineData("bank_sync,bank_sync", new[] { "bank_sync" })]
  [InlineData("extra_logins", new[] { "extra_logins" })]
  [InlineData("", new string[0])]
  [InlineData(null, new string[0])]
  [InlineData("admin", new string[0])]
  [InlineData("bank_sync,root,ai", new[] { "bank_sync", "ai" })]
  public void Parse_KeepsOnlyKnownNames_SoATypoGrantsNothing(string? csv, string[] expected) =>
    ProductEntitlements.Parse(csv).Should().Equal(expected);
}

public class ProductRulesTests
{
  private static readonly IReadOnlyDictionary<string, DateTime> NoEnds = new Dictionary<string, DateTime>();

  // ── instant refresh and extra logins need bank sync ──

  [Fact]
  public void InstantRefresh_WithoutBankSync_IsDropped()
  {
    var access = ProductRules.Normalize([ProductEntitlements.InstantRefresh, ProductEntitlements.Ai], 0);
    access.InstantRefresh.Should().BeFalse();
    access.Ai.Should().BeTrue();
    access.BankSync.Should().BeFalse();
  }

  [Fact]
  public void InstantRefresh_WithBankSync_Counts()
  {
    var access = ProductRules.Normalize([ProductEntitlements.BankSync, ProductEntitlements.InstantRefresh], 0);
    access.InstantRefresh.Should().BeTrue();
  }

  [Theory]
  [InlineData(false, 5, 0)]
  [InlineData(true, 5, 5)]
  [InlineData(true, 0, 0)]
  [InlineData(true, -2, 0)]
  public void ExtraLogins_CountOnlyWithBankSync(bool bank, int logins, int expected)
  {
    var flags = bank ? new[] { ProductEntitlements.BankSync } : new[] { ProductEntitlements.Ai };
    ProductRules.Normalize(flags, logins).ExtraLogins.Should().Be(expected);
  }

  [Fact]
  public void WithoutBankSync_TheEndDatesOfItsDependants_AreDropped()
  {
    var ends = new Dictionary<string, DateTime>
    {
      [ProductEntitlements.InstantRefresh] = DateTime.UtcNow.AddDays(1),
      [ProductEntitlements.Ai] = DateTime.UtcNow.AddDays(2)
    };
    var access = ProductRules.Normalize([ProductEntitlements.Ai], 0, ends);
    access.EndsAt.Keys.Should().BeEquivalentTo(ProductEntitlements.Ai);
  }

  [Fact]
  public void UnknownEndDateKeys_AreIgnored()
  {
    var ends = new Dictionary<string, DateTime> { ["root"] = DateTime.UtcNow };
    ProductRules.Normalize([ProductEntitlements.BankSync], 0, ends).EndsAt.Should().BeEmpty();
  }

  [Fact]
  public void NoProducts_IsEmpty()
  {
    ProductRules.Normalize([], 0).IsEmpty.Should().BeTrue();
    ProductAccess.None.IsEmpty.Should().BeTrue();
    ProductAccess.All.IsEmpty.Should().BeFalse();
  }

  [Theory]
  [InlineData("bank_sync", true)]
  [InlineData("ai", true)]
  [InlineData("instant_refresh", true)]
  [InlineData("extra_logins", false)]   // a quantity, not a flag
  [InlineData("nope", false)]
  public void Has_AnswersForFlags(string name, bool expected) =>
    ProductAccess.All.Has(name).Should().Be(expected);

  // ── the legacy tier derived from products ──

  [Theory]
  [InlineData(true, true, SubscriptionTier.Premium)]
  [InlineData(false, true, SubscriptionTier.Premium)]   // AI alone: the chat is a Premium feature
  [InlineData(true, false, SubscriptionTier.Standard)]
  public void LegacyTier_AnyAiIsPremium_BankSyncAloneIsStandard(bool bank, bool ai, SubscriptionTier expected)
  {
    var flags = new List<string>();
    if (bank) flags.Add(ProductEntitlements.BankSync);
    if (ai) flags.Add(ProductEntitlements.Ai);
    ProductRules.LegacyTierFor(ProductRules.Normalize(flags, 0)).Should().Be(expected);
  }

  [Fact]
  public void LegacyTier_OfNothing_IsNoTier() =>
    ProductRules.LegacyTierFor(ProductAccess.None).Should().BeNull();

  // ── hosted plans map to products ──

  [Theory]
  [InlineData(SubscriptionTier.Standard)]
  [InlineData(SubscriptionTier.Premium)]
  [InlineData(SubscriptionTier.PremiumInherited)]
  public void HostedPlans_GetBankSyncAndAi_ByDefault_ButNeverInstantRefresh(SubscriptionTier tier)
  {
    var access = ProductRules.ForLegacyTier(tier, _ => null);
    access.BankSync.Should().BeTrue();
    access.Ai.Should().BeTrue();
    access.InstantRefresh.Should().BeFalse();
    access.ExtraLogins.Should().Be(0);
  }

  [Fact]
  public void Admin_GetsEverything() =>
    ProductRules.ForLegacyTier(SubscriptionTier.Admin, _ => null).Should().Be(ProductAccess.All);

  [Fact]
  public void TheMapIsConfigurable_ForExampleRefreshForPremiumOnly()
  {
    string? Config(string key) => key switch
    {
      "Entitlements:LegacyTierProducts:Premium" => "bank_sync,ai,instant_refresh",
      "Entitlements:LegacyTierProducts:Standard" => "bank_sync",
      _ => null
    };

    ProductRules.ForLegacyTier(SubscriptionTier.Premium, Config).InstantRefresh.Should().BeTrue();
    ProductRules.ForLegacyTier(SubscriptionTier.PremiumInherited, Config).InstantRefresh.Should().BeTrue("inherited reads the Premium row");
    var standard = ProductRules.ForLegacyTier(SubscriptionTier.Standard, Config);
    standard.Ai.Should().BeFalse();
    standard.BankSync.Should().BeTrue();
  }

  [Fact]
  public void AConfiguredRefresh_StillNeedsBankSync()
  {
    string? Config(string key) => key == "Entitlements:LegacyTierProducts:Standard" ? "instant_refresh,ai" : null;
    var access = ProductRules.ForLegacyTier(SubscriptionTier.Standard, Config);
    access.InstantRefresh.Should().BeFalse();
    access.Ai.Should().BeTrue();
  }

  // ── union ──

  [Fact]
  public void Union_CombinesProducts_AndAddsExtraLogins()
  {
    var a = ProductRules.Normalize([ProductEntitlements.BankSync], 2);
    var b = ProductRules.Normalize([ProductEntitlements.Ai], 0);
    var u = ProductRules.Union(a, b);
    u.BankSync.Should().BeTrue();
    u.Ai.Should().BeTrue();
    u.ExtraLogins.Should().Be(2);
  }

  [Fact]
  public void Union_ReappliesTheRules_AfterCombining()
  {
    var refreshOnly = new ProductAccess(false, false, true, 0, NoEnds, false);
    var aiOnly = ProductRules.Normalize([ProductEntitlements.Ai], 0);
    var bankOnly = ProductRules.Normalize([ProductEntitlements.BankSync], 0);

    ProductRules.Union(refreshOnly, aiOnly).InstantRefresh.Should().BeFalse("no bank sync on either side");
    ProductRules.Union(refreshOnly, bankOnly).InstantRefresh.Should().BeTrue("bank sync from the other source satisfies it");
  }

  [Fact]
  public void Union_AProductEitherSideKeepsOpen_HasNoEnd()
  {
    var ends = new Dictionary<string, DateTime> { [ProductEntitlements.Ai] = DateTime.UtcNow.AddDays(5) };
    var ending = ProductRules.Normalize([ProductEntitlements.Ai], 0, ends);
    var open = ProductRules.Normalize([ProductEntitlements.Ai], 0);
    ProductRules.Union(ending, open).EndsAt.Should().BeEmpty();
    ProductRules.Union(open, ending).EndsAt.Should().BeEmpty();
  }

  [Fact]
  public void Union_WhenBothEnd_TheLaterDateWins()
  {
    var early = DateTime.UtcNow.AddDays(2);
    var late = DateTime.UtcNow.AddDays(9);
    var a = ProductRules.Normalize([ProductEntitlements.Ai], 0, new Dictionary<string, DateTime> { [ProductEntitlements.Ai] = early });
    var b = ProductRules.Normalize([ProductEntitlements.Ai], 0, new Dictionary<string, DateTime> { [ProductEntitlements.Ai] = late });
    ProductRules.Union(a, b).EndsAt[ProductEntitlements.Ai].Should().Be(late);
  }

  [Fact]
  public void Union_Founding_IfEitherIs()
  {
    var founding = ProductRules.Normalize([ProductEntitlements.Ai], 0, null, founding: true);
    var plain = ProductRules.Normalize([ProductEntitlements.BankSync], 0);
    ProductRules.Union(plain, founding).Founding.Should().BeTrue();
  }
}

public class EntitlementProjectorTests
{
  private static readonly DateTime PeriodEnd = DateTime.UtcNow.AddDays(120);

  private static StripeSnapshotItem Item(string entitlements, long qty = 1, bool founding = false, string interval = "year") =>
    new("price_" + entitlements, entitlements + "_" + interval, entitlements, qty, founding, interval, 1000);

  private static StripeSubscriptionSnapshot Snap(
    string status, bool cancelAtPeriodEnd = false, Dictionary<string, string>? metadata = null, params StripeSnapshotItem[] items) =>
    new("sub_1", status, null, items.FirstOrDefault()?.PriceId, cancelAtPeriodEnd, items, PeriodEnd, metadata);

  private static string[] Names(IReadOnlyList<ProjectedEntitlement>? rows) =>
    rows!.Select(r => r.Entitlement).OrderBy(x => x, StringComparer.Ordinal).ToArray();

  // ── status decides whether the products are granted ──

  [Theory]
  [InlineData("active", "active")]
  [InlineData("trialing", "active")]
  [InlineData("past_due", "past_due")]
  [InlineData("unpaid", "past_due")]
  public void LiveStatuses_GrantTheProducts_PastDueKeepsAccess(string status, string rowStatus)
  {
    var rows = EntitlementProjector.Project(Snap(status, items: [Item("bank_sync"), Item("ai")]))!;
    Names(rows).Should().Equal("ai", "bank_sync");
    rows.Should().OnlyContain(r => r.Status == rowStatus);
    rows.Should().OnlyContain(r => r.PaidThrough == PeriodEnd);
  }

  [Theory]
  [InlineData("paused")]
  [InlineData("canceled")]
  [InlineData("incomplete_expired")]
  public void EndedStatuses_GrantNothing(string status) =>
    EntitlementProjector.Project(Snap(status, items: [Item("bank_sync")]))!.Should().BeEmpty();

  [Theory]
  [InlineData("incomplete")]
  [InlineData("something_stripe_invents_next_year")]
  [InlineData("")]
  public void PendingOrUnknownStatuses_LeaveThingsAsTheyAre(string status) =>
    EntitlementProjector.Project(Snap(status, items: [Item("bank_sync")])).Should().BeNull();

  // ── what the items grant ──

  [Fact]
  public void ABundleItem_GrantsEveryProductItNames() =>
    Names(EntitlementProjector.Project(Snap("active", items: [Item("bank_sync,ai")]))).Should().Equal("ai", "bank_sync");

  [Fact]
  public void ExtraLogins_AreARowWithAQuantity()
  {
    var rows = EntitlementProjector.Project(Snap("active", items: [Item("bank_sync"), Item("extra_logins", qty: 4)]))!;
    rows.Single(r => r.Entitlement == "extra_logins").Quantity.Should().Be(4);
    rows.Single(r => r.Entitlement == "bank_sync").Quantity.Should().Be(1);
  }

  [Fact]
  public void ExtraLogins_WithoutBankSync_AreIgnored()
  {
    var rows = EntitlementProjector.Project(Snap("active", items: [Item("ai"), Item("extra_logins", qty: 2)]))!;
    Names(rows).Should().Equal("ai");
  }

  [Fact]
  public void InstantRefresh_WithoutBankSync_IsIgnored_NotGranted()
  {
    var rows = EntitlementProjector.Project(Snap("active", items: [Item("ai"), Item("instant_refresh")]))!;
    Names(rows).Should().Equal("ai");
  }

  [Fact]
  public void InstantRefresh_WithBankSync_IsGranted() =>
    Names(EntitlementProjector.Project(Snap("active", items: [Item("bank_sync"), Item("instant_refresh")]))).Should().Equal("bank_sync", "instant_refresh");

  [Fact]
  public void AnItemWithNoKnownEntitlement_GrantsNothing()
  {
    var rows = EntitlementProjector.Project(Snap("active", items: [Item("bank_sync"), Item("gold_tier"), Item("")]))!;
    Names(rows).Should().Equal("bank_sync");
  }

  [Fact]
  public void TwoItemsForTheSameProduct_AreOneRow() =>
    Names(EntitlementProjector.Project(Snap("active", items: [Item("ai"), Item("ai")]))).Should().Equal("ai");

  [Fact]
  public void TwoExtraLoginItems_AddTheirQuantities()
  {
    var rows = EntitlementProjector.Project(Snap("active", items: [Item("bank_sync"), Item("extra_logins", 2), Item("extra_logins", 3)]))!;
    rows.Single(r => r.Entitlement == "extra_logins").Quantity.Should().Be(5);
  }

  [Fact]
  public void AbsurdQuantities_AreClamped()
  {
    var rows = EntitlementProjector.Project(Snap("active", items: [Item("bank_sync"), Item("extra_logins", long.MaxValue)]))!;
    rows.Single(r => r.Entitlement == "extra_logins").Quantity.Should().Be(10_000);
  }

  [Fact]
  public void NoItemsAtAll_GrantsNothing() =>
    EntitlementProjector.Project(Snap("active"))!.Should().BeEmpty();

  [Fact]
  public void Founding_IsPerProduct()
  {
    var rows = EntitlementProjector.Project(Snap("active", items: [Item("bank_sync", founding: true), Item("ai", founding: false)]))!;
    rows.Single(r => r.Entitlement == "bank_sync").Founding.Should().BeTrue();
    rows.Single(r => r.Entitlement == "ai").Founding.Should().BeFalse();
  }

  // ── scheduled endings ──

  [Fact]
  public void ARemovalScheduledBySchedule_SetsThatProductsEndDate()
  {
    var end = DateTime.UtcNow.AddDays(30);
    var metadata = new Dictionary<string, string> { ["pending_end_ai"] = end.ToString("o") };
    var rows = EntitlementProjector.Project(Snap("active", metadata: metadata, items: [Item("bank_sync"), Item("ai")]))!;

    rows.Single(r => r.Entitlement == "ai").EndsAt.Should().BeCloseTo(end, TimeSpan.FromSeconds(1));
    rows.Single(r => r.Entitlement == "bank_sync").EndsAt.Should().BeNull();
  }

  [Fact]
  public void RemovingBankSync_EndsItsDependantsOnTheSameDay()
  {
    var end = DateTime.UtcNow.AddDays(30);
    var metadata = new Dictionary<string, string> { ["pending_end_bank_sync"] = end.ToString("o") };
    var rows = EntitlementProjector.Project(Snap("active", metadata: metadata,
      items: [Item("bank_sync"), Item("instant_refresh"), Item("extra_logins", 2), Item("ai")]))!;

    rows.Where(r => r.Entitlement != "ai").Should().OnlyContain(r => r.EndsAt != null);
    rows.Single(r => r.Entitlement == "ai").EndsAt.Should().BeNull();
  }

  [Fact]
  public void APastEndDate_IsALeftover_AndIgnored()
  {
    // The removal happened and the key was never cleared; the product was bought again since.
    var metadata = new Dictionary<string, string> { ["pending_end_ai"] = DateTime.UtcNow.AddDays(-40).ToString("o") };
    var rows = EntitlementProjector.Project(Snap("active", metadata: metadata, items: [Item("ai")]))!;
    rows.Single().EndsAt.Should().BeNull();
  }

  [Theory]
  [InlineData("not a date")]
  [InlineData("")]
  public void ABrokenEndDate_IsIgnored(string value)
  {
    var metadata = new Dictionary<string, string> { ["pending_end_ai"] = value };
    EntitlementProjector.Project(Snap("active", metadata: metadata, items: [Item("ai")]))!.Single().EndsAt.Should().BeNull();
  }

  [Fact]
  public void AnEndDateForAnUnknownProduct_IsIgnored()
  {
    var metadata = new Dictionary<string, string> { ["pending_end_admin"] = DateTime.UtcNow.AddDays(3).ToString("o") };
    EntitlementProjector.Project(Snap("active", metadata: metadata, items: [Item("ai")]))!.Single().EndsAt.Should().BeNull();
  }

  [Fact]
  public void CancelAtPeriodEnd_EndsEverythingAtThePeriodEnd()
  {
    var rows = EntitlementProjector.Project(Snap("active", cancelAtPeriodEnd: true, items: [Item("bank_sync"), Item("ai")]))!;
    rows.Should().OnlyContain(r => r.EndsAt == PeriodEnd);
  }

  [Fact]
  public void AnEarlierScheduledEnd_WinsOverTheCancelDate()
  {
    var earlier = DateTime.UtcNow.AddDays(10);
    var metadata = new Dictionary<string, string> { ["pending_end_ai"] = earlier.ToString("o") };
    var rows = EntitlementProjector.Project(Snap("active", cancelAtPeriodEnd: true, metadata: metadata, items: [Item("ai")]))!;
    rows.Single().EndsAt.Should().BeCloseTo(earlier, TimeSpan.FromSeconds(1));
  }

  // ── what the rows add up to ──

  [Fact]
  public void ToAccess_AppliesTheDependencyRules()
  {
    var rows = EntitlementProjector.Project(Snap("active", items: [Item("bank_sync"), Item("instant_refresh"), Item("extra_logins", 3), Item("ai")]))!;
    var access = EntitlementProjector.ToAccess(rows);
    access.Should().BeEquivalentTo(new { BankSync = true, Ai = true, InstantRefresh = true, ExtraLogins = 3 });
  }
}
