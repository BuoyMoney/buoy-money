using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BuoyAI.UnitTests.Data;

/// <summary>
/// Migration 0052 against a real Postgres: the table the SQL creates is the table the EF model reads
/// and writes, the one-row-per-product rule is enforced by the database, and deleting a user takes
/// their rows with them. Skipped without BUOYAI_MIGRATION_TEST_CONNECTION (see MigrationTestSupport).
/// </summary>
public class UserEntitlementsMigrationTests
{
  private static BuoyAIDbContext Context(string connectionString) =>
    new(new DbContextOptionsBuilder<BuoyAIDbContext>().UseNpgsql(connectionString).Options);

  private static int AddUser(ScratchDatabase db, string email)
  {
    using var ctx = Context(db.ConnectionString);
    var user = new User { Email = email, Username = email, PasswordHash = "x", FirstName = "U", LastName = "S" };
    ctx.Users.Add(user);
    ctx.SaveChanges();
    return user.Id;
  }

  [PostgresFact]
  public void TheMigration_CreatesTheTable_TheModelExpects()
  {
    using var db = new ScratchDatabase();
    SqlMigrationRunner.Run(db.ConnectionString);
    var userId = AddUser(db, "a@example.com");

    using (var ctx = Context(db.ConnectionString))
    {
      ctx.UserEntitlements.Add(new UserEntitlement
      {
        UserId = userId, Entitlement = "extra_logins", Quantity = 4, Provider = SubscriptionProvider.Stripe,
        ExternalId = "sub_1", Status = EntitlementStatus.PastDue, PaidThrough = DateTime.UtcNow.AddDays(100),
        EndsAt = DateTime.UtcNow.AddDays(30), Founding = true
      });
      ctx.SaveChanges();
    }

    using var read = Context(db.ConnectionString);
    var row = read.UserEntitlements.Single();
    row.Entitlement.Should().Be("extra_logins");
    row.Quantity.Should().Be(4);
    row.Status.Should().Be("past_due");
    row.Founding.Should().BeTrue();
    row.EndsAt.Should().NotBeNull();
    row.PaidThrough.Should().NotBeNull();
    row.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
  }

  [PostgresFact]
  public void TheDatabase_AllowsOneRowPerProductPerSubscription()
  {
    using var db = new ScratchDatabase();
    SqlMigrationRunner.Run(db.ConnectionString);
    var userId = AddUser(db, "a@example.com");

    db.Execute($"INSERT INTO \"UserEntitlements\" (\"UserId\", \"Entitlement\", \"ExternalId\") VALUES ({userId}, 'ai', 'sub_1')");
    // The same product from a different subscription is fine (a store plan and a Cloud plan).
    db.Execute($"INSERT INTO \"UserEntitlements\" (\"UserId\", \"Entitlement\", \"ExternalId\") VALUES ({userId}, 'ai', 'sub_2')");

    var act = () => db.Execute($"INSERT INTO \"UserEntitlements\" (\"UserId\", \"Entitlement\", \"ExternalId\") VALUES ({userId}, 'ai', 'sub_1')");
    act.Should().Throw<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
  }

  [PostgresFact]
  public void DeletingAUser_DeletesTheirProducts()
  {
    using var db = new ScratchDatabase();
    SqlMigrationRunner.Run(db.ConnectionString);
    var a = AddUser(db, "a@example.com");
    var b = AddUser(db, "b@example.com");
    db.Execute($"INSERT INTO \"UserEntitlements\" (\"UserId\", \"Entitlement\", \"ExternalId\") VALUES ({a}, 'ai', 'sub_a'), ({b}, 'ai', 'sub_b')");

    db.Execute($"DELETE FROM \"Users\" WHERE \"Id\" = {a}");

    db.Scalar("SELECT count(*) FROM \"UserEntitlements\"").Should().Be(1L);
    db.Scalar($"SELECT \"UserId\" FROM \"UserEntitlements\"").Should().Be(b);
  }

  [PostgresFact]
  public void ARowForAUserThatDoesNotExist_IsRefused()
  {
    using var db = new ScratchDatabase();
    SqlMigrationRunner.Run(db.ConnectionString);
    var act = () => db.Execute("INSERT INTO \"UserEntitlements\" (\"UserId\", \"Entitlement\", \"ExternalId\") VALUES (987654, 'ai', 'sub_x')");
    act.Should().Throw<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
  }

  [PostgresFact]
  public void TheDownScript_RemovesTheTable_AndItsMigrationStaysAdditive()
  {
    using var db = new ScratchDatabase();
    SqlMigrationRunner.Run(db.ConnectionString);
    db.TableExists("UserEntitlements").Should().BeTrue();

    var down = File.ReadAllText(Path.Combine(
      AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BuoyAI.Infrastructure", "Migrations", "Sql",
      "0052_add_user_entitlements.down.sql"));
    db.Execute(down);

    db.TableExists("UserEntitlements").Should().BeFalse();
  }

  [PostgresFact]
  public void TheProjection_WorksEndToEnd_OnRealPostgres()
  {
    // The sync service writes rows through EF; this proves the whole write path against the real table.
    using var db = new ScratchDatabase();
    SqlMigrationRunner.Run(db.ConnectionString);
    var userId = AddUser(db, "a@example.com");

    using var ctx = Context(db.ConnectionString);
    var service = new BuoyAI.Infrastructure.Services.SubscriptionSyncService(
      ctx, new Moq.Mock<BuoyAI.Core.Interfaces.IStripeService>().Object,
      new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<BuoyAI.Infrastructure.Services.SubscriptionSyncService>.Instance);

    var items = new List<BuoyAI.Core.DTOs.Billing.StripeSnapshotItem>
    {
      new("price_a", "bank_sync_yearly_list", "bank_sync", 1, false, "year", 3599),
      new("price_b", "extra_logins_yearly_list", "extra_logins", 2, false, "year", 600)
    };
    service.ProjectAsync(userId, new BuoyAI.Core.DTOs.Billing.StripeSubscriptionSnapshot(
      "sub_real", "active", null, "price_a", false, items, DateTime.UtcNow.AddDays(300), null)).GetAwaiter().GetResult();

    db.Scalar("SELECT count(*) FROM \"UserEntitlements\"").Should().Be(2L);
    db.Scalar("SELECT \"Quantity\" FROM \"UserEntitlements\" WHERE \"Entitlement\" = 'extra_logins'").Should().Be(2);

    // Raising the quantity and dropping nothing updates in place.
    items[1] = items[1] with { Quantity = 5 };
    service.ProjectAsync(userId, new BuoyAI.Core.DTOs.Billing.StripeSubscriptionSnapshot(
      "sub_real", "active", null, "price_a", false, items, DateTime.UtcNow.AddDays(300), null)).GetAwaiter().GetResult();
    db.Scalar("SELECT count(*) FROM \"UserEntitlements\"").Should().Be(2L);
    db.Scalar("SELECT \"Quantity\" FROM \"UserEntitlements\" WHERE \"Entitlement\" = 'extra_logins'").Should().Be(5);

    // Cancelling removes both.
    service.ProjectAsync(userId, new BuoyAI.Core.DTOs.Billing.StripeSubscriptionSnapshot(
      "sub_real", "canceled", null, "price_a", false, items, null, null)).GetAwaiter().GetResult();
    db.Scalar("SELECT count(*) FROM \"UserEntitlements\"").Should().Be(0L);
  }
}
