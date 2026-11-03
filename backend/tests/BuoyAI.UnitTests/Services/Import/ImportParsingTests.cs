using System.Linq;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Infrastructure.Services.Import;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services.Import;

public class ImportParsingTests
{
  [Theory]
  [InlineData("1,234.56", ImportNumberFormat.Us, 1234.56)]
  [InlineData("1234.56", ImportNumberFormat.Us, 1234.56)]
  [InlineData("-45.00", ImportNumberFormat.Us, -45.00)]
  [InlineData("(45.00)", ImportNumberFormat.Us, -45.00)]
  [InlineData("45.00-", ImportNumberFormat.Us, -45.00)]
  [InlineData("$1,000", ImportNumberFormat.Us, 1000)]
  [InlineData("45.00 DR", ImportNumberFormat.Us, -45.00)]
  [InlineData("45.00 CR", ImportNumberFormat.Us, 45.00)]
  [InlineData("1.234,56", ImportNumberFormat.European, 1234.56)]
  [InlineData("1 234,56", ImportNumberFormat.European, 1234.56)]
  [InlineData("12,5", ImportNumberFormat.European, 12.5)]
  [InlineData("-1.234,56 €", ImportNumberFormat.European, -1234.56)]
  public void TryParse_ReadsTheStatedFormat(string text, ImportNumberFormat format, double expected)
  {
    ImportNumberParser.TryParse(text, format, out var value).Should().BeTrue();
    value.Should().Be((decimal)expected);
  }

  // The old reader stripped every comma, so 1.234,56 silently became 1.23456 and 1,5 became 15.
  [Theory]
  [InlineData("1.234,56", ImportNumberFormat.Us)]
  [InlineData("1,5", ImportNumberFormat.Us)]
  [InlineData("1,234.56", ImportNumberFormat.European)]
  [InlineData("abc", ImportNumberFormat.Us)]
  [InlineData("", ImportNumberFormat.Us)]
  public void TryParse_RejectsWhatDoesNotFitTheFormat(string text, ImportNumberFormat format)
  {
    ImportNumberParser.TryParse(text, format, out _).Should().BeFalse();
  }

  [Theory]
  [InlineData("1.234,56", 1234.56)]
  [InlineData("1,234.56", 1234.56)]
  [InlineData("12,50", 12.50)]
  public void TryParse_AutoReadsClearCases(string text, double expected)
  {
    ImportNumberParser.TryParse(text, ImportNumberFormat.Auto, out var value).Should().BeTrue();
    value.Should().Be((decimal)expected);
  }

  [Fact]
  public void Detect_PicksEuropeanFromDecimalCommas()
  {
    ImportNumberParser.Detect(new[] { "12,50", "1.234,56", "-3,00" }).Should().Be(ImportNumberFormat.European);
    ImportNumberParser.Detect(new[] { "12.50", "1,234.56" }).Should().Be(ImportNumberFormat.Us);
    ImportNumberParser.Detect(new string?[] { null, "" }).Should().Be(ImportNumberFormat.Us);
  }

  [Theory]
  [InlineData("2026-01-02", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  [InlineData("20260102", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  [InlineData("01/02/2026", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  [InlineData("01/02/2026", ImportDateOrder.DayFirst, 2026, 2, 1)]
  [InlineData("31/01/2026", ImportDateOrder.DayFirst, 2026, 1, 31)]
  [InlineData("1/2/26", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  [InlineData("2026-01-02T10:30:00", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  [InlineData("2026-01-02 10:30:00", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  [InlineData("Jan 2, 2026", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  [InlineData("2 Jan 2026", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  [InlineData("2026.01.02", ImportDateOrder.MonthFirst, 2026, 1, 2)]
  public void DateParser_ReadsTheStatedOrder(string text, ImportDateOrder order, int y, int m, int d)
  {
    ImportDateParser.TryParse(text, order, out var date).Should().BeTrue();
    date.Should().Be(new System.DateOnly(y, m, d));
  }

  [Theory]
  [InlineData("13/13/2026")]
  [InlineData("2026-02-30")]
  [InlineData("not a date")]
  public void DateParser_RejectsImpossibleDates(string text)
  {
    ImportDateParser.TryParse(text, ImportDateOrder.MonthFirst, out _).Should().BeFalse();
  }

  [Fact]
  public void DateDetect_DayAbove12MeansDayFirst()
  {
    var (order, ambiguous) = ImportDateParser.Detect(new[] { "03/04/2026", "25/04/2026" });
    order.Should().Be(ImportDateOrder.DayFirst);
    ambiguous.Should().BeFalse();
  }

  [Fact]
  public void DateDetect_SecondNumberAbove12MeansMonthFirst()
  {
    var (order, ambiguous) = ImportDateParser.Detect(new[] { "03/04/2026", "04/25/2026" });
    order.Should().Be(ImportDateOrder.MonthFirst);
    ambiguous.Should().BeFalse();
  }

  [Fact]
  public void DateDetect_AllSmallNumbersIsAmbiguous()
  {
    var (order, ambiguous) = ImportDateParser.Detect(new[] { "03/04/2026", "05/06/2026" });
    order.Should().Be(ImportDateOrder.MonthFirst);
    ambiguous.Should().BeTrue();
  }

  [Fact]
  public void DateDetect_IsoDatesAreNeverAmbiguous()
  {
    var (order, ambiguous) = ImportDateParser.Detect(new[] { "2026-03-04", "2026-05-06" });
    order.Should().Be(ImportDateOrder.YearFirst);
    ambiguous.Should().BeFalse();
  }

  [Fact]
  public void PayeeSimilarity_IgnoresNoiseAndDigits()
  {
    ImportText.PayeeSimilarity("POS PURCHASE STARBUCKS #12345", "Starbucks").Should().BeGreaterThan(0.6);
    ImportText.PayeeSimilarity("AMZN Mktp US*2K4", "AMAZON MARKETPLACE").Should().BeLessThan(0.6);
    ImportText.PayeeSimilarity("", "Starbucks").Should().Be(0);
  }

  [Fact]
  public void Decode_FallsBackToWindows1252ForInvalidUtf8()
  {
    // "Café" in Windows-1252: 0xE9 is not valid UTF-8 on its own.
    var bytes = new byte[] { 0x43, 0x61, 0x66, 0xE9 };
    var (text, encoding) = ImportText.Decode(bytes);
    text.Should().Be("Café");
    encoding.Should().Be("windows-1252");
  }

  [Fact]
  public void Decode_StripsUtf8Bom()
  {
    var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("Date,Amount")).ToArray();
    ImportText.Decode(bytes).Text.Should().Be("Date,Amount");
  }
}
