import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/offline/connectivity_service.dart';
import 'package:buoy_ai/offline/offline_banner.dart';
import 'package:buoy_ai/offline/offline_status.dart';

void main() {
  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    connectivityService = ConnectivityService();
    offlineStatus = OfflineStatus();
  });

  Widget wrap() => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Stack(
            children: const [
              Positioned.fill(child: SizedBox()),
              OfflineBanner(),
            ],
          ),
        ),
      );

  void goOffline() {
    connectivityService.reportNetworkFailure();
    connectivityService.reportNetworkFailure();
  }

  /// Fires the pending collapse timer so no test ends with a live timer.
  Future<void> settleTimers(WidgetTester tester) async {
    await tester.pump(OfflineBanner.expandedDuration +
        const Duration(milliseconds: 400));
    await tester.pump(const Duration(milliseconds: 400));
  }

  testWidgets('hidden when online with nothing pending', (tester) async {
    await tester.pumpWidget(wrap());
    await tester.pump();

    expect(find.textContaining('Offline'), findsNothing);
    expect(find.textContaining('Syncing'), findsNothing);
    expect(find.byKey(const ValueKey('offline-banner-chip')), findsNothing);
  });

  testWidgets('offline with a cache shows the data-age message', (tester) async {
    goOffline();
    offlineStatus.markServedFromCache(DateTime.now().toUtc());

    await tester.pumpWidget(wrap());
    await tester.pump();

    expect(find.textContaining('Offline — showing data from'), findsOneWidget);
    await settleTimers(tester);
  });

  testWidgets('offline with queued writes shows the saved-on-device count',
      (tester) async {
    goOffline();
    offlineStatus.setPendingCount(2);

    await tester.pumpWidget(wrap());
    await tester.pump();

    expect(find.textContaining('Offline — 2 changes saved on this device'),
        findsOneWidget);
    await settleTimers(tester);
  });

  testWidgets('shows pending count while syncing after reconnect',
      (tester) async {
    // Online (default), but writes are still draining.
    offlineStatus.setPendingCount(3);

    await tester.pumpWidget(wrap());
    await tester.pump();

    expect(find.textContaining('Syncing 3 pending changes'), findsOneWidget);
    await settleTimers(tester);
  });

  testWidgets('collapses to an icon-only chip and re-expands on tap',
      (tester) async {
    goOffline();
    await tester.pumpWidget(wrap());
    await tester.pump();
    expect(find.text('Offline'), findsOneWidget);

    // After the expanded window the text is gone, only the chip remains.
    await settleTimers(tester);
    expect(find.text('Offline'), findsNothing);
    expect(find.byKey(const ValueKey('offline-banner-chip')), findsOneWidget);
    expect(find.byIcon(Icons.cloud_off_rounded), findsOneWidget);

    // Tapping the chip re-expands the banner.
    await tester.tap(find.byKey(const ValueKey('offline-banner-chip')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));
    expect(find.text('Offline'), findsOneWidget);
    await settleTimers(tester);
  });

  testWidgets('tapping the expanded banner reveals the local-sync explanation',
      (tester) async {
    goOffline();
    await tester.pumpWidget(wrap());
    await tester.pump();

    await tester.tap(find.text('Offline'));
    await tester.pump();
    expect(find.byKey(const ValueKey('offline-banner-info')), findsOneWidget);
    expect(find.textContaining('saved on this device and will sync'),
        findsOneWidget);

    // Close the info card; the collapse timer re-arms and fires.
    await tester.tap(find.text('Offline'));
    await tester.pump();
    expect(find.byKey(const ValueKey('offline-banner-info')), findsNothing);
    await settleTimers(tester);
  });
}
