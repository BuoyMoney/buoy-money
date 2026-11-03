using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.Ai;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>One price table, and no model is ever billed at some other model's rate.</summary>
public class AiPricingTests
{
    [Fact]
    public void KnownModel_UsesItsOwnRow()
    {
        // gemini-3.5-flash-lite: $0.30 in, $2.50 out per million
        AiPricing.Cost("gemini-3.5-flash-lite", 1_000_000, 0).Should().Be(0.30m);
        AiPricing.Cost("gemini-3.5-flash-lite", 0, 1_000_000).Should().Be(2.50m);
    }

    [Fact]
    public void DatedOrPreviewVariant_PricesAsItsBaseModel()
    {
        AiPricing.Cost("gemini-2.5-flash-preview-09-2025", 1_000_000, 0).Should().Be(AiPricing.Cost("gemini-2.5-flash", 1_000_000, 0));
        AiPricing.Cost("models/gemini-2.5-pro", 1_000_000, 0).Should().Be(1.25m);
    }

    [Fact]
    public void LiteIsItsOwnModel_NotAVariantOfFlash()
    {
        // "-lite" is not a dated or preview suffix, so a lite model without a row is unknown, not priced as plain flash.
        AiPricing.IsKnown("gemini-2.5-flash-lite").Should().BeFalse();
        AiPricing.IsKnown("gemini-3.5-flash-lite").Should().BeTrue();
        AiPricing.Cost("gemini-3.5-flash-lite", 1_000_000, 0).Should().Be(0.30m);
        AiPricing.Cost("gemini-3.5-flash", 1_000_000, 0).Should().Be(1.50m);
    }

    [Fact]
    public void UnknownGeminiModel_IsCountedAtTheDearestRowOfItsFamily_NotAsGemini15Flash()
    {
        var pro = AiPricing.Cost("gemini-9-pro", 1_000_000, 1_000_000);
        var flash = AiPricing.Cost("gemini-9-flash", 1_000_000, 1_000_000);

        pro.Should().Be(AiPricing.Cost("gemini-3-pro-preview", 1_000_000, 1_000_000));
        flash.Should().Be(AiPricing.Cost("gemini-3.5-flash", 1_000_000, 1_000_000));
        flash.Should().BeGreaterThan(AiPricing.Cost("gemini-1.5-flash", 1_000_000, 1_000_000));
    }

    [Theory]
    [InlineData("qwen2.5:7b")]
    [InlineData("llama3.1")]
    [InlineData("anthropic/claude-sonnet")]
    [InlineData("")]
    [InlineData(null)]
    public void NonGeminiModel_IsFree_BecauseThisTableOnlyKnowsGemini(string? model)
    {
        AiPricing.Cost(model, 5_000_000, 5_000_000).Should().Be(0m);
    }

    [Fact]
    public void NegativeTokenCounts_NeverProduceANegativeCost()
    {
        AiPricing.Cost("gemini-2.5-flash", -5, -5).Should().Be(0m);
    }

    [Fact]
    public void Thinking_IsBilledAsOutput()
    {
        GeminiNativeProvider.OutputTokens(40, 900).Should().Be(940);
        GeminiNativeProvider.OutputTokens(40, null).Should().Be(40);
        GeminiNativeProvider.OutputTokens(null, 900).Should().Be(900);
        GeminiNativeProvider.OutputTokens(null, null).Should().BeNull();
    }
}

