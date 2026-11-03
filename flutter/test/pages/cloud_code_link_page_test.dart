import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/cloud_code_link_page.dart';
import 'package:buoy_ai/services/cloud_service.dart';
import 'package:buoy_ai/widgets/cinematic/glass_settings.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

class _FakeCloud extends CloudService {
  _FakeCloud({
    List<Object>? lookUpErrors,
    List<Object>? approveErrors,
    List<Object>? denyErrors,
    this.status = 'pending',
  })  : lookUpErrors = lookUpErrors ?? [],
        approveErrors = approveErrors ?? [],
        denyErrors = denyErrors ?? [];

  final List<Object> lookUpErrors;
  final List<Object> approveErrors;
  final List<Object> denyErrors;
  final String status;
  final lookedUp = <String>[];
  final approved = <String>[];
  final denied = <String>[];

  CloudLinkSession _session(String code, String status) => CloudLinkSession(
        userCode: code,
        name: 'Home server',
        serverVersion: '1.5.36',
        createdAt: DateTime(2026, 10, 31, 9, 41),
        expiresAt: DateTime(2026, 10, 31, 9, 51),
        status: status,
      );

  @override
  Future<CloudLinkSession> linkSession(String code) async {
    lookedUp.add(code);
    if (lookUpErrors.isNotEmpty) throw lookUpErrors.removeAt(0);
    return _session(code, status);
  }

  @override
  Future<CloudLinkSession> approveLinkSession(String code) async {
    approved.add(code);
    if (approveErrors.isNotEmpty) throw approveErrors.removeAt(0);
    return _session(code, 'approved');
  }

  @override
  Future<CloudLinkSession> denyLinkSession(String code) async {
    denied.add(code);
    if (denyErrors.isNotEmpty) throw denyErrors.removeAt(0);
    return _session(code, 'denied');
  }
}

DioException _http(int status, Map<String, dynamic> data) => DioException(
      requestOptions: RequestOptions(path: '/cloud-servers/link-sessions/X'),
      response: Response(
        requestOptions: RequestOptions(path: '/cloud-servers/link-sessions/X'),
        statusCode: status,
        data: data,
      ),
    );

final _emailUnverified = _http(403, {
  'error': 'EmailVerificationRequired',
  'code': 'email_verification_required',
  'message': 'Confirm your email address first.',
});

final _mfaRequired = _http(403, {
  'error': 'MfaRequired',
  'message': 'This operation requires multi-factor authentication',
});

Future<void> _pump(
  WidgetTester tester,
  _FakeCloud cloud, {
  String? code,
  Future<bool> Function(BuildContext)? stepUp,
  Future<bool> Function(BuildContext)? verifyEmail,
}) async {
  tester.view.physicalSize = const Size(900, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: CloudCodeLinkPage(
      code: code,
      service: cloud,
      stepUp: stepUp ?? (_) async => true,
      verifyEmail: verifyEmail ?? (_) async => false,
      animateBackground: false,
    ),
  ));
  await tester.pumpAndSettle();
}

Future<void> _enter(WidgetTester tester, String code) async {
  await tester.enterText(find.byKey(const ValueKey('code-link-field')), code);
  await tester.tap(find.byKey(const ValueKey('code-link-continue')));
  await tester.pumpAndSettle();
}

/// The note shown for an expected answer: never the red error tone.
GlassNoteTone _toneOf(WidgetTester tester, String key) =>
    tester.widget<GlassNote>(find.byKey(ValueKey(key))).tone;

