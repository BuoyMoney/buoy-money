using System.Net;
using System.Net.Http.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>
/// The second login step is decided by the server only: the account's MFA settings and the
/// server-side trusted-device record. A client-sent "biometricVerified" flag has no effect.
/// Each test gets its own host so the database starts clean.
/// </summary>
public class LoginSecondStepIntegrationTests : IDisposable
{
  private readonly TestWebApplicationFactory<Program> _factory = new();

  public void Dispose() => _factory.Dispose();

  private async Task<int> EnableTotpAsync()
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var user = await db.Users.SingleAsync(u => u.Email == "test@BuoyAI.com");
    user.HasMfaEnabled = true;
    user.MfaType = MfaType.Totp;
    await db.SaveChangesAsync();
    return user.Id;
  }

  private static async Task<JObject> PostLoginAsync(HttpClient client, bool biometricVerified)
  {
    var response = await client.PostAsJsonAsync("/api/auth/login", new
    {
      email = "test@BuoyAI.com",
      password = "password123",
      rememberDevice = true,
      biometricVerified,
    });
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    return JObject.Parse(await response.Content.ReadAsStringAsync());
  }

  private static bool RequiresMfa(JObject body) =>
    (body["requiresMfa"] ?? body["RequiresMfa"])!.Value<bool>();

  private static string? Token(JObject body) =>
    (body["token"] ?? body["Token"])?.Value<string>();

  [Fact]
  public async Task PasswordPlusBiometricFlag_MfaAccountOnUntrustedDevice_GetsChallengeNotTokens()
  {
    await EnableTotpAsync();
    var client = _factory.CreateClient();

    var body = await PostLoginAsync(client, biometricVerified: true);

    RequiresMfa(body).Should().BeTrue();
    Token(body).Should().BeNullOrEmpty();
    ((body["mfaToken"] ?? body["MfaToken"])?.Value<string>()).Should().NotBeNullOrEmpty();
    AssertNoRefreshToken(body);

    // And asking to be remembered on a password-only step must not trust the device.
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    (await db.TrustedDevices.AnyAsync()).Should().BeFalse();
  }

  [Fact]
  public async Task MfaAccountOnServerTrustedDevice_SkipsChallenge()
  {
    var userId = await EnableTotpAsync();
    var client = _factory.CreateClient();

    // First attempt from this client is challenged and logs the device fingerprint the
    // server derives from the request. Trust that fingerprint server-side, as a completed
    // verify-mfa with "remember device" would.
    RequiresMfa(await PostLoginAsync(client, biometricVerified: false)).Should().BeTrue();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var fingerprint = await db.LoginAttempts
        .Where(a => a.UserId == userId)
        .Select(a => a.DeviceFingerprint)
        .FirstAsync();
      db.TrustedDevices.Add(new TrustedDevice
      {
        UserId = userId,
        DeviceFingerprint = fingerprint!,
        DeviceName = "Test device",
        LastUsedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(30),
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
      });
      await db.SaveChangesAsync();
    }

    var body = await PostLoginAsync(client, biometricVerified: false);

    RequiresMfa(body).Should().BeFalse();
    Token(body).Should().NotBeNullOrEmpty();
  }

  private static void AssertNoRefreshToken(JObject body) =>
    ((body["refreshToken"] ?? body["RefreshToken"])?.Value<string>()).Should().BeNullOrEmpty();
}
