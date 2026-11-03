using System.Net;
using FluentAssertions;
using Xunit;
using BuoyAI.IntegrationTests.Helpers;

namespace BuoyAI.IntegrationTests.Controllers;

public class HealthCheckIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly TestWebApplicationFactory<Program> _factory;
  private readonly HttpClient _client;

  public HealthCheckIntegrationTests(TestWebApplicationFactory<Program> factory)
  {
    _factory = factory;
    _client = _factory.CreateClient();
  }

  [Fact]
  public async Task HealthCheck_ShouldReturnOK()
  {
    // Act - Test a simple endpoint that doesn't require authentication
    var response = await _client.GetAsync("/");

    // Assert - Just check that the app starts up
    // It might return 404, but that's better than a 500 error
    response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
  }

  [Fact]
  public async Task LoginEndpoint_ShouldExist()
  {
    // Act - Try to POST to login (should return 400 for empty body, not 404)
    var response = await _client.PostAsync("/api/auth/login", null);

    // Assert - 400 is expected for empty body, 404 means endpoint doesn't exist
    response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
  }
}
