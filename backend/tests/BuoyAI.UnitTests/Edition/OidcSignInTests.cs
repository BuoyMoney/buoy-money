using BuoyAI.Core.Entities;
using BuoyAI.Core.Exceptions;
using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.Oidc;
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
/// Single sign-on (OpenID Connect) on a self-hosted server, end to end through the service against a
/// fake provider: start, provider callback, one-time code exchange, account linking and refusals.
/// </summary>
public class OidcSignInTests : IDisposable
{
  private const string Origin = "https://buoy.home.test";
  private readonly FakeOidcProvider _provider = new();

  public void Dispose() => _provider.Dispose();

  private ServiceProvider Build(Dictionary<string, string?>? extra = null)
  {
    var values = new Dictionary<string, string?>
    {
      ["Jwt:Key"] = "your-super-secret-jwt-key-that-should-be-at-least-32-characters-long",
      ["Jwt:Issuer"] = "BuoyAI",
      ["Jwt:Audience"] = "BuoyAI-Users",
      ["Encryption:Key"] = "test-encryption-key-for-unit-tests",
      ["BuoyMoney:Edition"] = "selfhost",
      ["App:BaseUrl"] = Origin,
      ["Oidc:Enabled"] = "true",
      ["Oidc:Authority"] = FakeOidcProvider.Issuer,
      ["Oidc:ClientId"] = FakeOidcProvider.ClientId,
      ["Oidc:ClientSecret"] = FakeOidcProvider.ClientSecret,
      ["Oidc:DisplayName"] = "Pocket ID",
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
    services.AddHttpClient(OidcProviderClient.HttpClientName)
      .ConfigurePrimaryHttpMessageHandler(() => _provider)
      .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
    return services.BuildServiceProvider();
  }

  private static async Task<User> SeedUserAsync(IServiceProvider sp, string email, int id = 10)
  {
    var db = sp.GetRequiredService<BuoyAIDbContext>();
    var user = new User
    {
      Id = id, Username = email.Split('@')[0], Email = email, PasswordHash = AuthService.HashPassword("Zx7!kQ2#mWp9"),
      IsEmailVerified = true, Currency = "USD", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return user;
  }

  /// <summary>Runs start and callback like a browser would; returns the redirect back to the app as query values.</summary>
  private async Task<(string Location, Dictionary<string, string> Query)> SignInAsync(
    OidcLoginService svc, string platform = "web", string? clientChallenge = null, string? invite = null)
  {
    var authorize = await svc.StartAsync(platform, null, invite, clientChallenge, Origin);
    var state = _provider.Remember(authorize);
    var back = await svc.CallbackAsync(state, "provider-code", null, Origin);
    return (back, FakeOidcProvider.ParseQuery(new Uri(back).Query));
  }

  [Fact]
  public async Task Start_BuildsAnAuthorizeUrl_WithStateNoncePkceAndOurCallback()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var url = await svc.StartAsync("web", null, null, null, Origin);

    url.Should().StartWith($"{FakeOidcProvider.Issuer}/authorize?");
    var q = FakeOidcProvider.ParseQuery(new Uri(url).Query);
    q["response_type"].Should().Be("code");
    q["client_id"].Should().Be(FakeOidcProvider.ClientId);
    q["redirect_uri"].Should().Be($"{Origin}/api/auth/oidc/callback");
    q["scope"].Should().Be("openid email profile");
    q["code_challenge_method"].Should().Be("S256");
    q["state"].Should().NotBeNullOrEmpty();
    q["nonce"].Should().NotBeNullOrEmpty();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    (await db.OidcLoginRequests.SingleAsync()).SecretHash.Should().NotBe(q["state"], "only a hash of the state is stored");
  }

  [Fact]
  public async Task Callback_ExistingUser_LinksTheAccountAndHandsTheAppAOneTimeCode()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    var user = await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (location, q) = await SignInAsync(svc);

    location.Should().StartWith($"{Origin}/app/auth/oidc?code=");
    q.Should().NotContainKey("error");
    _provider.LastTokenAuthorization.Should().StartWith("Basic ", "a confidential client sends its secret with client_secret_basic");
    _provider.LastTokenForm.Should().Contain("code_verifier=");
    var resolved = await svc.ExchangeAsync(q["code"], null);
    resolved.UserId.Should().Be(user.Id);
    resolved.IsNewUser.Should().BeFalse();
    var link = await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().UserExternalLogins.SingleAsync();
    link.Issuer.Should().Be(FakeOidcProvider.Issuer);
    link.Subject.Should().Be("pocket-user-1");
    link.UserId.Should().Be(user.Id);
  }