public class AiBillingPeriodTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CalendarMonth_IsTheFirstToTheFirst()
    {
        var p = AiBillingPeriod.CalendarMonth(Utc(2026, 3, 17, 12));
        p.Start.Should().Be(Utc(2026, 3, 1));
        p.End.Should().Be(Utc(2026, 4, 1));
    }

    [Fact]
    public void Anchor_GivesTheMonthlyWindowContainingNow()
    {
        var p = AiBillingPeriod.FromAnchor(Utc(2026, 11, 14), Utc(2026, 10, 20));
        p.Start.Should().Be(Utc(2026, 10, 14));
        p.End.Should().Be(Utc(2026, 11, 14));
    }

    [Fact]
    public void Anchor_OnTheBillingInstant_StartsANewWindow()
    {
        var p = AiBillingPeriod.FromAnchor(Utc(2026, 11, 14), Utc(2026, 11, 14));
        p.Start.Should().Be(Utc(2026, 11, 14));
        p.End.Should().Be(Utc(2026, 12, 14));
    }

    [Fact]
    public void YearlyPlan_StillGetsAMonthlyWindow_OnItsAnniversaryDay()
    {
        // next yearly renewal is a year out; the allowance still resets monthly on the 14th
        var p = AiBillingPeriod.FromAnchor(Utc(2027, 8, 14), Utc(2026, 10, 20));
        p.Start.Should().Be(Utc(2026, 10, 14));
        p.End.Should().Be(Utc(2026, 11, 14));
    }

    [Fact]
    public void ThirtyFirst_ClampsToShortMonths_WithoutDriftingForGood()
    {
        var feb = AiBillingPeriod.FromAnchor(Utc(2026, 3, 31), Utc(2026, 3, 10));
        feb.Start.Should().Be(Utc(2026, 2, 28));
        feb.End.Should().Be(Utc(2026, 3, 31));

        var apr = AiBillingPeriod.FromAnchor(Utc(2026, 3, 31), Utc(2026, 4, 30, 12));
        apr.Start.Should().Be(Utc(2026, 4, 30));
        apr.End.Should().Be(Utc(2026, 5, 31));
    }

    [Fact]
    public void WindowAlwaysContainsNow()
    {
        var anchor = Utc(2026, 12, 31);
        for (var day = 0; day < 400; day++)
        {
            var now = Utc(2026, 1, 1).AddDays(day).AddHours(5);
            var p = AiBillingPeriod.FromAnchor(anchor, now);
            (p.Start <= now && now < p.End).Should().BeTrue($"{now:O} should fall in {p.Start:O} to {p.End:O}");
        }
    }

    private static BuoyAIDbContext Db() =>
        new(new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UserSubscription Sub(int userId, UserSubscriptionStatus status, DateTime next) => new()
    {
        UserId = userId, Status = status, NextBillingDate = next, Tier = SubscriptionTier.Premium,
        BillingCycle = SubscriptionBillingCycle.Monthly, StartDate = next.AddMonths(-1),
    };

    [Fact]
    public async Task ForUser_ActiveSubscription_FollowsTheBillingDate()
    {
        using var db = Db();
        db.UserSubscriptions.Add(Sub(1, UserSubscriptionStatus.Active, Utc(2026, 11, 14)));
        await db.SaveChangesAsync();

        var p = await AiBillingPeriod.ForUserAsync(db, 1, Utc(2026, 10, 20));

        p.Start.Should().Be(Utc(2026, 10, 14));
    }

    [Fact]
    public async Task ForUser_NoSubscriptionOrOnlyACancelledOne_UsesTheCalendarMonth()
    {
        using var db = Db();
        db.UserSubscriptions.Add(Sub(2, UserSubscriptionStatus.Canceled, Utc(2026, 11, 14)));
        await db.SaveChangesAsync();

        (await AiBillingPeriod.ForUserAsync(db, 1, Utc(2026, 10, 20))).Start.Should().Be(Utc(2026, 10, 1));
        (await AiBillingPeriod.ForUserAsync(db, 2, Utc(2026, 10, 20))).Start.Should().Be(Utc(2026, 10, 1));
    }
}

public class AiUsageTrackerMeteringTests
{
    private static (AIUsageTracker Tracker, IMemoryCache Cache, BuoyAIDbContext Db) Tracker(
        EditionInfo? edition = null, Dictionary<string, string?>? config = null)
    {
        var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build();
        return (new AIUsageTracker(NullLogger<AIUsageTracker>.Instance, cache, cfg, db, new BuoyAiMetrics(new TestMeterFactory()), edition), cache, db);
    }

    private static string DayKey(int userId) => $"ai_usage_daily_{userId}_{DateTime.UtcNow:yyyyMMdd}";

    [Fact]
    public async Task RecordCall_KeepsTheProvidersOwnSplit_TheProvider_AndTheEstimatedFlag()
    {
        var (tracker, _, db) = Tracker();

        await tracker.RecordCallAsync(new AiUsageEntry(7, "qwen2.5:7b", "ollama", 1800, 40, false, 0m, "insights"));

        var row = await db.AIUsageRecords.SingleAsync();
        row.InputTokens.Should().Be(1800);
        row.OutputTokens.Should().Be(40);
        row.Provider.Should().Be("ollama");
        row.TokensEstimated.Should().BeFalse();
        row.RequestContext.Should().Be("insights");
    }

