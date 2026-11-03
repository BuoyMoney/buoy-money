using BuoyAI.API.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class DebugEndpointAvailabilityTests
{
  private static bool Available(string env, string? apiKey)
  {
    var host = new Mock<IHostEnvironment>();
    host.Setup(h => h.EnvironmentName).Returns(env);
    var cfg = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Debug:ApiKey"] = apiKey })
      .Build();
    return DebugEndpointAvailability.IsAvailable(host.Object, cfg);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("some-key-value")]
  public void Production_NeverExposesDebugRoutes(string? key) => Available("Production", key).Should().BeFalse();

  [Fact]
  public void Staging_NeedsAnApiKey()
  {
    Available("Staging", null).Should().BeFalse();
    Available("Staging", "").Should().BeFalse();
    Available("Staging", "Rt5Yu7Io9Pa1Sd3Fg5Hj").Should().BeTrue();
  }

  [Fact]
  public void Development_KeylessOnlyInDebugBuilds()
  {
    Available("Development", null).Should().Be(DebugEndpointAvailability.KeylessDevelopmentAllowed);
    Available("Development", "Rt5Yu7Io9Pa1Sd3Fg5Hj").Should().BeTrue();
  }
}
