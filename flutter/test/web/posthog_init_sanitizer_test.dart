@TestOn('vm')
library;

import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// web/posthog_init.js runs in the browser, before any Dart, and stamps every PostHog event
/// with the page URL. Its sanitizer must strip the Cloud link secrets, including the
/// portal's sign-in and sign-up detours (`/cloud/register?redirect=%2Flink%3Fcode%3D..`).
/// Runs the real script under Node with a stand-in `window.posthog`; skipped without Node.
void main() {
  final node = _which('node');

  Future<Map<String, dynamic>> sanitize(Map<String, String> props) async {
    final script = File('web/posthog_init.js').readAsStringSync();
    final harness = '''
var window = { posthog: { init: function (key, config) { window.config = config; } } };
$script
process.stdout.write(JSON.stringify(window.config.sanitize_properties(${jsonEncode(props)})));
''';
    final dir = Directory.systemTemp.createTempSync('posthog_init_test');
    try {
      final file = File('${dir.path}/harness.js')..writeAsStringSync(harness);
      final result = await Process.run(node!, [file.path]);
      expect(result.exitCode, 0, reason: '${result.stderr}');
      return jsonDecode(result.stdout as String) as Map<String, dynamic>;
    } finally {
      dir.deleteSync(recursive: true);
    }
  }

  test('portal sign-in and sign-up detours lose the pairing code', () async {
    final out = await sanitize({
      r'$current_url':
          'https://buoy-ai.com/cloud/register?redirect=%2Flink%3Fcode%3DBCDF-GH23',
      r'$referrer':
          'https://buoy-ai.com/cloud/login?redirect=%2Flink%3Fcode%3DBCDF-GH23',
      r'$pathname': '/cloud/link?code=BCDF-GH23',
    });
    expect(out[r'$current_url'], 'https://buoy-ai.com/cloud/register');
    expect(out[r'$referrer'], 'https://buoy-ai.com/cloud/login');
    expect(jsonEncode(out), isNot(contains('BCDF')));
  }, skip: node == null ? 'node is not installed' : false);

  test('the main app link handshake and a code in an API path are scrubbed',
      () async {
    final out = await sanitize({
      'a': 'https://buoy-ai.com/app/cloud/link?state=SECRET',
      'b': '/login?redirect=%2Fcloud%2Flink%3Fstate%3DSECRET',
      'c':
          'https://buoy-ai.com/api/cloud-servers/link-sessions/BCDF-GH23/approve',
    });
    expect(out['a'], 'https://buoy-ai.com/app/cloud/link');
    expect(out['b'], '/login');
    expect(out['c'],
        'https://buoy-ai.com/api/cloud-servers/link-sessions/[code]/approve');
  }, skip: node == null ? 'node is not installed' : false);

  test('unrelated URLs are left alone', () async {
    final out = await sanitize({
      'a': '/linked-accounts?x=1',
      'b': '/connect?zipcode=66601',
      'c': 'https://buoy-ai.com/app/budget?month=10',
    });
    expect(out, {
      'a': '/linked-accounts?x=1',
      'b': '/connect?zipcode=66601',
      'c': 'https://buoy-ai.com/app/budget?month=10',
    });
  }, skip: node == null ? 'node is not installed' : false);
}

String? _which(String name) {
  try {
    final result = Process.runSync(
        Platform.isWindows ? 'where' : 'which', [name],
        runInShell: true);
    if (result.exitCode != 0) return null;
    final first =
        (result.stdout as String).split(RegExp(r'\r?\n')).first.trim();
    return first.isEmpty ? null : first;
  } catch (_) {
    return null;
  }
}
