using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Characterization of the new-account sequence that the email, Google and Apple sign-up
/// paths each run: apply trial policy, create the user, record the grant, redeem a lead
/// offer, provision the Stripe trial, create email preferences, send the first email.
/// These pin the observable order and the error handling so the three copies can be folded
/// into one method without changing behaviour.
/// </summary>
public class AuthServiceSignUpSequenceTests : IDisposable
{
  private readonly Mock<IUserRepository> _users = new();
  private readonly Mock<IConfiguration> _config = new();
  private readonly Mock<IEmailService> _email = new();
  private readonly Mock<IEmailPreferencesService> _prefs = new();
  private readonly Mock<IMarketingLeadService> _leads = new();
  private readonly Mock<ITrialProvisioningService> _provisioning = new();
  private readonly Mock<IGoogleTokenValidator> _google = new();
  private readonly Mock<IAppleTokenValidator> _apple = new();
  private readonly BuoyAIDbContext _context;
  private readonly AuthService _auth;
  private readonly List<string> _events = new();
  private readonly List<string> _logged = new();

  private const string JwtKey = "your-super-secret-jwt-key-that-should-be-at-least-32-characters-long";

  public AuthServiceSignUpSequenceTests()
  {
    var jwt = new Mock<IConfigurationSection>();
    jwt.Setup(x => x["Key"]).Returns(JwtKey);
    jwt.Setup(x => x["Issuer"]).Returns("BuoyAI");
    jwt.Setup(x => x["Audience"]).Returns("BuoyAI-Users");
    _config.Setup(x => x.GetSection("Jwt")).Returns(jwt.Object);
    _config.Setup(x => x["Jwt:Key"]).Returns(JwtKey);
    _config.Setup(x => x["Jwt:Issuer"]).Returns("BuoyAI");
    _config.Setup(x => x["Jwt:Audience"]).Returns("BuoyAI-Users");
    _config.Setup(x => x["App:BaseUrl"]).Returns("http://localhost:3000");

    var env = new Mock<IHostEnvironment>();
    env.Setup(x => x.EnvironmentName).Returns("Development");

    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
      .Options;
    _context = new BuoyAIDbContext(options);

    _leads.Setup(x => x.GetUnredeemedOfferDaysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync((int?)null);
    _leads.Setup(x => x.MarkRedeemedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
      .Callback<string, int, CancellationToken>((_, _, _) =>
        _events.Add($"redeem(grantRecorded={_context.TrialHistories.Any()})"))
      .Returns(Task.CompletedTask);

    _provisioning.Setup(x => x.ProvisionStripeTrialAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
      .Callback<int, int, CancellationToken>((_, days, _) =>
        _events.Add($"provision(days={days},grantRecorded={_context.TrialHistories.Any()})"))
      .ReturnsAsync(true);

    _prefs.Setup(x => x.CreateDefaultPreferencesAsync(It.IsAny<int>()))
      .Callback<int>(_ => _events.Add("prefs"))
      .ReturnsAsync(new BuoyAI.Core.DTOs.EmailPreferences.UserEmailPreferencesDto());

    _email.Setup(x => x.SendTrialInvitationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
      .Callback<string, string, int>((_, _, days) => _events.Add($"trialEmail({days})"))
      .Returns(Task.CompletedTask);
    _email.Setup(x => x.SendWelcomeEmailAsync(It.IsAny<string>(), It.IsAny<string>()))
      .Callback<string, string>((_, _) => _events.Add("welcomeEmail"))
      .Returns(Task.CompletedTask);

    _users.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _users.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _users.Setup(r => r.GetByGoogleIdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _users.Setup(r => r.GetByAppleIdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
    _users.Setup(r => r.CountGoogleRegistrationsSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(0);
    _users.Setup(r => r.CreateAsync(It.IsAny<User>()))
      .ReturnsAsync((User u) => { u.Id = 777; _events.Add("create"); return u; });

    _auth = new AuthService(
      _users.Object,
      _config.Object,
      new Lazy<IBankAggregatorRegistry>(() => Mock.Of<IBankAggregatorRegistry>()),
      Mock.Of<ILastSyncTracker>(),
      new CapturingLogger<AuthService>(_logged),
      _context,
      _email.Object,
      _prefs.Object,
      env.Object,
      new ServiceCollection().AddSingleton(_provisioning.Object).BuildServiceProvider(),
      Mock.Of<IEntitlementService>(),
      Mock.Of<INotificationService>(),
      Mock.Of<IDataRetentionManager>(),
      Mock.Of<IFamilyInvitationRepository>(),
      _google.Object,
      _apple.Object,
      Mock.Of<IRefreshTokenService>(),
      new BuoyAiMetrics(new TestMeterFactory()),
      Mock.Of<IEncryptionService>(),
      Mock.Of<IAppleAuthClient>(),
      Mock.Of<IServiceScopeFactory>(),
      _leads.Object);
  }

  public void Dispose()
  {
    _context.Database.EnsureDeleted();
    _context.Dispose();
    GC.SuppressFinalize(this);
  }

  private void SetTrialDays(int days)
  {
    var section = new Mock<IConfigurationSection>();
    section.Setup(s => s.Value).Returns(days.ToString());
    _config.Setup(c => c.GetSection("Subscription:TrialPeriodDays")).Returns(section.Object);
  }

  private void OfferLead(int days) =>
    _leads.Setup(x => x.GetUnredeemedOfferDaysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(days);

  private Task<LoginResponse?> RegisterEmailAsync(string email = "email-new@example.com") =>
    _auth.RegisterAsync(new RegisterRequest
    {
      Email = email, Password = "Zx7!kQ2#mWp9", FirstName = "New", LastName = "User"
    });

  private async Task<LoginResponse?> RegisterGoogleAsync(string email = "google-new@example.com")
  {
    _google.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(new GoogleTokenPayload
    {
      Subject = "g-sub-1", Email = email, EmailVerified = true, GivenName = "New", FamilyName = "Person"
    });
    return await _auth.GoogleLoginAsync(new GoogleLoginRequest { IdToken = "t", IsRegistration = true });
  }

  private async Task<LoginResponse?> RegisterAppleAsync(string email = "apple-new@example.com")
  {
    _apple.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(new AppleTokenPayload
    {
      Subject = "a-sub-1", Email = email, EmailVerified = true
    });
    return await _auth.AppleLoginAsync(new AppleLoginRequest { IdentityToken = "t", Nonce = "n" });
  }

  public static IEnumerable<object[]> Paths() => new[]
  {
    new object[] { "email" },
    new object[] { "google" },
    new object[] { "apple" },
  };

  private Task<LoginResponse?> RunPathAsync(string path, string? email = null) => path switch
  {
    "email" => email == null ? RegisterEmailAsync() : RegisterEmailAsync(email),
    "google" => email == null ? RegisterGoogleAsync() : RegisterGoogleAsync(email),
    _ => email == null ? RegisterAppleAsync() : RegisterAppleAsync(email),
  };

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task FreshTrial_RunsTheSequenceInOrder_GrantRecordedBeforeLeadAndStripe(string path)
  {
    SetTrialDays(14);
    OfferLead(30);

    var response = await RunPathAsync(path);

    response.Should().NotBeNull();
    response!.IsNewUser.Should().BeTrue();
    _events.Should().Equal(
      "create",
      "redeem(grantRecorded=True)",
      "provision(days=30,grantRecorded=True)",
      "prefs",
      "trialEmail(30)");

    var created = (User)_users.Invocations.Single(i => i.Method.Name == "CreateAsync").Arguments[0];
    var hash = TrialEmailHasher.Compute(created.Email);
    var history = _context.TrialHistories.Single(t => t.EmailHash == hash);
    history.FirstSource.Should().Be(path);
    history.FirstUserId.Should().Be(777);
    _leads.Verify(x => x.MarkRedeemedAsync(It.IsAny<string>(), 777, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task NoTrial_SkipsGrantLeadAndStripe_ButStillCreatesPrefsAndSendsWelcome(string path)
  {
    SetTrialDays(0);

    await RunPathAsync(path);

    _events.Should().Equal("create", "prefs", "welcomeEmail");
    _context.TrialHistories.Should().BeEmpty();
    _provisioning.Verify(x => x.ProvisionStripeTrialAsync(
      It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    _leads.Verify(x => x.MarkRedeemedAsync(
      It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task PreferencesFailure_IsSwallowed_AndFirstEmailStillGoesOut(string path)
  {
    SetTrialDays(14);
    _prefs.Setup(x => x.CreateDefaultPreferencesAsync(It.IsAny<int>()))
      .Callback<int>(_ => _events.Add("prefs"))
      .ThrowsAsync(new InvalidOperationException("prefs down"));

    var response = await RunPathAsync(path);

    response.Should().NotBeNull();
    _events.Should().Equal(
      "create",
      "redeem(grantRecorded=True)",
      "provision(days=14,grantRecorded=True)",
      "prefs",
      "trialEmail(14)");
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task LeadRedeemFailure_IsSwallowed_AndSignUpContinues(string path)
  {
    SetTrialDays(14);
    OfferLead(30);
    _leads.Setup(x => x.MarkRedeemedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
      .Callback<string, int, CancellationToken>((_, _, _) => _events.Add("redeem"))
      .ThrowsAsync(new InvalidOperationException("lead store down"));

    var response = await RunPathAsync(path);

    response.Should().NotBeNull();
    _events.Should().Equal(
      "create",
      "redeem",
      "provision(days=30,grantRecorded=True)",
      "prefs",
      "trialEmail(30)");
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task ResumedTrial_ProvisionsRemainingDays_WithoutSpendingTheLeadOffer(string path)
  {
    SetTrialDays(14);
    OfferLead(30);
    var email = path + "-resume@example.com";
    _context.TrialHistories.Add(new TrialHistory
    {
      EmailHash = TrialEmailHasher.Compute(email)!,
      FirstTrialStartedAt = DateTime.UtcNow.AddDays(-18),
      OriginalTrialEndsAt = DateTime.UtcNow.AddDays(12).AddHours(-1),
      LastTrialGrantedAt = DateTime.UtcNow.AddDays(-18),
      TrialGrantCount = 1,
      FirstSource = "email",
      CreatedAt = DateTime.UtcNow.AddDays(-18),
      UpdatedAt = DateTime.UtcNow.AddDays(-18),
    });
    await _context.SaveChangesAsync();

    await RunPathAsync(path, email);

    _events.Should().Equal(
      "create",
      "provision(days=12,grantRecorded=True)",
      "prefs",
      "trialEmail(12)");
    _leads.Verify(x => x.MarkRedeemedAsync(
      It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  // ── Real MarketingLeadService wired through the same sign-up paths ──
  // One lookup (GetUnredeemedOfferDaysAsync) and one redeem path (MarkRedeemedAsync) serve both
  // the legacy 60-day promise and the current 30-day popup offer.

  private static readonly DateTime LeadCutoff = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

  private void UseRealLeadService()
  {
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["Subscription:TrialPeriodDays"] = "14",
      ["Subscription:ExtendedTrialPeriodDays"] = "30",
    }).Build();
    var real = new MarketingLeadService(
      _context, _email.Object, config, Mock.Of<ILogger<MarketingLeadService>>());
    _leads.Setup(x => x.GetUnredeemedOfferDaysAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .Returns((string e, CancellationToken ct) => real.GetUnredeemedOfferDaysAsync(e, ct));
    _leads.Setup(x => x.MarkRedeemedAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
      .Returns((string e, int u, CancellationToken ct) => real.MarkRedeemedAsync(e, u, ct));
  }

  private async Task AddLeadAsync(string path, string email, int offeredDays, DateTime createdAt, DateTime? redeemedAt = null)
  {
    _context.MarketingLeads.Add(new MarketingLead
    {
      Email = email,
      OfferedTrialDays = offeredDays,
      UnsubscribeToken = "lead-token-" + path,
      RedeemedAt = redeemedAt,
      CreatedAt = createdAt,
      UpdatedAt = createdAt,
    });
    await _context.SaveChangesAsync();
  }

  private int CreatedTrialDays()
  {
    var created = (User)_users.Invocations.Single(i => i.Method.Name == "CreateAsync").Arguments[0];
    return (created.TrialEndDate!.Value - created.TrialStartDate!.Value).Days;
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task DefaultSignUp_WithNoLead_Gets14Days(string path)
  {
    UseRealLeadService();
    SetTrialDays(14);

    await RunPathAsync(path);

    _events.Should().Equal(
      "create",
      "provision(days=14,grantRecorded=True)",
      "prefs",
      "trialEmail(14)");
    CreatedTrialDays().Should().Be(14);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task PostCutoffLead_Gets30Days_AndIsRedeemed(string path)
  {
    UseRealLeadService();
    SetTrialDays(14);
    var email = path + "-new-lead@example.com";
    await AddLeadAsync(path, email, 30, LeadCutoff.AddDays(2));

    await RunPathAsync(path, email);

    _events.Should().Equal(
      "create",
      "provision(days=30,grantRecorded=True)",
      "prefs",
      "trialEmail(30)");
    CreatedTrialDays().Should().Be(30);
    var lead = _context.MarketingLeads.Single();
    lead.RedeemedAt.Should().NotBeNull();
    lead.RedeemedByUserId.Should().Be(777);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task PostCutoffLead_WithAnInflatedOffer_IsCappedAt30(string path)
  {
    UseRealLeadService();
    SetTrialDays(14);
    var email = path + "-inflated-lead@example.com";
    await AddLeadAsync(path, email, 60, LeadCutoff.AddDays(2));

    await RunPathAsync(path, email);

    CreatedTrialDays().Should().Be(30);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task PreCutoffLead_PromisedSixtyDays_Gets60_AndIsRedeemed(string path)
  {
    UseRealLeadService();
    SetTrialDays(14);
    var email = path + "-legacy-lead@example.com";
    await AddLeadAsync(path, email, 60, LeadCutoff.AddDays(-5));

    await RunPathAsync(path, email);

    _events.Should().Equal(
      "create",
      "provision(days=60,grantRecorded=True)",
      "prefs",
      "trialEmail(60)");
    CreatedTrialDays().Should().Be(60);
    var lead = _context.MarketingLeads.Single();
    lead.RedeemedAt.Should().NotBeNull();
    lead.RedeemedByUserId.Should().Be(777);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task PreCutoffLead_WithAnInflatedOffer_IsCappedAt60(string path)
  {
    UseRealLeadService();
    SetTrialDays(14);
    var email = path + "-legacy-inflated@example.com";
    await AddLeadAsync(path, email, 90, LeadCutoff.AddDays(-5));

    await RunPathAsync(path, email);

    CreatedTrialDays().Should().Be(60);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task RedeemedLead_Gets14Days(string path)
  {
    UseRealLeadService();
    SetTrialDays(14);
    var email = path + "-redeemed-lead@example.com";
    await AddLeadAsync(path, email, 60, LeadCutoff.AddDays(-5), redeemedAt: LeadCutoff.AddDays(-2));

    await RunPathAsync(path, email);

    CreatedTrialDays().Should().Be(14);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task Lead_ForEmailThatAlreadyUsedItsTrial_StillGetsThePaywall(string path)
  {
    UseRealLeadService();
    SetTrialDays(14);
    var email = path + "-used-trial-lead@example.com";
    await AddLeadAsync(path, email, 30, LeadCutoff.AddDays(2));
    _context.TrialHistories.Add(new TrialHistory
    {
      EmailHash = TrialEmailHasher.Compute(email)!,
      FirstTrialStartedAt = DateTime.UtcNow.AddDays(-90),
      OriginalTrialEndsAt = DateTime.UtcNow.AddDays(-60),
      LastTrialGrantedAt = DateTime.UtcNow.AddDays(-90),
      TrialGrantCount = 1,
      FirstSource = "email",
      CreatedAt = DateTime.UtcNow.AddDays(-90),
      UpdatedAt = DateTime.UtcNow.AddDays(-90),
    });
    await _context.SaveChangesAsync();

    var response = await RunPathAsync(path, email);

    response.Should().NotBeNull();
    _events.Should().NotContain(e => e.StartsWith("provision("));
    _events.Should().NotContain(e => e.StartsWith("trialEmail("));
    response!.User.RequiresPaidPlan.Should().BeTrue("a used trial is paywalled, a lead offer does not resurrect it");
    _context.MarketingLeads.Single().RedeemedAt.Should().BeNull();
  }

  [Theory]
  [InlineData("email")]
  [InlineData("google")]
  [InlineData("apple")]
  public async Task SelfHost_ReRegisteringAfterTheTrialLapsed_IsNeverPaywalled(string path)
  {
    SetTrialDays(14);
    _config.Setup(c => c["BuoyMoney:Edition"]).Returns("selfhost");
    var email = path + "-selfhost@example.com";
    _context.TrialHistories.Add(new TrialHistory
    {
      EmailHash = TrialEmailHasher.Compute(email)!,
      FirstTrialStartedAt = DateTime.UtcNow.AddDays(-90),
      OriginalTrialEndsAt = DateTime.UtcNow.AddDays(-60),
      LastTrialGrantedAt = DateTime.UtcNow.AddDays(-90),
      TrialGrantCount = 1,
      FirstSource = "email",
      CreatedAt = DateTime.UtcNow.AddDays(-90),
      UpdatedAt = DateTime.UtcNow.AddDays(-90),
    });
    await _context.SaveChangesAsync();

    var response = await RunPathAsync(path, email);

    response.Should().NotBeNull();
    response!.User.RequiresPaidPlan.Should().BeFalse("self-host has no billing page to escape a paywall");
    response.User.SubscriptionTier.Should().Be("Premium");
  }

  // ── Cloud-only sign-up (started on the Cloud portal, buoy-ai.com/cloud/) ──

  private Task<LoginResponse?> RunPathWithContextAsync(string path, string? signupContext, bool stripeUp = true)
  {
    _provisioning.Setup(x => x.EnsureStripeCustomerAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
      .Callback<int, CancellationToken>((_, _) => _events.Add("customer"))
      .ReturnsAsync(stripeUp);
    switch (path)
    {
      case "email":
        return _auth.RegisterAsync(new RegisterRequest
        {
          Email = "cloud-new@example.com", Password = "Zx7!kQ2#mWp9", FirstName = "New", LastName = "User",
          SignupContext = signupContext
        });
      case "google":
        _google.Setup(v => v.ValidateAsync(It.IsAny<string>())).ReturnsAsync(new GoogleTokenPayload
        {
          Subject = "g-sub-cloud", Email = "cloud-google@example.com", EmailVerified = true, GivenName = "New", FamilyName = "Person"
        });
        return _auth.GoogleLoginAsync(new GoogleLoginRequest { IdToken = "t", IsRegistration = true, SignupContext = signupContext });
      default:
        _apple.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(new AppleTokenPayload
        {
          Subject = "a-sub-cloud", Email = "cloud-apple@example.com", EmailVerified = true
        });
        return _auth.AppleLoginAsync(new AppleLoginRequest { IdentityToken = "t", Nonce = "n", SignupContext = signupContext });
    }
  }

  private User CreatedUser() => (User)_users.Invocations.Single(i => i.Method.Name == "CreateAsync").Arguments[0];

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task CloudSignup_SkipsTheTrial_ButCreatesTheStripeCustomer(string path)
  {
    SetTrialDays(14);
    OfferLead(30);

    var response = await RunPathWithContextAsync(path, "cloud");

    response.Should().NotBeNull();
    response!.IsNewUser.Should().BeTrue();
    var created = CreatedUser();
    created.CloudOnly.Should().BeTrue();
    created.IsTrialActive.Should().BeFalse();
    created.TrialEndDate.Should().BeNull();
    created.HasUsedTrial.Should().BeFalse("the email keeps its one hosted trial for later");
    _context.TrialHistories.Should().BeEmpty();
    _provisioning.Verify(x => x.ProvisionStripeTrialAsync(
      It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    _provisioning.Verify(x => x.EnsureStripeCustomerAsync(777, It.IsAny<CancellationToken>()), Times.Once);
    _leads.Verify(x => x.MarkRedeemedAsync(
      It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task CloudSignup_SendsNoHostedWelcomeOrTrialEmail_ButKeepsEmailPreferences(string path)
  {
    SetTrialDays(14);

    await RunPathWithContextAsync(path, "cloud");

    _events.Should().Equal("create", "customer", "prefs");
    _email.Verify(x => x.SendTrialInvitationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    _email.Verify(x => x.SendWelcomeEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task CloudSignup_OpeningTheHostedAppLater_SeesTheChoosePlanScreen_NotATrial(string path)
  {
    SetTrialDays(14);

    var response = await RunPathWithContextAsync(path, "CLOUD ");

    CreatedUser().RequiresPaidPlan.Should().BeTrue();
    response!.User.SubscriptionTier.Should().Be("Free");
    response.User.TrialEndsAt.Should().BeNull();
  }

  [Theory]
  [MemberData(nameof(Paths))]
  public async Task CloudSignup_WhenStripeIsDown_StillCreatesTheAccount(string path)
  {
    SetTrialDays(14);

    var response = await RunPathWithContextAsync(path, "cloud", stripeUp: false);

    response.Should().NotBeNull();
    CreatedUser().CloudOnly.Should().BeTrue();
  }

  [Theory]
  [InlineData("email", null)]
  [InlineData("email", "hosted")]
  [InlineData("google", null)]
  [InlineData("google", "")]
  [InlineData("apple", null)]
  [InlineData("apple", "app")]
  public async Task NormalSignup_IsUnchanged_TrialAndNoCloudFlag(string path, string? signupContext)
  {
    SetTrialDays(14);

    await RunPathWithContextAsync(path, signupContext);

    var created = CreatedUser();
    created.CloudOnly.Should().BeFalse();
    created.IsTrialActive.Should().BeTrue();
    created.RequiresPaidPlan.Should().BeFalse();
    _events.Should().Equal(
      "create",
      "redeem(grantRecorded=True)",
      "provision(days=14,grantRecorded=True)",
      "prefs",
      "trialEmail(14)");
    _provisioning.Verify(x => x.EnsureStripeCustomerAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
  }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
  private readonly List<string> _sink;
  public CapturingLogger(List<string> sink) => _sink = sink;
  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
  public bool IsEnabled(LogLevel logLevel) => true;
  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
    Func<TState, Exception?, string> formatter)
  {
    if (logLevel >= LogLevel.Warning)
      _sink.Add($"{logLevel}: {formatter(state, exception)} {exception}");
  }
}
