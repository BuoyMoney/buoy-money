using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class DeviceTrustServiceTests
{
  private static DeviceInfoDto CreateDeviceInfo(string ip, string userAgent)
  {
    return new DeviceInfoDto
    {
      IpAddress = ip,
      UserAgent = userAgent,
      RememberDevice = true
    };
  }

  private static DeviceTrustService CreateService(BuoyAIDbContext context, Mock<IIpGeolocationService> geoMock, Mock<ILogger<DeviceTrustService>>? loggerMock = null)
  {
    return new DeviceTrustService(
      context,
      (loggerMock ?? new Mock<ILogger<DeviceTrustService>>()).Object,
      geoMock.Object);
  }

  private static BuoyAIDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: $"deviceTrust-{Guid.NewGuid()}")
      .Options;

    var context = new BuoyAIDbContext(options);
    context.Database.EnsureCreated();
    return context;
  }

  [Fact]
  public async Task EvaluateDeviceTrustAsync_NewDevice_ShouldRequireMfa()
  {
    await using var context = CreateContext();
    var geoMock = new Mock<IIpGeolocationService>();
    geoMock.Setup(s => s.GetLocationAsync(It.IsAny<string>())).ReturnsAsync((GeoLocationDto?)null);
    geoMock.Setup(s => s.CalculateDistance(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>())).Returns(0);

    var service = CreateService(context, geoMock);
    var deviceInfo = CreateDeviceInfo("198.51.100.10", "Mozilla/5.0 (Windows NT 10.0)");

    var evaluation = await service.EvaluateDeviceTrustAsync(userId: 42, deviceInfo);

    evaluation.IsTrustedDevice.Should().BeFalse();
    evaluation.RequiresMfa.Should().BeTrue();
    evaluation.RiskScore.Should().BeGreaterOrEqualTo(40);
    evaluation.MfaReasons.Should().Contain(reason => reason.Contains("New or untrusted device"));
  }

  [Fact]
  public async Task RegisterTrustedDeviceAsync_ExistingDevice_ShouldExtendExpiration()
  {
    await using var context = CreateContext();
    var geoMock = new Mock<IIpGeolocationService>();
    var service = CreateService(context, geoMock);

    var deviceInfo = CreateDeviceInfo("203.0.113.9", "Mozilla/5.0 (Macintosh; Intel Mac OS X)");
    var fingerprint = service.GenerateDeviceFingerprint(deviceInfo.UserAgent, deviceInfo.IpAddress);

    var trustedDevice = new TrustedDevice
    {
      Id = 5,
      UserId = 7,
      DeviceFingerprint = fingerprint,
      DeviceName = "Mac",
      DeviceType = "Desktop",
      IpAddress = deviceInfo.IpAddress,
      UserAgent = deviceInfo.UserAgent,
      LastUsedAt = DateTime.UtcNow.AddDays(-5),
      ExpiresAt = DateTime.UtcNow.AddDays(-10),
      IsActive = true,
      CreatedAt = DateTime.UtcNow.AddDays(-30),
      UpdatedAt = DateTime.UtcNow.AddDays(-5)
    };

    context.TrustedDevices.Add(trustedDevice);
    await context.SaveChangesAsync();

    var beforeUpdate = trustedDevice.ExpiresAt;

    var deviceId = await service.RegisterTrustedDeviceAsync(userId: 7, deviceInfo);

    deviceId.Should().Be(trustedDevice.Id);
    trustedDevice.ExpiresAt.Should().BeAfter(beforeUpdate);
    trustedDevice.IsActive.Should().BeTrue();
    trustedDevice.LastUsedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task CleanupExpiredDevicesAsync_ShouldDeactivateExpiredEntries()
  {
    await using var context = CreateContext();
    var geoMock = new Mock<IIpGeolocationService>();
    var service = CreateService(context, geoMock);

    var activeDevice = new TrustedDevice
    {
      UserId = 10,
      DeviceFingerprint = "active",
      DeviceName = "Active",
      DeviceType = "Desktop",
      IpAddress = "198.51.100.3",
      UserAgent = "agent",
      LastUsedAt = DateTime.UtcNow,
      ExpiresAt = DateTime.UtcNow.AddDays(5),
      IsActive = true,
      CreatedAt = DateTime.UtcNow.AddDays(-1),
      UpdatedAt = DateTime.UtcNow.AddDays(-1)
    };

    var expiredDevice = new TrustedDevice
    {
      UserId = 10,
      DeviceFingerprint = "expired",
      DeviceName = "Expired",
      DeviceType = "Mobile",
      IpAddress = "198.51.100.4",
      UserAgent = "agent",
      LastUsedAt = DateTime.UtcNow.AddDays(-40),
      ExpiresAt = DateTime.UtcNow.AddDays(-1),
      IsActive = true,
      CreatedAt = DateTime.UtcNow.AddDays(-40),
      UpdatedAt = DateTime.UtcNow.AddDays(-40)
    };

    context.TrustedDevices.AddRange(activeDevice, expiredDevice);
    await context.SaveChangesAsync();

    await service.CleanupExpiredDevicesAsync();

    activeDevice.IsActive.Should().BeTrue();
    expiredDevice.IsActive.Should().BeFalse();
    expiredDevice.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task LogLoginAttemptAsync_ShouldPersistGeolocationData()
  {
    await using var context = CreateContext();
    var geoMock = new Mock<IIpGeolocationService>();
    geoMock.Setup(s => s.GetLocationAsync(It.IsAny<string>())).ReturnsAsync(new GeoLocationDto
    {
      City = "Seattle",
      Region = "WA",
      Country = "United States",
      CountryCode = "US",
      Latitude = 47.6062,
      Longitude = -122.3321,
      Timezone = "America/Los_Angeles"
    });
    geoMock.Setup(s => s.CalculateDistance(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>())).Returns(0);

    var service = CreateService(context, geoMock);
    var deviceInfo = CreateDeviceInfo("198.51.100.20", "Mozilla/5.0 (Windows NT 10.0)");

    var fingerprint = service.GenerateDeviceFingerprint(deviceInfo.UserAgent, deviceInfo.IpAddress);
    context.TrustedDevices.Add(new TrustedDevice
    {
      UserId = 15,
      DeviceFingerprint = fingerprint,
      DeviceName = "Windows PC",
      DeviceType = "Desktop",
      IpAddress = deviceInfo.IpAddress,
      UserAgent = deviceInfo.UserAgent,
      LastUsedAt = DateTime.UtcNow,
      ExpiresAt = DateTime.UtcNow.AddDays(10),
      IsActive = true,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    });
    await context.SaveChangesAsync();

    await service.LogLoginAttemptAsync(
      userId: 15,
      deviceInfo,
      success: true,
      mfaChallenged: false,
      mfaSuccess: false,
      riskScore: 25);

    var attempt = await context.LoginAttempts.SingleAsync();
    attempt.Success.Should().BeTrue();
    attempt.TrustedDevice.Should().BeTrue();
    attempt.City.Should().Be("Seattle");
    attempt.Region.Should().Be("WA");
    attempt.CountryCode.Should().Be("US");
    attempt.Latitude.Should().Be(47.6062);
    attempt.Longitude.Should().Be(-122.3321);
  }
}