  [Fact]
  public async Task Exchange_ThenIssueLogin_GivesTheNormalTokenAnswer()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var (_, q) = await SignInAsync(svc);
    var resolved = await svc.ExchangeAsync(q["code"], null);

    var login = await scope.ServiceProvider.GetRequiredService<BuoyAI.Core.Interfaces.IAuthService>()
      .IssueLoginForUserAsync(resolved.UserId, resolved.IsNewUser);

    login.Should().NotBeNull();
    login!.Token.Should().NotBeNullOrEmpty();
    login.User.Email.Should().Be("owner@home.test");
  }

  [Fact]
  public async Task Callback_UnverifiedEmail_IsRefused()
  {
    _provider.EmailVerified = false;
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);

    q["error"].Should().Be("email_unverified");
    q.Should().NotContainKey("code");
    (await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().UserExternalLogins.AnyAsync()).Should().BeFalse();
  }

  [Fact]
  public async Task Callback_NoEmailVerifiedClaim_CountsAsUnverified()
  {
    _provider.EmailVerified = null;
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);

    q["error"].Should().Be("email_unverified");
  }

  [Fact]
  public async Task Callback_NoEmailVerifiedClaim_IsAcceptedWhenTheOperatorTrustsTheProvider()
  {
    _provider.EmailVerified = null;
    await using var sp = Build(new() { ["Oidc:TrustUnverifiedEmail"] = "true" });
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);

    q.Should().ContainKey("code");
  }

  [Fact]
  public async Task Callback_WrongNonce_IsRefused()
  {
    _provider.NonceOverride = "someone-elses-nonce";
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);

    q["error"].Should().Be("invalid_response");
  }

  [Fact]
  public async Task Callback_WrongAudience_IsRefused()
  {
    _provider.AudienceOverride = "another-app";
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);

    q["error"].Should().Be("invalid_response");
  }

  [Fact]
  public async Task Callback_WrongIssuer_IsRefused()
  {
    _provider.IssuerOverride = "https://evil.test";
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);

    q["error"].Should().Be("invalid_response");
  }

  [Fact]
  public async Task Callback_UnknownState_IsRefusedWithoutCallingTheProvider()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var back = await svc.CallbackAsync("made-up-state", "provider-code", null, Origin);

    FakeOidcProvider.ParseQuery(new Uri(back).Query)["error"].Should().Be("expired");
    _provider.TokenRequests.Should().Be(0);
  }

  [Fact]
  public async Task Callback_StateIsSingleUse()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var state = _provider.Remember(await svc.StartAsync("web", null, null, null, Origin));
    await svc.CallbackAsync(state, "provider-code", null, Origin);

    var again = await svc.CallbackAsync(state, "provider-code", null, Origin);

    FakeOidcProvider.ParseQuery(new Uri(again).Query)["error"].Should().Be("expired");
  }

  [Fact]
  public async Task Callback_ProviderSaysDenied_SendsTheAppBackWithDenied()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var state = _provider.Remember(await svc.StartAsync("web", null, null, null, Origin));

    var back = await svc.CallbackAsync(state, null, "access_denied", Origin);

    FakeOidcProvider.ParseQuery(new Uri(back).Query)["error"].Should().Be("denied");
  }

  [Fact]
  public async Task AutoRegisterOff_UnknownEmail_IsRefused_EvenWithSignUpOpen()
  {
    _provider.Email = "stranger@home.test";
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    await scope.ServiceProvider.GetRequiredService<RegistrationGate>().SetOpenAsync(true);
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);

    q["error"].Should().Be("no_account");
    (await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.CountAsync()).Should().Be(1);
  }

  [Fact]
  public async Task AutoRegisterOff_TheAdminEmail_CreatesTheFirstAccount()
  {
    _provider.Email = "owner@home.test";
    await using var sp = Build(new() { ["Admin:Emails:0"] = "owner@home.test" });
    using var scope = sp.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);
    var resolved = await svc.ExchangeAsync(q["code"], null);

    resolved.IsNewUser.Should().BeTrue();
    var user = await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.SingleAsync();
    user.IsInstanceAdmin.Should().BeTrue();
    user.IsEmailVerified.Should().BeTrue();
    user.FirstName.Should().Be("Pat");
    user.PasswordHash.Should().BeEmpty();
  }

  [Fact]
  public async Task AutoRegisterOn_ClosedServer_StillRefusesAStranger()
  {
    _provider.Email = "stranger@home.test";
    await using var sp = Build(new() { ["Oidc:AutoRegister"] = "true" });
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);

    q["error"].Should().Be("registration_closed");
  }

  [Fact]
  public async Task AutoRegisterOn_OpenServer_CreatesTheAccount()
  {
    _provider.Email = "member@home.test";
    await using var sp = Build(new() { ["Oidc:AutoRegister"] = "true" });
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    await scope.ServiceProvider.GetRequiredService<RegistrationGate>().SetOpenAsync(true);
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var (_, q) = await SignInAsync(svc);
    var resolved = await svc.ExchangeAsync(q["code"], null);

    resolved.IsNewUser.Should().BeTrue();
    (await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().Users.CountAsync()).Should().Be(2);
  }

  [Fact]
  public async Task SubjectLink_SurvivesAnEmailChangeAtTheProvider()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    var user = await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var (_, first) = await SignInAsync(svc);
    await svc.ExchangeAsync(first["code"], null);

    _provider.Email = "renamed@elsewhere.test";
    var (_, second) = await SignInAsync(svc);
    var resolved = await svc.ExchangeAsync(second["code"], null);

    resolved.UserId.Should().Be(user.Id);
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    (await db.Users.CountAsync()).Should().Be(1, "no second account for the new email");
    (await db.UserExternalLogins.SingleAsync()).Email.Should().Be("renamed@elsewhere.test");
  }

  [Fact]
  public async Task SubjectLink_AnotherProviderAccountWithTheSameEmailStillLinksByVerifiedEmail()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    var user = await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var (_, first) = await SignInAsync(svc);
    await svc.ExchangeAsync(first["code"], null);

    _provider.Subject = "pocket-user-2";
    var (_, second) = await SignInAsync(svc);

    (await svc.ExchangeAsync(second["code"], null)).UserId.Should().Be(user.Id);
    (await scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>().UserExternalLogins.CountAsync()).Should().Be(2);
  }

  [Fact]
  public async Task Exchange_CodeIsSingleUse()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var (_, q) = await SignInAsync(svc);
    await svc.ExchangeAsync(q["code"], null);

    var again = () => svc.ExchangeAsync(q["code"], null);

    (await again.Should().ThrowAsync<OidcLoginException>()).Which.Code.Should().Be("expired");
  }

  [Fact]
  public async Task Exchange_CodeExpiresAfterItsMinute()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var (_, q) = await SignInAsync(svc);
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var row = await db.OidcLoginRequests.SingleAsync(r => r.Kind == OidcLoginRequest.KindExchange);
    (row.ExpiresAt - row.CreatedAt).Should().BeCloseTo(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1));
    row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
    await db.SaveChangesAsync();

    var late = () => svc.ExchangeAsync(q["code"], null);

    (await late.Should().ThrowAsync<OidcLoginException>()).Which.Code.Should().Be("expired");
  }

  [Fact]
  public async Task Mobile_RequiresTheAppChallenge()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();

    var start = () => svc.StartAsync("mobile", null, null, null, Origin);

    (await start.Should().ThrowAsync<OidcLoginException>()).Which.Code.Should().Be("invalid_request");
  }

  [Fact]
  public async Task Mobile_GoesBackThroughTheDeepLink_AndTheCodeNeedsTheAppVerifier()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    var user = await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var verifier = new string('v', 50);

    var (location, q) = await SignInAsync(svc, "mobile", OidcLoginService.S256(verifier));

    location.Should().StartWith("com.buoyai.buoymoney://oidc?code=");
    (await svc.ExchangeAsync(q["code"], verifier)).UserId.Should().Be(user.Id);
  }

  [Fact]
  public async Task Mobile_ACodeCaughtByAnotherApp_IsUselessWithoutTheVerifier()
  {
    await using var sp = Build();
    using var scope = sp.CreateScope();
    await SeedUserAsync(scope.ServiceProvider, "owner@home.test");
    var svc = scope.ServiceProvider.GetRequiredService<OidcLoginService>();
    var (_, q) = await SignInAsync(svc, "mobile", OidcLoginService.S256(new string('v', 50)));

    var steal = () => svc.ExchangeAsync(q["code"], new string('x', 50));

    (await steal.Should().ThrowAsync<OidcLoginException>()).Which.Code.Should().Be("expired");
  }

  [Theory]
  [InlineData("/budgets", "/budgets")]
  [InlineData("//evil.test/x", null)]
  [InlineData("https://evil.test", null)]
  [InlineData("/\\evil.test", null)]
  [InlineData("budgets", null)]
  public void SafeReturnPath_KeepsOnlyLocalPaths(string input, string? expected)
  {
    OidcLoginService.SafeReturnPath(input).Should().Be(expected);
  }
}
