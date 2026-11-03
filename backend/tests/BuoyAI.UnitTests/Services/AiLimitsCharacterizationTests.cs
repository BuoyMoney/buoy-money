using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Entitlements;
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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Pins the AI caps that are actually enforced per tier. These are the numbers the usage
/// screen has to show too; they used to live in four hand-typed tables.
/// </summary>
public class AiLimitsCharacterizationTests
{
    private static (AIUsageTracker Tracker, IMemoryCache Cache, BuoyAIDbContext Db) Tracker(
        Dictionary<string, string?>? config = null)
    {
        var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build();
        var tracker = new AIUsageTracker(NullLogger<AIUsageTracker>.Instance, cache, cfg, db,
            new BuoyAiMetrics(new TestMeterFactory()));
        return (tracker, cache, db);
    }

    private static string HourKey(int userId) => $"ai_usage_hourly_{userId}_{DateTime.UtcNow:yyyyMMddHH}";
    private static string DayKey(int userId) => $"ai_usage_daily_{userId}_{DateTime.UtcNow:yyyyMMdd}";

    // tier, hourly cap, daily cap
    public static IEnumerable<object[]> RateCaps() => new[]
    {
        new object[] { SubscriptionTier.Standard, 10, 50 },
        new object[] { SubscriptionTier.Premium, 50, 200 },
        new object[] { SubscriptionTier.PremiumInherited, 50, 200 },
        new object[] { SubscriptionTier.Admin, 1000, 5000 },
    };

    [Theory]
    [MemberData(nameof(RateCaps))]
    public async Task Requests_AreBlockedAtTheHourlyCap_AndAllowedJustUnderIt(SubscriptionTier tier, int hourly, int daily)
    {
        var (tracker, cache, _) = Tracker();
        cache.Set(HourKey(1), hourly - 1);
        (await tracker.CanMakeRequestAsync(1, tier)).Should().BeTrue();
        cache.Set(HourKey(1), hourly);
        (await tracker.CanMakeRequestAsync(1, tier)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(RateCaps))]
    public async Task Requests_AreBlockedAtTheDailyCap_AndAllowedJustUnderIt(SubscriptionTier tier, int hourly, int daily)
    {
        var (tracker, cache, _) = Tracker();
        cache.Set(DayKey(2), daily - 1);
        (await tracker.CanMakeRequestAsync(2, tier)).Should().BeTrue();
        cache.Set(DayKey(2), daily);
        (await tracker.CanMakeRequestAsync(2, tier)).Should().BeFalse();
    }

    [Fact]
    public async Task DailyRequestCaps_FollowConfiguration_ForStandardAndPremium()
    {
        var (tracker, cache, _) = Tracker(new()
        {
            ["Gemini:RateLimiting:StandardUserRequestsPerDay"] = "3",
            ["Gemini:RateLimiting:PremiumUserRequestsPerDay"] = "7",
        });

        cache.Set(DayKey(3), 3);
        (await tracker.CanMakeRequestAsync(3, SubscriptionTier.Standard)).Should().BeFalse();
        cache.Set(DayKey(4), 6);
        (await tracker.CanMakeRequestAsync(4, SubscriptionTier.Premium)).Should().BeTrue();
        cache.Set(DayKey(4), 7);
        (await tracker.CanMakeRequestAsync(4, SubscriptionTier.Premium)).Should().BeFalse();
        // The hourly caps and the admin cap are not configurable.
        cache.Set(DayKey(5), 4999);
        (await tracker.CanMakeRequestAsync(5, SubscriptionTier.Admin)).Should().BeTrue();
    }

    // tier, monthly dollars
    public static IEnumerable<object[]> MonthlyCaps() => new[]
    {
        new object[] { SubscriptionTier.Standard, 5.00m },
        new object[] { SubscriptionTier.Premium, 15.00m },
        new object[] { SubscriptionTier.PremiumInherited, 15.00m },
        new object[] { SubscriptionTier.Admin, 100.00m },
    };

