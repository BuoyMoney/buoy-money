/// Currency partitioning helpers.
///
/// Amounts in different ISO-4217 currencies must never be summed together.
/// Everything that totals across accounts or transactions goes through this
/// seam, so when the user's primary currency becomes a real setting
/// (Users.Currency, currency PR 2) it can be swapped in one place.
library;

String _primaryCurrency = 'USD';
int _primaryCurrencyVersion = 0;

/// The user's primary display currency (Users.Currency). Set from the profile
/// at login via [setPrimaryCurrency]; defaults to USD until then. All callers
/// read it through [isPrimaryCurrency] / [splitByCurrency] / the formatters.
String get primaryCurrency => _primaryCurrency;

/// Bumps whenever [primaryCurrency] changes — formatters use it to invalidate
/// cached NumberFormat instances.
int get primaryCurrencyVersion => _primaryCurrencyVersion;

/// Set the primary display currency from the user's profile. Null/empty falls
/// back to USD (older backend responses omit it). Call on login/profile load
/// and on logout (with null) so a shared device never leaks the previous
/// user's currency into the next session's formatting.
void setPrimaryCurrency(String? code) {
  final normalized =
      (code == null || code.trim().isEmpty) ? 'USD' : code.trim().toUpperCase();
  if (normalized == _primaryCurrency) return;
  _primaryCurrency = normalized;
  _primaryCurrencyVersion++;
}

/// Currencies a user may pick as their primary display currency. Mirrors the
/// backend allowlist (CurrencyCodes.Supported) — keep the two in sync.
const List<String> supportedCurrencies = [
  'USD', 'CAD', 'EUR', 'GBP', 'AUD', 'NZD', 'CHF',
  'JPY', 'SEK', 'NOK', 'DKK', 'MXN', 'BRL',
];

/// Parse a user-typed amount, accepting BOTH decimal conventions: "1,234.56"
/// and "1.234,56" and the bare "12,50" a German/French keyboard produces.
/// The last separator present is the decimal one; a lone comma with more than
/// two digits after it is a thousands separator ("12,500" = 12500). Replaces
/// bare double.tryParse on amount fields, which rejected or silently mangled
/// comma-decimal input (#941).
double? parseFlexibleAmount(String? input) {
  if (input == null) return null;
  var t = input.trim().replaceAll(RegExp(r'[^\d.,\-]'), '');
  if (t.isEmpty) return null;
  final lastComma = t.lastIndexOf(',');
  final lastDot = t.lastIndexOf('.');
  if (lastComma >= 0 && lastDot >= 0) {
    if (lastComma > lastDot) {
      t = t.replaceAll('.', '').replaceAll(',', '.');
    } else {
      t = t.replaceAll(',', '');
    }
  } else if (lastComma >= 0) {
    final digitsAfter = t.length - lastComma - 1;
    final commaCount = ','.allMatches(t).length;
    if (commaCount == 1 && digitsAfter <= 2) {
      t = t.replaceAll(',', '.');
    } else {
      t = t.replaceAll(',', '');
    }
  }
  return double.tryParse(t);
}

/// Whether [code] counts as the primary currency. A null/empty code means the
/// bank didn't report one; those have always been treated as the primary
/// currency and continue to be.
bool isPrimaryCurrency(String? code) {
  if (code == null || code.isEmpty) return true;
  return code.toUpperCase() == primaryCurrency;
}

/// Totals partitioned by currency: one number that is safe to display as the
/// primary currency, plus per-code totals for everything else.
class CurrencySplit {
  /// Sum of the amounts whose currency is the primary currency.
  final double primaryTotal;

  /// Uppercased ISO code -> summed amount, for every non-primary currency seen.
  final Map<String, double> otherTotals;

  const CurrencySplit({required this.primaryTotal, required this.otherTotals});

  bool get hasOtherCurrencies => otherTotals.isNotEmpty;

  /// Non-primary codes, alphabetical, for stable display.
  List<String> get otherCodes => otherTotals.keys.toList()..sort();
}

/// Partition-sum [items]: amounts in the primary currency fold into
/// [CurrencySplit.primaryTotal]; every other currency accumulates under its own
/// code. Never mixes currencies into one number.
CurrencySplit splitByCurrency<T>(
  Iterable<T> items, {
  required String? Function(T) currencyOf,
  required double Function(T) amountOf,
}) {
  var primary = 0.0;
  final others = <String, double>{};
  for (final item in items) {
    final code = currencyOf(item);
    if (isPrimaryCurrency(code)) {
      primary += amountOf(item);
    } else {
      final key = code!.toUpperCase();
      others[key] = (others[key] ?? 0.0) + amountOf(item);
    }
  }
  return CurrencySplit(primaryTotal: primary, otherTotals: others);
}
