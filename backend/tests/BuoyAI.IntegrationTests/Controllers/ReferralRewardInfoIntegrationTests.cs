using System.Net;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>
/// #941: the advertised referral credit is anonymous (the register page shows it
/// pre-signup) and only advertises a currency whose reward coupon actually exists,
/// falling back to the USD offer otherwise — the promise must match what the
/// reward sweep can grant.
/// </summary>
[Collection("IntegrationTest")]
public class ReferralRewardInfoIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly HttpClient _client;

  public ReferralRewardInfoIntegrationTests(TestWebApplicationFactory<Program> factory)
  {
    _client = factory.CreateClient();
  }

  [Fact]
  public async Task RewardInfo_IsAnonymous_AndDefaultsToUsd()
  {
    var response = await _client.GetAsync("/api/referral/reward-info");
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    var body = await response.Content.ReadAsStringAsync();
    body.Should().Contain("\"currency\":\"USD\"");
    body.Should().Contain("\"amount\":15");
  }

  [Fact]
  public async Task RewardInfo_UnconfiguredCurrency_FallsBackToUsd()
  {
    // CAD is a supported display currency, but no CAD reward coupon is configured
    // in the test environment, so the USD offer is what gets advertised.
    var response = await _client.GetAsync("/api/referral/reward-info?currency=cad");
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    (await response.Content.ReadAsStringAsync()).Should().Contain("\"currency\":\"USD\"");
  }

  [Fact]
  public async Task RewardInfo_GarbageCurrency_FallsBackToUsd()
  {
    var response = await _client.GetAsync("/api/referral/reward-info?currency=DOGE");
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    (await response.Content.ReadAsStringAsync()).Should().Contain("\"currency\":\"USD\"");
  }
}
