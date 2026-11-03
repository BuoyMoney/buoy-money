import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Structural verification of every translation file (#941).
///
/// The owner is not multilingual — these checks are the mechanical half of
/// trusting a translation without reading it. They catch the failure modes
/// that break the APP regardless of language quality:
///
///  1. A locale key that doesn't exist in the English template (dead weight,
///     usually a typo'd key that silently never renders).
///  2. Placeholder mismatch: en has {amount} but es doesn't (the value never
///     appears), or es invents {amout} (gen_l10n fails or renders literally).
///  3. Empty translations (renders a blank label).
///  4. Suspiciously-identical-to-English values: usually a key someone forgot
///     to translate. Genuinely identical words (brand names, "OK") go in the
///     allowlist below with a reason.
///
/// Language QUALITY (meaning, tone) cannot be asserted here. It needs a paid
/// native review before a locale is marketed. See lib/l10n/README.md.
void main() {
  final l10nDir = Directory('lib/l10n');

  Map<String, dynamic> readArb(String name) =>
      jsonDecode(File('${l10nDir.path}/$name').readAsStringSync())
          as Map<String, dynamic>;

  Set<String> messageKeys(Map<String, dynamic> arb) =>
      arb.keys.where((k) => !k.startsWith('@')).toSet();

  /// Placeholder names declared in the template's @metadata — the authority
  /// when present (regex extraction misreads plural-branch text like
  /// "=1{once}" as a placeholder name).
  Set<String> declaredPlaceholders(Map<String, dynamic> templateArb, String key) {
    final meta = templateArb['@$key'];
    if (meta is Map<String, dynamic>) {
      final ph = meta['placeholders'];
      if (ph is Map<String, dynamic>) return ph.keys.toSet();
    }
    return const {};
  }

  /// Placeholder names used inside an ICU message string.
  Set<String> placeholdersIn(String message) {
    // Matches {name} and the argument of plural/select: {count, plural, ...}
    final simple = RegExp(r'\{([a-zA-Z0-9_]+)(?:[,}])');
    return simple.allMatches(message).map((m) => m.group(1)!).toSet()
      ..removeAll({'plural', 'select', 'other'});
  }

  // Values allowed to be byte-identical to English in ANY locale (brand names,
  // badges, notation, punctuation fragments).
  const identicalAllowedEverywhere = {
    'appTitle', // brand name
    'wrapKickerMoneyWrapped', // "money wrapped" brand phrase
    'insMoneyWrapped', // "Money Wrapped" brand
    'uiBadgePro', // "PRO" badge
    'uiBadgeDev', // "DEV" badge
    'aiChatProBadge', // "PRO" badge
    'budPerMinuteRate', // unit notation, not language
    'dashGreetingComma', // punctuation fragment
    'dashDisplayNamePeriod', // punctuation fragment
  };

  // Words that happen to be the same as English only in a specific language.
  // Keyed by language code so they never mask a missing translation elsewhere.
  const identicalAllowedByLocale = <String, Set<String>>{
    'es': {
      'budColor', // "Color" is the same word in Spanish
      'txnColor',
      'savPersonal',
      'txnOriginal',
      'aiChatIdeas',
      'uiFooterLegal',
      'uiChartTotal',
    },
  };

  Set<String> identicalAllowedFor(String file) {
    final lang = file.substring('app_'.length, file.length - '.arb'.length)
        .split(RegExp('[_-]'))
        .first;
    return {
      ...identicalAllowedEverywhere,
      ...?identicalAllowedByLocale[lang],
    };
  }

  final template = readArb('app_en.arb');
  final templateKeys = messageKeys(template);

  final localeFiles = l10nDir
      .listSync()
      .whereType<File>()
      .map((f) => f.uri.pathSegments.last)
      .where((n) =>
          n.startsWith('app_') && n.endsWith('.arb') && n != 'app_en.arb')
      .toList();

  // English only (owner decision 2026-10-12). The loop below stays so a future
  // locale file is checked automatically; with none present it runs zero groups.
  test('English template parses and has messages', () {
    expect(templateKeys, isNotEmpty);
  });

  for (final file in localeFiles) {
    group(file, () {
      final arb = readArb(file);
      final keys = messageKeys(arb);

      test('has no keys missing from the English template', () {
        final orphans = keys.difference(templateKeys);
        expect(orphans, isEmpty,
            reason: 'Keys in $file that app_en.arb does not define '
                '(typo or stale): $orphans');
      });

      test('every translated message keeps the exact placeholder set', () {
        final problems = <String>[];
        for (final key in keys.intersection(templateKeys)) {
          final declared = declaredPlaceholders(template, key);
          if (declared.isNotEmpty) {
            // Every declared placeholder must be referenced in the translation
            // (as {name} or as a plural/select argument).
            final missing = declared
                .where((p) =>
                    !(arb[key] as String).contains('{$p}') &&
                    !(arb[key] as String).contains('{$p,'))
                .toList();
            if (missing.isNotEmpty) {
              problems.add('$key: $file is missing placeholder(s) $missing');
            }
          } else {
            final want = placeholdersIn(template[key] as String);
            final got = placeholdersIn(arb[key] as String);
            if (want.length != got.length || !want.containsAll(got)) {
              problems.add('$key: en uses $want, $file uses $got');
            }
          }
        }
        expect(problems, isEmpty, reason: problems.join('\n'));
      });

      test('no empty translations', () {
        final empties = keys
            .where((k) => (arb[k] as String).trim().isEmpty)
            .toList();
        expect(empties, isEmpty);
      });

      test('no untranslated English left behind', () {
        final suspicious = keys
            .intersection(templateKeys)
            .where((k) => !identicalAllowedFor(file).contains(k))
            .where((k) => arb[k] == template[k])
            .toList();
        expect(suspicious, isEmpty,
            reason:
                'Identical to English (forgot to translate? if genuinely the '
                'same word, add to identicalAllowedEverywhere or identicalAllowedByLocale with a reason): $suspicious');
      });
    });
  }
}
