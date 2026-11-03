using System.Linq;
using System.Text;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Infrastructure.Services.Import;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services.Import;

public class CsvImportParserTests
{
  private static ParsedImport Parse(string csv, ImportMapping? mapping = null) =>
    CsvImportParser.Parse(Encoding.UTF8.GetBytes(csv), mapping ?? new ImportMapping());

  [Fact]
  public void SingleAmountColumn_ReadsRows()
  {
    var parsed = Parse("Date,Description,Amount\n2026-01-02,Groceries,-45.20\n2026-01-03,Paycheck,1500.00\n");

    parsed.Rows.Should().HaveCount(2);
    parsed.Rows[0].Amount.Should().Be(-45.20m);
    parsed.Rows[0].Payee.Should().Be("Groceries");
    parsed.Rows[1].Amount.Should().Be(1500m);
    parsed.Rows[1].Date.Should().Be(new System.DateOnly(2026, 1, 3));
  }

  [Fact]
  public void DebitAndCreditColumns_SignTheirAmounts()
  {
    var parsed = Parse(
      "Date,Description,Debit,Credit\n" +
      "2026-01-02,Paycheck,,1500.00\n" +
      "2026-01-03,Groceries,45.20,\n");

    parsed.Rows.Select(r => r.Amount).Should().Equal(1500m, -45.20m);
    parsed.Rows.Should().OnlyContain(r => r.Error == null);
  }

  [Fact]
  public void PreambleAndSemicolons_AreSkippedAndDetected()
  {
    const string csv =
      "Account Summary;;\n" +
      "Generated on 03.02.2026;;\n" +
      ";;\n" +
      "Datum;Beschreibung;Betrag\n" +
      "25.01.2026;Miete;-1.200,00\n" +
      "26.01.2026;Gehalt;2.500,50\n";

    var parsed = Parse(csv, new ImportMapping { DateColumn = 0, AmountColumn = 2, DescriptionColumn = 1 });

    parsed.Detected.Delimiter.Should().Be(";");
    parsed.Rows.Should().HaveCount(2);
    parsed.Rows[0].Amount.Should().Be(-1200.00m);
    parsed.Rows[1].Amount.Should().Be(2500.50m);
    parsed.Rows[0].Date.Should().Be(new System.DateOnly(2026, 1, 25)); // 25 can only be a day
    parsed.Detected.NumberFormat.Should().Be("European");
    parsed.Warnings.Should().Contain(w => w.Contains("decimal comma"));
  }

  [Fact]
  public void HeaderRowPastPreamble_IsFoundWithoutHelp()
  {
    const string csv =
      "Bank statement for John\n" +
      "Period,January 2026\n" +
      "\n" +
      "Date,Description,Amount\n" +
      "2026-01-02,Coffee,-4.50\n";
    Parse(csv).Rows.Should().ContainSingle().Which.Payee.Should().Be("Coffee");
  }

  [Fact]
  public void UnrecognizedHeaders_StillOfferColumnsToChooseFrom()
  {
    const string csv = "Fecha,Concepto,Importe\n2026-01-02,Cafe,-4.50\n";
    var parsed = Parse(csv);
    parsed.Detected.Headers.Should().Equal("Fecha", "Concepto", "Importe");
    parsed.Warnings.Should().NotBeEmpty();
  }

  [Fact]
  public void AmbiguousDates_AreFlagged_AndUserChoiceResolvesThem()
  {
    const string csv = "Date,Description,Amount\n03/04/2026,A,-1.00\n05/06/2026,B,-2.00\n";

    var guessed = Parse(csv);
    guessed.DateOrderAmbiguous.Should().BeTrue();
    guessed.Rows[0].Date.Should().Be(new System.DateOnly(2026, 3, 4));

    var dayFirst = Parse(csv, new ImportMapping { DateOrder = ImportDateOrder.DayFirst });
    dayFirst.DateOrderAmbiguous.Should().BeFalse();
    dayFirst.Rows[0].Date.Should().Be(new System.DateOnly(2026, 4, 3));
  }

  [Fact]
  public void FlipSign_ReversesCardExportsWhereChargesArePositive()
  {
    var parsed = Parse("Date,Description,Amount\n2026-01-02,Shop,25.00\n", new ImportMapping { FlipSign = true });
    parsed.Rows[0].Amount.Should().Be(-25m);
  }

  [Fact]
  public void CategoryAccountAndIdColumns_AreDetected()
  {
    const string csv =
      "Date,Account,Description,Category,Amount,Transaction ID\n" +
      "2026-01-02,Visa,Shop,Groceries,-25.00,T-1\n" +
      "2026-01-03,Checking,Pay,Salary,100.00,T-2\n";
    var parsed = Parse(csv);

    parsed.Accounts.Select(a => a.Name).Should().BeEquivalentTo("Visa", "Checking");
    parsed.Accounts.First(a => a.Name == "Visa").Type.Should().Be("CreditCard");
    parsed.Rows[0].CategoryName.Should().Be("Groceries");
    parsed.Rows[0].SourceId.Should().Be("T-1");
    parsed.Categories.Select(c => c.Name).Should().BeEquivalentTo("Groceries", "Salary");
  }

  [Fact]
  public void IdenticalRows_KeepDistinctOccurrences()
  {
    var parsed = Parse("Date,Description,Amount\n2026-01-02,Coffee,-4.50\n2026-01-02,Coffee,-4.50\n");
    parsed.Rows.Select(r => r.Occurrence).Should().Equal(0, 1);
    ImportPlanner.BuildExternalId(ImportSource.Csv, 1, parsed.Rows[0])
      .Should().NotBe(ImportPlanner.BuildExternalId(ImportSource.Csv, 1, parsed.Rows[1]));
  }

  [Fact]
  public void BadRows_AreMarkedInvalid_NotDropped()
  {
    var parsed = Parse("Date,Description,Amount\nsomeday,Mystery,-1.00\n2026-01-02,Fine,abc\n2026-01-03,Good,-2.00\n");
    parsed.Rows.Should().HaveCount(3);
    parsed.Rows.Count(r => r.Error != null).Should().Be(2);
  }

  [Fact]
  public void FormulaCells_AreKeptAsPlainText()
  {
    var parsed = Parse("Date,Description,Amount\n2026-01-02,=HYPERLINK(\"http://x\"),-1.00\n");
    parsed.Rows[0].Payee.Should().StartWith("=HYPERLINK");
  }

  [Fact]
  public void QuotedFieldsWithCommas_StayWhole()
  {
    var parsed = Parse("Date,Description,Amount\n2026-01-02,\"Smith, John & Co\",-9.00\n");
    parsed.Rows[0].Payee.Should().Be("Smith, John & Co");
  }

  [Fact]
  public void NoUsableColumns_WarnsAndReturnsNoRows()
  {
    var parsed = Parse("Foo,Bar\n1,2\n");
    parsed.Rows.Should().BeEmpty();
    parsed.Warnings.Should().NotBeEmpty();
    parsed.Detected.SampleRows.Should().NotBeEmpty();
  }

  [Fact]
  public void Windows1252File_DecodesAccents()
  {
    var bytes = Encoding.GetEncoding(1252).GetBytes("Date,Description,Amount\n2026-01-02,Café,-3.00\n");
    var parsed = CsvImportParser.Parse(bytes, new ImportMapping());
    parsed.Rows[0].Payee.Should().Be("Café");
  }
}
