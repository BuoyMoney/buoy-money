using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Infrastructure.Services.Import;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services.Import;

public class ActualImportParserTests
{
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void Parse_ReadsAccountsTransactionsAndCategories_InBothSchemaSpellings(bool newer)
  {
    var parsed = ActualImportParser.Parse(ActualFixture.Build(newer));

    // Deleted account is gone; the closed one and the off-budget one come through with sensible types.
    parsed.Accounts.Select(a => a.Name).Should().BeEquivalentTo("Checking", "Savings", "Old Card", "Brokerage");
    parsed.Accounts.First(a => a.Name == "Savings").Type.Should().Be("Savings");
    parsed.Accounts.First(a => a.Name == "Old Card").Closed.Should().BeTrue();
    parsed.Accounts.First(a => a.Name == "Old Card").Type.Should().Be("CreditCard");
    parsed.Accounts.First(a => a.Name == "Brokerage").Type.Should().Be("Investment");

    // Skipped: the tombstoned row, the split parent, and the row on the deleted account.
    parsed.Rows.Select(r => r.SourceId).Should().BeEquivalentTo(new[] { "t0", "t1", "t2", "t3", "t4", "t6a", "t6b" });
    parsed.Warnings.Should().Contain(w => w.Contains("deleted in Actual"));
    parsed.Warnings.Should().Contain(w => w.Contains("split"));

    var groceries = parsed.Rows.First(r => r.SourceId == "t1");
    groceries.Amount.Should().Be(-45.20m);
    groceries.Date.Should().Be(new System.DateOnly(2026, 1, 15));
    groceries.Payee.Should().Be("Grocery Mart");      // merged payee resolved to the survivor
    groceries.OriginalDescription.Should().Be("GROCERY MART #12");
    groceries.Memo.Should().Be("weekly");
    groceries.CategoryName.Should().Be("Groceries");
    groceries.CategoryGroup.Should().Be("Everyday");

    parsed.Rows.First(r => r.SourceId == "t2").CategoryName.Should().Be("Salary");
    parsed.Categories.First(c => c.Name == "Salary").IsIncome.Should().BeTrue();
  }

  [Fact]
  public void Parse_StartingBalanceIsKept()
  {
    var start = ActualImportParser.Parse(ActualFixture.Build()).Rows.First(r => r.SourceId == "t0");
    start.IsStartingBalance.Should().BeTrue();
    start.Amount.Should().Be(2000m);
  }

  [Fact]
  public void Parse_TransferLegsAreBothKeptAndFlagged()
  {
    var rows = ActualImportParser.Parse(ActualFixture.Build()).Rows;
    var out1 = rows.First(r => r.SourceId == "t3");
    var in1 = rows.First(r => r.SourceId == "t4");
    out1.IsTransfer.Should().BeTrue();
    out1.TransferAccountName.Should().Be("Savings");
    in1.IsTransfer.Should().BeTrue();
    in1.TransferAccountName.Should().Be("Checking");
    out1.CategoryName.Should().BeNull();
  }

  [Fact]
  public void Parse_SplitChildrenInheritTheParentPayee()
  {
    var rows = ActualImportParser.Parse(ActualFixture.Build()).Rows;
    rows.First(r => r.SourceId == "t6a").Payee.Should().Be("Big Store");
    rows.First(r => r.SourceId == "t6b").CategoryName.Should().Be("Fun");
  }

  [Fact]
  public void Parse_ReadsBudgetsFromBothMonthSpellings()
  {
    var budgets = ActualImportParser.Parse(ActualFixture.Build()).Budgets;

    budgets.Should().HaveCount(3); // the zero-amount row is skipped
    budgets.Select(b => b.Month).Distinct().Should().BeEquivalentTo("2025-12", "2026-01");
    budgets.First(b => b.Month == "2026-01" && b.Category == "Groceries").Budgeted.Should().Be(400m);
    budgets.First(b => b.Category == "Fun").Month.Should().Be("2026-01");
  }

  [Fact]
  public void Parse_RowsAreStableAcrossRuns()
  {
    var bytes = ActualFixture.Build();
    var a = ActualImportParser.Parse(bytes).Rows.Select(r => (r.SourceId, r.Amount)).ToList();
    var b = ActualImportParser.Parse(bytes).Rows.Select(r => (r.SourceId, r.Amount)).ToList();
    a.Should().Equal(b);
  }

  [Fact]
  public void LooksLikeActualZip_RequiresDbSqlite()
  {
    ActualImportParser.LooksLikeActualZip(ActualFixture.Build()).Should().BeTrue();
    ActualImportParser.LooksLikeActualZip(Encoding.UTF8.GetBytes("Date,Amount")).Should().BeFalse();

    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
    {
      using var w = new StreamWriter(zip.CreateEntry("Register.csv").Open());
      w.Write("a,b");
    }
    ActualImportParser.LooksLikeActualZip(ms.ToArray()).Should().BeFalse();
  }

  [Fact]
  public void Parse_ZipWithoutDatabase_IsRefused()
  {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
    {
      using var w = new StreamWriter(zip.CreateEntry("notes.txt").Open());
      w.Write("hello");
    }
    var act = () => ActualImportParser.Parse(ms.ToArray());
    act.Should().Throw<ImportException>().WithMessage("*db.sqlite*");
  }

  [Fact]
  public void Parse_DatabaseThatIsNotActual_IsRefused()
  {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
    {
      using var s = zip.CreateEntry("db.sqlite").Open();
      s.Write(Encoding.UTF8.GetBytes("this is not a sqlite database"));
    }
    var act = () => ActualImportParser.Parse(ms.ToArray());
    act.Should().Throw<System.Exception>();
  }

  // A zip that would expand to far more than the cap must stop early, not fill the disk.
  [Fact]
  public void Parse_ZipBomb_IsRefused()
  {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
    {
      using var s = zip.CreateEntry("db.sqlite", CompressionLevel.SmallestSize).Open();
      var block = new byte[1024 * 1024];
      for (var i = 0; i < 210; i++) s.Write(block);
    }
    ms.Length.Should().BeLessThan(5 * 1024 * 1024);

    var act = () => ActualImportParser.Parse(ms.ToArray());
    act.Should().Throw<ImportException>().WithMessage("*too large*");
  }
}
