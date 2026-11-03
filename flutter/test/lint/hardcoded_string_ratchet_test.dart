import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Ratchet against NEW hard-coded UI strings (#941, language workstream).
///
/// The FULL sweep is done: 874 keys live in lib/l10n/ and every language-
/// bearing Text() literal goes through AppLocalizations. The remaining
/// baseline covers only deliberate survivors:
///  - main.dart's global ErrorWidget.builder crash fallback (must render
///    outside any Localizations ancestor — dependency-free English)
///  - brand strings ("BuoyAI", "buoy-ai.com", "Plaid")
///  - data compositions (subtype·mask, name(mask), initials, +/- signs,
///    percent/number-only strings) with no language content
///
/// Fails HIGHER: put the new string in lib/l10n/app_en.arb (+ es translation)
/// and use AppLocalizations. Fails LOWER: lower the baseline to match.
/// A second tier (label:/hint:/toast literals, ~a few hundred) is tracked in
/// #941 and not counted here.
void main() {
  test('no new hard-coded Text() string literals in lib/', () {
    final libDir = Directory('lib');
    expect(libDir.existsSync(), isTrue,
        reason: 'run from the flutter/ package root');

    final textLiteral = RegExp('Text\\(\\s*[\'"]');
    var count = 0;
    final perFile = <String, int>{};
    for (final entity in libDir.listSync(recursive: true)) {
      if (entity is! File || !entity.path.endsWith('.dart')) continue;
      final rel = entity.path.replaceAll('\\', '/');
      if (rel.startsWith('lib/l10n/generated/')) continue;
      final matches = textLiteral.allMatches(entity.readAsStringSync()).length;
      if (matches > 0) {
        perFile[rel] = matches;
        count += matches;
      }
    }

    const baseline = 81;
    final top = (perFile.entries.toList()
          ..sort((a, b) => b.value.compareTo(a.value)))
        .take(5)
        .map((e) => '${e.key}: ${e.value}')
        .join(', ');

    expect(
      count,
      lessThanOrEqualTo(baseline),
      reason: 'New hard-coded Text() literal(s) added (count $count > baseline '
          '$baseline). Add the string to lib/l10n/app_en.arb and use '
          'AppLocalizations.of(context) instead. Top files: $top',
    );
  });
}
