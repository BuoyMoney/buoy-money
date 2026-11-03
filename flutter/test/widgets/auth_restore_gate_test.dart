import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/widgets/auth_restore_gate.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// Regression: reloading on a deep link built the protected page while the session
/// restore was still running, so its data calls went out tokenless and 401'd into a
/// false "session expired". The page must not be built until the restore resolves.
void main() {
  testWidgets('protected child is not built until the restore finishes', (tester) async {
    final auth = AuthProvider()..debugSetRestoring(true);
    var builds = 0;

    await tester.pumpWidget(MaterialApp(
      home: AuthRestoreGate(
        auth: auth,
        child: Builder(builder: (_) {
          builds++;
          return const Text('dashboard');
        }),
      ),
    ));

    expect(builds, 0, reason: 'dashboard must not build mid-restore');
    expect(find.text('dashboard'), findsNothing);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);

    auth.debugSetRestoring(false);
    await tester.pump();

    expect(builds, 1);
    expect(find.text('dashboard'), findsOneWidget);
  });
}
