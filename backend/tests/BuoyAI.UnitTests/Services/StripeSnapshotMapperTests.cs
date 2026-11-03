using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// A Stripe subscription (as a webhook delivers it, with the price inline) becomes a snapshot that
/// names every item and what it grants. These payloads have the shape Stripe sends.
/// </summary>
public class StripeSnapshotMapperTests
{
  private static string Price(string id, string lookupKey, string? entitlement, bool founding, long cents, string interval) =>
    "{\"id\":\"" + id + "\",\"object\":\"price\",\"currency\":\"usd\",\"unit_amount\":" + cents +
    ",\"lookup_key\":" + (lookupKey == "" ? "null" : "\"" + lookupKey + "\"") +
    ",\"metadata\":{" + (entitlement == null ? "" : "\"buoy_entitlement\":\"" + entitlement + "\",") +
    "\"founding\":\"" + (founding ? "true" : "false") + "\"}" +
    ",\"recurring\":{\"interval\":\"" + interval + "\",\"interval_count\":1},\"type\":\"recurring\"}";

  private static string Item(string id, string price, long qty, long periodEnd) =>
    "{\"id\":\"" + id + "\",\"object\":\"subscription_item\",\"quantity\":" + qty +
    ",\"current_period_start\":1766464000,\"current_period_end\":" + periodEnd + ",\"price\":" + price + "}";

  private static Stripe.Subscription Sub(string status, string metadata, bool cancelAtPeriodEnd, params string[] items) =>
    Stripe.Subscription.FromJson(
      "{\"id\":\"sub_1\",\"object\":\"subscription\",\"status\":\"" + status + "\",\"customer\":\"cus_1\"" +
      ",\"cancel_at_period_end\":" + (cancelAtPeriodEnd ? "true" : "false") + ",\"metadata\":" + metadata +
      ",\"items\":{\"object\":\"list\",\"data\":[" + string.Join(",", items) + "],\"has_more\":false,\"url\":\"/x\"}}");

  [Fact]
  public void EveryItem_IsMapped_WithWhatItGrants()
  {
    var sub = Sub("active", "{}", false,
      Item("si_1", Price("price_a", "bank_sync_yearly_founding", "bank_sync", true, 2399, "year"), 1, 1798000000),
      Item("si_2", Price("price_b", "extra_logins_yearly_founding", "extra_logins", true, 600, "year"), 3, 1798000000));

    var snapshot = StripeSnapshotMapper.From(sub);

    snapshot.SubscriptionId.Should().Be("sub_1");
    snapshot.Status.Should().Be("active");
    snapshot.PriceId.Should().Be("price_a");
    snapshot.IsCatalog.Should().BeTrue();
    snapshot.Items!.Should().HaveCount(2);
    var logins = snapshot.Items!.Single(i => i.PriceId == "price_b");
    logins.Quantity.Should().Be(3);
    logins.Entitlements.Should().Be("extra_logins");
    logins.Founding.Should().BeTrue();
    logins.Interval.Should().Be("year");
    logins.UnitAmountCents.Should().Be(600);
    logins.LookupKey.Should().Be("extra_logins_yearly_founding");
  }

