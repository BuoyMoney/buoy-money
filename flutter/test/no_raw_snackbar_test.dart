@TestOn('vm')
library;

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Guards the invariant established when every toast in the app was migrated
/// onto `showAppToast`: a raw `ScaffoldMessenger.showSnackBar` renders as the
/// Material 3 default (a near-white bar on a #04040A app) and, with the default
/// `fixed` behavior, is pinned under the bottom nav inside the Android gesture
/// zone where it can't be swiped away. Use `showAppToast` instead.
void main() {
  test('no raw showSnackBar outside the shared toast helper', () {
    const helper = 'lib/widgets/app_toast.dart';

    final offenders = <String>[];
    for (final entity in Directory('lib').listSync(recursive: true)) {
      if (entity is! File || !entity.path.endsWith('.dart')) continue;

      final path = entity.path.replaceAll(r'\', '/');
      if (path.endsWith(helper)) continue;

      final lines = entity.readAsLinesSync();
      for (var i = 0; i < lines.length; i++) {
        if (lines[i].contains('showSnackBar')) {
          offenders.add('$path:${i + 1}');
        }
      }
    }

    expect(
      offenders,
      isEmpty,
      reason: 'Use showAppToast(context, message, type: ...) from '
          '$helper instead of a raw SnackBar. Offending lines:\n'
          '${offenders.join('\n')}',
    );
  });
}