    [Fact]
    public async Task RecordCall_ByDefault_DoesNotCountAgainstTheHourlyAndDailyRequestLimits()
    {
        var (tracker, cache, _) = Tracker();

        await tracker.RecordCallAsync(new AiUsageEntry(7, "m", "gemini", 10, 5, false, 0.01m, "insights"));
        cache.Get<int>(DayKey(7)).Should().Be(0);

        await tracker.RecordCallAsync(new AiUsageEntry(7, "m", "gemini", 10, 5, false, 0.01m, "chat", CountsAgainstRequestLimits: true));
        cache.Get<int>(DayKey(7)).Should().Be(1);
    }

    [Fact]
    public async Task RecordCall_CutsLongModelAndProviderNamesToTheColumnWidths()
    {
        var (tracker, _, db) = Tracker();

        await tracker.RecordCallAsync(new AiUsageEntry(7, new string('m', 80), new string('p', 60), 1, 1, false, 0m, "insights"));

        var row = await db.AIUsageRecords.SingleAsync();
        row.Model.Should().HaveLength(50);
        row.Provider.Should().HaveLength(30);
    }

    [Fact]
    public async Task RecordUsage_TheOldOverload_IsFlaggedAsAGuess()
    {
        var (tracker, _, db) = Tracker();

        await tracker.RecordUsageAsync(7, "gemini-2.5-flash", 1000, 0.01m);

        var row = await db.AIUsageRecords.SingleAsync();
        row.TokensEstimated.Should().BeTrue();
        row.InputTokens.Should().Be(700);
    }

    [Fact]
    public async Task MonthlyUsage_CountsTheBillingPeriod_NotTheCalendarMonth()
    {
        var (tracker, _, db) = Tracker();
        var now = DateTime.UtcNow;
        // billed on this day each month, so the period began at the most recent anniversary
        var anchor = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddDays(-1);
        db.UserSubscriptions.Add(new UserSubscription
        {
            UserId = 7, Status = UserSubscriptionStatus.Active, NextBillingDate = anchor, Tier = SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly, StartDate = anchor.AddMonths(-1),
        });
        var period = AiBillingPeriod.FromAnchor(anchor, now);
        db.AIUsageRecords.Add(new AIUsageRecord { UserId = 7, Model = "m", EstimatedCost = 1m, Timestamp = period.Start.AddMinutes(-5), CreatedAt = now });
        db.AIUsageRecords.Add(new AIUsageRecord { UserId = 7, Model = "m", EstimatedCost = 2m, Timestamp = period.Start.AddMinutes(5), CreatedAt = now });
        await db.SaveChangesAsync();

        var stats = await tracker.GetMonthlyUsageAsync(7);

        stats.CostThisMonth.Should().Be(2m, "the older row belongs to the previous billing period");
    }

    [Fact]
    public async Task Hosted_MonthlyCheck_FailsClosed_WhenTheMeterCantBeRead()
    {
        var (tracker, _, db) = Tracker(new EditionInfo(Edition.Hosted));
        await db.DisposeAsync(); // every query now throws

        var (canProceed, warning, _) = await tracker.CheckMonthlyLimitAsync(7, SubscriptionTier.Standard);

        canProceed.Should().BeFalse();
        warning.Should().Be("unavailable");
    }

    [Fact]
    public async Task Hosted_FailClosed_CanBeTurnedOffInSettings()
    {
        var (tracker, _, db) = Tracker(new EditionInfo(Edition.Hosted), new() { ["Ai:Metering:FailClosed"] = "false", ["Ai:Metering:EnforceMonthlyCap"] = "true" });
        await db.DisposeAsync();

        (await tracker.CheckMonthlyLimitAsync(7, SubscriptionTier.Standard)).CanProceed.Should().BeTrue();
    }

