using System.Linq;
using System.Text;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Infrastructure.Services.Import;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services.Import;

public class YnabImportParserTests
{
  // Synthetic, in YNAB's Register layout.
  internal const string Register =
    "\"Account\",\"Flag\",\"Date\",\"Payee\",\"Category Group/Category\",\"Category Group\",\"Category\",\"Memo\",\"Outflow\",\"Inflow\",\"Cleared\"\n" +
    "\"Checking\",\"\",\"01/02/2026\",\"Starting Balance\",\"Inflow: Ready to Assign\",\"Inflow\",\"Ready to Assign\",\"\",\"$0.00\",\"$2,000.00\",\"Reconciled\"\n" +
    "\"Checking\",\"\",\"01/15/2026\",\"Grocery Mart\",\"Everyday: Groceries\",\"Everyday\",\"Groceries\",\"weekly shop\",\"$45.20\",\"$0.00\",\"Cleared\"\n" +
    "\"Checking\",\"\",\"01/16/2026\",\"Transfer : Savings\",\"\",\"\",\"\",\"\",\"$300.00\",\"$0.00\",\"Cleared\"\n" +
    "\"Savings\",\"\",\"01/16/2026\",\"Transfer : Checking\",\"\",\"\",\"\",\"\",\"$0.00\",\"$300.00\",\"Cleared\"\n" +
    "\"Visa Card\",\"\",\"01/20/2026\",\"Big Store\",\"Everyday: Groceries\",\"Everyday\",\"Groceries\",\"(Split 1/2) food\",\"$20.00\",\"$0.00\",\"Cleared\"\n" +
    "\"Visa Card\",\"\",\"01/20/2026\",\"Big Store\",\"Home: Supplies\",\"Home\",\"Supplies\",\"(Split 2/2) tools\",\"$30.00\",\"$0.00\",\"Cleared\"\n" +
    "\"Visa Card\",\"\",\"01/25/2026\",\"Visa Card\",\"Credit Card Payments: Visa Card\",\"Credit Card Payments\",\"Visa Card\",\"\",\"$0.00\",\"$50.00\",\"Cleared\"\n";

  internal const string Plan =
    "\"Month\",\"Category Group/Category\",\"Category Group\",\"Category\",\"Budgeted\",\"Activity\",\"Available\"\n" +
    "\"Dec 2025\",\"Everyday: Groceries\",\"Everyday\",\"Groceries\",\"$350.00\",\"-$300.00\",\"$50.00\"\n" +
    "\"Jan 2026\",\"Everyday: Groceries\",\"Everyday\",\"Groceries\",\"$400.00\",\"-$65.20\",\"$384.80\"\n" +
    "\"Jan 2026\",\"Home: Supplies\",\"Home\",\"Supplies\",\"$100.00\",\"-$30.00\",\"$70.00\"\n" +
    "\"Jan 2026\",\"Internal Master Category: Hidden\",\"Internal Master Category\",\"Hidden\",\"$0.00\",\"$0.00\",\"$0.00\"\n";

  private static ParsedImport Parse(string register, string? plan = null, ImportMapping? mapping = null)
  {
    var files = new System.Collections.Generic.List<byte[]> { Encoding.UTF8.GetBytes(register) };
    if (plan != null) files.Add(Encoding.UTF8.GetBytes(plan));
    return YnabImportParser.Parse(files, mapping ?? new ImportMapping());
  }

  [Fact]
  public void Sniff_TellsRegisterFromPlan()
  {
    YnabImportParser.Sniff(Encoding.UTF8.GetBytes(Register)).Should().Be(YnabImportParser.YnabFileKind.Register);
    YnabImportParser.Sniff(Encoding.UTF8.GetBytes(Plan)).Should().Be(YnabImportParser.YnabFileKind.Plan);
    YnabImportParser.Sniff(Encoding.UTF8.GetBytes("Date,Amount\n")).Should().Be(YnabImportParser.YnabFileKind.None);
  }

  [Fact]
  public void Register_ReadsAccountsAmountsAndCategories()
  {
    var parsed = Parse(Register);

    parsed.Accounts.Select(a => a.Name).Should().BeEquivalentTo("Checking", "Savings", "Visa Card");
    parsed.Accounts.First(a => a.Name == "Visa Card").Type.Should().Be("CreditCard");
    parsed.Accounts.First(a => a.Name == "Savings").Type.Should().Be("Savings");

    var grocery = parsed.Rows.First(r => r.Payee == "Grocery Mart");
    grocery.Amount.Should().Be(-45.20m);
    grocery.Date.Should().Be(new System.DateOnly(2026, 1, 15));
    grocery.CategoryGroup.Should().Be("Everyday");
    grocery.CategoryName.Should().Be("Groceries");
    grocery.Memo.Should().Be("weekly shop");
  }

  [Fact]
  public void Register_StartingBalanceIsKeptAndNoted()
  {
    var start = Parse(Register).Rows.First(r => r.IsStartingBalance);
    start.Amount.Should().Be(2000m);
    start.Memo.Should().Be("Starting balance");
  }

