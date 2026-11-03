using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class MfaPreferencesServiceTests
{
  private static BuoyAIDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: $"mfa-preferences-{Guid.NewGuid()}")
      .Options;

    var context = new BuoyAIDbContext(options);
    context.Database.EnsureCreated();
    return context;
  }

  private static MfaPreferencesService CreateService(BuoyAIDbContext context)
  {
    var logger = new Mock<ILogger<MfaPreferencesService>>();
    return new MfaPreferencesService(context, logger.Object);
  }

  private static User CreateUser(int id, string? totpSecret = null, bool hasPasskey = false)
  {
    return new User
    {
      Id = id,
      Username = $"user{id}",
      PasswordHash = "hash",
      Email = $"user{id}@example.com",
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      TotpSecret = totpSecret,
      HasPasskeyRegistered = hasPasskey,
      HasMfaEnabled = totpSecret != null || hasPasskey,
      MfaType = totpSecret != null ? MfaType.Totp : hasPasskey ? MfaType.Passkey : MfaType.Email
    };
  }

  [Fact]
  public async Task GetPreferencesAsync_ShouldCreateDefaultPreferences_WhenNoneExist()
  {
    await using var context = CreateContext();
    context.Users.Add(CreateUser(1));
    await context.SaveChangesAsync();

    var service = CreateService(context);

    var result = await service.GetPreferencesAsync(1);

    result.PrimaryMethod.Should().Be("Email");
    result.AvailableMethods.Should().BeEquivalentTo(new[] { "Email" });
    result.NotifyOnNewDevice.Should().BeTrue();
    result.NotifyOnSuspiciousLogin.Should().BeTrue();

    var preferences = await context.UserMfaPreferences.SingleAsync();
    preferences.UserId.Should().Be(1);
    preferences.PrimaryMethod.Should().Be(MfaType.Email);
  }

  [Fact]
  public async Task GetPreferencesAsync_ShouldDefaultToPasskey_WhenPasskeyExists()
  {
    await using var context = CreateContext();
    var user = CreateUser(2, hasPasskey: true);
    context.Users.Add(user);
    context.UserPasskeys.Add(new UserPasskey
    {
      UserId = user.Id,
      CredentialId = "cred-123",
      PublicKey = "key",
      Name = "MacBook",
      DeviceType = "Desktop",
      CreatedAt = DateTime.UtcNow,
      IsActive = true
    });

    await context.SaveChangesAsync();

    var service = CreateService(context);

    var result = await service.GetPreferencesAsync(user.Id);

    result.PrimaryMethod.Should().Be("Passkey");
    result.AvailableMethods.Should().BeEquivalentTo(new[] { "Email", "Passkey" });

    var preferences = await context.UserMfaPreferences.SingleAsync();
    preferences.PrimaryMethod.Should().Be(MfaType.Passkey);
  }

  [Fact]
  public async Task UpdatePreferencesAsync_ShouldPersistValidChanges()
  {
    await using var context = CreateContext();
    var user = CreateUser(3, totpSecret: "ABC123");
    context.Users.Add(user);
    context.UserMfaPreferences.Add(new UserMfaPreferences
    {
      UserId = user.Id,
      PrimaryMethod = MfaType.Email,
      NotifyOnNewDevice = true,
      NotifyOnSuspiciousLogin = true,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    });
    await context.SaveChangesAsync();

    var service = CreateService(context);

    var update = new UpdateMfaPreferencesDto
    {
      PrimaryMethod = "Totp",
      FallbackMethod = "Email",
      NotifyOnNewDevice = false,
      NotifyOnSuspiciousLogin = true
    };

    await service.UpdatePreferencesAsync(user.Id, update);

    var preferences = await context.UserMfaPreferences.SingleAsync();
    preferences.PrimaryMethod.Should().Be(MfaType.Totp);
    preferences.FallbackMethod.Should().Be(MfaType.Email);
    preferences.NotifyOnNewDevice.Should().BeFalse();
    preferences.NotifyOnSuspiciousLogin.Should().BeTrue();
  }

  [Fact]
  public async Task UpdatePreferencesAsync_ShouldRejectUnavailableMethods()
  {
    await using var context = CreateContext();
    var user = CreateUser(4, totpSecret: "SECRET");
    context.Users.Add(user);
    await context.SaveChangesAsync();

    var service = CreateService(context);

    var update = new UpdateMfaPreferencesDto
    {
      PrimaryMethod = "Totp",
      FallbackMethod = "Passkey",
      NotifyOnNewDevice = true,
      NotifyOnSuspiciousLogin = true
    };

    var act = async () => await service.UpdatePreferencesAsync(user.Id, update);

    await act.Should().ThrowAsync<InvalidOperationException>()
      .WithMessage("Fallback MFA method Passkey is not available for this user");
  }

  [Fact]
  public async Task GetPreferredMfaMethodAsync_ShouldRespectStoredPreferences()
  {
    await using var context = CreateContext();
    var user = CreateUser(5);
    context.Users.Add(user);
    context.UserMfaPreferences.Add(new UserMfaPreferences
    {
      UserId = user.Id,
      PrimaryMethod = MfaType.Passkey,
      FallbackMethod = MfaType.Email,
      NotifyOnNewDevice = true,
      NotifyOnSuspiciousLogin = true,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    });
    await context.SaveChangesAsync();

    var service = CreateService(context);

    var preferred = await service.GetPreferredMfaMethodAsync(user.Id);

    preferred.Should().Be(MfaType.Passkey);
  }

  [Fact]
  public async Task GetPreferredMfaMethodAsync_ShouldInferFromUserState_WhenNoPreferences()
  {
    await using var context = CreateContext();
    var user = CreateUser(6, totpSecret: "TOTP");
    context.Users.Add(user);
    context.UserPasskeys.Add(new UserPasskey
    {
      UserId = user.Id,
      CredentialId = "cred-xyz",
      PublicKey = "key",
      Name = "YubiKey",
      DeviceType = "Security Key",
      CreatedAt = DateTime.UtcNow,
      IsActive = true
    });
    await context.SaveChangesAsync();

    var service = CreateService(context);

    var preferred = await service.GetPreferredMfaMethodAsync(user.Id);

    preferred.Should().Be(MfaType.Passkey);
  }

  [Fact]
  public async Task GetAvailableMethodsAsync_ShouldReflectConfiguredFactors()
  {
    await using var context = CreateContext();
    var user = CreateUser(7, totpSecret: "XYZ");
    context.Users.Add(user);
    context.UserPasskeys.Add(new UserPasskey
    {
      UserId = user.Id,
      CredentialId = "cred-789",
      PublicKey = "key",
      Name = "Pixel",
      DeviceType = "Mobile",
      CreatedAt = DateTime.UtcNow,
      IsActive = true
    });
    await context.SaveChangesAsync();

    var service = CreateService(context);

    var methods = await service.GetAvailableMethodsAsync(user.Id);

    methods.Should().Contain(new[] { "Email", "Totp", "Passkey" });
    methods.Should().HaveCount(3);
  }
}
