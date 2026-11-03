using System;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Data;

/// <summary>
/// Verifies the centralized UpdatedAt bump in BuoyAIDbContext.SaveChanges/SaveChangesAsync (#493).
/// This is the prerequisite that makes offline-sync conflict detection reliable — before it,
/// SavingsGoal.UpdatedAt was effectively frozen.
/// </summary>
public class UpdatedAtBumpTests
{
  private static BuoyAIDbContext NewContext() =>
    new(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options);

  private static SavingsGoal NewGoal(DateTime updatedAt) => new()
  {
    Id = 1,
    UserId = 7,
    Name = "Vacation",
    TargetAmount = 1000,
    AllocationPercentage = 10,
    CreatedAt = updatedAt,
    UpdatedAt = updatedAt,
  };

  [Fact]
  public async Task SaveChanges_ModifiedEntityWithUpdatedAt_AdvancesUpdatedAt()
  {
    using var ctx = NewContext();
    var stale = DateTime.UtcNow.AddDays(-30);
    ctx.SavingsGoals.Add(NewGoal(stale));
    await ctx.SaveChangesAsync();

    var goal = await ctx.SavingsGoals.FindAsync(1);
    goal!.Name = "New name";
    await ctx.SaveChangesAsync();

    goal.UpdatedAt.Should().BeAfter(stale);
    goal.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task SaveChanges_AddedEntity_LeavesCallerSuppliedUpdatedAt()
  {
    using var ctx = NewContext();
    var explicitTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    ctx.SavingsGoals.Add(NewGoal(explicitTime));
    await ctx.SaveChangesAsync();

    // Bump only fires on Modified entries — an insert keeps the value the caller set.
    (await ctx.SavingsGoals.FindAsync(1))!.UpdatedAt.Should().Be(explicitTime);
  }
}
