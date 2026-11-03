using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.MCP.Tools;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.EditionSetting;

/// <summary>
/// The self-host edition built from the real container with NO Stripe keys and NO Plaid keys, the
/// situation of every home install. The hosted edition (the default) must register what it always did.
/// </summary>
public class SelfHostContainerTests
{
    private const string Password = "Zx7!kQ2#mWp9";

    private static ServiceProvider Build(string? edition, string dbName, bool? simpleFinEnabled = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "your-super-secret-jwt-key-that-should-be-at-least-32-characters-long",
            ["Jwt:Issuer"] = "BuoyAI",
            ["Jwt:Audience"] = "BuoyAI-Users",
            ["Encryption:Key"] = "test-encryption-key-for-unit-tests",
            ["Admin:Emails:0"] = "owner@home.test",
            // Lets the hosted edition build PlaidService; self-host never reads them.
            ["Plaid:ClientId"] = "test-client",
            ["Plaid:SecretKey"] = "test-secret",
        };
        if (edition != null) values["BuoyMoney:Edition"] = edition;
        if (simpleFinEnabled != null) values["SimpleFin:Enabled"] = simpleFinEnabled.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Development");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(env.Object);
        services.AddSingleton<Qdrant.Client.QdrantClient>(_ => null!);
        services.AddInfrastructure(config, skipDbContext: true);
        services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName), ServiceLifetime.Scoped);
        return services.BuildServiceProvider();
    }

    private static bool Registered<T>(ServiceProvider sp) =>
        sp.GetRequiredService<IServiceProviderIsService>().IsService(typeof(T));

    /// <summary>Whether a job class is registered (job-locked jobs are registered as their own singleton).</summary>
    private static bool JobRegistered(ServiceProvider sp, Type job) =>
        sp.GetRequiredService<IServiceProviderIsService>().IsService(job);

    private static async Task SeedAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        db.Users.Add(new User
        {
            Id = 1, Username = "member", Email = "member@home.test",
            PasswordHash = AuthService.HashPassword(Password), IsEmailVerified = true,
            Currency = "USD", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        db.Users.Add(new User
        {
            Id = 2, Username = "owner", Email = "owner@home.test", PasswordHash = "x", IsEmailVerified = true,
            Currency = "USD", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static readonly Type[] HostedOnlyJobs =
    {
        typeof(PlaidBackgroundImportService), typeof(PlaidTransactionSyncService), typeof(BroadcastDeliveryService),
        typeof(TrialReconciliationService), typeof(TrialReminderService), typeof(LeadReminderService),
        typeof(ReferralRewardService), typeof(ReferralInvitePromptService), typeof(BusinessMetricsService),
        typeof(WrappedStatsService),
    };

    private static readonly Type[] HouseholdJobs =
    {
        typeof(DataRetentionService), typeof(SubscriptionDetectionService), typeof(BudgetHealthCheckService),
        typeof(BillReminderService), typeof(WrappedRecapService), typeof(GamificationDailyService),
        typeof(AICategorizationQueueDrainService), typeof(EncryptionReEncryptionService),
    };

    [Fact]
    public void NoEditionSet_IsHosted_AndKeepsEveryBillingAndPlaidRegistration()
    {
        using var sp = Build(null, Guid.NewGuid().ToString());

        sp.GetRequiredService<EditionInfo>().IsHosted.Should().BeTrue();
        Registered<IStripeService>(sp).Should().BeTrue();
        Registered<IBillingService>(sp).Should().BeTrue();
        Registered<IPlaidService>(sp).Should().BeTrue();
        Registered<IReferralService>(sp).Should().BeTrue();
        Registered<IRevenueCatSyncService>(sp).Should().BeTrue();
        Registered<ITierRefreshHook>(sp).Should().BeTrue();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IEntitlementService>().Should().BeOfType<EntitlementService>();
    }

    [Fact]
    public void Hosted_StillRunsTheHostedOnlyJobs()
    {
        using var sp = Build("hosted", Guid.NewGuid().ToString());

        HostedOnlyJobs.Where(j => !JobRegistered(sp, j)).Should().BeEmpty();
    }

    [Fact]
    public void SelfHost_RegistersNoStripeOrPlaid_SoNeitherConstructorCanBeReached()
    {
        using var sp = Build("selfhost", Guid.NewGuid().ToString());

        sp.GetRequiredService<EditionInfo>().IsSelfHost.Should().BeTrue();
        Registered<IStripeService>(sp).Should().BeFalse();
        Registered<IBillingService>(sp).Should().BeFalse();
        Registered<ITierRefreshHook>(sp).Should().BeFalse();
        Registered<ITrialProvisioningService>(sp).Should().BeFalse();
        Registered<ISubscriptionSyncService>(sp).Should().BeFalse();
        Registered<IRevenueCatSyncService>(sp).Should().BeFalse();
        Registered<IReservationService>(sp).Should().BeFalse();
        Registered<IReferralService>(sp).Should().BeFalse();
        Registered<IBroadcastService>(sp).Should().BeFalse();
        Registered<IPlaidService>(sp).Should().BeFalse();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IEntitlementService>().Should().BeOfType<SelfHostEntitlementService>();

        // The one bank provider a home install has is SimpleFIN. Plaid is not among them.
        scope.ServiceProvider.GetRequiredService<IBankAggregatorRegistry>().AvailableProviders
            .Should().BeEquivalentTo(new[] { BankProvider.SimpleFin });
    }

    [Fact]
    public void SelfHost_WithSimpleFinSwitchedOff_HasNoBankProviderAndNoSyncJob()
    {
        using var sp = Build("selfhost", Guid.NewGuid().ToString(), simpleFinEnabled: false);

        Registered<IBankAggregator>(sp).Should().BeFalse();
        JobRegistered(sp, typeof(PlaidTransactionSyncService)).Should().BeFalse();

        // Not offered or synced, but a connection made earlier can still be disconnected.
        using var scope = sp.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IBankAggregatorRegistry>();
        registry.AvailableProviders.Should().BeEmpty();
        registry.TryGet(BankProvider.SimpleFin, out _).Should().BeFalse();
        registry.TryGetForLifecycle(BankProvider.SimpleFin, out var lifecycle).Should().BeTrue();
        lifecycle.Should().NotBeNull();
    }

    [Fact]
    public void SelfHost_WithSimpleFin_RunsTheBankSyncJob_ThatPullsItOnASchedule()
    {
        using var sp = Build("selfhost", Guid.NewGuid().ToString());

        JobRegistered(sp, typeof(PlaidTransactionSyncService)).Should().BeTrue();
        JobRegistered(sp, typeof(PlaidBackgroundImportService)).Should().BeFalse();
    }

    [Fact]
    public void Hosted_OffersNoSimpleFinUntilItIsSwitchedOn()
    {
        using (var sp = Build("hosted", Guid.NewGuid().ToString()))
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<IBankAggregatorRegistry>().AvailableProviders
                .Should().BeEquivalentTo(new[] { BankProvider.Plaid });
        }

        using (var sp = Build("hosted", Guid.NewGuid().ToString(), simpleFinEnabled: true))
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<IBankAggregatorRegistry>().AvailableProviders
                .Should().BeEquivalentTo(new[] { BankProvider.Plaid, BankProvider.SimpleFin });
        }
    }

    [Fact]
    public void SelfHost_RunsNoHostedOnlyJobs_ButKeepsTheHouseholdOnes()
    {
        using var sp = Build("selfhost", Guid.NewGuid().ToString());

        // The bank sync job is the one hosted job a home server also runs, to pull SimpleFIN.
        HostedOnlyJobs.Where(j => j != typeof(PlaidTransactionSyncService))
            .Where(j => JobRegistered(sp, j)).Select(j => j.Name).Should().BeEmpty();
        HouseholdJobs.Where(j => !JobRegistered(sp, j)).Select(j => j.Name).Should().BeEmpty();
    }

    [Fact]
    public async Task SelfHost_SignsInAndDeletesAnAccount_WithNoStripeAndNoPlaid()
    {
        using var sp = Build("selfhost", Guid.NewGuid().ToString());
        await SeedAsync(sp);
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var login = await auth.LoginAsync(new LoginRequest { Email = "member@home.test", Password = Password });
        login.Should().NotBeNull();
        login!.Token.Should().NotBeNullOrEmpty();

        (await auth.DeleteAccountAsync(1, Password)).Should().BeTrue();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        (await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 1)).IsDeleted.Should().BeTrue();
    }

    [Fact]
    public void SelfHost_BuildsTheServicesThatUsedToTakePlaidInTheirConstructor()
    {
        using var sp = Build("selfhost", Guid.NewGuid().ToString());
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDataRetentionManager>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<FinancialReadTools>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IAuthService>().Should().NotBeNull();
    }

    [Fact]
    public void BadEditionValue_StopsTheContainerFromBuilding()
    {
        var act = () => Build("selfhsot", Guid.NewGuid().ToString());
        act.Should().Throw<InvalidOperationException>().WithMessage("*BuoyMoney:Edition*");
    }

    [Fact]
    public async Task SelfHostEntitlements_GiveEveryAccountPremium_AndAllowlistedOperatorsAdmin()
    {
        using var sp = Build("selfhost", Guid.NewGuid().ToString());
        await SeedAsync(sp);
        using var scope = sp.CreateScope();
        var ent = scope.ServiceProvider.GetRequiredService<IEntitlementService>();

        (await ent.GetEffectiveTierAsync(1)).Should().Be(SubscriptionTier.Premium);
        (await ent.GetEffectiveTierAsync(2)).Should().Be(SubscriptionTier.Admin);
        (await ent.GetEffectiveTierAsync(99)).Should().BeNull("an id that is not an account has no tier");
        (await ent.GetTierOrStandardAsync(99)).Should().Be(SubscriptionTier.Standard);
        (await ent.HasPremiumAccessAsync(1)).Should().BeTrue();
        (await ent.HasFeatureAsync(1, "ai_chat_bot")).Should().BeTrue();
        (await ent.HasFeatureAsync(1, "advanced_analytics")).Should().BeTrue();
        (await ent.HasFeatureAsync(1, "admin_dashboard")).Should().BeFalse("admin is a role, not a plan");
        (await ent.HasFeatureAsync(2, "admin_dashboard")).Should().BeTrue();
        (await ent.HasFeatureAsync(99, "ai_chat_bot")).Should().BeFalse();
        ent.HasPremiumAccess(new User { Email = "anyone@home.test" }).Should().BeTrue();
        ent.GetAiLimits(SubscriptionTier.Premium).DailyRequests.Should().BeGreaterThan(0);
    }
}
