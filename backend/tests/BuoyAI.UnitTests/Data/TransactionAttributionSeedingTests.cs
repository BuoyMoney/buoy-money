using System;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Data;

public class TransactionAttributionSeedingTests
{
  private static BuoyAIDbContext NewContext() =>
    new(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options);

  private static Account Card(int id, int ownerId, int? associatedUserId) => new()
  {
    Id = id,
    UserId = ownerId,
    AssociatedUserId = associatedUserId,
    PlaidAccountId = $"plaid-{id}",
    Name = "Card",
  };

  [Fact]
  public async Task SaveChanges_SeedsClaimedByUserId_FromAccountAssociatedUser()
  {
    using var ctx = NewContext();
    ctx.Accounts.Add(Card(1, ownerId: 9, associatedUserId: 5)); // a member's personal card
    await ctx.SaveChangesAsync();

    ctx.Transactions.Add(new Transaction { Id = 1, AccountId = 1, Amount = 20, Type = TransactionType.Debit, Date = DateTime.UtcNow });
    await ctx.SaveChangesAsync();

    (await ctx.Transactions.FindAsync(1))!.ClaimedByUserId.Should().Be(5);
  }

  [Fact]
  public async Task SaveChanges_UnassociatedAccount_LeavesClaimedByUserIdNull()
  {
    using var ctx = NewContext();
    ctx.Accounts.Add(Card(2, ownerId: 9, associatedUserId: null)); // shared family card
    await ctx.SaveChangesAsync();

    ctx.Transactions.Add(new Transaction { Id = 2, AccountId = 2, Amount = 30, Type = TransactionType.Debit, Date = DateTime.UtcNow });
    await ctx.SaveChangesAsync();

    // null => goes to the family budget by default
    (await ctx.Transactions.FindAsync(2))!.ClaimedByUserId.Should().BeNull();
  }

  [Fact]
  public async Task SaveChanges_ExplicitClaim_IsNotOverwritten()
  {
    using var ctx = NewContext();
    ctx.Accounts.Add(Card(3, ownerId: 9, associatedUserId: 5));
    await ctx.SaveChangesAsync();

    ctx.Transactions.Add(new Transaction { Id = 3, AccountId = 3, Amount = 40, Type = TransactionType.Debit, Date = DateTime.UtcNow, ClaimedByUserId = 7 });
    await ctx.SaveChangesAsync();

    (await ctx.Transactions.FindAsync(3))!.ClaimedByUserId.Should().Be(7); // explicit claim wins
  }

  [Fact]
  public async Task Reattribute_MovesOldOwnersClaims_PreservesOtherMembers()
  {
    using var ctx = NewContext();
    ctx.Accounts.Add(Card(1, ownerId: 9, associatedUserId: 33));
    await ctx.SaveChangesAsync();
    ctx.Transactions.AddRange(
      new Transaction { Id = 1, AccountId = 1, Amount = 10, Type = TransactionType.Debit, Date = DateTime.UtcNow, ClaimedByUserId = 33 },
      new Transaction { Id = 2, AccountId = 1, Amount = 20, Type = TransactionType.Debit, Date = DateTime.UtcNow, ClaimedByUserId = 33 },
      new Transaction { Id = 3, AccountId = 1, Amount = 45, Type = TransactionType.Debit, Date = DateTime.UtcNow, ClaimedByUserId = 34 });
    await ctx.SaveChangesAsync();

    var moved = await new TransactionRepository(ctx)
      .ReattributeAccountClaimsAsync(accountId: 1, fromUserId: 33, toUserId: null);

    moved.Should().Be(2);
    (await ctx.Transactions.FindAsync(1))!.ClaimedByUserId.Should().BeNull(); // -> family
    (await ctx.Transactions.FindAsync(2))!.ClaimedByUserId.Should().BeNull();
    (await ctx.Transactions.FindAsync(3))!.ClaimedByUserId.Should().Be(34); // other member untouched
  }
}
