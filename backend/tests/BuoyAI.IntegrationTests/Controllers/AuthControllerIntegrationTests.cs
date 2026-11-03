using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;
using BuoyAI.IntegrationTests.Helpers;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.IntegrationTests.Controllers;

public class AuthControllerIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly TestWebApplicationFactory<Program> _factory;
  private readonly HttpClient _client;

  public AuthControllerIntegrationTests(TestWebApplicationFactory<Program> factory)
  {
    _factory = factory;
    _client = _factory.CreateClient();
  }

  [Fact]
  public async Task Login_WithValidCredentials_ShouldReturnSuccessWithToken()
  {
    // Arrange
    var loginRequest = new LoginRequest
    {
      Email = "test@BuoyAI.com",
      Password = "password123"
    };

    // Act
    var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    
    var content = await response.Content.ReadAsStringAsync();
    var loginResponse = JsonConvert.DeserializeObject<LoginResponse>(content);
    
    loginResponse.Should().NotBeNull();
    loginResponse!.Token.Should().NotBeNullOrEmpty();
    loginResponse.User.Username.Should().Be("testuser");
    loginResponse.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
  }

  [Fact]
  public async Task Login_WithInvalidCredentials_ShouldReturnUnauthorized()
  {
    // Arrange
    var loginRequest = new LoginRequest
    {
      Email = "test@BuoyAI.com",
      Password = "wrongpassword"
    };

    // Act
    var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  [Fact]
  public async Task Login_WithNonExistentUser_ShouldReturnUnauthorized()
  {
    // Arrange
    var loginRequest = new LoginRequest
    {
      Email = "nonexistent@example.com",
      Password = "password123"
    };

    // Act
    var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  [Fact]
  public async Task Login_WithEmptyCredentials_ShouldReturnBadRequest()
  {
    // Arrange
    var loginRequest = new LoginRequest
    {
      Email = "",
      Password = ""
    };

    // Act
    var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
  }

  [Fact]
  public async Task GetProfile_WithoutAuthentication_ShouldReturnUnauthorized()
  {
    // Act
    var response = await _client.GetAsync("/api/user/profile");

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  [Fact]
  public async Task GetProfile_WithValidAuthentication_ShouldReturnUserProfile()
  {
    // Arrange - First login to get a valid token
    var loginRequest = new LoginRequest
    {
      Email = "test@BuoyAI.com",
      Password = "password123"
    };

    var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
    loginResponse.EnsureSuccessStatusCode();
    
    var loginContent = await loginResponse.Content.ReadAsStringAsync();
    var loginResult = JsonConvert.DeserializeObject<LoginResponse>(loginContent);
    
    // Add authorization header
    _client.DefaultRequestHeaders.Authorization = 
      new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.Token);

    // Act
    var response = await _client.GetAsync("/api/user/profile");

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    
    var content = await response.Content.ReadAsStringAsync();
    var userProfile = JsonConvert.DeserializeObject<UserProfileDto>(content);
    
    userProfile.Should().NotBeNull();
    userProfile!.Username.Should().Be("testuser");
    userProfile.Email.Should().Be("test@BuoyAI.com");
    userProfile.FirstName.Should().Be("Test");
    userProfile.LastName.Should().Be("User");
  }
}
