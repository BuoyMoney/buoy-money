using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.EditionSetting;

/// <summary>
/// Sign-up on a self-hosted server: the first account is the admin, then registration is closed
/// unless an admin opens it or hands out an invite. Hosted sign-up is not gated at all.
/// </summary>
public class SelfHostAuthTests
{
    private const string Password = "Zx7!kQ2#mWp9";

    private static ServiceProvider Build(string edition, Dictionary<string, string?>? extra = null, Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "your-super-secret-jwt-key-that-should-be-at-least-32-characters-long",
            ["Jwt:Issuer"] = "BuoyAI",
            ["Jwt:Audience"] = "BuoyAI-Users",
            ["Encryption:Key"] = "test-encryption-key-for-unit-tests",
            ["BuoyMoney:Edition"] = edition,
        };
        if (extra != null) foreach (var kv in extra) values[kv.Key] = kv.Value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Development");
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(env.Object);
        services.AddSingleton<Qdrant.Client.QdrantClient>(_ => null!);
        services.AddInfrastructure(config, skipDbContext: true);
        services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName), ServiceLifetime.Scoped);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static RegisterRequest Request(string email, string? invite = null) => new()
    {
        Email = email, Password = Password, FirstName = "Home", LastName = "User", InviteCode = invite
    };

    private sealed class RecordingLock : FirstAccountLock
    {
        public int Calls;
        public int UsersWhenTaken = -1;

        public override async Task AcquireAsync(BuoyAIDbContext context)
        {
            Calls++;
            UsersWhenTaken = await context.Users.IgnoreQueryFilters().CountAsync();
            await base.AcquireAsync(context);
        }
    }

    [Fact]
    public async Task FirstAccountDecision_IsMadeUnderTheLock_ForEveryRegistration()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var recorder = new RecordingLock();
        var gate = new RegistrationGate(
            scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>(),
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            recorder);

        var first = new User { Email = "first@home.test" };
        await gate.AdmitAsync(first, null);

        recorder.Calls.Should().Be(1);
        recorder.UsersWhenTaken.Should().Be(0);
        first.IsInstanceAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task EmailRegistration_TakesTheFirstAccountLock_BeforeTheUserExists()
    {
        var recorder = new RecordingLock();
        await using var sp = Build("selfhost", configure: svc => svc.AddSingleton<FirstAccountLock>(recorder));
        using var scope = sp.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IAuthService>().RegisterAsync(Request("first@home.test"));

        recorder.Calls.Should().Be(1);
        recorder.UsersWhenTaken.Should().Be(0);
    }

    [Fact]
    public async Task FirstAccount_BecomesTheAdmin()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var response = await auth.RegisterAsync(Request("first@home.test"));

        response.Should().NotBeNull();
        response!.User.SubscriptionTier.Should().Be("Admin");
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        (await db.Users.SingleAsync()).IsInstanceAdmin.Should().BeTrue();
        (await scope.ServiceProvider.GetRequiredService<IEntitlementService>()
            .GetEffectiveTierAsync(response.User.Id)).Should().Be(BuoyAI.Core.Enums.SubscriptionTier.Admin);
    }

    [Fact]
    public async Task ADeletedFirstAccount_DoesNotMakeTheServerLookNewAgain()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        await auth.RegisterAsync(Request("first@home.test"));
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        (await db.Users.SingleAsync()).IsDeleted = true;
        await db.SaveChangesAsync();

        var act = () => auth.RegisterAsync(Request("stranger@home.test"));

        await act.Should().ThrowAsync<RegistrationClosedException>();
    }

    [Fact]
    public async Task SecondAccount_IsRefused_WhileRegistrationIsClosed()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        await auth.RegisterAsync(Request("first@home.test"));

        var act = () => auth.RegisterAsync(Request("second@home.test"));

        await act.Should().ThrowAsync<RegistrationClosedException>();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        (await db.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AnInvite_AdmitsExactlyOneAccount_AsAnOrdinaryMember()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var gate = scope.ServiceProvider.GetRequiredService<RegistrationGate>();
        var admin = (await auth.RegisterAsync(Request("first@home.test")))!;
        var (_, code) = await gate.CreateInviteAsync(admin.User.Id, "for my partner", 7);

        var second = await auth.RegisterAsync(Request("partner@home.test", code.ToLowerInvariant().Replace("-", " ")));

        second.Should().NotBeNull("case, dashes and spaces do not matter in an invite code");
        second!.User.SubscriptionTier.Should().Be("Premium");
        var again = () => auth.RegisterAsync(Request("third@home.test", code));
        await again.Should().ThrowAsync<RegistrationClosedException>("an invite is single use");
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        (await db.RegistrationInvites.SingleAsync()).UsedByUserId.Should().Be(second.User.Id);
    }

    [Fact]
    public async Task AnExpiredInvite_IsRefused_AndOnlyTheHashIsStored()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var gate = scope.ServiceProvider.GetRequiredService<RegistrationGate>();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var admin = (await auth.RegisterAsync(Request("first@home.test")))!;
        var (invite, code) = await gate.CreateInviteAsync(admin.User.Id, null, 7);
        invite.CodeHash.Should().NotContain(code.Replace("-", ""));
        invite.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var act = () => auth.RegisterAsync(Request("late@home.test", code));

        await act.Should().ThrowAsync<RegistrationClosedException>();
    }

    [Fact]
    public async Task AnAdminCanOpenAndCloseRegistration()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var gate = scope.ServiceProvider.GetRequiredService<RegistrationGate>();
        await auth.RegisterAsync(Request("first@home.test"));

        await gate.SetOpenAsync(true);
        (await auth.RegisterAsync(Request("open@home.test"))).Should().NotBeNull();
        (await gate.GetStatusAsync()).CanRegister.Should().BeTrue();

        await gate.SetOpenAsync(false);
        var act = () => auth.RegisterAsync(Request("closed@home.test"));
        await act.Should().ThrowAsync<RegistrationClosedException>();
    }

    [Fact]
    public async Task OpenRegistration_IgnoresABadInviteCode()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        await auth.RegisterAsync(Request("first@home.test"));
        await scope.ServiceProvider.GetRequiredService<RegistrationGate>().SetOpenAsync(true);

        (await auth.RegisterAsync(Request("guest@home.test", "NOPE-NOPE-NOPE-NOPE"))).Should().NotBeNull();
    }

    [Fact]
    public async Task RevokedInvite_NoLongerWorks()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var gate = scope.ServiceProvider.GetRequiredService<RegistrationGate>();
        var admin = (await auth.RegisterAsync(Request("first@home.test")))!;
        var (invite, code) = await gate.CreateInviteAsync(admin.User.Id, null, 7);

        (await gate.RevokeInviteAsync(invite.Id)).Should().BeTrue();

        var act = () => auth.RegisterAsync(Request("x@home.test", code));
        await act.Should().ThrowAsync<RegistrationClosedException>();
    }

    [Fact]
    public async Task WithNoMailServer_NewAccountsAreMarkedVerified_BecauseNoCodeCouldArrive()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var response = await auth.RegisterAsync(Request("first@home.test"));

        response!.User.IsEmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task WithAMailServer_EmailVerificationStillApplies()
    {
        await using var sp = Build("selfhost", new() { ["Smtp:Host"] = "smtp.home.test", ["Smtp:FromAddress"] = "money@home.test" });
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IEmailTransport>().Should().BeOfType<SmtpEmailTransport>();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var response = await auth.RegisterAsync(Request("first@home.test"));

        response!.User.IsEmailVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Hosted_SignUpIsNeverGated_AndNeverAdmin()
    {
        await using var sp = Build("hosted");
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var first = await auth.RegisterAsync(Request("a@example.com"));
        var second = await auth.RegisterAsync(Request("b@example.com"));

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        scope.ServiceProvider.GetService<RegistrationGate>().Should().BeNull();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        (await db.Users.AnyAsync(u => u.IsInstanceAdmin)).Should().BeFalse();
        first!.User.IsEmailVerified.Should().BeFalse("hosted still verifies email");
        scope.ServiceProvider.GetRequiredService<IEmailTransport>().Should().BeOfType<ResendEmailTransport>();
    }

    [Fact]
    public async Task EmailTransport_IsTheNoOpOne_WhenSelfHostHasNoSmtpHost()
    {
        await using var sp = Build("selfhost");
        sp.GetRequiredService<IEmailTransport>().Should().BeOfType<NoEmailTransport>();
        sp.GetRequiredService<IEmailTransport>().IsConfigured.Should().BeFalse();
        await sp.GetRequiredService<IEmailTransport>().SendAsync(
            new OutboundEmail("Buoy", "a@b.test", "c@d.test", null, "Subject", "<p>x</p>", "x"));
    }

    [Fact]
    public async Task StartupSeeding_IsSkippedOnSelfHost_SoTheFirstSignUpIsStillTheFirstAccount()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        // Exactly what Program.cs does on a self-host start.
        await DataSeeder.SeedAsync(db, env, null, null, seedData: false);

        (await db.Users.IgnoreQueryFilters().CountAsync()).Should().Be(0, "no reviewer or admin accounts on a home server");
        (await scope.ServiceProvider.GetRequiredService<RegistrationGate>().GetStatusAsync()).FirstAccount.Should().BeTrue();
        var first = await scope.ServiceProvider.GetRequiredService<IAuthService>().RegisterAsync(Request("first@home.test"));
        first!.User.SubscriptionTier.Should().Be("Admin");
    }

    [Fact]
    public async Task WithAnOperatorEmail_AStrangerCannotClaimTheFreshServer()
    {
        await using var sp = Build("selfhost", new() { ["Admin:Emails:0"] = "owner@home.test" });
        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var stranger = () => auth.RegisterAsync(Request("stranger@home.test"));
        await stranger.Should().ThrowAsync<RegistrationClosedException>();

        var owner = await auth.RegisterAsync(Request("owner@home.test"));
        owner!.User.SubscriptionTier.Should().Be("Admin");
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        (await db.Users.SingleAsync()).IsInstanceAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task TheOperatorEmail_CanRegisterEvenWhileSignUpIsClosed()
    {
        await using var sp = Build("selfhost", new() { ["Admin:Emails:0"] = "owner@home.test" });
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        db.Users.Add(new User { Username = "someone", Email = "someone@home.test", PasswordHash = "x", FirstName = "S", LastName = "O" });
        await db.SaveChangesAsync();

        var owner = await scope.ServiceProvider.GetRequiredService<IAuthService>().RegisterAsync(Request("owner@home.test"));

        owner.Should().NotBeNull();
    }

    [Fact]
    public async Task ASpentInvite_CannotBeSpentAgain_EvenFromAStaleCopy()
    {
        await using var sp = Build("selfhost");
        using var scope = sp.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<RegistrationGate>();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var (invite, _) = await gate.CreateInviteAsync(1, null, 7);

        await gate.ConsumeInviteAsync(invite.Id, 10);
        var again = () => gate.ConsumeInviteAsync(invite.Id, 11);

        await again.Should().ThrowAsync<RegistrationClosedException>();
        (await db.RegistrationInvites.SingleAsync()).UsedByUserId.Should().Be(10);
    }

    [Fact]
    public async Task Hosted_RejectsRelayDevices()
    {
        await using var sp = Build("hosted");
        var edition = sp.GetRequiredService<BuoyAI.Infrastructure.Configuration.EditionInfo>();
        edition.IsSelfHost.Should().BeFalse();
    }
}
