using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Data;

public class TestDataSeederPlanTests
{
  private static BuoyAIDbContext NewDb() =>
    new(new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

  [Fact]
  public async Task SeededPremiumUsers_ArePremiumToTheAccessChecks_NotJustOnTheirProfile()
  {
    await using var db = NewDb();

    await TestDataSeeder.SeedTestDataAsync(db);

    var entitlements = new EntitlementService(db, new ConfigurationBuilder().Build(), [], NullLogger<EntitlementService>.Instance);
    foreach (var email in new[] { "premium-user@buoy-ai.com", "family-owner@buoy-ai.com" })
    {
      var user = await db.Users.SingleAsync(u => u.Email == email);
      (await entitlements.GetTierOrStandardAsync(user.Id)).Should().Be(SubscriptionTier.Premium, email);
    }
  }

  [Fact]
  public async Task SeedingTwice_DoesNotAddASecondPlanRow()
  {
    await using var db = NewDb();

    await TestDataSeeder.SeedTestDataAsync(db);
    await TestDataSeeder.SeedTestDataAsync(db, idempotent: true);

    var premium = await db.Users.SingleAsync(u => u.Email == "premium-user@buoy-ai.com");
    (await db.UserSubscriptions.CountAsync(s => s.UserId == premium.Id)).Should().Be(1);
  }

  [Fact]
  public async Task IdempotentReseed_BackfillsPlanRowsForPremiumUsersThatLackThem()
  {
    await using var db = NewDb();
    await TestDataSeeder.SeedTestDataAsync(db);
    db.UserSubscriptions.RemoveRange(db.UserSubscriptions);
    await db.SaveChangesAsync();

    await TestDataSeeder.SeedTestDataAsync(db, idempotent: true);

    var entitlements = new EntitlementService(db, new ConfigurationBuilder().Build(), [], NullLogger<EntitlementService>.Instance);
    foreach (var email in new[] { "premium-user@buoy-ai.com", "family-owner@buoy-ai.com" })
    {
      var user = await db.Users.SingleAsync(u => u.Email == email);
      (await db.UserSubscriptions.CountAsync(s => s.UserId == user.Id)).Should().Be(1, email);
      (await entitlements.GetTierOrStandardAsync(user.Id)).Should().Be(SubscriptionTier.Premium, email);
    }
  }
}
