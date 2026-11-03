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
/// One table of user states and the effective tier each must resolve to. It pins what
/// <c>BillingService.GetUserTierAsync</c> answered before the entitlement service existed,
/// and <see cref="EntitlementServiceTests"/> runs the same table against the new service so
/// the two cannot drift.
/// </summary>
public static class TierTable
{
    public const string AdminEmail = "operator@example.com";

    public sealed record Case(string Name, Func<DateTime, (User User, UserSubscription[] Subs)> Build, SubscriptionTier? Expected);

    private static User U(string email = "person@example.com") => new()
    {
        Id = 1, Username = "person", Email = email, PasswordHash = "hash"
    };

    private static UserSubscription S(
        UserSubscriptionStatus status,
        SubscriptionTier tier,
        SubscriptionProvider provider = SubscriptionProvider.Stripe,
        DateTime? createdAt = null) => new()
    {
        UserId = 1, Tier = tier, Status = status, Provider = provider,
        BillingCycle = SubscriptionBillingCycle.Monthly, Amount = 10m,
        StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow.AddMonths(1),
        CreatedAt = createdAt ?? DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static User Trial(DateTime now, SubscriptionTier? tier, int daysLeft = 10)
    {
        var u = U();
        u.IsTrialActive = true;
        u.TrialStartDate = now.AddDays(-20);
        u.TrialEndDate = now.AddDays(daysLeft);
        u.SubscriptionTier = tier;
        return u;
    }

    public static readonly Case[] Cases =
    {
        new("admin allowlist", _ => (U(AdminEmail), Array.Empty<UserSubscription>()), SubscriptionTier.Admin),
        new("admin allowlist ignores case and padding", _ => (U("  Operator@Example.com "), Array.Empty<UserSubscription>()), SubscriptionTier.Admin),
        new("admin beats an active standard subscription", _ => (U(AdminEmail), new[] { S(UserSubscriptionStatus.Active, SubscriptionTier.Standard) }), SubscriptionTier.Admin),
        new("active trial with no tier on the row is Premium", n => (Trial(n, null), Array.Empty<UserSubscription>()), SubscriptionTier.Premium),
        new("active trial keeps the tier on the row (Standard)", n => (Trial(n, SubscriptionTier.Standard), Array.Empty<UserSubscription>()), SubscriptionTier.Standard),
        new("active trial keeps the tier on the row (PremiumInherited)", n => (Trial(n, SubscriptionTier.PremiumInherited), Array.Empty<UserSubscription>()), SubscriptionTier.PremiumInherited),
        new("active trial wins over an active subscription", n => (Trial(n, SubscriptionTier.Standard), new[] { S(UserSubscriptionStatus.Active, SubscriptionTier.Premium) }), SubscriptionTier.Standard),
        new("expired trial and nothing else has no tier", n => { var u = Trial(n, SubscriptionTier.Premium); u.TrialEndDate = n.AddDays(-1); return (u, Array.Empty<UserSubscription>()); }, null),
        new("trial flag set but no end date has no tier", n => { var u = Trial(n, SubscriptionTier.Premium); u.TrialEndDate = null; return (u, Array.Empty<UserSubscription>()); }, null),
        new("trial flag off with future end date has no tier", n => { var u = Trial(n, SubscriptionTier.Premium); u.IsTrialActive = false; return (u, Array.Empty<UserSubscription>()); }, null),
        new("expired trial plus active subscription uses the subscription", n => { var u = Trial(n, SubscriptionTier.Premium); u.TrialEndDate = n.AddDays(-1); return (u, new[] { S(UserSubscriptionStatus.Active, SubscriptionTier.Standard) }); }, SubscriptionTier.Standard),
        new("active Stripe Premium subscription", _ => (U(), new[] { S(UserSubscriptionStatus.Active, SubscriptionTier.Premium) }), SubscriptionTier.Premium),
        new("active App Store Standard subscription", _ => (U(), new[] { S(UserSubscriptionStatus.Active, SubscriptionTier.Standard, SubscriptionProvider.AppStore) }), SubscriptionTier.Standard),
        new("active Play Store Premium subscription", _ => (U(), new[] { S(UserSubscriptionStatus.Active, SubscriptionTier.Premium, SubscriptionProvider.PlayStore) }), SubscriptionTier.Premium),
        new("past due subscription has no tier", _ => (U(), new[] { S(UserSubscriptionStatus.PastDue, SubscriptionTier.Premium) }), null),
        new("canceled subscription has no tier", _ => (U(), new[] { S(UserSubscriptionStatus.Canceled, SubscriptionTier.Premium) }), null),
        new("expired subscription has no tier", _ => (U(), new[] { S(UserSubscriptionStatus.Expired, SubscriptionTier.Premium) }), null),
        new("trialing subscription row has no tier", _ => (U(), new[] { S(UserSubscriptionStatus.Trialing, SubscriptionTier.Premium) }), null),
        new("trial-expired subscription row has no tier", _ => (U(), new[] { S(UserSubscriptionStatus.TrialExpired, SubscriptionTier.Premium) }), null),
        new("newest active subscription wins", n => (U(), new[]
        {
            S(UserSubscriptionStatus.Active, SubscriptionTier.Standard, createdAt: n.AddDays(-30)),
            S(UserSubscriptionStatus.Active, SubscriptionTier.Premium, createdAt: n.AddDays(-1)),
        }), SubscriptionTier.Premium),
        new("inherited tier on the row with no trial and no subscription has no tier", _ => { var u = U(); u.SubscriptionTier = SubscriptionTier.PremiumInherited; return (u, Array.Empty<UserSubscription>()); }, null),
        new("premium tier on the row alone does not grant a tier", _ => { var u = U(); u.SubscriptionTier = SubscriptionTier.Premium; return (u, Array.Empty<UserSubscription>()); }, null),
        new("fresh user with nothing has no tier", _ => (U(), Array.Empty<UserSubscription>()), null),
    };

    public static IEnumerable<object[]> Names() => Cases.Select(c => new object[] { c.Name });

    public static Case Find(string name) => Cases.Single(c => c.Name == name);

    public static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:Emails:0"] = AdminEmail })
        .Build();

    public static BuoyAIDbContext Seed(Case c, DateTime now)
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var ctx = new BuoyAIDbContext(options);
        var (user, subs) = c.Build(now);
        ctx.Users.Add(user);
        ctx.UserSubscriptions.AddRange(subs);
        ctx.SaveChanges();
        return ctx;
    }

