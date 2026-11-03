using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The [Authorize(Roles = "Admin")] routes read the role from the token's tier claim. On hosted the
/// Admin:Emails allowlist used to unlock admin screens in the app while their API calls answered
/// 403, because the claim carried the stored tier (Premium or none).
/// </summary>
public class AdminAllowlistTokenTests
{
  private static AuthService Create(Dictionary<string, string?> settings)
  {
    var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    return new AuthService(
      Mock.Of<IUserRepository>(), config, new Lazy<IBankAggregatorRegistry>(() => Mock.Of<IBankAggregatorRegistry>()),
      Mock.Of<ILastSyncTracker>(), NullLogger<AuthService>.Instance, db,
      Mock.Of<IEmailService>(), Mock.Of<IEmailPreferencesService>(), Mock.Of<IHostEnvironment>(),
      new ServiceCollection().BuildServiceProvider(), Mock.Of<IEntitlementService>(), Mock.Of<INotificationService>(),
      Mock.Of<IDataRetentionManager>(), Mock.Of<IFamilyInvitationRepository>(), Mock.Of<IGoogleTokenValidator>(),
      Mock.Of<IAppleTokenValidator>(), Mock.Of<IRefreshTokenService>(), new BuoyAiMetrics(new TestMeterFactory()),
      Mock.Of<IEncryptionService>(), Mock.Of<IAppleAuthClient>(), Mock.Of<IServiceScopeFactory>(), Mock.Of<IMarketingLeadService>());
  }

  [Fact]
  public void Hosted_AllowlistedEmail_GetsTheAdminClaim_WhateverTheStoredTier()
  {
    var auth = Create(new() { ["Admin:Emails:0"] = "owner@example.com" });

    auth.ResolveTierClaim(new User { Email = "Owner@Example.com", SubscriptionTier = SubscriptionTier.Premium })
      .Should().Be(SubscriptionTier.Admin);
    auth.ResolveTierClaim(new User { Email = "owner@example.com", SubscriptionTier = null })
      .Should().Be(SubscriptionTier.Admin);
  }

  [Fact]
  public void Hosted_OtherUsers_KeepTheirStoredTier()
  {
    var auth = Create(new() { ["Admin:Emails:0"] = "owner@example.com" });

    auth.ResolveTierClaim(new User { Email = "someone@example.com", SubscriptionTier = SubscriptionTier.Premium })
      .Should().Be(SubscriptionTier.Premium);
    auth.ResolveTierClaim(new User { Email = "someone@example.com", SubscriptionTier = null })
      .Should().BeNull();
  }

  [Fact]
  public void Hosted_WithNoAllowlist_NobodyBecomesAdmin()
  {
    var auth = Create(new());

    auth.ResolveTierClaim(new User { Email = "owner@example.com", SubscriptionTier = SubscriptionTier.Standard })
      .Should().Be(SubscriptionTier.Standard);
  }
}
