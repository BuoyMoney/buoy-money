using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>
/// #941 currency step 2: the primary display currency is a real, user-editable
/// profile setting. Setting it marks the choice explicit (CurrencyIsUserSet) so
/// the first-link default never overrides it; invalid codes are rejected; a
/// currency-only update must not require resending the name.
/// </summary>
[Collection("IntegrationTest")]
public class UserProfileCurrencyIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly WebApplicationFactory<Program> _factory;
  private readonly HttpClient _client;

  public UserProfileCurrencyIntegrationTests(TestWebApplicationFactory<Program> factory)
  {
    var uniqueDbName = $"TestDatabase_{Guid.NewGuid()}";
    _factory = factory.WithWebHostBuilder(builder =>
    {
      builder.ConfigureServices(services =>
      {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BuoyAIDbContext>));
        if (descriptor != null) services.Remove(descriptor);
        services.AddDbContext<BuoyAIDbContext>(options => options.UseInMemoryDatabase(uniqueDbName));
      });
    });
    _client = _factory.CreateClient();
  }

  private async Task SeedUserAsync(int id, string email)
  {
    using var scope = _factory.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    if (await context.Users.FindAsync(id) != null) return;
    context.Users.Add(new User
    {
      Id = id,
      Email = email,
      FirstName = "Test",
      PasswordHash = "hashed_password",
      CreatedAt = DateTime.UtcNow
    });
    await context.SaveChangesAsync();
  }

  private void Authenticate(int userId, string email)
  {
    var token = TestAuthenticationHelper.GenerateJwtToken(userId.ToString(), email, "User");
    _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
  }

  private static StringContent Json(object body) =>
    new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

  private async Task<User> GetUserAsync(int id)
  {
    using var scope = _factory.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    return (await context.Users.AsNoTracking().SingleAsync(u => u.Id == id));
  }

  [Fact]
  public async Task CurrencyOnlyUpdate_SetsCurrencyAndMarksExplicit_WithoutName()
  {
    await SeedUserAsync(801, "currency1@example.com");
    Authenticate(801, "currency1@example.com");

    var response = await _client.PutAsync("/api/user/profile", Json(new { currency = "cad" }));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    (await response.Content.ReadAsStringAsync()).Should().Contain("CAD");

    var user = await GetUserAsync(801);
    user.Currency.Should().Be("CAD");
    user.CurrencyIsUserSet.Should().BeTrue();
    user.FirstName.Should().Be("Test"); // untouched by a currency-only update
  }

  [Fact]
  public async Task UnsupportedCurrency_IsRejected()
  {
    await SeedUserAsync(802, "currency2@example.com");
    Authenticate(802, "currency2@example.com");

    var response = await _client.PutAsync("/api/user/profile", Json(new { currency = "DOGE" }));

    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    (await response.Content.ReadAsStringAsync()).Should().Contain("Unsupported currency");

    var user = await GetUserAsync(802);
    user.Currency.Should().Be("USD");
    user.CurrencyIsUserSet.Should().BeFalse();
  }

  [Fact]
  public async Task NameUpdate_WithoutCurrency_LeavesCurrencyUntouched()
  {
    await SeedUserAsync(803, "currency3@example.com");
    Authenticate(803, "currency3@example.com");

    var response = await _client.PutAsync("/api/user/profile",
      Json(new { firstName = "Renamed", lastName = "User" }));

    response.StatusCode.Should().Be(HttpStatusCode.OK);

    var user = await GetUserAsync(803);
    user.FirstName.Should().Be("Renamed");
    user.Currency.Should().Be("USD");
    user.CurrencyIsUserSet.Should().BeFalse(); // still eligible for the first-link default
  }

  [Fact]
  public async Task EmptyUpdate_IsRejected()
  {
    await SeedUserAsync(804, "currency4@example.com");
    Authenticate(804, "currency4@example.com");

    var response = await _client.PutAsync("/api/user/profile", Json(new { }));

    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
  }
}
