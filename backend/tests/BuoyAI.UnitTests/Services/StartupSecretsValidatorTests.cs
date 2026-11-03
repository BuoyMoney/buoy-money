using BuoyAI.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class StartupSecretsValidatorTests
{
  private const string GoodJwt = "q3Zp8vN2xK7mR5tY1wB9cD4fG6hJ0aLsE8uI3oP5";
  private const string GoodEnc = "Zx9Lk2Mv7Qw4Er1Ty6Ui8Op3As5Df0GhJk2Lz";

  private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
      .Build();

  [Fact]
  public void GoodKeys_Pass_WithNoWarnings()
  {
    var r = StartupSecretsValidator.Validate(Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc)), "Production");

    r.IsValid.Should().BeTrue();
    r.Warnings.Should().BeEmpty();
  }

  [Theory]
  [InlineData("YOUR_JWT_SECRET_KEY_AT_LEAST_32_CHARACTERS_LONG")]
  [InlineData("your-secure-jwt-signing-key-must-be-at-least-32-characters-long-for-security")]
  [InlineData("CHANGE_ME_CHANGE_ME_CHANGE_ME_CHANGE_ME")]
  [InlineData("replace-me-with-a-real-secret-value-please")]
  [InlineData("<put-a-secret-here-of-32-characters>")]
  [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
  public void PlaceholderJwtKey_IsRefused(string jwt)
  {
    var r = StartupSecretsValidator.Validate(Config(("Jwt:Key", jwt), ("Encryption:Key", GoodEnc)), "Development");

    r.IsValid.Should().BeFalse();
    r.Errors.Should().ContainSingle().Which.Should().Contain("Jwt:Key");
  }

  [Fact]
  public void NonAsciiSigningKey_IsRefused()
  {
    var jwt = "q3Zp8vN2xK7mR5tY1wB9cD4fG6hJ0aLséééé";
    var r = StartupSecretsValidator.Validate(Config(("Jwt:Key", jwt), ("Encryption:Key", GoodEnc)), "Production");

    r.IsValid.Should().BeFalse();
    r.Errors.Should().ContainSingle().Which.Should().Contain("ASCII");
  }

  [Fact]
  public void NonAsciiEncryptionKey_IsRefused()
  {
    var enc = "Zx9Lk2Mv7Qw4Er1Ty6Ui8Op3As5Df0Ghéééé";
    var r = StartupSecretsValidator.Validate(Config(("Jwt:Key", GoodJwt), ("Encryption:Key", enc)), "Production");

    r.IsValid.Should().BeFalse();
    r.Errors.Should().ContainSingle().Which.Should().Contain("ASCII");
  }

  [Theory]
  [InlineData("YOUR_32_CHARACTER_ENCRYPTION_KEY")]
  [InlineData("your-32-character-encryption-key")]
  public void PlaceholderEncryptionKey_IsRefused(string enc)
  {
    var r = StartupSecretsValidator.Validate(Config(("Jwt:Key", GoodJwt), ("Encryption:Key", enc)), "Development");

    r.Errors.Should().ContainSingle().Which.Should().Contain("Encryption:Key");
  }

  [Fact]
  public void MissingKeys_AreRefused()
  {
    var r = StartupSecretsValidator.Validate(Config(("Jwt:Key", ""), ("Encryption:Key", null)), "Development");

    r.Errors.Should().HaveCount(2);
  }

  [Fact]
  public void ShortJwtKey_IsRefused()
  {
    var r = StartupSecretsValidator.Validate(Config(("Jwt:Key", "Zx9Lk2Mv7Qw4Er1Ty6U"), ("Encryption:Key", GoodEnc)), "Development");

    r.Errors.Should().ContainSingle().Which.Should().Contain("at least 32");
  }

  [Fact]
  public void EncryptionKey_Under16_IsRefused_16To31_WarnsOnly()
  {
    var tooShort = StartupSecretsValidator.Validate(Config(("Jwt:Key", GoodJwt), ("Encryption:Key", "Zx9Lk2Mv7Qw4")), "Production");
    var shortish = StartupSecretsValidator.Validate(Config(("Jwt:Key", GoodJwt), ("Encryption:Key", "Zx9Lk2Mv7Qw4Er1Ty6Ui8Op3")), "Production");

    tooShort.IsValid.Should().BeFalse();
    shortish.IsValid.Should().BeTrue();
    shortish.Warnings.Should().ContainSingle().Which.Should().Contain("Encryption:Key");
  }

  [Theory]
  [InlineData("Production")]
  [InlineData("Staging")]
  public void KnownDevKey_IsRefusedInProductionAndStaging_ButNotDevelopment(string env)
  {
    const string devKey = "letsagoitsameamarioyahookoopatroopagoombabowserdonkeykong";
    var cfg = Config(("Jwt:Key", devKey), ("Encryption:Key", GoodEnc));

    StartupSecretsValidator.Validate(cfg, env).IsValid.Should().BeFalse();
    StartupSecretsValidator.Validate(cfg, "Development").IsValid.Should().BeTrue();
  }

  [Fact]
  public void McpSigningKey_Absent_FallsBackToJwtKey()
  {
    var cfg = Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc));

    StartupSecretsValidator.ResolveMcpSigningKey(cfg).Should().Be(GoodJwt);
  }

  [Fact]
  public void McpSigningKey_Present_IsUsed()
  {
    var cfg = Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc), ("Mcp:SigningKey", GoodEnc));

    StartupSecretsValidator.ResolveMcpSigningKey(cfg).Should().Be(GoodEnc);
  }

  [Fact]
  public void McpEnabled_WithoutOwnKey_Warns_AndWithOwnKey_DoesNot()
  {
    var without = StartupSecretsValidator.Validate(
      Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc), ("Mcp:Enabled", "true")), "Production");
    var with = StartupSecretsValidator.Validate(
      Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc), ("Mcp:Enabled", "true"), ("Mcp:SigningKey", "Rt5Yu7Io9Pa1Sd3Fg5Hj7Kl9Zx2Cv4Bn6Mq8We0")), "Production");

    without.IsValid.Should().BeTrue();
    without.Warnings.Should().ContainSingle().Which.Should().Contain("Mcp:SigningKey");
    with.Warnings.Should().BeEmpty();
  }

  [Fact]
  public void McpSigningKey_PlaceholderOrShort_IsRefused()
  {
    var placeholder = StartupSecretsValidator.Validate(
      Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc), ("Mcp:SigningKey", "YOUR_MCP_SIGNING_KEY_PLACEHOLDER_VALUE")), "Production");
    var shortKey = StartupSecretsValidator.Validate(
      Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc), ("Mcp:SigningKey", "Rt5Yu7Io9Pa1")), "Production");

    placeholder.IsValid.Should().BeFalse();
    shortKey.IsValid.Should().BeFalse();
  }

  [Fact]
  public void DebugApiKey_TooShort_IsRefused()
  {
    var result = StartupSecretsValidator.Validate(
      Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc), ("Debug:ApiKey", "abcdefgh")), "Staging");

    result.IsValid.Should().BeFalse();
  }

  [Fact]
  public void DebugApiKey_Placeholder_IsRefused_RealOrEmptyIsFine()
  {
    var bad = StartupSecretsValidator.Validate(
      Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc), ("Debug:ApiKey", "your-debug-key")), "Staging");
    var ok = StartupSecretsValidator.Validate(
      Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc), ("Debug:ApiKey", "Rt5Yu7Io9Pa1Sd3Fg5Hj")), "Staging");
    var none = StartupSecretsValidator.Validate(Config(("Jwt:Key", GoodJwt), ("Encryption:Key", GoodEnc)), "Staging");

    bad.IsValid.Should().BeFalse();
    ok.IsValid.Should().BeTrue();
    none.IsValid.Should().BeTrue();
  }

  [Fact]
  public void ValidateOrThrow_ThrowsWithEveryProblem_AndReportsWarnings()
  {
    var warnings = new List<string>();
    var act = () => StartupSecretsValidator.ValidateOrThrow(
      Config(("Jwt:Key", "YOUR_JWT_SECRET_KEY_AT_LEAST_32_CHARACTERS_LONG"), ("Encryption:Key", "YOUR_32_CHARACTER_ENCRYPTION_KEY")),
      "Development", warnings.Add);

    act.Should().Throw<InvalidOperationException>()
      .Where(e => e.Message.Contains("Jwt:Key") && e.Message.Contains("Encryption:Key"));
  }
}
