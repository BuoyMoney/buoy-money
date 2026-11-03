using System.Net;
using System.Net.Http.Headers;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>
/// A JWT is only as good as the user row behind it. These tests pin the OnTokenValidated
/// existence check: a well-signed, unexpired token for a user id that no longer exists (hard
/// delete, or DemoAccountSeeder wipe + re-create) or is soft-deleted must 401 rather than
/// authenticate and let controllers return empty data.
/// </summary>
[Collection("IntegrationTest")]
public class DeletedUserTokenIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly TestWebApplicationFactory<Program> _factory;

  public DeletedUserTokenIntegrationTests(TestWebApplicationFactory<Program> factory)
  {
    _factory = factory;
  }

  private HttpClient ClientWithToken(string userId, string email)
  {
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
      "Bearer", TestAuthenticationHelper.GenerateJwtToken(userId, email));
    return client;
  }

  [Fact]
  public async Task ValidToken_ForExistingUser_IsAccepted()
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var user = db.Users.First(u => u.Email == "test@BuoyAI.com");

    var client = ClientWithToken(user.Id.ToString(), user.Email);
    var response = await client.GetAsync("/api/user/profile");

    response.StatusCode.Should().Be(HttpStatusCode.OK);
  }

  [Fact]
  public async Task ValidToken_ForNonExistentUserId_Returns401()
  {
    // Signature, issuer, audience and expiry are all valid; only the user row is missing.
    var client = ClientWithToken("987654", "ghost@example.com");

    var response = await client.GetAsync("/api/user/profile");

    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  [Fact]
  public async Task ValidToken_ForSoftDeletedUser_Returns401()
  {
    int userId;
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var user = new User
      {
        Username = "soft-deleted",
        Email = "soft-deleted@example.com",
        PasswordHash = "x",
        IsDeleted = true,
        DeletedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      };
      db.Users.Add(user);
      await db.SaveChangesAsync();
      userId = user.Id;
    }

    var client = ClientWithToken(userId.ToString(), "soft-deleted@example.com");
    var response = await client.GetAsync("/api/user/profile");

    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  [Fact]
  public async Task ValidToken_ForUserDeletedAfterIssue_Returns401_NotEmptyData()
  {
    // The staging repro: the device holds a token minted while the user existed; the row is
    // then wiped out from under it. Data endpoints must 401, not 200-with-nothing.
    int userId;
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var user = new User
      {
        Username = "wiped",
        Email = "wiped@example.com",
        PasswordHash = "x",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      };
      db.Users.Add(user);
      await db.SaveChangesAsync();
      userId = user.Id;
    }

    var client = ClientWithToken(userId.ToString(), "wiped@example.com");

    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      db.Users.Remove(await db.Users.FindAsync(userId) ?? throw new InvalidOperationException());
      await db.SaveChangesAsync();
    }

    var response = await client.GetAsync("/api/transactions");

    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }
}