void main() {
  setUpAll(() => GoogleFonts.config.allowRuntimeFetching = false);

  group('entering a code', () {
    testWidgets('starts with the code box and looks nothing up',
        (tester) async {
      final cloud = _FakeCloud();
      await _pump(tester, cloud);

      expect(find.byKey(const ValueKey('code-link-entry')), findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-server')), findsNothing);
      expect(cloud.lookedUp, isEmpty);
    });

    testWidgets('a typed code is tidied up and shows the waiting server',
        (tester) async {
      final cloud = _FakeCloud();
      await _pump(tester, cloud);

      await _enter(tester, 'bcdf gh23');

      expect(cloud.lookedUp, ['BCDF-GH23']);
      expect(find.text('Home server'), findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-version')), findsOneWidget);
      expect(find.text('1.5.36'), findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-asked')), findsOneWidget);
      expect(find.text('Only approve a code shown on your own server.'),
          findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-approve')), findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-deny')), findsOneWidget);
    });

    testWidgets('a malformed code is caught before any request, calmly',
        (tester) async {
      final cloud = _FakeCloud();
      await _pump(tester, cloud);

      await _enter(tester, 'ABC');

      expect(cloud.lookedUp, isEmpty);
      expect(
          find.byKey(const ValueKey('code-link-note-badCode')), findsOneWidget);
      expect(_toneOf(tester, 'code-link-note-badCode'),
          isNot(GlassNoteTone.error));
      expect(find.byKey(const ValueKey('code-link-error')), findsNothing);
    });

    testWidgets('an unknown or expired code says so, with no red banner',
        (tester) async {
      final cloud = _FakeCloud(lookUpErrors: [
        _http(404,
            {'code': 'invalid_code', 'message': 'That code is not valid.'}),
      ]);
      await _pump(tester, cloud);

      await _enter(tester, 'BCDF-GH23');

      expect(
          find.byKey(const ValueKey('code-link-note-badCode')), findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-error')), findsNothing);
      expect(find.byKey(const ValueKey('code-link-entry')), findsOneWidget);
    });

    testWidgets('too many wrong codes asks to wait', (tester) async {
      final cloud = _FakeCloud(lookUpErrors: [
        _http(429, {'code': 'rate_limited', 'message': 'Too many tries.'}),
      ]);
      await _pump(tester, cloud);

      await _enter(tester, 'BCDF-GH23');

      expect(find.byKey(const ValueKey('code-link-note-tooManyTries')),
          findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-error')), findsNothing);
    });

    testWidgets('someone with no Cloud account is pointed at the add-ons',
        (tester) async {
      final cloud = _FakeCloud(lookUpErrors: [
        _http(409, {
          'code': 'no_cloud_account',
          'message': 'Buy a Cloud product first.'
        }),
      ]);
      await _pump(tester, cloud);

      await _enter(tester, 'BCDF-GH23');

      expect(find.byKey(const ValueKey('code-link-note-noAccount')),
          findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-products')), findsOneWidget);
    });

    testWidgets('a real failure shows the error', (tester) async {
      final cloud = _FakeCloud(lookUpErrors: [
        _http(502, {
          'code': 'cloud_unavailable',
          'message': 'BuoyAI Cloud is not reachable right now.'
        }),
      ]);
      await _pump(tester, cloud);

      await _enter(tester, 'BCDF-GH23');

      expect(find.byKey(const ValueKey('code-link-error')), findsOneWidget);
    });
  });

  group('a code from the QR link', () {
    testWidgets('is filled in and looked up straight away', (tester) async {
      final cloud = _FakeCloud();
      await _pump(tester, cloud, code: 'bcdf-gh23');

      expect(cloud.lookedUp, ['BCDF-GH23']);
      expect(find.byKey(const ValueKey('code-link-server')), findsOneWidget);
    });

    testWidgets('a broken one is shown in the box but not looked up',
        (tester) async {
      final cloud = _FakeCloud();
      await _pump(tester, cloud, code: 'nope');

      expect(cloud.lookedUp, isEmpty);
      expect(find.byKey(const ValueKey('code-link-entry')), findsOneWidget);
      expect(find.text('nope'), findsOneWidget);
    });
  });

  group('deciding', () {
    testWidgets('approving runs the second factor, then says linked',
        (tester) async {
      final cloud = _FakeCloud(approveErrors: [_mfaRequired]);
      var stepUps = 0;
      await _pump(tester, cloud, code: 'BCDF-GH23', stepUp: (_) async {
        stepUps++;
        return true;
      });

      await tester.tap(find.byKey(const ValueKey('code-link-approve')));
      await tester.pumpAndSettle();

      expect(stepUps, 1);
      expect(cloud.approved, ['BCDF-GH23', 'BCDF-GH23']);
      expect(find.byKey(const ValueKey('code-link-approved')), findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-servers')), findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-approve')), findsNothing);
    });

    testWidgets('stopping the second factor approves nothing', (tester) async {
      final cloud = _FakeCloud(approveErrors: [_mfaRequired]);
      await _pump(tester, cloud, code: 'BCDF-GH23', stepUp: (_) async => false);

      await tester.tap(find.byKey(const ValueKey('code-link-approve')));
      await tester.pumpAndSettle();

      expect(cloud.approved, ['BCDF-GH23']);
      expect(find.byKey(const ValueKey('code-link-approved')), findsNothing);
      expect(find.byKey(const ValueKey('code-link-approve')), findsOneWidget);
    });

    testWidgets(
        'a new Cloud-only account confirms its email, then the same code goes on to step-up and is approved',
        (tester) async {
      final cloud = _FakeCloud(approveErrors: [_emailUnverified, _mfaRequired]);
      var confirms = 0;
      var stepUps = 0;
      await _pump(tester, cloud, code: 'BCDF-GH23', verifyEmail: (_) async {
        confirms++;
        return true;
      }, stepUp: (_) async {
        stepUps++;
        return true;
      });

      await tester.tap(find.byKey(const ValueKey('code-link-approve')));
      await tester.pumpAndSettle();

      expect(confirms, 1);
      expect(stepUps, 1);
      expect(cloud.approved, ['BCDF-GH23', 'BCDF-GH23', 'BCDF-GH23']);
      expect(find.byKey(const ValueKey('code-link-approved')), findsOneWidget);
    });

    testWidgets(
        'stopping the email confirmation leaves a note and a button that resumes the same code',
        (tester) async {
      final cloud = _FakeCloud(approveErrors: [_emailUnverified]);
      var confirmed = false;
      await _pump(tester, cloud,
          code: 'BCDF-GH23', verifyEmail: (_) async => confirmed);

      await tester.tap(find.byKey(const ValueKey('code-link-approve')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('code-link-note-needsEmail')),
          findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-error')), findsNothing);
      expect(find.byKey(const ValueKey('code-link-approved')), findsNothing);

      confirmed = true;
      await tester.tap(find.byKey(const ValueKey('code-link-confirm-email')));
      await tester.pumpAndSettle();

      expect(cloud.approved.last, 'BCDF-GH23');
      expect(find.byKey(const ValueKey('code-link-approved')), findsOneWidget);
    });

    testWidgets('a code someone already decided says so calmly',
        (tester) async {
      final cloud = _FakeCloud(approveErrors: [
        _http(409, {'code': 'already_decided', 'message': 'Already decided.'}),
      ]);
      await _pump(tester, cloud, code: 'BCDF-GH23');

      await tester.tap(find.byKey(const ValueKey('code-link-approve')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('code-link-note-alreadyDecided')),
          findsOneWidget);
      expect(_toneOf(tester, 'code-link-note-alreadyDecided'),
          isNot(GlassNoteTone.error));
      expect(find.byKey(const ValueKey('code-link-error')), findsNothing);
    });

    testWidgets('denying needs no second factor and says denied',
        (tester) async {
      final cloud = _FakeCloud();
      var stepUps = 0;
      await _pump(tester, cloud, code: 'BCDF-GH23', stepUp: (_) async {
        stepUps++;
        return true;
      });

      await tester.tap(find.byKey(const ValueKey('code-link-deny')));
      await tester.pumpAndSettle();

      expect(stepUps, 0);
      expect(cloud.denied, ['BCDF-GH23']);
      expect(cloud.approved, isEmpty);
      expect(find.byKey(const ValueKey('code-link-denied')), findsOneWidget);
    });

    testWidgets('a code this owner already approved shows linked, no buttons',
        (tester) async {
      await _pump(tester, _FakeCloud(status: 'approved'), code: 'BCDF-GH23');

      expect(find.byKey(const ValueKey('code-link-approved')), findsOneWidget);
      expect(find.byKey(const ValueKey('code-link-approve')), findsNothing);
      expect(find.byKey(const ValueKey('code-link-deny')), findsNothing);
    });

    testWidgets('use a different code goes back to the code box',
        (tester) async {
      await _pump(tester, _FakeCloud(), code: 'BCDF-GH23');

      await tester.tap(find.byKey(const ValueKey('code-link-other')));
      await tester.pumpAndSettle();

      expect(find.byKey(const ValueKey('code-link-entry')), findsOneWidget);
    });
  });

  group('the link session model', () {
    test('reads camelCase and PascalCase', () {
      final camel = CloudLinkSession.fromJson({
        'userCode': 'BCDF-GH23',
        'name': 'Home',
        'serverVersion': '1.5.36',
        'createdAt': '2026-10-31T12:00:00Z',
        'expiresAt': '2026-10-31T12:10:00Z',
        'status': 'pending',
      });
      final pascal = CloudLinkSession.fromJson({
        'UserCode': 'BCDF-GH23',
        'Name': 'Home',
        'ServerVersion': '1.5.36',
        'CreatedAt': '2026-10-31T12:00:00Z',
        'ExpiresAt': '2026-10-31T12:10:00Z',
        'Status': 'approved',
      });
      expect(camel.name, 'Home');
      expect(camel.pending, isTrue);
      expect(camel.expiresAt, isNotNull);
      expect(pascal.serverVersion, '1.5.36');
      expect(pascal.status, 'approved');
    });

    test('normalises codes like the server does', () {
      expect(CloudLinkSession.normalizeCode('bcdf gh23'), 'BCDF-GH23');
      expect(CloudLinkSession.normalizeCode('BCDF-GH23'), 'BCDF-GH23');
      for (final bad in [
        null,
        '',
        'ABCD-EFGH',
        'BCDF-GH0K',
        'BCDF-GH2',
        'BCDF/GH23'
      ]) {
        expect(CloudLinkSession.normalizeCode(bad), isNull, reason: '$bad');
      }
    });
  });
}
