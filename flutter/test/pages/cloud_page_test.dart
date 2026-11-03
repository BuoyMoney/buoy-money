import 'dart:async';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/cloud_page.dart';
import 'package:buoy_ai/services/ai_status_service.dart';
import 'package:buoy_ai/services/ai_usage_service.dart';
import 'package:buoy_ai/services/cloud_service.dart';
import 'package:buoy_ai/services/simplefin_service.dart';
import 'package:buoy_ai/widgets/cinematic/glass_settings.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:qr_flutter/qr_flutter.dart';

class _FakeCloud extends CloudService {
  _FakeCloud({
    this.pairing,
    this.bankFeed = false,
    List<BankConnectionSummary>? banks,
    this.catalogValue,
    this.planValue = const CloudPlan(),
    this.pairError,
    this.finishProblem,
    this.serversValue,
    this.serversError,
    List<Object>? codeErrors,
    List<Object>? confirmErrors,
  })  : banks = banks ?? [],
        codeErrors = codeErrors ?? [],
        confirmErrors = confirmErrors ?? [];

  List<CloudServer>? serversValue;
  final Object? serversError;
  final List<Object> codeErrors;
  final List<Object> confirmErrors;
  var codeCalls = 0;
  final confirmed = <String>[];
  final revoked = <String>[];

  @override
  Future<List<CloudServer>?> cloudServers() async {
    serversCalls++;
    if (serversError != null) throw serversError!;
    return serversValue;
  }

  var serversCalls = 0;

  /// What `GET /cloud-servers/visibility` says. The existing hosted tests are about someone who may see the list.
  bool serversShown = true;
  var visibilityCalls = 0;

  @override
  Future<bool> serversVisible() async {
    visibilityCalls++;
    return serversShown;
  }

  @override
  Future<CloudPairingCode> createPairingCode() async {
    codeCalls++;
    if (codeErrors.isNotEmpty) throw codeErrors.removeAt(0);
    return CloudPairingCode(
        code: 'ABCDE-FGHJK', expiresAt: DateTime(2026, 10, 31, 12, 15));
  }

  @override
  Future<void> confirmServer(String id) async {
    if (confirmErrors.isNotEmpty) throw confirmErrors.removeAt(0);
    confirmed.add(id);
    serversValue = [
      for (final s in serversValue ?? <CloudServer>[])
        s.id == id ? _server(id: s.id, name: s.name) : s,
    ];
  }

  @override
  Future<void> revokeServer(String id) async {
    revoked.add(id);
    serversValue = [
      for (final s in serversValue ?? <CloudServer>[])
        s.id == id ? _server(id: s.id, name: s.name, status: 'revoked') : s,
    ];
  }

  CloudPairingStatus? pairing;
  final bool bankFeed;
  final List<BankConnectionSummary> banks;
  final CloudCatalog? catalogValue;
  final CloudPlan planValue;
  final Object? pairError;
  String? finishProblem;

  final paired = <String>[];
  var unpaired = 0;
  var started = 0;
  final finished = <String>[];
  final checkouts = <Map<String, Object>>[];

  final linkStarts = <String?>[];
  Object? linkStartError;
  var statusCalls = 0;

  /// The code link. [deviceStatuses] answers polls in order; when empty, the session keeps waiting.
  final deviceStarts = <String?>[];
  Object? deviceStartError;
  final deviceStatuses = <CloudDeviceLinkStatus>[];
  CloudDeviceLinkStatus? resumeStatus;
  var deviceStatusCalls = 0;
  var deviceCancels = 0;
  DateTime? deviceExpiresAt;

  CloudDeviceLink get deviceLink => CloudDeviceLink(
      userCode: 'BCDF-GHJK',
      verificationUri: 'https://cloud.buoymoney.com/link',
      verificationUriComplete:
          'https://cloud.buoymoney.com/link?code=BCDF-GHJK',
      expiresAt:
          deviceExpiresAt ?? DateTime.now().add(const Duration(minutes: 10)),
      interval: 5);

  @override
  Future<CloudDeviceLink> startDeviceLink(String? name) async {
    deviceStarts.add(name);
    if (deviceStartError != null) throw deviceStartError!;
    return deviceLink;
  }

  @override
  Future<CloudDeviceLinkStatus> deviceLinkStatus() async {
    deviceStatusCalls++;
    if (deviceStarts.isEmpty) {
      if (resumeStatus?.state == CloudDeviceLinkState.linked) {
        // The background loop linked the server just after the page read the pairing.
        pairing = CloudPairingStatus(
            configured: true,
            paired: true,
            name: 'Home server',
            pairedAt: DateTime(2026, 10, 31));
      }
      return resumeStatus ??
          const CloudDeviceLinkStatus(state: CloudDeviceLinkState.none);
    }
    if (deviceStatuses.isNotEmpty) {
      final next = deviceStatuses.removeAt(0);
      if (next.state == CloudDeviceLinkState.linked) {
        pairing = CloudPairingStatus(
            configured: true,
            paired: true,
            name: 'Home server',
            pairedAt: DateTime(2026, 10, 31));
      }
      return next;
    }
    return CloudDeviceLinkStatus(
        state: CloudDeviceLinkState.waiting, link: deviceLink);
  }

  /// Errors the next cancels throw, in order; [cancelGate] holds a cancel in flight.
  final cancelErrors = <Object>[];
  Completer<void>? cancelGate;

  @override
  Future<void> cancelDeviceLink() async {
    deviceCancels++;
    await cancelGate?.future;
    if (cancelErrors.isNotEmpty) throw cancelErrors.removeAt(0);
  }

  /// When set, [pairingStatus] waits for it, so a test can look at the page before the link state is known.
  Completer<void>? pairingGate;

  @override
  Future<CloudPairingStatus?> pairingStatus() async {
    statusCalls++;
    await pairingGate?.future;
    return pairing;
  }

  @override
  Future<String> startCloudLink(String? name) async {
    linkStarts.add(name);
    if (linkStartError != null) throw linkStartError!;
    return 'https://buoy-ai.com/app/cloud/link?state=s1&name=Home&callback=https%3A%2F%2Fbox.ts.net%2Fapi%2FCloudBridge%2Flink%2Fcallback';
  }

  @override
  Future<CloudPairingStatus> pair(String code, String? name) async {
    paired.add('$code|${name ?? ''}');
    if (pairError != null) throw pairError!;
    pairing = CloudPairingStatus(
        configured: true,
        paired: true,
        name: name,
        pairedAt: DateTime(2026, 10, 12),
        awaitingOwnerConfirmation: true);
    return pairing!;
  }

  @override
  Future<void> unpair() async {
    unpaired++;
    pairing = const CloudPairingStatus(configured: true, paired: false);
  }

