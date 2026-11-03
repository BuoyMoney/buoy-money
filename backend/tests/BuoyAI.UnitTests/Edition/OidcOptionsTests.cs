using BuoyAI.Infrastructure.Configuration;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.EditionSetting;

/// <summary>The provider address carries signing keys and the token exchange, so plain http is
/// only acceptable to a provider on this machine.</summary>
public class OidcOptionsTests
{
  private static OidcOptions With(string authority) =>
    new() { Enabled = true, Authority = authority, ClientId = "buoy" };

  [Theory]
  [InlineData("https://id.home.test")]
  [InlineData("http://localhost:1411")]
  [InlineData("http://127.0.0.1:1411")]
  public void Configured_for_https_or_loopback(string authority) =>
    With(authority).IsConfigured.Should().BeTrue();

  [Theory]
  [InlineData("http://id.home.test")]
  [InlineData("http://192.168.1.20:1411")]
  [InlineData("http://auth.example.com")]
  [InlineData("not a url")]
  public void Not_configured_for_remote_plaintext_or_garbage(string authority) =>
    With(authority).IsConfigured.Should().BeFalse();
}
