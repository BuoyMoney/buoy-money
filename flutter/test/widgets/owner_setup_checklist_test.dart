import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/auth_models.dart';
import 'package:buoy_ai/services/owner_setup_service.dart';
import 'package:buoy_ai/utils/server_roles.dart';
import 'package:buoy_ai/widgets/owner_setup_checklist.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// The self-hosted owner's setup steps on the dashboard.
void main() {
  late List<String> opened;
  late int serverChecks;

  OwnerSetupService service({
    bool banks = false,
    bool ai = false,
    bool household = false,
    bool notifications = false,
    NotificationSetup? setup,
    bool failBanks = false,
  }) {
    Future<bool> check(bool value, {bool fail = false}) async {
      serverChecks++;
      if (fail) throw Exception('offline');
      return value;
    }

    return OwnerSetupService(
      hasAccounts: () => check(banks, fail: failBanks),
      aiEnabled: () => check(ai),
      hasHousehold: () => check(household),
      notificationSetup: () async {
        serverChecks++;
        if (setup != null) return setup;
        if (notifications) return NotificationSetup.ready;
        return ServerEdition.current.value.pushRelayUrl == null
            ? NotificationSetup.noRelay
            : NotificationSetup.phoneNotSetUp;
      },
    );
  }

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    SharedPreferences.setMockInitialValues({});
    opened = [];
    serverChecks = 0;
    ServerEdition.current.value = const ServerCapabilities(edition: 'selfhost');
  });

  tearDown(() => ServerEdition.current.value = ServerCapabilities.hosted);

  Future<void> pump(
    WidgetTester tester,
    OwnerSetupService svc, {
    bool isAdmin = true,
    Future<bool> Function()? enablePush,
    Future<void> Function()? refreshServer,
  }) async {
    tester.view.physicalSize = const Size(900, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(
        body: SingleChildScrollView(
          child: OwnerSetupChecklist(
            service: svc,
            isAdmin: isAdmin,
            navigate: (context, path) async => opened.add(path),
            enablePush: enablePush ?? () async => false,
            refreshServer: refreshServer ?? () async {},
          ),
        ),
      ),
    ));
    await tester.pumpAndSettle();
  }

  Finder stepDone(String step) => find.descendant(
      of: find.byKey(ValueKey('owner-setup-$step')),
      matching: find.byKey(const ValueKey('owner-setup-step-done')));

  group('who sees it', () {
    testWidgets('the owner of a self-hosted server sees the four steps', (tester) async {
      await pump(tester, service());

      expect(find.byKey(const ValueKey('owner-setup')), findsOneWidget);
      expect(find.text('Set up your server'), findsOneWidget);
      expect(find.text('Add your banks'), findsOneWidget);
      expect(find.text('Turn on AI'), findsOneWidget);
      expect(find.text('Invite your household'), findsOneWidget);
      expect(find.text('Phone notifications'), findsOneWidget);
      expect(find.text('0 of 4 done'), findsOneWidget);
    });

    testWidgets('hidden on the hosted service, with no server calls', (tester) async {
      ServerEdition.current.value = ServerCapabilities.hosted;
      await pump(tester, service());

      expect(find.byKey(const ValueKey('owner-setup')), findsNothing);
      expect(serverChecks, 0);
    });

    testWidgets('hidden for a household member, with no server calls', (tester) async {
      await pump(tester, service(), isAdmin: false);

      expect(find.byKey(const ValueKey('owner-setup')), findsNothing);
      expect(serverChecks, 0);
    });

    testWidgets('hidden once every step is done', (tester) async {
      await pump(tester, service(banks: true, ai: true, household: true, notifications: true));

      expect(find.byKey(const ValueKey('owner-setup')), findsNothing);
    });
  });

  group('steps', () {
    testWidgets('done steps show a check and Done; open steps a number', (tester) async {
      await pump(tester, service(banks: true, notifications: true));

      expect(find.text('2 of 4 done'), findsOneWidget);
      expect(stepDone('banks'), findsOneWidget);
      expect(stepDone('notifications'), findsOneWidget);
      expect(stepDone('ai'), findsNothing);
      expect(stepDone('household'), findsNothing);
      expect(find.text('Done'), findsNWidgets(2));
      expect(find.descendant(of: find.byKey(const ValueKey('owner-setup-ai')), matching: find.text('2')),
          findsOneWidget);
    });

    testWidgets('a check that fails counts as not done and does not hide the rest', (tester) async {
      await pump(tester, service(failBanks: true, ai: true));

      expect(stepDone('banks'), findsNothing);
      expect(stepDone('ai'), findsOneWidget);
      expect(find.text('1 of 4 done'), findsOneWidget);
    });

    testWidgets('each open step opens its screen', (tester) async {
      await pump(tester, service());

      await tester.tap(find.byKey(const ValueKey('owner-setup-banks')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('owner-setup-ai')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Invite your household'));
      await tester.pumpAndSettle();

      expect(opened, ['/accounts?add=1', '/settings/ai', '/family']);
    });

    testWidgets('a done step does nothing when tapped', (tester) async {
      await pump(tester, service(banks: true));

      await tester.tap(find.byKey(const ValueKey('owner-setup-banks')));
      await tester.pumpAndSettle();

      expect(opened, isEmpty);
    });

    testWidgets('coming back from a step checks again', (tester) async {
      await pump(tester, service());
      final before = serverChecks;

      await tester.tap(find.byKey(const ValueKey('owner-setup-ai')));
      await tester.pumpAndSettle();

      expect(serverChecks, before + 4);
    });

    testWidgets('notifications with no relay address show how to set one, not Cloud linking', (tester) async {
      await pump(tester, service());

      await tester.tap(find.byKey(const ValueKey('owner-setup-notifications')));
      await tester.pumpAndSettle();

      expect(opened, isEmpty);
      expect(find.byKey(const ValueKey('owner-setup-relay-help')), findsOneWidget);
      expect(find.text('BUOY_PUSH_RELAY_URL='), findsOneWidget);
    });

    testWidgets('checking again after setting the relay reads the server and the steps again', (tester) async {
      var refreshed = 0;
      await pump(tester, service(), refreshServer: () async {
        refreshed++;
        ServerEdition.current.value =
            const ServerCapabilities(edition: 'selfhost', pushRelayUrl: 'https://relay.test');
      });
      final before = serverChecks;

      await tester.tap(find.byKey(const ValueKey('owner-setup-notifications')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('owner-setup-relay-check')));
      await tester.pumpAndSettle();

      expect(refreshed, 1);
      expect(serverChecks, before + 4);
      expect(find.byKey(const ValueKey('owner-setup-relay-help')), findsNothing);
      expect(opened, isEmpty);
    });

    testWidgets('a relay the server holds no credential for opens Cloud linking', (tester) async {
      ServerEdition.current.value =
          const ServerCapabilities(edition: 'selfhost', pushRelayUrl: 'https://relay.test');
      var asked = 0;
      await pump(tester, service(setup: NotificationSetup.serverNotLinked), enablePush: () async {
        asked++;
        return true;
      });

      await tester.tap(find.byKey(const ValueKey('owner-setup-notifications')));
      await tester.pumpAndSettle();

      expect(opened, ['/settings/cloud']);
      expect(asked, 0);
      expect(stepDone('notifications'), findsNothing);
    });

    testWidgets('notifications with a relay ask this phone first, then check again', (tester) async {
      ServerEdition.current.value =
          const ServerCapabilities(edition: 'selfhost', pushRelayUrl: 'https://relay.test');
      var asked = 0;
      await pump(tester, service(), enablePush: () async {
        asked++;
        return true;
      });
      final before = serverChecks;

      await tester.tap(find.byKey(const ValueKey('owner-setup-notifications')));
      await tester.pumpAndSettle();

      expect(asked, 1);
      expect(opened, isEmpty);
      expect(serverChecks, before + 4);
    });

    testWidgets('notifications refused on the phone open Settings', (tester) async {
      ServerEdition.current.value =
          const ServerCapabilities(edition: 'selfhost', pushRelayUrl: 'https://relay.test');
      await pump(tester, service(), enablePush: () async => false);

      await tester.tap(find.byKey(const ValueKey('owner-setup-notifications')));
      await tester.pumpAndSettle();

      expect(opened, ['/settings']);
    });

    testWidgets('skipping the household marks it done and is remembered', (tester) async {
      await pump(tester, service());

      await tester.tap(find.byKey(const ValueKey('owner-setup-household-skip')));
      await tester.pumpAndSettle();

      expect(stepDone('household'), findsOneWidget);
      expect(find.text('1 of 4 done'), findsOneWidget);
      expect(opened, isEmpty);
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getBool(OwnerSetupService.householdSkippedPrefKey), isTrue);

      // A later load (another day) still counts it.
      final status = await service().load();
      expect(status.householdDone, isTrue);
    });
  });

  group('notifications step', () {
    Future<NotificationSetup> setupOf({
      String? relayUrl = 'https://relay.test',
      bool serverReady = true,
      bool web = false,
      bool permission = true,
      bool registered = true,
    }) =>
        OwnerSetupService.notificationSetupFrom(
          relayUrl: relayUrl,
          serverReady: () async => serverReady,
          web: web,
          osPermission: () async => permission,
          registered: () async => registered,
        );

    test('done only with a relay, a server credential, OS permission and a registration', () async {
      expect(await setupOf(), NotificationSetup.ready);
    });

    test('no relay address on the server', () async {
      expect(await setupOf(relayUrl: null), NotificationSetup.noRelay);
    });

    test('a relay the server cannot send through (no credential) is not done', () async {
      expect(await setupOf(serverReady: false), NotificationSetup.serverNotLinked);
    });

    test('permission revoked on the phone is not done', () async {
      expect(await setupOf(permission: false), NotificationSetup.phoneNotSetUp);
    });

    test('a relay pairing whose registration with this server failed is not done', () async {
      expect(await setupOf(registered: false), NotificationSetup.phoneNotSetUp);
    });

    test('on the web the server side is all there is', () async {
      expect(await setupOf(web: true, permission: false, registered: false), NotificationSetup.ready);
      expect(await setupOf(web: true, serverReady: false), NotificationSetup.serverNotLinked);
    });

    test('a failing check counts as not done', () async {
      ServerEdition.current.value =
          const ServerCapabilities(edition: 'selfhost', pushRelayUrl: 'https://relay.test');
      final status = await OwnerSetupService(
        hasAccounts: () async => true,
        aiEnabled: () async => true,
        hasHousehold: () async => true,
        notificationSetup: () async => throw Exception('offline'),
      ).load();

      expect(status.notifications, isFalse);
      expect(status.notificationSetup, NotificationSetup.phoneNotSetUp);
    });

    test('the enum reads from strings and ints', () {
      expect(NotificationSetupX.fromString('noRelay'), NotificationSetup.noRelay);
      expect(NotificationSetupX.fromString('READY'), NotificationSetup.ready);
      expect(NotificationSetupX.fromString('nonsense'), NotificationSetup.phoneNotSetUp);
      expect(NotificationSetupX.fromInt(2), NotificationSetup.serverNotLinked);
      expect(NotificationSetupX.fromInt(99), NotificationSetup.phoneNotSetUp);
      expect(NotificationSetup.noRelay.displayName, 'No relay address');
    });
  });

  group('hide', () {
    testWidgets('hiding removes it and it stays hidden next time', (tester) async {
      await pump(tester, service());

      await tester.tap(find.byKey(const ValueKey('owner-setup-hide')));
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('owner-setup')), findsNothing);

      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getBool(OwnerSetupService.dismissedPrefKey), isTrue);

      serverChecks = 0;
      await tester.pumpWidget(const SizedBox());
      await pump(tester, service());
      expect(find.byKey(const ValueKey('owner-setup')), findsNothing);
      expect(serverChecks, 0, reason: 'a hidden list asks the server nothing');
    });
  });

  group('server admin', () {
    User user(String tier) => User(
          id: '1',
          email: 'o@example.com',
          firstName: 'O',
          lastName: 'W',
          subscriptionTier: tier,
          createdAt: DateTime(2026, 10, 1),
        );

    test('the Admin tier is the server administrator', () {
      expect(isServerAdminUser(user('Admin')), isTrue);
    });

    test('anyone else is not', () {
      expect(isServerAdminUser(user('Standard')), isFalse);
      expect(isServerAdminUser(user('PremiumInherited')), isFalse);
      expect(isServerAdminUser(null), isFalse);
    });
  });
}
