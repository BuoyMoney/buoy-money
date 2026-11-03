using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Covers the audience-pinning configuration for Google ID-token validation. The
/// actual signature/audience enforcement is delegated to the Google SDK via
/// <c>ValidationSettings.Audience</c>; these tests lock in which audiences we feed
/// it: the explicit override, the deployed <c>GoogleAuth:ClientId</c> (the token
/// audience of every real login), and the baked-in public default — while the
/// server-side-only <c>Google:ClientId</c> is never trusted as an audience.
/// </summary>
public class GoogleTokenValidatorTests
{
  private static IConfiguration Config(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();

  [Fact]
  public void ResolveAllowedAudiences_IncludesExplicitOverrides_AndDefault()
  {
    var cfg = Config(new Dictionary<string, string?>
    {
      ["Google:AllowedAudiences:0"] = "web-client",
      ["Google:AllowedAudiences:1"] = "ios-client",
    });

    var result = GoogleTokenValidator.ResolveAllowedAudiences(cfg);

    result.Should().Contain("web-client");
    result.Should().Contain("ios-client");
    // The public default stays pinned so source/local-dev builds keep working.
    result.Should().Contain(GoogleTokenValidator.DefaultWebClientId);
  }

  [Fact]
  public void ResolveAllowedAudiences_UsesPublicDefault_WhenNoConfig()
  {
    var result = GoogleTokenValidator.ResolveAllowedAudiences(Config(new Dictionary<string, string?>()));

    result.Should().BeEquivalentTo(new[] { GoogleTokenValidator.DefaultWebClientId, GoogleTokenValidator.DefaultIosClientId });
  }

  [Fact]
  public void ResolveAllowedAudiences_TrustsDeployedGoogleAuthClientId()
  {
    // Regression guard: the deployed Flutter apps are compiled with the SAME client
    // ID the backend holds as GoogleAuth:ClientId, so it is the `aud` of every real
    // token. If it isn't trusted, every deployed Google login fails with
    // "JWT contains untrusted 'aud' claim". (This was the staging/prod login bug.)
    var cfg = Config(new Dictionary<string, string?>
    {
      ["GoogleAuth:ClientId"] = "841207882659-deployed.apps.googleusercontent.com",
    });

    var result = GoogleTokenValidator.ResolveAllowedAudiences(cfg);

    result.Should().Contain("841207882659-deployed.apps.googleusercontent.com");
    result.Should().Contain(GoogleTokenValidator.DefaultWebClientId);
  }

  [Fact]
  public void ResolveAllowedAudiences_IgnoresServerSideGoogleClientId()
  {
    // Google:ClientId is the server-side OAuth client (used for code exchange), NOT a
    // token audience — it must never slip into the pinned set. Only GoogleAuth:ClientId
    // (the token-minting client) and explicit overrides count.
    var cfg = Config(new Dictionary<string, string?> { ["Google:ClientId"] = "backend-oauth-client" });

    var result = GoogleTokenValidator.ResolveAllowedAudiences(cfg);

    result.Should().NotContain("backend-oauth-client");
    result.Should().BeEquivalentTo(new[] { GoogleTokenValidator.DefaultWebClientId, GoogleTokenValidator.DefaultIosClientId });
  }

  [Fact]
  public void ResolveAllowedAudiences_DeduplicatesRepeatedClientIds()
  {
    // Override, deployed client ID, and default may overlap; the pinned set stays unique.
    var cfg = Config(new Dictionary<string, string?>
    {
      ["Google:AllowedAudiences:0"] = "shared-client",
      ["GoogleAuth:ClientId"] = "shared-client",
    });

    var result = GoogleTokenValidator.ResolveAllowedAudiences(cfg);

    result.Count(a => a == "shared-client").Should().Be(1);
  }

  [Fact]
  public void ResolveAllowedAudiences_AlwaysTrustsTheNativeIosClient()
  {
    // iOS mints ID tokens against GIDClientID, not the Dart serverClientId, so the native
    // client must be trusted even when a deploy configures only the web client.
    var cfg = Config(new Dictionary<string, string?> { ["GoogleAuth:ClientId"] = "deployed-web-client" });

    var result = GoogleTokenValidator.ResolveAllowedAudiences(cfg);

    result.Should().Contain(GoogleTokenValidator.DefaultIosClientId);
    result.Should().Contain("deployed-web-client");
  }

  [Fact]
  public async Task ValidateAsync_ReturnsNull_ForEmptyToken()
  {
    var validator = new GoogleTokenValidator(
      Config(new Dictionary<string, string?>()),
      NullLogger<GoogleTokenValidator>.Instance);

    var result = await validator.ValidateAsync("");

    result.Should().BeNull();
  }

  [Fact]
  public void SelfHost_TrustsOnlyTheOperatorsOwnClient_NotBuoyAIsHostedOnes()
  {
    var result = GoogleTokenValidator.ResolveAllowedAudiences(Config(new Dictionary<string, string?>
    {
      ["BuoyMoney:Edition"] = "selfhost",
      ["GoogleAuth:ClientId"] = "my-own-client",
    }));

    result.Should().BeEquivalentTo(new[] { "my-own-client" });
  }

  [Fact]
  public void SelfHost_WithNoClientConfigured_TrustsNothing()
  {
    GoogleTokenValidator.ResolveAllowedAudiences(Config(new Dictionary<string, string?> { ["BuoyMoney:Edition"] = "selfhost" }))
      .Should().BeEmpty();
  }
}
