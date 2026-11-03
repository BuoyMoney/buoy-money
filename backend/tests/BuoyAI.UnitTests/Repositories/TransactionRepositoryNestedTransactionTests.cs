using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class TransactionRepositoryNestedTransactionTests : IDisposable
{
  private readonly SqliteConnection _connection = new("DataSource=:memory:;Foreign Keys=False");
  private readonly BuoyAIDbContext _db;

  public TransactionRepositoryNestedTransactionTests()
  {
    _connection.Open();
    _db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>().UseSqlite(_connection).Options);
    _db.Database.EnsureCreated();
  }

  public void Dispose()
  {
    _db.Dispose();
    _connection.Dispose();
  }

  private Transaction Row(int id) =>
    new() { Id = id, AccountId = 1, Amount = -5m, Type = TransactionType.Debit, Date = DateTime.UtcNow, PlaidTransactionId = $"t{id}" };

  [Fact]
  public async Task Called_inside_a_transaction_it_joins_it_and_rolls_back_with_it()
  {
    var repo = new TransactionRepository(_db);
    await using (var outer = await _db.Database.BeginTransactionAsync())
    {
      await repo.ExecuteInTransactionAsync(async () =>
      {
        _db.Transactions.Add(Row(1));
        await _db.SaveChangesAsync();
      });
      await outer.RollbackAsync();
    }
    _db.ChangeTracker.Clear();
    (await _db.Transactions.CountAsync()).Should().Be(0);
  }

  [Fact]
  public async Task Called_on_its_own_it_still_commits()
  {
    await new TransactionRepository(_db).ExecuteInTransactionAsync(async () =>
    {
      _db.Transactions.Add(Row(2));
      await _db.SaveChangesAsync();
    });
    _db.ChangeTracker.Clear();
    (await _db.Transactions.CountAsync()).Should().Be(1);
  }
}