    /// <summary>The features BillingService maps to a minimum tier, with that tier.</summary>
    public static readonly (string Feature, SubscriptionTier Required)[] Features =
    {
        ("plaid_connections", SubscriptionTier.Standard),
        ("ai_insights", SubscriptionTier.Standard),
        ("automatic_categorization", SubscriptionTier.Standard),
        ("budgeting_tools", SubscriptionTier.Standard),
        ("subscription_tracking", SubscriptionTier.Standard),
        ("ai_chat_bot", SubscriptionTier.Premium),
        ("enhanced_ai_insights", SubscriptionTier.Premium),
        ("priority_support", SubscriptionTier.Premium),
        ("advanced_analytics", SubscriptionTier.Premium),
        ("admin_dashboard", SubscriptionTier.Admin),
    };

    public static IEnumerable<object[]> FeatureTiers()
    {
        foreach (var (feature, _) in Features)
            foreach (var tier in new SubscriptionTier?[] { null, SubscriptionTier.Standard, SubscriptionTier.Premium, SubscriptionTier.PremiumInherited, SubscriptionTier.Admin })
                yield return new object[] { feature, tier is null ? "none" : tier.Value.ToString() };
        yield return new object[] { "not_a_real_feature", "none" };
        yield return new object[] { "not_a_real_feature", "Standard" };
    }
}

