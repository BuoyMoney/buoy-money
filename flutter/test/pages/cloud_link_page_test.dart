import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/cloud_link_page.dart';
import 'package:buoy_ai/services/cloud_service.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

class _FakeCloud extends CloudService {
  _FakeCloud({List<Object>? errors}) : errors = errors ?? [];

  final List<Object> errors;
  final calls = <Map<String, String?>>[];

  @override
  Future<String> approveCloudLink(
      {required String callback, required String state, String? name}) async {
    calls.add({'callback': callback, 'state': state, 'name': name});
    if (errors.isNotEmpty) throw errors.removeAt(0);
    return '$callback?code=ABCDE-FGHJK&state=$state';
  }
}

DioException _httpError(int status, Map<String, dynamic> data) => DioException(
      requestOptions: RequestOptions(path: '/cloud-servers/link'),
      response: Response(
        requestOptions: RequestOptions(path: '/cloud-servers/link'),
        statusCode: status,
        data: data,
      ),
    );

final _emailUnverified = _httpError(403, {
  'error': 'EmailVerificationRequired',
  'code': 'email_verification_required',
  'message': 'Confirm your email address first.',
});

final _mfaRequired = _httpError(403, {
  'error': 'MfaRequired',
  'message': 'This operation requires multi-factor authentication',
});

const _callback =
    'https://desktop-abc.tail1234.ts.net:10111/api/CloudBridge/link/callback';

Future<void> _pump(
  WidgetTester tester,
  _FakeCloud cloud, {
  String? callback = _callback,
  String? state = 'st_123',
  String? name = 'Home server',
  List<String>? opened,
  Future<bool> Function(BuildContext)? stepUp,
  Future<bool> Function(BuildContext)? verifyEmail,
  bool openWorks = true,
  bool selfHost = false,
}) async {
  tester.view.physicalSize = const Size(900, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  ServerEdition.current.value =
      ServerCapabilities(edition: selfHost ? 'selfhost' : 'hosted');
  addTearDown(() => ServerEdition.current.value = ServerCapabilities.hosted);
  await tester.pumpWidget(MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: CloudLinkPage(
      callback: callback,
      state: state,
      name: name,
      service: cloud,
      stepUp: stepUp ?? (_) async => true,
      verifyEmail: verifyEmail ?? (_) async => false,
      navigate: (url) async {
        opened?.add(url);
        return openWorks;
      },
      animateBackground: false,
    ),
  ));
  await tester.pumpAndSettle();
}

