using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Family members on PremiumInherited get Premium through the family owner. The AI gates and
/// every tier-keyed limit table used to list only Standard/Premium/Admin, so a family member was
/// refused chat and Connect, and would have thrown inside AIUsageTracker (which fails closed).
/// </summary>
public class PremiumInheritedAccessTests
{
  [Theory]
  [InlineData(SubscriptionTier.Premium, true)]
  [InlineData(SubscriptionTier.PremiumInherited, true)]
  [InlineData(SubscriptionTier.Admin, true)]
  [InlineData(SubscriptionTier.Standard, false)]
  public void HasPremiumAccess_TreatsInheritedAsPremium(SubscriptionTier tier, bool expected)
  {
    tier.HasPremiumAccess().Should().Be(expected);
    ((SubscriptionTier?)tier).HasPremiumAccess().Should().Be(expected);
  }

  [Fact]
  public void HasPremiumAccess_NoPlanIsFalse()
  {
    ((SubscriptionTier?)null).HasPremiumAccess().Should().BeFalse();
  }

  [Theory]
  [InlineData(SubscriptionTier.PremiumInherited, SubscriptionTier.Premium)]
  [InlineData(SubscriptionTier.Premium, SubscriptionTier.Premium)]
  [InlineData(SubscriptionTier.Standard, SubscriptionTier.Standard)]
  [InlineData(SubscriptionTier.Admin, SubscriptionTier.Admin)]
  public void ForLimits_MapsInheritedToPremium(SubscriptionTier tier, SubscriptionTier expected)
  {
    tier.ForLimits().Should().Be(expected);
  }

  private static AIUsageTracker Tracker()
  {
    var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
    return new AIUsageTracker(NullLogger<AIUsageTracker>.Instance, new MemoryCache(new MemoryCacheOptions()),
      config, db, new BuoyAiMetrics(new TestMeterFactory()));
  }

  [Fact]
  public async Task UsageTracker_AllowsAFamilyMemberWithNoUsage()
  {
    (await Tracker().CanMakeRequestAsync(42, SubscriptionTier.PremiumInherited)).Should().BeTrue();
  }

  [Fact]
  public async Task UsageTracker_MonthlyCheckWorksForAFamilyMember()
  {
    var (canProceed, _, _) = await Tracker().CheckMonthlyLimitAsync(42, SubscriptionTier.PremiumInherited);
    canProceed.Should().BeTrue();
  }
}