  [Fact]
  public void Register_TransfersBetweenImportedAccountsAreFlagged()
  {
    var transfers = Parse(Register).Rows.Where(r => r.TransferAccountName != null).ToList();
    transfers.Should().HaveCount(2);
    transfers.Should().OnlyContain(t => t.IsTransfer && t.CategoryName == null);
    transfers.Select(t => t.Amount).Should().BeEquivalentTo(new[] { -300m, 300m });
  }

  [Fact]
  public void Register_TransferToAnAccountNotInTheFile_IsNotFlagged()
  {
    var lonely = Register.Replace("Transfer : Savings", "Transfer : Brokerage");
    var parsed = Parse(lonely);
    parsed.Rows.First(r => r.TransferAccountName == "Brokerage").IsTransfer.Should().BeFalse();
  }

  [Fact]
  public void Register_SplitRowsAreSeparateRowsWithAWarning()
  {
    var parsed = Parse(Register);
    var pieces = parsed.Rows.Where(r => r.Payee == "Big Store").ToList();
    pieces.Should().HaveCount(2);
    pieces.Select(p => p.CategoryName).Should().BeEquivalentTo("Groceries", "Supplies");
    pieces[0].Memo.Should().Be("food"); // the (Split 1/2) prefix is removed
    parsed.Warnings.Should().Contain(w => w.Contains("split"));
  }

  [Fact]
  public void Register_CreditCardPaymentCategoryIsDropped()
  {
    var payment = Parse(Register).Rows.Last();
    payment.Amount.Should().Be(50m);
    payment.CategoryName.Should().BeNull();
  }

  [Fact]
  public void Register_CategoriesListSkipsBookkeepingGroups()
  {
    var names = Parse(Register).Categories.Select(c => c.Name).ToList();
    names.Should().BeEquivalentTo("Ready to Assign", "Groceries", "Supplies");
    Parse(Register).Categories.First(c => c.Name == "Ready to Assign").IsIncome.Should().BeTrue();
  }

  [Fact]
  public void Register_AmbiguousDatesNeedAChoice_DayFirstReadsThem()
  {
    var ambiguous = Register
      .Replace("01/15/2026", "03/04/2026")
      .Replace("01/16/2026", "05/06/2026")
      .Replace("01/20/2026", "07/08/2026")
      .Replace("01/25/2026", "09/10/2026")
      .Replace("01/02/2026", "02/01/2026");
    var guessed = Parse(ambiguous);
    guessed.DateOrderAmbiguous.Should().BeTrue();

    var dayFirst = Parse(ambiguous, null, new ImportMapping { DateOrder = ImportDateOrder.DayFirst });
    dayFirst.Rows.First(r => r.Payee == "Grocery Mart").Date.Should().Be(new System.DateOnly(2026, 4, 3));
  }

  [Fact]
  public void Register_EuropeanTsv_ReadsDecimalCommas()
  {
    var tsv = Register.Replace("\",\"", "\"\t\"")
      .Replace("$2,000.00", "2.000,00 EUR").Replace("$45.20", "45,20 EUR").Replace("$300.00", "300,00 EUR")
      .Replace("$20.00", "20,00 EUR").Replace("$30.00", "30,00 EUR").Replace("$50.00", "50,00 EUR").Replace("$0.00", "0,00 EUR");
    var parsed = Parse(tsv);

    parsed.Detected.Delimiter.Should().Be("\\t");
    parsed.Rows.First(r => r.IsStartingBalance).Amount.Should().Be(2000m);
    parsed.Rows.First(r => r.Payee == "Grocery Mart").Amount.Should().Be(-45.20m);
  }

  [Fact]
  public void Plan_ReadsBudgetedAmountsByMonth()
  {
    var parsed = Parse(Register, Plan);

    parsed.Budgets.Select(b => b.Month).Distinct().Should().BeEquivalentTo("2025-12", "2026-01");
    var jan = parsed.Budgets.Where(b => b.Month == "2026-01").ToList();
    jan.Should().HaveCount(2); // the Internal Master Category row is skipped
    jan.First(b => b.Category == "Groceries").Budgeted.Should().Be(400m);
    jan.First(b => b.Category == "Groceries").Available.Should().Be(384.80m);
  }

  [Fact]
  public void PlanAlone_StillYieldsABudget()
  {
    var parsed = YnabImportParser.Parse(new[] { Encoding.UTF8.GetBytes(Plan) }, new ImportMapping());
    parsed.Rows.Should().BeEmpty();
    parsed.Budgets.Should().NotBeEmpty();
  }

  [Fact]
  public void RegisterWithoutPlan_SaysBudgetsWereNotImported()
  {
    Parse(Register).Warnings.Should().Contain(w => w.Contains("Plan"));
  }

  [Fact]
  public void NotYnab_WarnsAndReadsNothing()
  {
    var parsed = Parse("Date,Amount\n2026-01-01,1\n");
    parsed.Rows.Should().BeEmpty();
    parsed.Warnings.Should().NotBeEmpty();
  }
}
