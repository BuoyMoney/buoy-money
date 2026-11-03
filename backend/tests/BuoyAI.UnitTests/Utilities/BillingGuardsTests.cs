using System.Collections.Generic;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuoyAI.UnitTests.Utilities;

public class BillingGuardsTests
{
  [Theory]
  [InlineData(SubscriptionTier.Standard, SubscriptionBillingCycle.Monthly)]
  [InlineData(SubscriptionTier.Standard, SubscriptionBillingCycle.Yearly)]
  [InlineData(SubscriptionTier.Premium, SubscriptionBillingCycle.Monthly)]
  [InlineData(SubscriptionTier.Premium, SubscriptionBillingCycle.Yearly)]
  public void ValidatePurchasablePlan_AllowsStandardAndPremium(SubscriptionTier tier, SubscriptionBillingCycle cycle)
  {
    var act = () => BillingHelpers.ValidatePurchasablePlan(tier, cycle);
    act.Should().NotThrow();
  }

  [Theory]
  [InlineData(SubscriptionTier.Admin)]
  [InlineData(SubscriptionTier.PremiumInherited)]
  [InlineData((SubscriptionTier)0)]
  public void ValidatePurchasablePlan_RejectsNonPurchasableTiers(SubscriptionTier tier)
  {
    var act = () => BillingHelpers.ValidatePurchasablePlan(tier, SubscriptionBillingCycle.Monthly);
    act.Should().Throw<System.InvalidOperationException>();
  }

  [Fact]
  public void ValidatePurchasablePlan_RejectsUnknownBillingCycle()
  {
    var act = () => BillingHelpers.ValidatePurchasablePlan(SubscriptionTier.Standard, (SubscriptionBillingCycle)5);
    act.Should().Throw<System.InvalidOperationException>();
  }

  [Fact]
  public void IsAdminEmail_MatchesAllowlistCaseInsensitively_AndRejectsOthers()
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Admin:Emails:0"] = "operator@example.com",
      })
      .Build();

    AdminPolicy.IsAdminEmail(config, "operator@example.com").Should().BeTrue();
    AdminPolicy.IsAdminEmail(config, "  OPERATOR@example.com ").Should().BeTrue();
    AdminPolicy.IsAdminEmail(config, "someone@else.com").Should().BeFalse();
    AdminPolicy.IsAdminEmail(config, null).Should().BeFalse();
  }

  [Fact]
  public void IsAdminEmail_NoAllowlistConfigured_ReturnsFalse()
  {
    var config = new ConfigurationBuilder().Build();
    AdminPolicy.IsAdminEmail(config, "operator@example.com").Should().BeFalse();
  }
}