    [Theory]
    [MemberData(nameof(MonthlyCaps))]
    public async Task MonthlySpend_BlocksAtTheCap_WarnsAt80Percent_AndIsQuietBelow(SubscriptionTier tier, decimal cap)
    {
        var (tracker, _, db) = Tracker();
        db.AIUsageRecords.Add(new AIUsageRecord
        {
            UserId = 10, Model = "m", EstimatedCost = cap, Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var atCap = await tracker.CheckMonthlyLimitAsync(10, tier);
        atCap.CanProceed.Should().BeFalse();
        atCap.WarningLevel.Should().Be("blocked");
        atCap.UsagePercent.Should().Be(100m);

        db.AIUsageRecords.RemoveRange(db.AIUsageRecords);
        db.AIUsageRecords.Add(new AIUsageRecord
        {
            UserId = 10, Model = "m", EstimatedCost = cap * 0.8m, Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var warn = await tracker.CheckMonthlyLimitAsync(10, tier);
        warn.CanProceed.Should().BeTrue();
        warn.WarningLevel.Should().Be("warning");

        db.AIUsageRecords.RemoveRange(db.AIUsageRecords);
        db.AIUsageRecords.Add(new AIUsageRecord
        {
            UserId = 10, Model = "m", EstimatedCost = cap * 0.5m, Timestamp = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var quiet = await tracker.CheckMonthlyLimitAsync(10, tier);
        quiet.CanProceed.Should().BeTrue();
        quiet.WarningLevel.Should().BeNull();
        quiet.UsagePercent.Should().Be(50m);
    }

    // tier, daily dollars
    public static IEnumerable<object[]> DailyCostCaps() => new[]
    {
        new object[] { SubscriptionTier.Standard, 0.25m },
        new object[] { SubscriptionTier.Premium, 1.00m },
        new object[] { SubscriptionTier.PremiumInherited, 1.00m },
        new object[] { SubscriptionTier.Admin, 5.00m },
    };

    [Theory]
    [MemberData(nameof(DailyCostCaps))]
    public async Task DailyCost_IsBlockedPastTheCap_AndAllowedWellUnderIt(SubscriptionTier tier, decimal cap)
    {
        var usage = new Mock<IAIUsageTracker>();
        var service = CostTracking.Build(usage.Object, tier);

        usage.Setup(u => u.GetDailyUsageAsync(1)).ReturnsAsync(new AIUsageStats { CostToday = cap });
        (await service.CanUserMakeRequestAsync(1, 1000)).Should().BeFalse();

        usage.Setup(u => u.GetDailyUsageAsync(1)).ReturnsAsync(new AIUsageStats { CostToday = cap - 0.05m });
        (await service.CanUserMakeRequestAsync(1, 1)).Should().BeTrue();
    }

    [Fact]
    public async Task DailyCost_NoTier_FallsBackToTheStandardCap()
    {
        var usage = new Mock<IAIUsageTracker>();
        var service = CostTracking.Build(usage.Object, null);

        usage.Setup(u => u.GetDailyUsageAsync(1)).ReturnsAsync(new AIUsageStats { CostToday = 0.25m });
        (await service.CanUserMakeRequestAsync(1, 1000)).Should().BeFalse();
        usage.Setup(u => u.GetDailyUsageAsync(1)).ReturnsAsync(new AIUsageStats { CostToday = 0.20m });
        (await service.CanUserMakeRequestAsync(1, 1)).Should().BeTrue();
    }
}

/// <summary>Builds CostTrackingService with a fixed tier for the user. One place to change.</summary>
internal static class CostTracking
{
    public static CostTrackingService Build(IAIUsageTracker usage, SubscriptionTier? tier)
    {
        var entitlements = new Mock<IEntitlementService>();
        entitlements.Setup(e => e.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(tier);
        entitlements.Setup(e => e.GetAiLimits(It.IsAny<SubscriptionTier>()))
            .Returns((SubscriptionTier t) => AiLimits.Default.For(t));
        return new CostTrackingService(
            NullLogger<CostTrackingService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            usage,
            entitlements.Object);
    }
}
