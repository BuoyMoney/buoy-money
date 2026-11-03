using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class EntitlementServiceTests
{
    [Theory]
    [MemberData(nameof(TierTable.Names), MemberType = typeof(TierTable))]
    public async Task GetEffectiveTierAsync_MatchesTheTable(string name)
    {
        var c = TierTable.Find(name);
        using var ctx = TierTable.Seed(c, DateTime.UtcNow);
        var entitlements = TestEntitlements.Create(ctx, TierTable.Config());

        (await entitlements.GetEffectiveTierAsync(1)).Should().Be(c.Expected);
        (await entitlements.HasPremiumAccessAsync(1)).Should().Be(c.Expected.HasPremiumAccess());
        (await entitlements.GetTierOrStandardAsync(1)).Should().Be(c.Expected ?? SubscriptionTier.Standard);
    }

    [Theory]
    [MemberData(nameof(TierTable.Names), MemberType = typeof(TierTable))]
    public async Task SameTable_GivesTheSameTierAsTheBillingServiceDelegate(string name)
    {
        var c = TierTable.Find(name);
        using var ctx = TierTable.Seed(c, DateTime.UtcNow);
        var sync = new Mock<ISubscriptionSyncService>();
        var entitlements = TestEntitlements.Create(ctx, TierTable.Config(), sync.Object);
        var billing = new BillingService(
            ctx, Mock.Of<IStripeService>(), sync.Object, entitlements, Mock.Of<IEmailService>(),
            Mock.Of<IFamilyService>(), TierTable.Config(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            NullLogger<BillingService>.Instance,
            new BuoyAI.Infrastructure.Telemetry.BuoyAiMetrics(new TestBillingMeterFactory()));

        (await billing.GetUserTierAsync(1)).Should().Be(await entitlements.GetEffectiveTierAsync(1));
        (await billing.GetUserTierAsync(1)).Should().Be(c.Expected);
    }

    [Theory]
    [MemberData(nameof(TierTable.FeatureTiers), MemberType = typeof(TierTable))]
    public async Task HasFeatureAsync_FollowsTheFeatureMap(string feature, string tierName)
    {
        var caseName = tierName switch
        {
            "Standard" => "active App Store Standard subscription",
            "Premium" => "active Stripe Premium subscription",
            "PremiumInherited" => "active trial keeps the tier on the row (PremiumInherited)",
            "Admin" => "admin allowlist",
            _ => "fresh user with nothing has no tier",
        };
        using var ctx = TierTable.Seed(TierTable.Find(caseName), DateTime.UtcNow);
        var entitlements = TestEntitlements.Create(ctx, TierTable.Config());

        var map = TierTable.Features.ToDictionary(f => f.Feature, f => f.Required);
        var expected = !map.TryGetValue(feature, out var required)
            ? true
            : TierTable.Find(caseName).Expected is SubscriptionTier t && t >= required;

        (await entitlements.HasFeatureAsync(1, feature)).Should().Be(expected);
    }

    [Fact]
    public void CanBeConstructedWithNoBillingConfigurationAtAll()
    {
        // No Stripe keys, no hooks, nothing but a database and configuration: this is the
        // dependency set a deployment without billing has to be able to build.
        using var ctx = TierTable.Seed(TierTable.Cases[0], DateTime.UtcNow);
        var act = () => new EntitlementService(
            ctx, new ConfigurationBuilder().Build(), Array.Empty<ITierRefreshHook>(),
            NullLogger<EntitlementService>.Instance);

        act.Should().NotThrow();
    }

    [Fact]
    public void ConstructorTakesNoStripeOrBillingService()
    {
        var parameters = typeof(EntitlementService).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType).ToArray();

        parameters.Should().NotContain(typeof(IStripeService));
        parameters.Should().NotContain(typeof(IBillingService));
        parameters.Should().NotContain(typeof(ISubscriptionSyncService));
    }

    [Fact]
    public async Task TheStripeHook_IsNotResolvedUntilADenialAsksForIt()
    {
        // StripeService throws at construction without keys, so the hook must reach it lazily.
        var services = new ServiceCollection()
            .AddScoped<ISubscriptionSyncService>(_ => throw new InvalidOperationException("no Stripe keys"))
            .BuildServiceProvider();
        var hook = new StripeTierRefreshHook(services, NullLogger<StripeTierRefreshHook>.Instance);

        using var granted = TierTable.Seed(TierTable.Find("active Stripe Premium subscription"), DateTime.UtcNow);
        var entitlements = new EntitlementService(
            granted, TierTable.Config(), new ITierRefreshHook[] { hook }, NullLogger<EntitlementService>.Instance);

        (await entitlements.GetEffectiveTierAsync(1)).Should().Be(SubscriptionTier.Premium);
    }

    [Fact]
    public async Task ADenial_WithAnUnbuildableStripeHook_StaysDenied_InsteadOfThrowing()
    {
        var services = new ServiceCollection()
            .AddScoped<ISubscriptionSyncService>(_ => throw new InvalidOperationException("no Stripe keys"))
            .BuildServiceProvider();
        var hook = new StripeTierRefreshHook(services, NullLogger<StripeTierRefreshHook>.Instance);

        using var ctx = TierTable.Seed(TierTable.Find("fresh user with nothing has no tier"), DateTime.UtcNow);
        var entitlements = new EntitlementService(
            ctx, TierTable.Config(), new ITierRefreshHook[] { hook }, NullLogger<EntitlementService>.Instance);

        (await entitlements.GetEffectiveTierAsync(1)).Should().BeNull();
        (await entitlements.HasPremiumAccessAsync(1)).Should().BeFalse();
    }

    [Fact]
    public async Task ADenial_AsksTheHookOnce()
    {
        using var ctx = TierTable.Seed(TierTable.Find("fresh user with nothing has no tier"), DateTime.UtcNow);
        var hook = new Mock<ITierRefreshHook>();
        hook.Setup(h => h.RefreshAsync(1, "tier-denial")).ReturnsAsync(false);
        var entitlements = new EntitlementService(
            ctx, TierTable.Config(), new[] { hook.Object }, NullLogger<EntitlementService>.Instance);

        (await entitlements.GetEffectiveTierAsync(1)).Should().BeNull();
        hook.Verify(h => h.RefreshAsync(1, "tier-denial"), Times.Once);
    }

    [Fact]
    public async Task AGrant_NeverAsksTheHook()
    {
        using var ctx = TierTable.Seed(TierTable.Find("admin allowlist"), DateTime.UtcNow);
        var hook = new Mock<ITierRefreshHook>();
        var entitlements = new EntitlementService(
            ctx, TierTable.Config(), new[] { hook.Object }, NullLogger<EntitlementService>.Instance);

        await entitlements.GetEffectiveTierAsync(1);

        hook.Verify(h => h.RefreshAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void HasPremiumAccess_ForAUserRow_NeedsNoQuery()
    {
        var entitlements = TestEntitlements.For(TierTable.Config());

        entitlements.HasPremiumAccess(new User { Email = TierTable.AdminEmail }).Should().BeTrue();
        entitlements.HasPremiumAccess(new User { Email = "x@example.com", SubscriptionTier = SubscriptionTier.PremiumInherited }).Should().BeTrue();
        entitlements.HasPremiumAccess(new User { Email = "x@example.com", SubscriptionTier = SubscriptionTier.Standard }).Should().BeFalse();
        entitlements.HasPremiumAccess(new User { Email = "x@example.com" }).Should().BeFalse();
    }

    [Theory]
    [InlineData(SubscriptionTier.Standard, 10, 50, 5.00, 0.25, 5)]
    [InlineData(SubscriptionTier.Premium, 50, 200, 15.00, 1.00, 15)]
    [InlineData(SubscriptionTier.PremiumInherited, 50, 200, 15.00, 1.00, 15)]
    [InlineData(SubscriptionTier.Admin, 1000, 5000, 100.00, 5.00, 50)]
    public void GetAiLimits_ReturnsTheEnforcedNumbersPerTier(
        SubscriptionTier tier, int hourly, int daily, double monthly, double dailyCost, int refreshes)
    {
        var limits = TestEntitlements.For(new ConfigurationBuilder().Build()).GetAiLimits(tier);

        limits.Should().Be(new AiTierLimits(hourly, daily, (decimal)monthly, (decimal)dailyCost, refreshes));
    }

    [Fact]
    public void GetAiLimits_HonoursTheConfiguredDailyRequestCounts()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gemini:RateLimiting:StandardUserRequestsPerDay"] = "3",
            ["Gemini:RateLimiting:PremiumUserRequestsPerDay"] = "7",
        }).Build();
        var entitlements = TestEntitlements.For(config);

        entitlements.GetAiLimits(SubscriptionTier.Standard).DailyRequests.Should().Be(3);
        entitlements.GetAiLimits(SubscriptionTier.PremiumInherited).DailyRequests.Should().Be(7);
        entitlements.GetAiLimits(SubscriptionTier.Admin).DailyRequests.Should().Be(5000);
    }

    [Theory]
    [InlineData("Premium", true)]
    [InlineData("PremiumInherited", true)]
    [InlineData("Admin", true)]
    [InlineData("Standard", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPremiumClaim_MatchesThePremiumClass(string? claim, bool expected)
    {
        SubscriptionTierExtensions.IsPremiumClaim(claim).Should().Be(expected);
    }
}
