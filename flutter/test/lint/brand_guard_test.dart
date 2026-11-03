import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Keeps the old product name out of anything a person can read.
///
/// The app is called Buoy Money (see `AppBrand`). Identifiers keep their old spelling on purpose (package
/// and bundle ids, storage keys, the `buoy-ai.com` host, OAuth scopes, the passkey relying-party id), and
/// none of those contain the capitalised word this test looks for. What is allowed to say "BuoyAI":
///  - the company, "BuoyAI LLC" (legal text and the copyright line)
///  - the paid hosted add-ons, "BuoyAI Cloud"
///  - the `BuoyAIApp` class name
///  - "formerly BuoyAI", where we tell people the new name was the old one
///  - comments
/// The backend and the marketing site have the same guard in `backend/tests/.../RenameGuardTests.cs`.
void main() {
  final allowed = RegExp(
      r'BuoyAIApp|_BuoyAIAppState|BuoyAI LLC|BuoyAI Cloud|formerly BuoyAI');
  final oldName = RegExp(r'BuoyAI|Buoy AI');

  bool isComment(String line) {
    final t = line.trim();
    return t.startsWith('//') ||
        t.startsWith('*') ||
        t.startsWith('/*') ||
        t.startsWith('<!--') ||
        t.startsWith('#');
  }

  List<String> offences(File file) {
    final out = <String>[];
    final lines = file.readAsLinesSync();
    for (var i = 0; i < lines.length; i++) {
      final line = lines[i];
      if (isComment(line)) continue;
      if (oldName.hasMatch(line.replaceAll(allowed, ''))) {
        out.add('${file.path.replaceAll('\\', '/')}:${i + 1}: ${line.trim()}');
      }
    }
    return out;
  }

  test('no Dart source or string table says BuoyAI outside the allowed forms',
      () {
    final files = <File>[
      for (final e in Directory('lib').listSync(recursive: true))
        if (e is File &&
            e.path.endsWith('.dart') &&
            !e.path.replaceAll('\\', '/').contains('lib/l10n/generated/'))
          e,
      File('lib/l10n/app_en.arb'),
    ];

    final found = [for (final f in files) ...offences(f)];

    expect(found, isEmpty,
        reason:
            'Use AppBrand.name (or the ARB string) instead:\n${found.join('\n')}');
  });

  test('the platform shells say Buoy Money', () {
    final shells = [
      File('web/index.html'),
      File('web/manifest.json'),
      File('android/app/src/main/AndroidManifest.xml'),
      File('ios/Runner/Info.plist'),
      File('pubspec.yaml'),
    ];

    final found = [for (final f in shells) ...offences(f)];

    expect(found, isEmpty,
        reason:
            'The label people see must be Buoy Money:\n${found.join('\n')}');
    // The Android label is a manifest placeholder so profile builds can rename themselves.
    expect(File('android/app/src/main/AndroidManifest.xml').readAsStringSync(),
        contains(r'android:label="${appLabel}"'));
    expect(File('android/app/build.gradle.kts').readAsStringSync(),
        contains('manifestPlaceholders["appLabel"] = "Buoy Money"'));
    expect(File('web/manifest.json').readAsStringSync(),
        contains('"name": "Buoy Money"'));
    expect(File('ios/Runner/Info.plist').readAsStringSync(),
        contains('<string>Buoy Money</string>'));
  });

  test('the identifiers that must not change are still the old ones', () {
    // Changing any of these signs people out, orphans stored data or breaks store identity.
    expect(File('android/app/build.gradle.kts').readAsStringSync(),
        contains('com.buoyai.buoy_ai'));
    expect(File('pubspec.yaml').readAsStringSync(), contains('name: buoy_ai'));
    expect(File('lib/config/app_config.dart').readAsStringSync(),
        contains("'https://buoy-ai.com/api'"));
  });
}