public class BillingServiceTierCharacterizationTests
{
    private static BillingService Build(BuoyAIDbContext ctx, Mock<ISubscriptionSyncService>? sync = null)
    {
        sync ??= new Mock<ISubscriptionSyncService>();
        sync.Setup(x => x.RefreshFromStripeAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return new BillingService(
            ctx,
            Mock.Of<IStripeService>(),
            sync.Object,
            TestEntitlements.Create(ctx, TierTable.Config(), sync.Object),
            Mock.Of<IEmailService>(),
            Mock.Of<IFamilyService>(),
            TierTable.Config(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<BillingService>.Instance,
            new BuoyAiMetrics(new TestBillingMeterFactory()));
    }

    [Theory]
    [MemberData(nameof(TierTable.Names), MemberType = typeof(TierTable))]
    public async Task GetUserTierAsync_MatchesTheTable(string name)
    {
        var c = TierTable.Find(name);
        using var ctx = TierTable.Seed(c, DateTime.UtcNow);
        var billing = Build(ctx);

        (await billing.GetUserTierAsync(1)).Should().Be(c.Expected);
    }

    [Fact]
    public async Task GetUserTierAsync_UnknownUser_IsNull()
    {
        using var ctx = TierTable.Seed(TierTable.Cases[0], DateTime.UtcNow);
        (await Build(ctx).GetUserTierAsync(999)).Should().BeNull();
    }

    [Fact]
    public async Task GetUserTierAsync_AskedAboutADenial_RefreshesOnceAndReadsAgain()
    {
        using var ctx = TierTable.Seed(TierTable.Find("fresh user with nothing has no tier"), DateTime.UtcNow);
        var sync = new Mock<ISubscriptionSyncService>();
        var billing = Build(ctx, sync);
        sync.Reset();
        sync.Setup(x => x.RefreshFromStripeAsync(1, "tier-denial", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                ctx.UserSubscriptions.Add(new UserSubscription
                {
                    UserId = 1, Tier = SubscriptionTier.Premium, Status = UserSubscriptionStatus.Active,
                    BillingCycle = SubscriptionBillingCycle.Monthly, StartDate = DateTime.UtcNow,
                    NextBillingDate = DateTime.UtcNow.AddMonths(1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
                ctx.SaveChanges();
            })
            .ReturnsAsync(true);

        (await billing.GetUserTierAsync(1)).Should().Be(SubscriptionTier.Premium);
        sync.Verify(x => x.RefreshFromStripeAsync(1, "tier-denial", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUserTierAsync_AGrantNeverTriggersARefresh()
    {
        using var ctx = TierTable.Seed(TierTable.Find("active Stripe Premium subscription"), DateTime.UtcNow);
        var sync = new Mock<ISubscriptionSyncService>();
        var billing = Build(ctx, sync);

        await billing.GetUserTierAsync(1);

        sync.Verify(x => x.RefreshFromStripeAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never,
            "a grant must never put Stripe in the path");
    }

    [Theory]
    [MemberData(nameof(TierTable.FeatureTiers), MemberType = typeof(TierTable))]
    public async Task CanAccessFeatureAsync_FollowsTheFeatureMap(string feature, string tierName)
    {
        // Seed a user whose effective tier is the one under test.
        var caseName = tierName switch
        {
            "Standard" => "active App Store Standard subscription",
            "Premium" => "active Stripe Premium subscription",
            "PremiumInherited" => "active trial keeps the tier on the row (PremiumInherited)",
            "Admin" => "admin allowlist",
            _ => "fresh user with nothing has no tier",
        };
        using var ctx = TierTable.Seed(TierTable.Find(caseName), DateTime.UtcNow);
        var billing = Build(ctx);

        var map = TierTable.Features.ToDictionary(f => f.Feature, f => f.Required);
        var expected = !map.TryGetValue(feature, out var required)
            ? true
            : TierTable.Find(caseName).Expected is SubscriptionTier t && t >= required;

        (await billing.CanAccessFeatureAsync(1, feature)).Should().Be(expected);
    }
}