  @override
  Future<bool> bankFeedAvailable() async => bankFeed;

  /// Whether the Cloud feed can connect (the server is linked). Defaults to "linked" whenever the feed is on.
  bool? bankLinked;

  /// The providers answer cannot be read (null from the service).
  bool bankLinkedUnreadable = false;

  @override
  Future<bool?> bankFeedLinked() async =>
      bankLinkedUnreadable ? null : (bankLinked ?? bankFeed);

  @override
  Future<List<BankConnectionSummary>> bankConnections() async => banks;

  @override
  Future<String?> startBankConnect() async {
    started++;
    return 'https://cloud.test/link/s1';
  }

  @override
  Future<String?> finishBankConnect(String pageUrl) async {
    finished.add(pageUrl);
    return finishProblem;
  }

  @override
  Future<CloudCatalog?> catalog() async => catalogValue;

  @override
  Future<CloudPlan> plan() async => planValue;

  @override
  Future<String> checkout(
      {required String interval,
      required List<String> products,
      required String platform}) async {
    checkouts.add(
        {'interval': interval, 'products': products, 'platform': platform});
    return 'https://checkout.test/s';
  }
}

/// A hosted app that cannot answer whether to show "Your servers".
class _ThrowingVisibilityCloud extends _FakeCloud {
  _ThrowingVisibilityCloud({super.serversValue});

  @override
  Future<bool> serversVisible() async => throw StateError('offline');
}

/// A server whose status call fails (network): not the same as "not the administrator".
class _ThrowingStatusCloud extends _FakeCloud {
  @override
  Future<CloudPairingStatus?> pairingStatus() async => throw Exception('offline');
}

/// A linked server whose link goes away between loading the page and tapping Connect a bank.
class _FailingConnectCloud extends _FakeCloud {
  _FailingConnectCloud() : super(bankFeed: true);

  @override
  Future<String?> startBankConnect() async {
    started++;
    bankLinked = false;
    pairing = const CloudPairingStatus(configured: true, paired: false);
    throw DioException(
      requestOptions: RequestOptions(path: '/aggregators/Bridge/connect/start'),
      response: Response(
        requestOptions:
            RequestOptions(path: '/aggregators/Bridge/connect/start'),
        statusCode: 400,
        data: {'message': 'This server is not paired with the Cloud.'},
      ),
    );
  }
}

BankConnectionSummary _bank(
        {int id = 5,
        String name = 'First Platypus Bank',
        String status = 'Active',
        bool action = false}) =>
    BankConnectionSummary(
      id: id,
      provider: 'Bridge',
      institutionName: name,
      isActive: true,
      requiresUserAction: action,
      status: status,
      lastSyncAt: DateTime(2026, 10, 11),
      message: null,
      accountCount: 2,
    );

AiStatus _aiOn({String provider = 'buoy-cloud'}) => AiStatus(
    enabled: true, provider: provider, edition: 'selfhost', features: const []);

AiUsageSummary _usage(
        {int percent = 40,
        String state = 'ok',
        bool unlimited = false,
        String source = 'cloud'}) =>
    AiUsageSummary(
      source: source,
      unlimited: unlimited,
      periodStart: DateTime(2026, 10, 1),
      periodEnd: DateTime(2026, 11, 1),
      allowanceMicros: 5000000,
      usedMicros: 2000000,
      percent: percent,
      state: state,
      blocked: state == 'over',
      payg: const AiPaygStatus(),
      byFeature: const [],
      projectedMicros: 0,
    );

CloudCatalog _catalog({bool founding = true}) => CloudCatalog(
      founding: founding,
      products: [
        CloudProduct(
          name: 'bank_sync',
          displayName: 'Bank sync',
          includedLogins: 3,
          monthly:
              CloudPrice(amountCents: founding ? 279 : 429, currency: 'usd'),
          yearly:
              CloudPrice(amountCents: founding ? 2399 : 3599, currency: 'usd'),
        ),
        CloudProduct(
          name: 'ai',
          displayName: 'AI',
          monthly:
              CloudPrice(amountCents: founding ? 199 : 349, currency: 'usd'),
          yearly:
              CloudPrice(amountCents: founding ? 1499 : 2699, currency: 'usd'),
        ),
        CloudProduct(
          name: 'extra_logins',
          displayName: 'Extra bank logins',
          requires: const ['bank_sync'],
          monthly: CloudPrice(amountCents: 75, currency: 'usd'),
          yearly: CloudPrice(amountCents: 900, currency: 'usd'),
        ),
      ],
    );

CloudServer _server(
        {String id = 's1',
        String name = 'Home server',
        String status = 'linked',
        String? version = '1.5.33'}) =>
    CloudServer(
      id: id,
      name: name,
      serverVersion: version,
      pairedAt: DateTime(2026, 10, 30),
      status: status,
    );

DioException _httpError(int status, Map<String, dynamic> data) => DioException(
      requestOptions: RequestOptions(path: '/cloud-servers'),
      response: Response(
        requestOptions: RequestOptions(path: '/cloud-servers'),
        statusCode: status,
        data: data,
      ),
    );

final _mfaRequired = _httpError(403, {
  'error': 'MfaRequired',
  'message': 'This operation requires multi-factor authentication',
  'operation': 'ManageCloudServers',
});

Future<void> _pump(
  WidgetTester tester,
  _FakeCloud cloud, {
  Future<bool> Function(BuildContext)? stepUp,
  bool selfHost = true,
  Future<AiStatus> Function()? ai,
  Future<AiUsageSummary> Function()? usage,
  List<String>? opened,
  bool linked = false,
  String? linkError,
  bool settle = true,
  bool portal = false,
}) async {
  tester.view.physicalSize = const Size(900, 3000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  ServerEdition.current.value =
      ServerCapabilities(edition: selfHost ? 'selfhost' : 'hosted');
  addTearDown(() => ServerEdition.current.value = ServerCapabilities.hosted);
  await tester.pumpWidget(MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: CloudPage(
      key: UniqueKey(),
      service: cloud,
      aiStatusLoader: ai ?? () async => _aiOn(),
      aiUsageLoader: usage ?? () async => _usage(),
      open: (url) async => opened?.add(url),
      stepUp: stepUp ?? (_) async => true,
      linked: linked,
      linkError: linkError,
      portal: portal,
      animateBackground: false,
    ),
  ));
  if (settle) await tester.pumpAndSettle();
}

