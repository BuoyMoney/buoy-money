using BuoyAI.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Pins which Gemini key wins for each config combination. The environment is injected so the
/// tests never touch real process variables.
/// </summary>
public class GeminiApiKeyTests
{
  private static IConfiguration Config(string? apiKey)
  {
    var values = new Dictionary<string, string?>();
    if (apiKey != null) values["Gemini:ApiKey"] = apiKey;
    return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
  }

  private static Func<string, string?> Env(string? google = null, string? gemini = null) =>
    name => name switch
    {
      "GOOGLE_API_KEY" => google,
      "GEMINI_API_KEY" => gemini,
      _ => null,
    };

  [Fact]
  public void Google_env_var_beats_config_and_gemini_env_var()
  {
    GeminiApiKey.Resolve(Config("cfg"), Env("google", "gemini")).Should().Be("google");
  }

  [Fact]
  public void Config_wins_when_google_env_var_is_absent()
  {
    GeminiApiKey.Resolve(Config("cfg"), Env(null, "gemini")).Should().Be("cfg");
  }

  [Fact]
  public void Gemini_env_var_is_the_last_resort()
  {
    GeminiApiKey.Resolve(Config(null), Env(null, "gemini")).Should().Be("gemini");
  }

  [Fact]
  public void Hosted_shape_resolves_to_the_google_env_var()
  {
    // Production: GOOGLE_API_KEY from the deploy, Gemini:ApiKey shipped as "".
    GeminiApiKey.Resolve(Config(""), Env("prod-key")).Should().Be("prod-key");
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void Blank_google_env_var_falls_through_to_config(string blank)
  {
    GeminiApiKey.Resolve(Config("cfg"), Env(blank)).Should().Be("cfg");
  }

  [Fact]
  public void Blank_config_falls_through_to_the_gemini_env_var()
  {
    GeminiApiKey.Resolve(Config(""), Env(null, "gemini")).Should().Be("gemini");
  }

  [Fact]
  public void Nothing_configured_resolves_to_empty_not_null()
  {
    GeminiApiKey.Resolve(Config(null), Env()).Should().BeEmpty();
    GeminiApiKey.Resolve(Config(""), Env("", " ")).Should().BeEmpty();
  }

  // The two lookups below keep two features off on hosted. They must not change by accident.

  [Fact]
  public void Memory_lookup_ignores_environment_variables()
  {
    GeminiApiKey.ResolveConfigOnly(Config("cfg")).Should().Be("cfg");
    GeminiApiKey.ResolveConfigOnly(Config(null)).Should().BeEmpty();
    GeminiApiKey.ResolveConfigOnly(Config("")).Should().BeEmpty();
  }

  [Fact]
  public void Prompt_cache_lookup_keeps_a_present_but_empty_config_value_on_hosted()
  {
    // Hosted: Gemini:ApiKey is "" and GOOGLE_API_KEY is set. The old null-coalesce stopped at "",
    // so the cache had no key and stayed off.
    GeminiApiKey.ResolveConfigFirstNullCoalesced(Config(""), Env("prod-key")).Should().BeEmpty();
  }

  [Fact]
  public void Prompt_cache_lookup_falls_back_to_environment_only_when_config_is_missing()
  {
    GeminiApiKey.ResolveConfigFirstNullCoalesced(Config(null), Env("google", "gemini")).Should().Be("google");
    GeminiApiKey.ResolveConfigFirstNullCoalesced(Config(null), Env(null, "gemini")).Should().Be("gemini");
    GeminiApiKey.ResolveConfigFirstNullCoalesced(Config("cfg"), Env("google")).Should().Be("cfg");
  }
}
