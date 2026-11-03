import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Ratchet against hard-coded dollar signs in UI code (#941 step 3).
///
/// The sweep is DONE and the baseline is ZERO: every currency amount goes
/// through formatCurrency / formatCurrencyCode / primaryCurrencySymbol, which
/// follow the user's primary currency (even the referral credit is served
/// per-currency by /referral/reward-info). A new `\$` literal fails this test.
///
/// The exempt files below contain `\$` that is provably NOT currency display —
/// each entry must say why. Do not exempt a file to display an amount.
void main() {
  test('no new hard-coded dollar-sign literals in lib/', () {
    // Files whose '\$' is provably not a displayed currency amount.
    const exempt = {
      // Defines the symbol table itself.
      'lib/utils/formatters.dart',
      // PostHog protocol event name '$pageview' — an identifier, not money.
      'lib/services/analytics_service.dart',
      // Regex that MATCHES currency in AI text to sparkle it; changing the
      // pattern would change what gets highlighted, not what gets displayed.
      'lib/widgets/ai_sparkle.dart',
    };

    final libDir = Directory('lib');
    expect(libDir.existsSync(), isTrue,
        reason: 'run from the flutter/ package root');

    final escapedDollar = RegExp(r'\\\$');
    var count = 0;
    final perFile = <String, int>{};
    for (final entity in libDir.listSync(recursive: true)) {
      if (entity is! File || !entity.path.endsWith('.dart')) continue;
      final rel = entity.path.replaceAll('\\', '/');
      if (exempt.contains(rel)) continue;
      if (rel.startsWith('lib/l10n/generated/')) continue;
      final matches = escapedDollar.allMatches(entity.readAsStringSync()).length;
      if (matches > 0) {
        perFile[rel] = matches;
        count += matches;
      }
    }

    const baseline = 0;
    final top = (perFile.entries.toList()
          ..sort((a, b) => b.value.compareTo(a.value)))
        .take(5)
        .map((e) => '${e.key}: ${e.value}')
        .join(', ');

    expect(
      count,
      lessThanOrEqualTo(baseline),
      reason:
          'New hard-coded \\\$ literal(s) added (count $count > baseline $baseline). '
          'Format money via formatCurrency/formatCurrencyCode instead. Top files: $top',
    );
  });
}