void main() {
  setUpAll(() => GoogleFonts.config.allowRuntimeFetching = false);

  group('link this server', () {
    testWidgets('says so when the server has no Cloud address', (tester) async {
      await _pump(
          tester,
          _FakeCloud(
              pairing:
                  const CloudPairingStatus(configured: false, paired: false)));

      expect(
          find.byKey(const ValueKey('cloud-not-configured')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-pair')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsNothing);
    });

    testWidgets(
        'a pairing code links the server and then asks the owner to confirm',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      await _pump(tester, cloud);

      expect(find.byKey(const ValueKey('cloud-code')), findsNothing,
          reason: 'entering a code is the secondary way');
      await tester.tap(find.byKey(const ValueKey('cloud-show-code')));
      await tester.pumpAndSettle();
      await tester.enterText(
          find.byKey(const ValueKey('cloud-code')), 'ABCDE-FGHJK');
      await tester.enterText(
          find.byKey(const ValueKey('cloud-name')), 'Home server');
      await tester.tap(find.byKey(const ValueKey('cloud-pair')));
      await tester.pumpAndSettle();

      expect(cloud.paired, ['ABCDE-FGHJK|Home server']);
      expect(find.byKey(const ValueKey('cloud-paired')), findsOneWidget);
      expect(find.text('Linked as Home server'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-awaiting')), findsOneWidget);
    });

    testWidgets(
        'a wrong code shows the reason from the server and stays unlinked',
        (tester) async {
      final cloud = _FakeCloud(
        pairing: const CloudPairingStatus(configured: true, paired: false),
        pairError: DioException(
          requestOptions: RequestOptions(path: '/CloudBridge/pair'),
          response: Response(
            requestOptions: RequestOptions(path: '/CloudBridge/pair'),
            statusCode: 400,
            data: {'message': 'That pairing code is wrong, used or expired.'},
          ),
        ),
      );
      await _pump(tester, cloud);

      await tester.tap(find.byKey(const ValueKey('cloud-show-code')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const ValueKey('cloud-code')), 'WRONG');
      await tester.tap(find.byKey(const ValueKey('cloud-pair')));
      await tester.pumpAndSettle();

      expect(find.text('That pairing code is wrong, used or expired.'),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-paired')), findsNothing);
    });

    testWidgets('unlinking asks first', (tester) async {
      final cloud = _FakeCloud(
          pairing: CloudPairingStatus(
              configured: true,
              paired: true,
              name: 'Home',
              pairedAt: DateTime(2026, 10, 12)));
      await _pump(tester, cloud);

      await tester.tap(find.byKey(const ValueKey('cloud-unlink')));
      await tester.pumpAndSettle();
      expect(cloud.unpaired, 0,
          reason: 'nothing happens until the dialog is confirmed');
      await tester.tap(find.byKey(const ValueKey('cloud-confirm-unlink')));
      await tester.pumpAndSettle();

      expect(cloud.unpaired, 1);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsOneWidget);
    });

    testWidgets('Link with BuoyAI Cloud is the main action', (tester) async {
      await _pump(
          tester,
          _FakeCloud(
              pairing:
                  const CloudPairingStatus(configured: true, paired: false)));

      expect(find.text('Link with BuoyAI Cloud'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-show-code')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-code')), findsNothing);
    });

    testWidgets(
        'the link button gets a code and shows it big, with a QR code and an open button',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      await _pump(tester, cloud);

      // One clear action: no form to fill in before it.
      expect(find.byKey(const ValueKey('cloud-name')), findsNothing);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      expect(cloud.deviceStarts, ['']);
      expect(find.byKey(const ValueKey('cloud-device-code')), findsOneWidget);
      expect(find.text('BCDF-GHJK'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-device-qr')), findsOneWidget);
      expect(
          find.text(
              'On any phone or computer, go to cloud.buoymoney.com/link and enter this code.'),
          findsOneWidget);
      expect(find.text('Open cloud.buoymoney.com'), findsOneWidget);
      expect(find.text('Waiting for approval...'), findsOneWidget);
      expect(
          find.byKey(const ValueKey('cloud-device-expires')), findsOneWidget);
      expect(find.textContaining('Code expires in 9:'), findsOneWidget);
      // The start button and the code form step aside while the code is up.
      expect(find.byKey(const ValueKey('cloud-link-start')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-show-code')), findsNothing);
    });

    testWidgets('the QR code is labelled for screen readers', (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      final qr = tester
          .widget<QrImageView>(find.byKey(const ValueKey('cloud-device-qr')));
      expect(qr.semanticsLabel, 'QR code for the BuoyAI Cloud approve page');
    });

    testWidgets(
        'the open button opens the approve page with the code filled in',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      final opened = <String>[];
      await _pump(tester, cloud, opened: opened);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.tap(find.byKey(const ValueKey('cloud-device-open')));
      await tester.pump();

      expect(opened, ['https://cloud.buoymoney.com/link?code=BCDF-GHJK']);
    });

    testWidgets('copy puts the link code on the clipboard', (tester) async {
      final copied = <String>[];
      tester.binding.defaultBinaryMessenger
          .setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'Clipboard.setData') {
          copied.add((call.arguments as Map)['text'] as String);
        }
        return null;
      });
      addTearDown(() => tester.binding.defaultBinaryMessenger
          .setMockMethodCallHandler(SystemChannels.platform, null));
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.tap(find.byKey(const ValueKey('cloud-device-copy')));
      await tester.pump();

      expect(copied, ['BCDF-GHJK']);
      await tester.pump(const Duration(seconds: 5));
    });

    testWidgets(
        'asks the server every 3 seconds and says Linked once the owner approves',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      cloud.deviceStatuses.addAll([
        CloudDeviceLinkStatus(
            state: CloudDeviceLinkState.waiting, link: cloud.deviceLink),
        const CloudDeviceLinkStatus(state: CloudDeviceLinkState.linked),
      ]);
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();
      final before = cloud.deviceStatusCalls;

      await tester.pump(const Duration(seconds: 2));
      expect(cloud.deviceStatusCalls, before, reason: 'not before 3 seconds');
      await tester.pump(const Duration(seconds: 1));
      expect(cloud.deviceStatusCalls, before + 1);
      expect(find.byKey(const ValueKey('cloud-device-code')), findsOneWidget);

      await tester.pump(const Duration(seconds: 3));
      await tester.pump();
      await tester.pump();

      expect(cloud.deviceStatusCalls, before + 2);
      expect(find.byKey(const ValueKey('cloud-device-code')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-link-done')), findsOneWidget);
      expect(find.text('Linked as Home server'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-awaiting')), findsNothing);

      // Linked: no more asking.
      await tester.pump(const Duration(seconds: 9));
      expect(cloud.deviceStatusCalls, before + 2);
    });

    testWidgets('an expired code says so and offers a fresh start',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      cloud.deviceStatuses.add(
          const CloudDeviceLinkStatus(state: CloudDeviceLinkState.expired));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.pump(const Duration(seconds: 3));
      await tester.pump();

      expect(find.byKey(const ValueKey('cloud-device-code')), findsNothing);
      expect(find.text('That code expired. Get a new one to try again.'),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsOneWidget);
    });

    testWidgets(
        'the countdown running out asks the server and ends on its word',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false))
        ..deviceExpiresAt = DateTime.now().subtract(const Duration(seconds: 1));
      cloud.deviceStatuses.add(
          const CloudDeviceLinkStatus(state: CloudDeviceLinkState.expired));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Code expires in 0:00'), findsOneWidget);
      final before = cloud.deviceStatusCalls;

      await tester.pump(const Duration(seconds: 1));
      await tester.pump();

      expect(cloud.deviceStatusCalls, before + 1,
          reason: 'it asked the server rather than deciding alone');
      expect(find.byKey(const ValueKey('cloud-device-ended')), findsOneWidget);
      expect(find.text('That code expired. Get a new one to try again.'),
          findsOneWidget);
    });

    testWidgets(
        'a code that ran out here but was approved on the server still ends linked',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false))
        ..deviceExpiresAt = DateTime.now().subtract(const Duration(seconds: 1));
      cloud.deviceStatuses
          .add(const CloudDeviceLinkStatus(state: CloudDeviceLinkState.linked));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.pump(const Duration(seconds: 1));
      await tester.pump();
      await tester.pump();

      expect(find.byKey(const ValueKey('cloud-device-ended')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-link-done')), findsOneWidget);
      expect(find.text('Linked as Home server'), findsOneWidget);
    });

    testWidgets(
        'a server that never says the code is over is given up on after a grace',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false))
        ..deviceExpiresAt = DateTime.now().subtract(const Duration(seconds: 1));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.pump(const Duration(seconds: 10));
      expect(find.byKey(const ValueKey('cloud-device-ended')), findsNothing,
          reason: 'still waiting on the server within the grace');

      await tester.pump(const Duration(seconds: 25));
      await tester.pump();

      expect(find.byKey(const ValueKey('cloud-device-ended')), findsOneWidget);
    });

    testWidgets('a turned down code says so', (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      cloud.deviceStatuses
          .add(const CloudDeviceLinkStatus(state: CloudDeviceLinkState.denied));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.pump(const Duration(seconds: 3));
      await tester.pump();

      expect(
          find.text(
              'The code was turned down on BuoyAI Cloud. Get a new one to try again.'),
          findsOneWidget);
    });

    testWidgets('a failed link shows the server reason', (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      cloud.deviceStatuses.add(const CloudDeviceLinkStatus(
          state: CloudDeviceLinkState.failed,
          message: 'Could not reach the BuoyAI Cloud.'));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.pump(const Duration(seconds: 3));
      await tester.pump();

      expect(find.text('Could not reach the BuoyAI Cloud.'), findsOneWidget);
    });

    testWidgets('cancel stops waiting and tells the server', (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.tap(find.byKey(const ValueKey('cloud-device-cancel')));
      await tester.pump();
      final calls = cloud.deviceStatusCalls;
      await tester.pump(const Duration(seconds: 9));

      expect(cloud.deviceCancels, 1);
      expect(cloud.deviceStatusCalls, calls, reason: 'no polling after cancel');
      expect(find.byKey(const ValueKey('cloud-device-code')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-device-ended')), findsNothing);
    });

    testWidgets(
        'a cancel that does not reach the server keeps the code, keeps asking and can be retried',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      cloud.cancelErrors.add(DioException(
          requestOptions: RequestOptions(path: '/CloudBridge/link/device'),
          type: DioExceptionType.connectionError));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.tap(find.byKey(const ValueKey('cloud-device-cancel')));
      await tester.pump();
      await tester.pump();

      expect(find.byKey(const ValueKey('cloud-device-code')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-device-cancel-error')),
          findsOneWidget);
      final calls = cloud.deviceStatusCalls;
      await tester.pump(const Duration(seconds: 3));
      expect(cloud.deviceStatusCalls, greaterThan(calls),
          reason: 'still polling: the server is still waiting');

      await tester.tap(find.byKey(const ValueKey('cloud-device-cancel')));
      await tester.pump();
      await tester.pump();

      expect(cloud.deviceCancels, 2);
      expect(find.byKey(const ValueKey('cloud-device-code')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-device-cancel-error')),
          findsNothing);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsOneWidget);
    });

    testWidgets('while a cancel is in flight the code stays and Cancel is off',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false))
        ..cancelGate = Completer<void>();
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      await tester.tap(find.byKey(const ValueKey('cloud-device-cancel')));
      await tester.pump();
      await tester.tap(find.byKey(const ValueKey('cloud-device-cancel')),
          warnIfMissed: false);
      await tester.pump();

      expect(find.byKey(const ValueKey('cloud-device-code')), findsOneWidget);
      expect(cloud.deviceCancels, 1);

      cloud.cancelGate!.complete();
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const ValueKey('cloud-device-code')), findsNothing);
    });

    testWidgets(
        'a link that finished while the page loaded shows as linked, not as a fresh start',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      cloud.resumeStatus =
          const CloudDeviceLinkStatus(state: CloudDeviceLinkState.linked);
      await _pump(tester, cloud, settle: false);
      await tester.pump();
      await tester.pump();
      await tester.pump();

      expect(find.byKey(const ValueKey('cloud-link-done')), findsOneWidget);
      expect(find.text('Linked as Home server'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsNothing);
      expect(cloud.statusCalls, 2);
    });

    testWidgets('stops asking while the app is in the background',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pump();
      await tester.pump();

      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
      final calls = cloud.deviceStatusCalls;
      await tester.pump(const Duration(seconds: 9));
      expect(cloud.deviceStatusCalls, calls);

      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pump();
      expect(cloud.deviceStatusCalls, calls + 1,
          reason: 'asks at once on return');
      await tester.pump(const Duration(seconds: 3));
      expect(cloud.deviceStatusCalls, calls + 2);
    });

    testWidgets('a code started earlier is shown again when the page reopens',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false));
      cloud.resumeStatus = CloudDeviceLinkStatus(
          state: CloudDeviceLinkState.waiting, link: cloud.deviceLink);
      await _pump(tester, cloud, settle: false);
      await tester.pump();
      await tester.pump();

      expect(find.text('BCDF-GHJK'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-device-qr')), findsOneWidget);
      expect(cloud.deviceStarts, isEmpty);
    });

    testWidgets('a failed start shows the reason and shows no code',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false))
        ..deviceStartError = DioException(
          requestOptions:
              RequestOptions(path: '/CloudBridge/link/device/start'),
          response: Response(
            requestOptions:
                RequestOptions(path: '/CloudBridge/link/device/start'),
            statusCode: 409,
            data: {
              'message': 'This server is already linked. Unlink it first.'
            },
          ),
        );
      await _pump(tester, cloud);

      await tester.tap(find.byKey(const ValueKey('cloud-link-start')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('cloud-device-code')), findsNothing);
      expect(find.text('This server is already linked. Unlink it first.'),
          findsOneWidget);
    });

    testWidgets('back from the Cloud with linked=1 says so, with no waiting',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: CloudPairingStatus(
              configured: true,
              paired: true,
              name: 'Home server',
              pairedAt: DateTime(2026, 10, 31)));
      await _pump(tester, cloud, linked: true);

      expect(find.byKey(const ValueKey('cloud-link-done')), findsOneWidget);
      expect(find.text('Linked as Home server'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-awaiting')), findsNothing);
      expect(cloud.statusCalls, 1);
    });

    testWidgets('a cancelled link says so', (tester) async {
      await _pump(
          tester,
          _FakeCloud(
              pairing:
                  const CloudPairingStatus(configured: true, paired: false)),
          linkError: 'cancelled');

      expect(find.text('Linking was cancelled.'), findsOneWidget);
    });

    testWidgets('an unknown link error shows a plain message, not the reason',
        (tester) async {
      await _pump(
          tester,
          _FakeCloud(
              pairing:
                  const CloudPairingStatus(configured: true, paired: false)),
          linkError: '<b>boom</b>');

      expect(find.text('Linking did not work. Try again.'), findsOneWidget);
      expect(find.textContaining('boom'), findsNothing);
    });

    testWidgets(
        'a person who is not the administrator sees that the owner manages linking and billing',
        (tester) async {
      await _pump(tester, _FakeCloud(pairing: null));

      expect(find.byKey(const ValueKey('cloud-pairing')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-owner-manages')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-billing-owner-manages')),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-open-billing')), findsNothing);
      expect(find.text("Your server's owner manages this."), findsNWidgets(2));
    });

    testWidgets('a failed status load is not mistaken for a household member',
        (tester) async {
      await _pump(tester, _ThrowingStatusCloud());

      expect(find.byKey(const ValueKey('cloud-owner-manages')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-billing-owner-manages')),
          findsNothing);
    });
  });

  group('not linked yet (self-hosted, Cloud address set)', () {
    testWidgets(
        'an administrator sees Link with BuoyAI Cloud as the one primary button',
        (tester) async {
      await _pump(
          tester,
          _FakeCloud(
              pairing:
                  const CloudPairingStatus(configured: true, paired: false)));

      final primary = find.byKey(const ValueKey('cloud-link-start'));
      expect(primary, findsOneWidget);
      expect(tester.widget(primary), isA<GlassPrimaryButton>());
      expect(tester.widget<GlassPrimaryButton>(primary).onPressed, isNotNull);
      expect(
          find.descendant(
              of: primary, matching: find.text('Link with BuoyAI Cloud')),
          findsOneWidget);
      // The only primary button on the page.
      expect(find.byType(GlassPrimaryButton), findsOneWidget);
      // The code is the quiet second way, hidden until asked for.
      expect(find.text('Use a pairing code instead'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-code')), findsNothing);
      await tester.tap(find.byKey(const ValueKey('cloud-show-code')));
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('cloud-code')), findsOneWidget);
    });

    testWidgets(
        'the bank feed offers no Connect a bank; the link card says the feed needs the link',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false),
          bankFeed: true)
        ..bankLinked = false;
      await _pump(tester, cloud);

      expect(find.byKey(const ValueKey('cloud-connect-bank')), findsNothing);
      expect(find.text('Connect a bank'), findsNothing);
      expect(
          find.byKey(const ValueKey('cloud-bank-needs-link')), findsOneWidget);
      expect(find.text('Link this server to use the Cloud bank feed.'),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsOneWidget);
      expect(cloud.started, 0);
    });

    testWidgets(
        'someone who is not the administrator sees why, and no Connect a bank',
        (tester) async {
      final cloud = _FakeCloud(pairing: null, bankFeed: true)
        ..bankLinked = false;
      await _pump(tester, cloud);

      expect(find.byKey(const ValueKey('cloud-bank-not-linked')),
          findsOneWidget);
      expect(
          find.descendant(
              of: find.byKey(const ValueKey('cloud-bank-not-linked')),
              matching: find.text("Your server's owner manages this.")),
          findsOneWidget);
      expect(find.text('Link this server to use the Cloud bank feed.'),
          findsNothing);
      expect(find.byKey(const ValueKey('cloud-connect-bank')), findsNothing);
    });

    testWidgets(
        'the billing card says to link first instead of opening billing',
        (tester) async {
      await _pump(
          tester,
          _FakeCloud(
              pairing:
                  const CloudPairingStatus(configured: true, paired: false)));

      expect(find.byKey(const ValueKey('cloud-billing-not-linked')),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-open-billing')), findsNothing);
    });

    testWidgets(
        'a bank connect that fails because the link went away shows the calm note, not a red toast',
        (tester) async {
      final cloud = _FailingConnectCloud();
      await _pump(tester, cloud);

      await tester.tap(find.byKey(const ValueKey('cloud-connect-bank')));
      await tester.pumpAndSettle();

      expect(find.textContaining('not paired'), findsNothing);
      expect(find.byKey(const ValueKey('cloud-connect-bank')), findsNothing);
      // The pairing card follows: it no longer says Linked, and offers the link again.
      expect(find.byKey(const ValueKey('cloud-link-start')), findsOneWidget);
    });

    testWidgets(
        'an unreadable providers answer does not count as linked when the link card says it is not',
        (tester) async {
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false),
          bankFeed: true)
        ..bankLinkedUnreadable = true;
      await _pump(tester, cloud);

      expect(find.byKey(const ValueKey('cloud-connect-bank')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-link-start')), findsOneWidget);
    });
  });

  group('bank feed', () {
    testWidgets('is hidden when the Cloud bank feed is not turned on',
        (tester) async {
      await _pump(tester, _FakeCloud(bankFeed: false));

      expect(find.byKey(const ValueKey('cloud-bank')), findsNothing);
    });

    testWidgets(
        'a connected bank shows its health and offers no refresh button',
        (tester) async {
      await _pump(tester, _FakeCloud(bankFeed: true, banks: [_bank()]));

      expect(find.byKey(const ValueKey('cloud-bank-5')), findsOneWidget);
      expect(find.textContaining('Working, Last synced'), findsOneWidget);
      // Instant refresh is not on sale, so nothing on this screen mentions or offers it.
      expect(find.byKey(const ValueKey('cloud-refresh-5')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-refresh-left')), findsNothing);
      expect(find.textContaining('efresh'), findsNothing);
    });

    testWidgets(
        'connecting a bank opens the Cloud page, then finishes and reloads the list',
        (tester) async {
      final cloud = _FakeCloud(bankFeed: true);
      final opened = <String>[];
      await _pump(tester, cloud, opened: opened);
      expect(find.byKey(const ValueKey('cloud-no-banks')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-bank-done')), findsNothing);

      await tester.tap(find.byKey(const ValueKey('cloud-connect-bank')));
      await tester.pumpAndSettle();

      expect(opened, ['https://cloud.test/link/s1']);
      expect(find.byKey(const ValueKey('cloud-bank-done')), findsOneWidget);

      cloud.banks.add(_bank());
      await tester.tap(find.byKey(const ValueKey('cloud-bank-done')));
      await tester.pumpAndSettle();

      expect(cloud.finished, ['https://cloud.test/link/s1']);
      expect(find.byKey(const ValueKey('cloud-bank-5')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-bank-done')), findsNothing);
    });

    testWidgets(
        'finishing too early keeps the page and says it has not finished',
        (tester) async {
      final cloud = _FakeCloud(bankFeed: true, finishProblem: '');
      await _pump(tester, cloud);
      await tester.tap(find.byKey(const ValueKey('cloud-connect-bank')));
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const ValueKey('cloud-bank-done')));
      await tester.pumpAndSettle();

      expect(find.textContaining('has not finished yet'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-bank-done')), findsOneWidget);
    });
  });

  group('billing card', () {
    testWidgets(
        'self-host waits for the link state: no Open Billing button until it is known',
        (tester) async {
      final gate = Completer<void>();
      final cloud = _FakeCloud(
          pairing: const CloudPairingStatus(configured: true, paired: false))
        ..pairingGate = gate;
      await _pump(tester, cloud, settle: false);
      await tester.pump();

      expect(
          find.byKey(const ValueKey('cloud-billing-loading')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-open-billing')), findsNothing);

      gate.complete();
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('cloud-billing-loading')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-billing-not-linked')),
          findsOneWidget);
    });

    testWidgets(
        'is on the page for a self-hosted server and for the hosted service',
        (tester) async {
      // The administrator of a linked server.
      await _pump(
          tester,
          _FakeCloud(
              pairing: const CloudPairingStatus(
                  configured: true, paired: true, name: 'Home')));
      expect(find.byKey(const ValueKey('cloud-billing-card')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-open-billing')), findsOneWidget);

      await _pump(tester, _FakeCloud(), selfHost: false);
      expect(find.byKey(const ValueKey('cloud-billing-card')), findsOneWidget);
    });
  });

  group('AI', () {
    testWidgets('shows the allowance meter and who runs the AI',
        (tester) async {
      await _pump(tester, _FakeCloud(), usage: () async => _usage(percent: 40));

      expect(find.text('AI is on. It runs on BuoyAI Cloud.'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-ai-meter')), findsOneWidget);
      expect(find.text(r'$2.00 of $5.00 used this period'), findsOneWidget);
    });

    testWidgets('says when the allowance is used up', (tester) async {
      await _pump(tester, _FakeCloud(),
          usage: () async => _usage(percent: 100, state: 'over'));

      expect(find.byKey(const ValueKey('cloud-ai-over')), findsOneWidget);
    });

    testWidgets('a server with its own provider has no cap and no meter bar',
        (tester) async {
      await _pump(
        tester,
        _FakeCloud(),
        ai: () async => _aiOn(provider: 'ollama'),
        usage: () async => _usage(unlimited: true, source: 'local'),
      );

      expect(find.text("AI is on. It runs on this server's own provider."),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-ai-meter')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-ai-used')), findsOneWidget);
    });

    testWidgets('says so when AI is off', (tester) async {
      await _pump(tester, _FakeCloud(),
          ai: () async => const AiStatus(
              enabled: false,
              provider: 'none',
              edition: 'selfhost',
              features: []));

      expect(find.byKey(const ValueKey('cloud-ai-off')), findsOneWidget);
    });
  });

  group('add-ons on the hosted service', () {
    testWidgets('founding prices are labelled as such', (tester) async {
      await _pump(tester, _FakeCloud(catalogValue: _catalog(founding: true)),
          selfHost: false);
      expect(find.byKey(const ValueKey('cloud-founding')), findsOneWidget);
      expect(find.text(r'$2.79 a month or $23.99 a year'), findsOneWidget);
    });

    testWidgets('list prices are labelled as list prices', (tester) async {
      await _pump(tester, _FakeCloud(catalogValue: _catalog(founding: false)),
          selfHost: false);
      expect(find.byKey(const ValueKey('cloud-list-prices')), findsOneWidget);
      expect(find.text(r'$4.29 a month or $35.99 a year'), findsOneWidget);
    });

    testWidgets(
        'picking extra logins adds the bank sync they need, and checkout sends both',
        (tester) async {
      final cloud = _FakeCloud(catalogValue: _catalog());
      final opened = <String>[];
      await _pump(tester, cloud, selfHost: false, opened: opened);

      await tester
          .tap(find.byKey(const ValueKey('cloud-product-extra_logins')));
      await tester.pump();
      // Yearly is the default: bank sync $23.99 + extra logins $9.00.
      expect(find.text(r'Total: $32.99 a year'), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('cloud-checkout')));
      await tester.pumpAndSettle();

      expect(cloud.checkouts.single['interval'], 'yearly');
      expect((cloud.checkouts.single['products'] as List).toSet(),
          {'bank_sync', 'extra_logins'});
      expect(opened, ['https://checkout.test/s']);
    });

    testWidgets('a catalog that still lists instant refresh does not show it',
        (tester) async {
      final catalog = CloudCatalog.fromJson({
        'founding': true,
        'products': [
          {
            'name': 'bank_sync',
            'displayName': 'Bank sync',
            'monthly': {'amountCents': 279, 'currency': 'usd'},
            'yearly': {'amountCents': 2399, 'currency': 'usd'},
          },
          {
            'name': 'instant_refresh',
            'displayName': 'Instant refresh',
            'requires': ['bank_sync'],
            'monthly': {'amountCents': 289, 'currency': 'usd'},
            'yearly': {'amountCents': 2499, 'currency': 'usd'},
          },
        ],
      });
      await _pump(tester, _FakeCloud(catalogValue: catalog), selfHost: false);

      expect(find.byKey(const ValueKey('cloud-product-bank_sync')),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-product-instant_refresh')),
          findsNothing);
      expect(find.text('Instant refresh'), findsNothing);
    });

    testWidgets(
        'someone who already has a plan is sent to billing instead of a second checkout',
        (tester) async {
      final cloud = _FakeCloud(
          catalogValue: _catalog(),
          planValue: const CloudPlan(hasSubscription: true, bankSync: true));
      await _pump(tester, cloud, selfHost: false);

      expect(find.byKey(const ValueKey('cloud-has-plan')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-checkout')), findsNothing);
      expect(find.text('On your plan'), findsOneWidget);
    });

    testWidgets('no catalog means no add-ons section', (tester) async {
      await _pump(tester, _FakeCloud(catalogValue: null), selfHost: false);

      expect(find.byKey(const ValueKey('cloud-products')), findsNothing);
    });

    testWidgets('a self-hosted server never shows the add-on shop',
        (tester) async {
      await _pump(tester, _FakeCloud(catalogValue: _catalog()));

      expect(find.byKey(const ValueKey('cloud-products')), findsNothing);
    });
  });

  group('your servers on the hosted service', () {
    testWidgets(
        'lists servers with their status and only the actions that apply',
        (tester) async {
      final cloud = _FakeCloud(serversValue: [
        _server(
            id: 'new',
            name: 'New box',
            status: 'awaiting_confirmation',
            version: null),
        _server(id: 'home', name: 'Home server'),
        _server(
            id: 'old', name: 'Old box', status: 'revoked', version: '1.5.30'),
      ]);
      await _pump(tester, cloud, selfHost: false);

      expect(find.byKey(const ValueKey('cloud-servers')), findsOneWidget);
      expect(
          find.text(
              'Run Buoy Money on your own computer? Link it here to use bank sync and AI from BuoyAI Cloud.'),
          findsOneWidget);
      expect(find.text('New box'), findsOneWidget);
      expect(find.text('Home server'), findsOneWidget);
      expect(find.text('Waiting for you to confirm'), findsOneWidget);
      expect(find.text('Linked'), findsOneWidget);
      expect(find.text('Revoked'), findsOneWidget);
      expect(find.textContaining('Version 1.5.33'), findsOneWidget);

      expect(find.byKey(const ValueKey('cloud-server-confirm-new')),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-server-confirm-home')),
          findsNothing);
      expect(find.byKey(const ValueKey('cloud-server-revoke-home')),
          findsOneWidget);
      expect(
          find.byKey(const ValueKey('cloud-server-revoke-old')), findsNothing);
    });

    testWidgets('an empty list says so and still offers a code',
        (tester) async {
      await _pump(tester, _FakeCloud(serversValue: const []), selfHost: false);

      expect(find.byKey(const ValueKey('cloud-servers-empty')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-servers-make-code')),
          findsOneWidget);
    });

    testWidgets('is hidden when this app has no Cloud portal', (tester) async {
      await _pump(tester, _FakeCloud(serversValue: null), selfHost: false);

      expect(find.byKey(const ValueKey('cloud-servers')), findsNothing);
    });

    testWidgets(
        'is hidden for a hosted user with no Cloud add-on and no server, and never loads the list',
        (tester) async {
      final cloud = _FakeCloud(serversValue: [_server()])..serversShown = false;
      await _pump(tester, cloud, selfHost: false);

      expect(cloud.visibilityCalls, 1);
      expect(find.byKey(const ValueKey('cloud-servers')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-servers-hidden')), findsOneWidget);
      expect(cloud.serversCalls, 0);
    });

    testWidgets('is hidden when the visibility check fails', (tester) async {
      final cloud = _ThrowingVisibilityCloud(serversValue: [_server()]);
      await _pump(tester, cloud, selfHost: false);

      expect(find.byKey(const ValueKey('cloud-servers')), findsNothing);
    });

    testWidgets('a self-hosted server never shows the server list',
        (tester) async {
      await _pump(tester, _FakeCloud(serversValue: [_server()]));

      expect(find.byKey(const ValueKey('cloud-servers')), findsNothing);
    });

    testWidgets('shows a load error when the list fails', (tester) async {
      await _pump(
          tester,
          _FakeCloud(
              serversError: _httpError(502, {'code': 'cloud_unavailable'})),
          selfHost: false);

      expect(find.byKey(const ValueKey('cloud-servers')), findsOneWidget);
      expect(find.text('This could not be loaded. Try again in a moment.'),
          findsWidgets);
    });

    testWidgets(
        'making a code runs the second factor first, then shows the code and its expiry',
        (tester) async {
      final cloud =
          _FakeCloud(serversValue: const [], codeErrors: [_mfaRequired]);
      var stepUps = 0;
      await _pump(tester, cloud, selfHost: false, stepUp: (_) async {
        stepUps++;
        return true;
      });

      await tester.tap(find.byKey(const ValueKey('cloud-servers-make-code')));
      await tester.pumpAndSettle();

      expect(stepUps, 1);
      expect(cloud.codeCalls, 2,
          reason: 'once to learn MFA is needed, once after it');
      expect(find.text('ABCDE-FGHJK'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-servers-code-expires')),
          findsOneWidget);
      expect(find.text('Expires at 12:15 PM'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-servers-copy')), findsOneWidget);
    });

    testWidgets('copy puts the code on the clipboard', (tester) async {
      final cloud = _FakeCloud(serversValue: const []);
      String? copied;
      tester.binding.defaultBinaryMessenger
          .setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'Clipboard.setData') {
          copied = (call.arguments as Map)['text'] as String?;
        }
        return null;
      });
      addTearDown(() => tester.binding.defaultBinaryMessenger
          .setMockMethodCallHandler(SystemChannels.platform, null));
      await _pump(tester, cloud, selfHost: false);

      await tester.tap(find.byKey(const ValueKey('cloud-servers-make-code')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('cloud-servers-copy')));
      await tester.pumpAndSettle();

      expect(copied, 'ABCDE-FGHJK');
    });

    testWidgets('stopping the second factor makes no code', (tester) async {
      final cloud =
          _FakeCloud(serversValue: const [], codeErrors: [_mfaRequired]);
      await _pump(tester, cloud, selfHost: false, stepUp: (_) async => false);

      await tester.tap(find.byKey(const ValueKey('cloud-servers-make-code')));
      await tester.pumpAndSettle();

      expect(cloud.codeCalls, 1);
      expect(find.byKey(const ValueKey('cloud-servers-code')), findsNothing);
    });

    testWidgets(
        'an account whose email is not confirmed is told to confirm it, with no dialog',
        (tester) async {
      final cloud = _FakeCloud(serversValue: const [], codeErrors: [
        _httpError(403, {
          'error': 'EmailVerificationRequired',
          'code': 'email_verification_required',
          'message': 'Confirm your email address first.',
        }),
      ]);
      var stepUps = 0;
      await _pump(tester, cloud, selfHost: false, stepUp: (_) async {
        stepUps++;
        return true;
      });

      await tester.tap(find.byKey(const ValueKey('cloud-servers-make-code')));
      await tester.pumpAndSettle();

      expect(stepUps, 0);
      expect(find.byKey(const ValueKey('cloud-servers-needs-email')),
          findsOneWidget);
      expect(find.textContaining('Confirm your email address first'),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-servers-code')), findsNothing);
    });

    testWidgets('someone with no Cloud account is pointed at the add-ons',
        (tester) async {
      final cloud = _FakeCloud(
        serversError: _httpError(409, {
          'code': 'no_cloud_account',
          'message': 'Buy a Cloud product first.'
        }),
      );
      await _pump(tester, cloud, selfHost: false);

      expect(find.byKey(const ValueKey('cloud-servers-no-account')),
          findsOneWidget);
      expect(
          find.text(
              'Buy a Cloud add-on above first. Your servers use that Cloud account.'),
          findsOneWidget);
      expect(
          find.byKey(const ValueKey('cloud-servers-make-code')), findsNothing);
    });

    testWidgets(
        'confirming a waiting server runs the second factor and then shows it linked',
        (tester) async {
      final cloud = _FakeCloud(
        serversValue: [
          _server(id: 'new', name: 'New box', status: 'awaiting_confirmation')
        ],
        confirmErrors: [_mfaRequired],
      );
      var stepUps = 0;
      await _pump(tester, cloud, selfHost: false, stepUp: (_) async {
        stepUps++;
        return true;
      });

      await tester.tap(find.byKey(const ValueKey('cloud-server-confirm-new')));
      await tester.pumpAndSettle();

      expect(stepUps, 1);
      expect(cloud.confirmed, ['new']);
      expect(find.text('Linked'), findsOneWidget);
      expect(
          find.byKey(const ValueKey('cloud-server-confirm-new')), findsNothing);
    });

    testWidgets('revoking asks first, then shows the server as revoked',
        (tester) async {
      final cloud =
          _FakeCloud(serversValue: [_server(id: 'home', name: 'Home server')]);
      await _pump(tester, cloud, selfHost: false);

      await tester.tap(find.byKey(const ValueKey('cloud-server-revoke-home')));
      await tester.pumpAndSettle();
      expect(find.text('Revoke Home server?'), findsOneWidget);
      expect(cloud.revoked, isEmpty,
          reason: 'nothing happens until the dialog is confirmed');

      await tester
          .tap(find.byKey(const ValueKey('cloud-server-confirm-revoke')));
      await tester.pumpAndSettle();

      expect(cloud.revoked, ['home']);
      expect(find.text('Revoked'), findsOneWidget);
      expect(
          find.byKey(const ValueKey('cloud-server-revoke-home')), findsNothing);
    });

    testWidgets('cancelling the revoke dialog keeps the server',
        (tester) async {
      final cloud =
          _FakeCloud(serversValue: [_server(id: 'home', name: 'Home server')]);
      await _pump(tester, cloud, selfHost: false);

      await tester.tap(find.byKey(const ValueKey('cloud-server-revoke-home')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(cloud.revoked, isEmpty);
      expect(find.text('Linked'), findsOneWidget);
    });
  });

  group('the Cloud portal home (buoy-ai.com/cloud/)', () {
    testWidgets(
        'always shows your servers without asking, plus add-ons, billing, link and sign out',
        (tester) async {
      final cloud = _FakeCloud(
          serversValue: [_server()], catalogValue: _catalog())
        ..serversShown = false;
      await _pump(tester, cloud, selfHost: false, portal: true);

      expect(cloud.visibilityCalls, 0);
      expect(find.byKey(const ValueKey('cloud-servers')), findsOneWidget);
      expect(find.text('Home server'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-products')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-billing-card')), findsOneWidget);
      expect(find.byKey(const ValueKey('portal-link-server')), findsOneWidget);
      expect(find.byKey(const ValueKey('portal-sign-out')), findsOneWidget);
      expect(find.text('BuoyAI Cloud'), findsOneWidget);
    });

    testWidgets('shows nothing about this server, banks or the AI allowance',
        (tester) async {
      await _pump(tester, _FakeCloud(serversValue: const []),
          selfHost: false, portal: true);

      expect(find.byKey(const ValueKey('cloud-pairing')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-bank')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-ai')), findsNothing);
      expect(find.byKey(const ValueKey('glass-subpage-back')), findsNothing);
    });

    testWidgets('buying from the portal asks Stripe to come back to the portal',
        (tester) async {
      final cloud =
          _FakeCloud(serversValue: const [], catalogValue: _catalog());
      final opened = <String>[];
      await _pump(tester, cloud,
          selfHost: false, portal: true, opened: opened);

      await tester.tap(find.byKey(const ValueKey('cloud-product-bank_sync')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const ValueKey('cloud-checkout')));
      await tester.pumpAndSettle();

      expect(cloud.checkouts.single['platform'], 'portal');
      expect(opened, ['https://checkout.test/s']);
    });
  });

  group('cloud server models', () {
    test('a server reads camelCase and PascalCase', () {
      final camel = CloudServer.fromJson({
        'id': 'a',
        'name': 'Box',
        'serverVersion': '1.5.33',
        'pairedAt': '2026-10-30T10:00:00Z',
        'status': 'awaiting_confirmation',
      });
      final pascal = CloudServer.fromJson({
        'Id': 'a',
        'Name': 'Box',
        'ServerVersion': '1.5.33',
        'PairedAt': '2026-10-30T10:00:00Z',
        'Status': 'awaiting_confirmation',
      });
      for (final s in [camel, pascal]) {
        expect(s.id, 'a');
        expect(s.name, 'Box');
        expect(s.serverVersion, '1.5.33');
        expect(s.pairedAt, DateTime.utc(2026, 10, 30, 10).toLocal());
        expect(s.awaitingConfirmation, isTrue);
        expect(s.revoked, isFalse);
      }
    });

    test('a pairing code reads camelCase and PascalCase', () {
      for (final json in [
        {'code': 'ABCDE-FGHJK', 'expiresAt': '2026-10-31T12:15:00Z'},
        {'Code': 'ABCDE-FGHJK', 'ExpiresAt': '2026-10-31T12:15:00Z'},
      ]) {
        final code = CloudPairingCode.fromJson(json);
        expect(code.code, 'ABCDE-FGHJK');
        expect(code.expiresAt, DateTime.utc(2026, 10, 31, 12, 15).toLocal());
      }
    });
  });
}
