import 'package:intl/intl.dart';

import 'currency.dart';

/// Format a number as currency in the user's primary currency
/// ([primaryCurrency]; USD until the profile loads). Only safe for amounts
/// known to be in the primary currency — for bank-reported amounts that may
/// differ, use [formatCurrencyCode].
String formatCurrency(double amount) {
  final formatter = NumberFormat.currency(
    locale: 'en_US',
    symbol: _symbolFor(primaryCurrency),
    decimalDigits: 2,
  );
  return formatter.format(amount);
}

/// Unambiguous symbols per ISO code. Deliberately NOT intl's simpleCurrency
/// table, which renders CAD/AUD/etc. as a bare "$" — exactly the ambiguity this
/// map exists to remove. Anything absent falls back to "CODE 1,234.56".
const Map<String, String> _currencySymbols = {
  'USD': '\$',
  'CAD': 'CA\$',
  'EUR': '€',
  'GBP': '£',
  'AUD': 'A\$',
  'NZD': 'NZ\$',
  'MXN': 'MX\$',
  'JPY': '¥',
  'CHF': 'CHF ',
  'SEK': 'SEK ',
  'NOK': 'NOK ',
  'DKK': 'DKK ',
  'BRL': 'R\$',
};

/// Display symbol for an ISO code ("CA$", "€"; the code itself when unmapped).
String currencySymbol(String code) =>
    (_currencySymbols[code.toUpperCase()] ?? '${code.toUpperCase()} ').trim();

/// Symbol of the user's primary currency, for terse UI ("$50–$1K" filter
/// chips, amount-field prefixes) where a full formatted amount is too heavy.
String get primaryCurrencySymbol => currencySymbol(primaryCurrency);

String _symbolFor(String code) => _currencySymbols[code] ?? '$code ';

/// Format an amount in an explicit ISO-4217 currency ("CA$1,234.56", "€88.00",
/// "SEK 12.00"). Null/empty codes mean the bank didn't report one and format as
/// the primary currency. This is the only formatter safe for amounts that may
/// not be in the user's primary currency.
String formatCurrencyCode(double amount, String? currencyCode,
    {int decimalDigits = 2}) {
  final code =
      (currencyCode == null || currencyCode.isEmpty) ? primaryCurrency : currencyCode.toUpperCase();
  final formatter = NumberFormat.currency(
    locale: 'en_US',
    symbol: _symbolFor(code),
    decimalDigits: decimalDigits,
  );
  return formatter.format(amount);
}

/// Cached across every live-ticker frame — constructing a NumberFormat per
/// call is too wasteful at ticker frequency. Rebuilt when the primary currency
/// changes (login/profile load), which never happens mid-ticker.
NumberFormat? _liveCurrencyFormatter;
int _liveFormatterVersion = -1;

/// Format currency with four decimals for the live count-up ticker ($12.3456)
String formatCurrencyLive(double amount) {
  if (_liveCurrencyFormatter == null || _liveFormatterVersion != primaryCurrencyVersion) {
    _liveFormatterVersion = primaryCurrencyVersion;
    _liveCurrencyFormatter = NumberFormat.currency(
      locale: 'en_US',
      symbol: _symbolFor(primaryCurrency),
      decimalDigits: 4,
    );
  }
  return _liveCurrencyFormatter!.format(amount);
}

/// Format a number as compact currency ($1.5K, $2.3M, etc.) in the user's
/// primary currency.
String formatCompactCurrency(double amount) {
  final formatter = NumberFormat.compactCurrency(
    locale: 'en_US',
    symbol: _symbolFor(primaryCurrency),
    decimalDigits: 1,
  );
  return formatter.format(amount);
}

/// Format a number with commas (1,234,567)
String formatNumber(double amount, {int decimalDigits = 0}) {
  final pattern = decimalDigits > 0 ? '#,##0.${'0' * decimalDigits}' : '#,##0';
  final formatter = NumberFormat(pattern);
  return formatter.format(amount);
}

/// Format a date relative to today (Today, Yesterday, or date)
String formatRelativeDate(DateTime date) {
  final now = DateTime.now();
  final today = DateTime(now.year, now.month, now.day);
  final yesterday = today.subtract(const Duration(days: 1));
  final dateOnly = DateTime(date.year, date.month, date.day);
  
  if (dateOnly == today) {
    return 'Today';
  } else if (dateOnly == yesterday) {
    return 'Yesterday';
  } else if (dateOnly.isAfter(today.subtract(const Duration(days: 7)))) {
    return DateFormat.EEEE().format(date); // Day name
  } else if (dateOnly.year == now.year) {
    return DateFormat.MMMd().format(date); // "Jan 5"
  } else {
    return DateFormat.yMMMd().format(date); // "Jan 5, 2024"
  }
}

/// Format a date as short date (1/5/24)
String formatShortDate(DateTime date) {
  return DateFormat.yMd().format(date);
}

/// Format a date as medium date (Jan 5, 2024)
String formatMediumDate(DateTime date) {
  return DateFormat.yMMMd().format(date);
}

/// Format a date (alias for formatMediumDate)
String formatDate(DateTime date) {
  return formatMediumDate(date);
}

/// Format a date as full date (January 5, 2024)
String formatFullDate(DateTime date) {
  return DateFormat.yMMMMd().format(date);
}

/// Format a percentage (45.5%)
String formatPercent(double value, {int decimalDigits = 1}) {
  return '${value.toStringAsFixed(decimalDigits)}%';
}

/// Truncate text with ellipsis
String truncate(String text, int maxLength) {
  if (text.length <= maxLength) return text;
  return '${text.substring(0, maxLength - 3)}...';
}

/// Map a subscription tier value (int or string) to its display name.
/// Backend may return an int enum (Standard=1, Premium=2, PremiumInherited=3, Admin=99)
/// or a pre-mapped string. [fallback] is returned for null/unknown values.
String mapSubscriptionTier(dynamic value, {String fallback = 'Free'}) {
  if (value == null) return fallback;
  if (value is String) return value;
  return switch (value) {
    1 => 'Standard',
    2 => 'Premium',
    3 => 'PremiumInherited',
    99 => 'Admin',
    _ => fallback,
  };
}