void main() {
  setUpAll(() => GoogleFonts.config.allowRuntimeFetching = false);

  group('link page', () {
    testWidgets('shows the server address, its name and the warning',
        (tester) async {
      await _pump(tester, _FakeCloud());

      expect(find.text('desktop-abc.tail1234.ts.net:10111'), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-name')), findsOneWidget);
      expect(find.text('Home server'), findsOneWidget);
      expect(find.text('Only approve a server you set up yourself.'),
          findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-approve')), findsOneWidget);
    });

    testWidgets('approve sends the request, then leaves for the redirect',
        (tester) async {
      final cloud = _FakeCloud();
      final opened = <String>[];
      await _pump(tester, cloud, opened: opened);

      await tester.tap(find.byKey(const ValueKey('cloud-link-approve')));
      await tester.pumpAndSettle();

      expect(cloud.calls, [
        {'callback': _callback, 'state': 'st_123', 'name': 'Home server'}
      ]);
      expect(opened, ['$_callback?code=ABCDE-FGHJK&state=st_123']);
      expect(find.byKey(const ValueKey('cloud-link-leaving')), findsOneWidget);
    });

    testWidgets('asks for the second factor, then tries once more',
        (tester) async {
      final cloud = _FakeCloud(errors: [_mfaRequired]);
      final opened = <String>[];
      var asked = 0;
      await _pump(tester, cloud, opened: opened, stepUp: (_) async {
        asked++;
        return true;
      });

      await tester.tap(find.byKey(const ValueKey('cloud-link-approve')));
      await tester.pumpAndSettle();

      expect(asked, 1);
      expect(cloud.calls, hasLength(2));
      expect(opened, hasLength(1));
    });

    testWidgets('stopping the second factor leaves nothing', (tester) async {
      final cloud = _FakeCloud(errors: [_mfaRequired]);
      final opened = <String>[];
      await _pump(tester, cloud,
          opened: opened, stepUp: (_) async => false);

      await tester.tap(find.byKey(const ValueKey('cloud-link-approve')));
      await tester.pumpAndSettle();

      expect(cloud.calls, hasLength(1));
      expect(opened, isEmpty);
      expect(find.byKey(const ValueKey('cloud-link-approve')), findsOneWidget);
    });

    testWidgets(
        'an account whose email is not confirmed is told so when it stops the confirmation',
        (tester) async {
      final cloud = _FakeCloud(errors: [_emailUnverified]);
      await _pump(tester, cloud);

      await tester.tap(find.byKey(const ValueKey('cloud-link-approve')));
      await tester.pumpAndSettle();

      expect(
          find.byKey(const ValueKey('cloud-link-needs-email')), findsOneWidget);
    });

    testWidgets('confirming the email carries on to step-up and approves',
        (tester) async {
      final cloud = _FakeCloud(errors: [_emailUnverified, _mfaRequired]);
      final opened = <String>[];
      await _pump(tester, cloud,
          opened: opened, verifyEmail: (_) async => true);

      await tester.tap(find.byKey(const ValueKey('cloud-link-approve')));
      await tester.pumpAndSettle();

      expect(cloud.calls, hasLength(3));
      expect(opened, hasLength(1));
      expect(find.byKey(const ValueKey('cloud-link-needs-email')), findsNothing);
    });

    testWidgets('cancel sends the browser back to the server as denied',
        (tester) async {
      final cloud = _FakeCloud();
      final opened = <String>[];
      await _pump(tester, cloud, opened: opened);

      await tester.tap(find.byKey(const ValueKey('cloud-link-cancel')));
      await tester.pumpAndSettle();

      expect(cloud.calls, isEmpty);
      expect(opened, ['$_callback?error=access_denied&state=st_123']);
    });

    testWidgets(
        'with no Cloud account it points at the products and keeps the request',
        (tester) async {
      final cloud = _FakeCloud(errors: [
        _httpError(409,
            {'code': 'no_cloud_account', 'message': 'Buy a Cloud product first.'})
      ]);
      final opened = <String>[];
      await _pump(tester, cloud, opened: opened);

      await tester.tap(find.byKey(const ValueKey('cloud-link-approve')));
      await tester.pumpAndSettle();

      expect(
          find.byKey(const ValueKey('cloud-link-no-account')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-products')), findsOneWidget);
      expect(opened, isEmpty);

      // Back with a product bought: the same request can be approved.
      await tester.tap(find.byKey(const ValueKey('cloud-link-approve')));
      await tester.pumpAndSettle();
      expect(opened, hasLength(1));
      expect(cloud.calls.last['state'], 'st_123');
    });

    testWidgets('an error from the server is shown', (tester) async {
      final cloud = _FakeCloud(errors: [
        _httpError(429, {
          'code': 'too_many_codes',
          'message': 'Too many unused pairing codes. Wait for one to expire.'
        })
      ]);
      await _pump(tester, cloud);

      await tester.tap(find.byKey(const ValueKey('cloud-link-approve')));
      await tester.pumpAndSettle();

      expect(find.text('Too many unused pairing codes. Wait for one to expire.'),
          findsOneWidget);
    });

    testWidgets('a bad callback shows no approve button', (tester) async {
      await _pump(tester, _FakeCloud(),
          callback: 'https://evil.test/api/CloudBridge/pair');

      expect(find.byKey(const ValueKey('cloud-link-invalid')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-approve')), findsNothing);
    });

    testWidgets('a missing state shows no approve button', (tester) async {
      await _pump(tester, _FakeCloud(), state: null);

      expect(find.byKey(const ValueKey('cloud-link-invalid')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-approve')), findsNothing);
    });

    testWidgets('a self-hosted server does not approve links', (tester) async {
      await _pump(tester, _FakeCloud(), selfHost: true);

      expect(
          find.byKey(const ValueKey('cloud-link-hosted-only')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-link-approve')), findsNothing);
    });
  });

  group('callback check', () {
    test('accepts the link callback on https, or http on loopback', () {
      expect(CloudLinkPage.parseCallback(_callback), isNotNull);
      expect(
          CloudLinkPage.parseCallback(
              'http://localhost:5000/api/CloudBridge/link/callback'),
          isNotNull);
    });

    test('refuses anything else', () {
      for (final bad in [
        null,
        '',
        '/api/CloudBridge/link/callback',
        'http://desktop-abc.ts.net/api/CloudBridge/link/callback',
        'javascript:alert(1)',
        'https://desktop-abc.ts.net/api/CloudBridge/pair',
        'https://desktop-abc.ts.net/api/CloudBridge/link/callback?x=1',
        'https://desktop-abc.ts.net/api/CloudBridge/link/callback#x',
        'https://evil.test@desktop-abc.ts.net/api/CloudBridge/link/callback',
        'https://desktop-abc.ts.net/api/CloudBridge/link/../link/callback',
        'https://desktop-abc.ts.net/api/CloudBridge%2Flink/callback',
      ]) {
        expect(CloudLinkPage.parseCallback(bad), isNull, reason: '$bad');
      }
    });

    test('shows the port only when the link names one', () {
      expect(
          CloudLinkPage.displayHost(Uri.parse(
              'https://box.ts.net/api/CloudBridge/link/callback')),
          'box.ts.net');
      expect(CloudLinkPage.displayHost(Uri.parse(_callback)),
          'desktop-abc.tail1234.ts.net:10111');
    });
  });
}