    [Fact]
    public async Task SelfHost_NeverBlocks_AtAnyHourlyCount_OrAnySpend()
    {
        var (tracker, cache, db) = Tracker(new EditionInfo(Edition.SelfHost));
        cache.Set($"ai_usage_hourly_7_{DateTime.UtcNow:yyyyMMddHH}", 10_000);
        db.AIUsageRecords.Add(new AIUsageRecord { UserId = 7, Model = "m", EstimatedCost = 500m, Timestamp = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        (await tracker.CanMakeRequestAsync(7, SubscriptionTier.Standard)).Should().BeTrue();
        var (canProceed, _, percent) = await tracker.CheckMonthlyLimitAsync(7, SubscriptionTier.Standard);
        canProceed.Should().BeTrue();
        percent.Should().BeGreaterThan(100, "the number is still reported, it just doesn't block");
    }

    [Fact]
    public async Task Hosted_StillBlocks_AtTheHourlyCap_AsBefore()
    {
        var (tracker, cache, _) = Tracker(new EditionInfo(Edition.Hosted));
        cache.Set($"ai_usage_hourly_7_{DateTime.UtcNow:yyyyMMddHH}", 10);

        (await tracker.CanMakeRequestAsync(7, SubscriptionTier.Standard)).Should().BeFalse();
    }

    [Fact]
    public async Task Hosted_RequestGate_FailsClosed_WhenTheMeterCantBeRead()
    {
        var (tracker, _, db) = Tracker(new EditionInfo(Edition.Hosted), new() { ["Ai:Metering:EnforceMonthlyCap"] = "true" });
        await db.DisposeAsync();

        (await tracker.CanMakeRequestAsync(7, SubscriptionTier.Standard)).Should().BeFalse();
    }

    [Fact]
    public async Task Hosted_RequestGate_CanBeOpenedWhenFailClosedIsOff()
    {
        var (tracker, _, db) = Tracker(new EditionInfo(Edition.Hosted), new() { ["Ai:Metering:FailClosed"] = "false", ["Ai:Metering:EnforceMonthlyCap"] = "true" });
        await db.DisposeAsync();

        (await tracker.CanMakeRequestAsync(7, SubscriptionTier.Standard)).Should().BeTrue();
    }

    [Fact]
    public async Task Hosted_RequestGate_BlocksOnceTheMonthlyCostCapIsSpent()
    {
        var (tracker, _, db) = Tracker(new EditionInfo(Edition.Hosted), new() { ["Ai:Metering:EnforceMonthlyCap"] = "true" });
        db.AIUsageRecords.Add(new AIUsageRecord { UserId = 7, Model = "m", EstimatedCost = 500m, Timestamp = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        (await tracker.CanMakeRequestAsync(7, SubscriptionTier.Standard)).Should().BeFalse();
    }

    [Fact]
    public async Task Hosted_RequestGate_IgnoresTheMonthlyCap_WhenEnforcementIsOff()
    {
        var (tracker, _, db) = Tracker(new EditionInfo(Edition.Hosted), new() { ["Ai:Metering:EnforceMonthlyCap"] = "false" });
        db.AIUsageRecords.Add(new AIUsageRecord { UserId = 7, Model = "m", EstimatedCost = 500m, Timestamp = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        (await tracker.CanMakeRequestAsync(7, SubscriptionTier.Standard)).Should().BeTrue("warning-only mode never blocks");
    }
}

public class MeteredAiProviderTests
{
    private readonly Mock<IAIUsageTracker> _tracker = new();
    private readonly List<AiUsageEntry> _rows = new();

    private MeteredAiProvider Make(FakeAiProvider inner)
    {
        _tracker.Setup(t => t.RecordCallAsync(It.IsAny<AiUsageEntry>()))
            .Callback<AiUsageEntry>(_rows.Add).Returns(Task.CompletedTask);
        var services = new ServiceCollection().AddSingleton(_tracker.Object).BuildServiceProvider();
        return new MeteredAiProvider(inner, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MeteredAiProvider>.Instance);
    }

    [Fact]
    public async Task ACallInsideAUserScope_WritesOneRowWithTheProvidersCounts()
    {
        var provider = Make(new FakeAiProvider { Reply = _ => new AiTextResult("ok", 1200, 80) });

        using (AiUsageScope.ForUser(42))
            await provider.GenerateTextAsync(new AiTextRequest("p", "gemini-2.5-flash", "insights"));

        var row = _rows.Should().ContainSingle().Subject;
        row.UserId.Should().Be(42);
        row.Provider.Should().Be("gemini");
        row.Model.Should().Be("gemini-2.5-flash");
        row.InputTokens.Should().Be(1200);
        row.OutputTokens.Should().Be(80);
        row.TokensEstimated.Should().BeFalse();
        row.RequestContext.Should().Be("insights");
        row.Cost.Should().Be(AiPricing.Cost("gemini-2.5-flash", 1200, 80));
        row.CountsAgainstRequestLimits.Should().BeFalse("insights never counted against the chat request limits");
    }

    [Fact]
    public async Task ACallWithNoUserInScope_WritesNothing()
    {
        var provider = Make(new FakeAiProvider { Reply = _ => new AiTextResult("ok", 10, 5) });

        await provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));

        _rows.Should().BeEmpty();
    }

    [Fact]
    public async Task TheScope_EndsWithItsBlock()
    {
        var provider = Make(new FakeAiProvider { Reply = _ => new AiTextResult("ok", 10, 5) });

        using (AiUsageScope.ForUser(1)) { }
        await provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));

        AiUsageScope.CurrentUserId.Should().BeNull();
        _rows.Should().BeEmpty();
    }

    [Fact]
    public async Task NestedScopes_RestoreTheOuterUser()
    {
        using (AiUsageScope.ForUser(1))
        {
            using (AiUsageScope.ForUser(2)) AiUsageScope.CurrentUserId.Should().Be(2);
            AiUsageScope.CurrentUserId.Should().Be(1);
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AProviderThatReportsNoCounts_GetsALengthGuess_FlaggedAsEstimated()
    {
        var provider = Make(new FakeAiProvider { Reply = _ => new AiTextResult(new string('x', 400), null, null) });

        using (AiUsageScope.ForUser(3))
            await provider.GenerateTextAsync(new AiTextRequest(new string('y', 800), "gemini-2.5-flash", "subscriptions"));

        var row = _rows.Single();
        row.InputTokens.Should().Be(200);
        row.OutputTokens.Should().Be(100);
        row.TokensEstimated.Should().BeTrue();
    }

    [Fact]
    public async Task ACostTheProviderReports_WinsOverTheGeminiTable_AndALocalModelIsFree()
    {
        var provider = Make(new FakeAiProvider { Id = "ollama", ModelOverride = "qwen2.5:7b", Reply = _ => new AiTextResult("ok", 900, 30, 0m) });

        using (AiUsageScope.ForUser(3))
            await provider.GenerateTextAsync(new AiTextRequest("p", "gemini-3.5-flash", "memory-extraction"));

        var row = _rows.Single();
        row.Cost.Should().Be(0m);
        row.Model.Should().Be("qwen2.5:7b", "the row shows the model that ran, not the one the caller asked for");
        row.Provider.Should().Be("ollama");
    }

    [Fact]
    public async Task AFailedCall_WritesNoRow_AndTheErrorStillReachesTheCaller()
    {
        var provider = Make(new FakeAiProvider { IsConfigured = false });

        using (AiUsageScope.ForUser(3))
        {
            var act = () => provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));
            await act.Should().ThrowAsync<AiUnavailableException>();
        }

        _rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ARecordingFailure_NeverFailsTheCall()
    {
        _tracker.Setup(t => t.RecordCallAsync(It.IsAny<AiUsageEntry>())).ThrowsAsync(new InvalidOperationException("db down"));
        var services = new ServiceCollection().AddSingleton(_tracker.Object).BuildServiceProvider();
        var provider = new MeteredAiProvider(
            new FakeAiProvider { Reply = _ => new AiTextResult("fine", 1, 1) },
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MeteredAiProvider>.Instance);

        using (AiUsageScope.ForUser(3))
        {
            var result = await provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));
            result.Text.Should().Be("fine");
        }
    }

    [Fact]
    public void ItReportsTheInnerProvidersIdentityAndCapabilities()
    {
        var inner = new FakeAiProvider { Id = "openrouter", ModelOverride = "x/y" };
        var provider = Make(inner);

        provider.Id.Should().Be("openrouter");
        provider.IsConfigured.Should().BeTrue();
        provider.Capabilities.Should().Be(inner.Capabilities);
        provider.ResolveModel("anything", "insights").Should().Be("x/y");
    }
}

public class ChatTokenAccountingTests
{
    [Fact]
    public void RealCountsFromGemini_AreUsedAsTheyCame()
    {
        int input = 0, output = 0; bool estimated = false;

        ChatTokenAccounting.AddCall(5000, 999, 300, ref input, ref output, ref estimated);
        ChatTokenAccounting.AddCall(6200, 999, 450, ref input, ref output, ref estimated);

        input.Should().Be(11200);
        output.Should().Be(750);
        estimated.Should().BeFalse();
    }

    [Fact]
    public void ACallWithoutCounts_FallsBackToTheEstimate_AndMarksTheTotalsAsAGuess()
    {
        int input = 0, output = 0; bool estimated = false;

        ChatTokenAccounting.AddCall(null, 800, null, ref input, ref output, ref estimated);

        input.Should().Be(800);
        output.Should().Be(0, "the caller adds an estimate from the text it received");
        estimated.Should().BeTrue();
    }

    [Fact]
    public void OnceAnyCallWasAGuess_TheTurnStaysFlagged()
    {
        int input = 0, output = 0; bool estimated = false;

        ChatTokenAccounting.AddCall(null, 100, 10, ref input, ref output, ref estimated);
        ChatTokenAccounting.AddCall(500, 100, 10, ref input, ref output, ref estimated);

        estimated.Should().BeTrue();
        input.Should().Be(600);
    }
}
