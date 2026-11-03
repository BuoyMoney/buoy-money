using BuoyAI.Core.ValueObjects;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.ValueObjects;

/// <summary>
/// #941: Normalize gates the user's primary-currency preference; Format replaces
/// bare ":C" (which renders "¤" in locale-less containers and hard-codes USD) in
/// user-facing text. Symbols must stay unambiguous — CAD is never a bare "$".
/// </summary>
public class CurrencyCodesTests
{
  [Theory]
  [InlineData("cad", "CAD")]
  [InlineData(" USD ", "USD")]
  [InlineData("EUR", "EUR")]
  public void Normalize_SupportedCodes_Uppercases(string input, string expected) =>
    CurrencyCodes.Normalize(input).Should().Be(expected);

  [Theory]
  [InlineData("DOGE")]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData(null)]
  public void Normalize_UnsupportedOrEmpty_ReturnsNull(string? input) =>
    CurrencyCodes.Normalize(input).Should().BeNull();

  [Fact]
  public void Format_NullCode_FormatsAsUsd() =>
    CurrencyCodes.Format(1234.5m, null).Should().Be("$1,234.50");

  [Fact]
  public void Format_Cad_UsesUnambiguousSymbol() =>
    CurrencyCodes.Format(1234.5m, "cad").Should().Be("CA$1,234.50");

  [Fact]
  public void Format_Negative_PutsSignBeforeSymbol() =>
    CurrencyCodes.Format(-12m, "EUR").Should().Be("-€12.00");

  [Fact]
  public void Format_UnknownCode_FallsBackToCodePrefix() =>
    CurrencyCodes.Format(5m, "XTS").Should().Be("XTS 5.00");

  [Fact]
  public void Format_ZeroDecimals_RoundsForDisplay() =>
    CurrencyCodes.Format(1234.56m, "USD", 0).Should().Be("$1,235");
}
