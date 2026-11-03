using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Entities;

public class TransactionDisplayNameTests
{
  [Fact]
  public void DisplayName_NicknameSet_WinsOverBankFields()
  {
    var tx = new Transaction { Nickname = "Roof repair", MerchantName = "Sam's Club", Description = "SAMS CLUB #4791" };

    tx.DisplayName.Should().Be("Roof repair");
  }

  [Fact]
  public void DisplayName_NoNickname_PrefersMerchantName()
  {
    var tx = new Transaction { MerchantName = "Sam's Club", Description = "SAMS CLUB #4791" };

    tx.DisplayName.Should().Be("Sam's Club");
  }

  [Fact]
  public void DisplayName_EmptyMerchantName_FallsBackToDescription()
  {
    // The trap: the bank fields default to string.Empty, NOT null. A `??` chain would return
    // "" here and render a blank title even though Description has perfectly good text.
    var tx = new Transaction { MerchantName = "", Description = "CHECK #1042" };

    tx.DisplayName.Should().Be("CHECK #1042");
  }

  [Fact]
  public void DisplayName_ClearedNickname_RevealsTheBankNameAgain()
  {
    // Renaming is reversible because the bank's text is never overwritten.
    var tx = new Transaction { Nickname = null, MerchantName = "Sam's Club" };

    tx.DisplayName.Should().Be("Sam's Club");
  }

  [Fact]
  public void DisplayName_NothingAtAll_ReturnsPlaceholderNotBlank()
  {
    var tx = new Transaction { MerchantName = "", Description = "" };

    tx.DisplayName.Should().Be("Unnamed transaction");
  }

  [Fact]
  public void DisplayName_IsNotMappedToAColumn()
  {
    // Computed; the SQL-first schema (#533) has no such column, so a mapped property would
    // break every Transactions query against real Postgres. InMemory would NOT catch that —
    // it has no schema to disagree with — so assert the model, where mapping is decided.
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    using var context = new BuoyAIDbContext(options);

    var entity = context.Model.FindEntityType(typeof(Transaction));

    entity.Should().NotBeNull();
    entity!.FindProperty(nameof(Transaction.DisplayName)).Should().BeNull();
    entity.FindProperty(nameof(Transaction.Nickname)).Should().NotBeNull(
      "Nickname is a real column added by 0017_add_nickname_to_transactions");
  }
}
