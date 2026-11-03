using Moq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

/// <summary>
/// Real unique-index enforcement (SQLite), which the InMemory provider cannot do. Only the
/// Reservations table is created, with the same unique index the production schema has.
/// </summary>
public sealed class ReservationUniqueIndexRaceTests : IDisposable
{
  private readonly SqliteConnection _connection = new("DataSource=:memory:");

  public ReservationUniqueIndexRaceTests()
  {
    _connection.Open();
    using var cmd = _connection.CreateCommand();
    cmd.CommandText = @"
      CREATE TABLE ""Reservations"" (
        ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Reservations"" PRIMARY KEY AUTOINCREMENT,
        ""Email"" TEXT NULL,
        ""StripeCheckoutSessionId"" TEXT NOT NULL,
        ""StripeCustomerId"" TEXT NULL,
        ""StripePaymentIntentId"" TEXT NULL,
        ""AmountCents"" INTEGER NOT NULL,
        ""Currency"" TEXT NOT NULL,
        ""PlanChoice"" TEXT NOT NULL,
        ""Status"" TEXT NOT NULL,
        ""Source"" TEXT NULL,
        ""CreatedAt"" TEXT NOT NULL,
        ""RefundedAt"" TEXT NULL,
        ""BetaInvitedAt"" TEXT NULL
      );
      CREATE UNIQUE INDEX ""IX_Reservations_StripeCheckoutSessionId"" ON ""Reservations"" (""StripeCheckoutSessionId"");";
    cmd.ExecuteNonQuery();
  }

  public void Dispose() => _connection.Dispose();

  private BuoyAIDbContext NewContext(params IInterceptor[] interceptors)
  {
    var builder = new DbContextOptionsBuilder<BuoyAIDbContext>().UseSqlite(_connection);
    if (interceptors.Length > 0)
    {
      builder.AddInterceptors(interceptors);
    }

    return new BuoyAIDbContext(builder.Options);
  }

  private static ReservationService NewService(BuoyAIDbContext context) =>
    new(
      context,
      Options.Create(new ReservationOptions { Enabled = true }),
      Mock.Of<IReservationCheckoutClient>(),
      NullLogger<ReservationService>.Instance,
      Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(e => e.EnvironmentName == "Production"));

  private static CompletedReservationCheckout Checkout(string sessionId) =>
    new(sessionId, "pat@example.com", "cus_1", "pi_1", 100, "usd",
      new Dictionary<string, string> { ["kind"] = "reservation", ["choice"] = "ai" });

  /// <summary>Runs an action right before the first save, after the service's existence check has passed.</summary>
  private sealed class BeforeFirstSave(Func<Task> action) : SaveChangesInterceptor
  {
    private bool _fired;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
      DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
      if (!_fired)
      {
        _fired = true;
        await action();
      }

      return result;
    }
  }

  [Fact]
  public async Task RecordCompletedAsync_LosingTheUniqueIndexRace_CompletesAndKeepsOneRow()
  {
    await using var winner = NewContext();
    var winningService = NewService(winner);
    await using var loser = NewContext(new BeforeFirstSave(() => winningService.RecordCompletedAsync(Checkout("cs_race"))));
    var losingService = NewService(loser);

    // The loser's existence check has already passed (no row) when the rival commits, so its
    // own insert hits the unique index and must fall into the idempotent branch.
    var act = () => losingService.RecordCompletedAsync(Checkout("cs_race"));

    await act.Should().NotThrowAsync();
    await using var verify = NewContext();
    (await verify.Reservations.CountAsync(r => r.StripeCheckoutSessionId == "cs_race")).Should().Be(1);
  }

  [Fact]
  public async Task RecordCompletedAsync_SaveFailsWithNoRowToExplainIt_Rethrows()
  {
    // Not a race: the table vanishes after the existence check, so the save fails and the
    // recheck cannot find a row either. The failure must surface, not be swallowed.
    await using var context = NewContext(new BeforeFirstSave(() =>
    {
      using var cmd = _connection.CreateCommand();
      cmd.CommandText = "DROP TABLE \"Reservations\"";
      cmd.ExecuteNonQuery();
      return Task.CompletedTask;
    }));
    var service = NewService(context);

    var act = () => service.RecordCompletedAsync(Checkout("cs_broken"));

    await act.Should().ThrowAsync<Exception>();
  }
}