  [Fact]
  public void ThePeriodEnd_IsTheEarliestItemEnd()
  {
    var sub = Sub("active", "{}", false,
      Item("si_1", Price("price_a", "", "bank_sync", false, 1, "year"), 1, 1798000000),
      Item("si_2", Price("price_b", "", "ai", false, 1, "month"), 1, 1790000000));

    StripeSnapshotMapper.From(sub).CurrentPeriodEnd.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790000000).UtcDateTime);
  }

  [Fact]
  public void AHostedPlansPrice_HasNoEntitlements_SoItIsNotACatalogSubscription()
  {
    var sub = Sub("active", "{}", false, Item("si_1", Price("price_prem_m", "", null, false, 1499, "month"), 1, 1798000000));

    var snapshot = StripeSnapshotMapper.From(sub);

    snapshot.IsCatalog.Should().BeFalse();
    snapshot.Items.Should().ContainSingle().Which.Entitlements.Should().BeNull();
    snapshot.PriceId.Should().Be("price_prem_m");
  }

  [Fact]
  public void ABundlePrice_CarriesBothEntitlements()
  {
    var sub = Sub("active", "{}", false, Item("si_1", Price("price_b", "bank_sync_ai_bundle_yearly_list", "bank_sync,ai", false, 5499, "year"), 1, 1798000000));
    StripeSnapshotMapper.From(sub).Items!.Single().Entitlements.Should().Be("bank_sync,ai");
  }

  [Fact]
  public void SubscriptionMetadata_AndTheCancelFlag_ArePassedOn()
  {
    var sub = Sub("active", "{\"environment\":\"production\",\"pending_end_ai\":\"2027-01-01T00:00:00.0000000Z\"}", true,
      Item("si_1", Price("price_a", "", "ai", false, 1, "year"), 1, 1798000000));

    var snapshot = StripeSnapshotMapper.From(sub);

    snapshot.CancelAtPeriodEnd.Should().BeTrue();
    snapshot.Metadata!["environment"].Should().Be("production");
    snapshot.Metadata["pending_end_ai"].Should().Contain("2027");
  }

  [Fact]
  public void NoItems_IsAnEmptyList_NotNull()
  {
    var snapshot = StripeSnapshotMapper.From(Sub("active", "{}", false));
    snapshot.Items.Should().NotBeNull().And.BeEmpty();
    snapshot.PriceId.Should().BeNull();
    snapshot.CurrentPeriodEnd.Should().BeNull();
  }

  // The Cloud AI pay-as-you-go item: metered, one cent a unit, monthly even on a yearly plan, no entitlement of its own.
  private static string MeteredPrice() =>
    "{\"id\":\"price_overage\",\"object\":\"price\",\"currency\":\"usd\",\"unit_amount\":1,\"lookup_key\":\"ai_overage_monthly_list\"," +
    "\"metadata\":{\"buoy_product\":\"ai_overage\",\"founding\":\"false\"}," +
    "\"recurring\":{\"interval\":\"month\",\"interval_count\":1,\"usage_type\":\"metered\",\"meter\":\"mtr_1\"},\"type\":\"recurring\"}";

  [Fact]
  public void AMeteredPayAsYouGoItem_IsLeftOutOfTheSnapshot()
  {
    var sub = Sub("active", "{}", false,
      Item("si_m", MeteredPrice(), 0, 1768000000),
      Item("si_ai", Price("price_ai", "ai_yearly_founding", "ai", true, 1499, "year"), 1, 1798000000));

    var snapshot = StripeSnapshotMapper.From(sub);

    snapshot.Items!.Should().ContainSingle().Which.PriceId.Should().Be("price_ai");
    snapshot.IsCatalog.Should().BeTrue();
  }

  [Fact]
  public void AMeteredItem_DoesNotPullThePaidThroughDateAYearEarly_OrBecomeThePlansFirstPrice()
  {
    var sub = Sub("active", "{}", false,
      Item("si_m", MeteredPrice(), 0, 1768000000),
      Item("si_ai", Price("price_ai", "ai_yearly_founding", "ai", true, 1499, "year"), 1, 1798000000));

    var snapshot = StripeSnapshotMapper.From(sub);

    snapshot.CurrentPeriodEnd.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1798000000).UtcDateTime);
    snapshot.PriceId.Should().Be("price_ai");
  }

  [Fact]
  public void AMeteredItem_GrantsNothing_AndSoNeverLogsAsAnUnknownEntitlement()
  {
    var sub = Sub("active", "{}", false,
      Item("si_ai", Price("price_ai", "ai_yearly_founding", "ai", true, 1499, "year"), 1, 1798000000),
      Item("si_m", MeteredPrice(), 0, 1768000000));

    var rows = EntitlementProjector.Project(StripeSnapshotMapper.From(sub));

    rows.Should().NotBeNull();
    rows!.Select(r => r.Entitlement).Should().Equal("ai");
  }

  [Fact]
  public void FoundingIsReadFromThePrice_NotTheSubscription()
  {
    var sub = Sub("active", "{\"founding\":\"true\"}", false,
      Item("si_1", Price("price_a", "", "bank_sync", false, 1, "year"), 1, 1798000000));
    StripeSnapshotMapper.From(sub).Items!.Single().Founding.Should().BeFalse();
  }
}
