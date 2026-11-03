using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using BuoyAI.IntegrationTests.Helpers;
using BuoyAI.Core.DTOs.Auth;

namespace BuoyAI.IntegrationTests.Controllers;

public class LoginTokenClaimsTests : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public LoginTokenClaimsTests(TestWebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_ReturnsJwtWithIdentityClaimsAndFutureExpiry()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "test@BuoyAI.com",
            Password = "password123"
        });
        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        login!.Token.Should().NotBeNullOrEmpty();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.Token);
        jwt.Claims.Should().Contain(c => c.Type == "nameid" && !string.IsNullOrEmpty(c.Value));
        jwt.Claims.Should().Contain(c => c.Type == "unique_name" && !string.IsNullOrEmpty(c.Value));
        jwt.ValidTo.Should().BeAfter(DateTime.UtcNow);
    }
}
