using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Sign-in must not depend on billing: a deployment with no Stripe keys has to be able to
/// build AuthService from the real container and sign a user in.
/// </summary>
public class AuthServiceNoStripeTests
{
    private const string Password = "Zx7!kQ2#mWp9";

    private static ServiceProvider BuildContainer(string dbName, IBillingService? billing = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "your-super-secret-jwt-key-that-should-be-at-least-32-characters-long",
            ["Jwt:Issuer"] = "BuoyAI",
            ["Jwt:Audience"] = "BuoyAI-Users",
            ["Encryption:Key"] = "test-encryption-key-for-unit-tests",
            // PlaidService also throws at construction without keys; that is a separate seam.
            ["Plaid:ClientId"] = "test-client",
            ["Plaid:SecretKey"] = "test-secret",
            // Deliberately no Stripe:SecretKey.
        }).Build();

        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Development");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(env.Object);
        services.AddInfrastructure(config, skipDbContext: true);
        services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName).ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName), ServiceLifetime.Scoped);
        if (billing != null) services.AddScoped(_ => billing);
        return services.BuildServiceProvider();
    }

    private static async Task SeedUserAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        db.Users.Add(new User
        {
            Id = 1, Username = "nostripe", Email = "nostripe@example.com",
            PasswordHash = AuthService.HashPassword(Password), IsEmailVerified = true,
            Currency = "USD", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task WithNoStripeKey_BillingCannotBeBuilt_ButAuthServiceBuildsAndSignsInAUser()
    {
        await using var root = BuildContainer(Guid.NewGuid().ToString());
        await SeedUserAsync(root);
        using var scope = root.CreateScope();

        var billing = () => scope.ServiceProvider.GetRequiredService<IBillingService>();
        billing.Should().Throw<InvalidOperationException>("this is the failure the test guards against");

        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var response = await auth.LoginAsync(new LoginRequest { Email = "nostripe@example.com", Password = Password });

        response.Should().NotBeNull();
        response!.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task WhenBillingIsAvailable_DeleteAccount_StillCancelsAndDeletesTheStripeCustomer()
    {
        var billing = new Mock<IBillingService>();
        await using var root = BuildContainer(Guid.NewGuid().ToString(), billing.Object);
        await SeedUserAsync(root);
        using var scope = root.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        (await auth.DeleteAccountAsync(1, Password)).Should().BeTrue();

        billing.Verify(b => b.CancelSubscriptionAsync(1,
            It.Is<BuoyAI.Core.DTOs.Billing.CancelSubscriptionRequest>(r => r.CancelImmediately)), Times.Once);
        billing.Verify(b => b.DeleteStripeCustomerAsync(1), Times.Once);
    }

    [Fact]
    public async Task WithNoStripeKey_DeleteAccount_SkipsTheStripeStepsAndStillDeletes()
    {
        await using var root = BuildContainer(Guid.NewGuid().ToString());
        await SeedUserAsync(root);
        using var scope = root.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        (await auth.DeleteAccountAsync(1, Password)).Should().BeTrue();

        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == 1);
        user.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task WithNoStripeKey_Register_SkipsTheStripeTrialAndStillCreatesTheAccount()
    {
        await using var root = BuildContainer(Guid.NewGuid().ToString());
        using var scope = root.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var response = await auth.RegisterAsync(new RegisterRequest
        {
            Email = "newuser@example.com", Password = Password, FirstName = "New", LastName = "User"
        });

        response.Should().NotBeNull();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == "newuser@example.com");
        user.IsTrialActive.Should().BeTrue("a trial was granted, so the Stripe step was reached and skipped");
    }
}
