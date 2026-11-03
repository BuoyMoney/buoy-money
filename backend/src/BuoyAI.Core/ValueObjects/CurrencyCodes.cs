namespace BuoyAI.Core.ValueObjects;

/// <summary>
/// The ISO-4217 currency codes a user may pick as their primary display currency.
/// Deliberately curated (expansion shortlist + neighbours, docs/international_readiness.md)
/// rather than the full ISO table: every code here must render correctly client-side.
/// Bank-reported codes on accounts/transactions are stored as-is and are NOT limited
/// to this list — this gates only the user's own preference. (#941 step 2)
/// </summary>
public static class CurrencyCodes
{
  public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "USD", "CAD", "EUR", "GBP", "AUD", "NZD", "CHF", "JPY", "SEK", "NOK", "DKK", "MXN", "BRL"
  };

  /// <summary>Uppercased canonical form when supported; null otherwise.</summary>
  public static string? Normalize(string? code)
  {
    if (string.IsNullOrWhiteSpace(code)) return null;
    var trimmed = code.Trim();
    return Supported.Contains(trimmed) ? trimmed.ToUpperInvariant() : null;
  }

  // Unambiguous display symbols, mirroring flutter/lib/utils/formatters.dart —
  // keep the two maps in sync. Deliberately NOT CultureInfo currency symbols:
  // a bare ":C" renders "¤" on locale-less Linux containers, and dollar
  // currencies (CAD/AUD/...) would all render as an ambiguous "$".
  private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
  {
    ["USD"] = "$",
    ["CAD"] = "CA$",
    ["EUR"] = "€",
    ["GBP"] = "£",
    ["AUD"] = "A$",
    ["NZD"] = "NZ$",
    ["MXN"] = "MX$",
    ["JPY"] = "¥",
    ["CHF"] = "CHF ",
    ["SEK"] = "SEK ",
    ["NOK"] = "NOK ",
    ["DKK"] = "DKK ",
    ["BRL"] = "R$",
  };

  /// <summary>
  /// Format an amount for user-facing text in the given currency ("$1,234.56",
  /// "CA$1,234.56", "-€12.00", "XTS 5.00"). Null/empty code formats as USD (the
  /// pre-#941 behavior for users who never chose). Replaces bare ":C", which
  /// renders "¤" in locale-less containers and hard-codes the seller's currency.
  /// </summary>
  public static string Format(decimal amount, string? code, int decimals = 2)
  {
    var upper = string.IsNullOrWhiteSpace(code) ? "USD" : code.Trim().ToUpperInvariant();
    var symbol = Symbols.TryGetValue(upper, out var s) ? s : upper + " ";
    var magnitude = Math.Abs(amount).ToString("N" + decimals, System.Globalization.CultureInfo.InvariantCulture);
    return amount < 0 ? $"-{symbol}{magnitude}" : $"{symbol}{magnitude}";
  }
}
