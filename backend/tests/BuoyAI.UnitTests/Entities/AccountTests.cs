using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Entities;

public class AccountTests
{
  // Plaid's account names are frequently useless for telling accounts apart — a real user had
  // three separate cards all arrive named "CREDIT CARD", making every transaction on them
  // indistinguishable. The nickname exists to fix that, so it must win anywhere user-facing.

  [Fact]
  public void DisplayName_NicknameSet_ReturnsNickname()
  {
    var account = new Account { Name = "CREDIT CARD", Nickname = "Travel Card" };

    account.DisplayName.Should().Be("Travel Card");
  }

  [Fact]
  public void DisplayName_NoNickname_ReturnsBankName()
  {
    var account = new Account { Name = "BuoyAI Checking", Nickname = null };

    account.DisplayName.Should().Be("BuoyAI Checking");
  }

  [Fact]
  public void DisplayName_WhitespaceNickname_FallsBackToBankName()
  {
    // Clearing a nickname in the UI sends an empty string, not null.
    var account = new Account { Name = "BuoyAI Checking", Nickname = "   " };

    account.DisplayName.Should().Be("BuoyAI Checking");
  }

  [Fact]
  public void DisplayName_NoNicknameOrName_FallsBackToOfficialName()
  {
    var account = new Account { Name = "", OfficialName = "Big Bank Rewards Card" };

    account.DisplayName.Should().Be("Big Bank Rewards Card");
  }

  [Fact]
  public void DisplayName_NothingSet_FallsBackToAccountId()
  {
    var account = new Account { Id = 42, Name = "", OfficialName = "" };

    account.DisplayName.Should().Be("Account 42");
  }

  [Fact]
  public void DisplayName_DistinguishesCardsSharingTheSameBankName()
  {
    // The actual reported bug: without the nickname these are the same string.
    var travel = new Account { Id = 9, Name = "CREDIT CARD", Nickname = "Travel Card" };
    var cashback = new Account { Id = 10, Name = "CREDIT CARD", Nickname = "Cashback Card" };

    travel.DisplayName.Should().NotBe(cashback.DisplayName);
  }

  // DisplayName is computed, and there is no "DisplayName" column — schema is SQL-first via
  // DbUp (#533), so nothing would ever create one. If EF mapped it, every query touching
  // Accounts would fail against real Postgres with 'column a.DisplayName does not exist'.
  // The InMemory provider would NOT catch that: it has no schema to disagree with. So assert
  // the model directly, which is where mapping is decided and is provider-independent.
  [Fact]
  public void DisplayName_IsNotMappedToAColumn()
  {
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .Options;
    using var context = new BuoyAIDbContext(options);

    var account = context.Model.FindEntityType(typeof(Account));

    account.Should().NotBeNull();
    account!.FindProperty(nameof(Account.DisplayName)).Should().BeNull(
      "DisplayName is computed from Nickname/Name and has no column in the SQL-first schema");
  }

  [Fact]
  public void Nickname_IsStillMapped()
  {
    // Guards the flip side: the [NotMapped] above must not swallow the real column the
    // nickname is read from and written to.
    var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .Options;
    using var context = new BuoyAIDbContext(options);

    var account = context.Model.FindEntityType(typeof(Account));

    account!.FindProperty(nameof(Account.Nickname)).Should().NotBeNull();
  }
}
