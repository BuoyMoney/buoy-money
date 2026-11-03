import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/auth_models.dart';
import 'package:buoy_ai/pages/connect_page.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/connect_service.dart';

/// Widget coverage for the "Buoy Money Connect" OAuth consent page (RFC #782).
///
/// The page reads its data from the global [connectService] and the signed-in
/// user from an [AuthProvider] above it in the tree. We swap a fake service
/// (global swap, as with accountsService) and provide a fake provider seeded
/// with a user, exactly like test/widgets/app_lock_gate_test.dart.
///
/// NOTE: the cinematic background (`InteractiveBackgroundLayer`) runs a
/// continuous ticker, so the tree never "settles" — these tests use explicit
/// `pump()` calls and never `pumpAndSettle`.
class _FakeConnectService extends ConnectService {
  final ConsentRequest? consent;
  final Object? loadError;
  final String redirectUrl;
  final Duration loadDelay;

  int decideCalls = 0;
  bool? lastApprove;

  _FakeConnectService({
    this.consent,
    this.loadError,
    this.redirectUrl = 'https://chat.openai.com/callback?code=abc&state=xyz',
    this.loadDelay = Duration.zero,
  });

  @override
  Future<ConsentRequest> getConsentRequest(String id) async {
    if (loadDelay > Duration.zero) await Future<void>.delayed(loadDelay);
    if (loadError != null) throw loadError!;
    return consent!;
  }

  @override
  Future<String> decide(String id, bool approve) async {
    decideCalls++;
    lastApprove = approve;
    return redirectUrl;
  }
}

class _FakeAuthProvider extends AuthProvider {
  final User _user;
  _FakeAuthProvider(this._user);

  @override
  User? get user => _user;

  @override
  AuthStatus get status => AuthStatus.authenticated;

  @override
  bool get isAuthenticated => true;
}

User _seedUser({String email = 'user@example.com'}) => User(
      id: '2',
      email: email,
      firstName: 'Test',
      lastName: 'User',
      subscriptionTier: 'Premium',
      createdAt: DateTime(2026, 1, 1),
    );

ConsentRequest _seedConsent({
  String clientName = 'ChatGPT',
  String? clientUri = 'https://chat.openai.com',
  List<String> scopeDescriptions = const [
    'Read your budgets and transactions',
    'Read your savings goals and insights',
  ],
}) =>
    ConsentRequest(
      clientName: clientName,
      clientUri: clientUri,
      scopes: const ['buoyai:read'],
      scopeDescriptions: scopeDescriptions,
      expiresAt: DateTime(2030, 1, 1),
    );

DioException _notFound() => DioException(
      requestOptions: RequestOptions(path: '/oauth/request/x'),
      response: Response(
        requestOptions: RequestOptions(path: '/oauth/request/x'),
        statusCode: 404,
      ),
    );

void main() {
  late ConnectService realService;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realService = connectService;
  });

  tearDown(() {
    connectService = realService;
  });

  Widget wrap({String? requestId = 'req-1'}) {
    return ChangeNotifierProvider<AuthProvider>.value(
      value: _FakeAuthProvider(_seedUser()),
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: ConnectPage(requestId: requestId),
      ),
    );
  }

  testWidgets('shows the loading state while the request is in flight',
      (tester) async {
    connectService = _FakeConnectService(
      consent: _seedConsent(),
      loadDelay: const Duration(milliseconds: 200),
    );

    await tester.pumpWidget(wrap());
    await tester.pump(); // initState → _load (still pending)

    expect(find.textContaining('Loading connection request'), findsOneWidget);
    expect(find.text('Approve'), findsNothing);

    // Let the delayed load resolve so no timer outlives the test.
    await tester.pump(const Duration(milliseconds: 250));
  });

  testWidgets('renders client name, scope bullets and the signed-in email',
      (tester) async {
    connectService = _FakeConnectService(consent: _seedConsent());

    await tester.pumpWidget(wrap());
    await tester.pump(); // resolve the (zero-delay) load

    expect(find.text('Connect ChatGPT to Buoy Money'), findsOneWidget);
    expect(find.textContaining('Read your budgets and transactions'),
        findsOneWidget);
    expect(find.textContaining('Read your savings goals and insights'),
        findsOneWidget);
    expect(find.text('user@example.com'), findsOneWidget);
    expect(find.text('Approve'), findsOneWidget);
    expect(find.text('Deny'), findsOneWidget);
  });

  testWidgets(
      'falls back to the default scope bullet when none are provided',
      (tester) async {
    connectService = _FakeConnectService(
      consent: _seedConsent(scopeDescriptions: const []),
    );

    await tester.pumpWidget(wrap());
    await tester.pump();

    expect(
      find.textContaining(
          'Read your budgets, transactions, accounts, savings goals'),
      findsOneWidget,
    );
  });

  testWidgets('Approve calls decide(true) and shows the redirecting state',
      (tester) async {
    final fake = _FakeConnectService(
      consent: _seedConsent(),
      redirectUrl: 'https://chat.openai.com/callback?code=abc&state=xyz',
    );
    connectService = fake;

    await tester.pumpWidget(wrap());
    await tester.pump(); // load resolves → consent card

    await tester.tap(find.text('Approve'));
    await tester.pump(); // _decide → deciding
    await tester.pump(); // decide() resolves → redirecting

    expect(fake.decideCalls, 1);
    expect(fake.lastApprove, isTrue);
    expect(find.text('Connected'), findsOneWidget);
    expect(find.textContaining('Return to ChatGPT'), findsOneWidget);
    expect(find.textContaining('You can close this window'), findsOneWidget);
  });

  testWidgets('Deny calls decide(false) and shows the denied confirmation',
      (tester) async {
    final fake = _FakeConnectService(consent: _seedConsent());
    connectService = fake;

    await tester.pumpWidget(wrap());
    await tester.pump();

    await tester.ensureVisible(find.text('Deny'));
    await tester.pump();
    await tester.tap(find.text('Deny'));
    await tester.pump();
    await tester.pump();

    expect(fake.decideCalls, 1);
    expect(fake.lastApprove, isFalse);
    expect(find.text('Access denied'), findsOneWidget);
    expect(find.textContaining('Nothing was shared'), findsOneWidget);
  });

  testWidgets('a 404 on load shows the expired message', (tester) async {
    connectService = _FakeConnectService(loadError: _notFound());

    await tester.pumpWidget(wrap());
    await tester.pump();

    expect(
      find.textContaining('This connection request has expired'),
      findsOneWidget,
    );
    expect(find.text('Approve'), findsNothing);
  });

  testWidgets('a missing request id shows the expired message',
      (tester) async {
    connectService = _FakeConnectService(consent: _seedConsent());

    await tester.pumpWidget(wrap(requestId: null));
    await tester.pump();

    expect(
      find.textContaining('This connection request has expired'),
      findsOneWidget,
    );
  });

  group('ConsentRequest.fromJson', () {
    test('parses camelCase keys', () {
      final json = <String, dynamic>{
        'clientName': 'Claude',
        'clientUri': 'https://claude.ai',
        'scopes': <dynamic>['buoyai:read'],
        'scopeDescriptions': <dynamic>['Read your data'],
        'expiresAt': '2030-01-01T00:00:00Z',
      };

      final req = ConsentRequest.fromJson(json);

      expect(req.clientName, 'Claude');
      expect(req.clientUri, 'https://claude.ai');
      expect(req.scopes, ['buoyai:read']);
      expect(req.scopeDescriptions, ['Read your data']);
      expect(req.expiresAt.year, 2030);
    });

    test('parses PascalCase keys', () {
      final json = <String, dynamic>{
        'ClientName': 'Gemini',
        'ClientUri': 'https://gemini.google.com',
        'Scopes': <dynamic>['buoyai:read'],
        'ScopeDescriptions': <dynamic>['Read your data'],
        'ExpiresAt': '2030-01-01T00:00:00Z',
      };

      final req = ConsentRequest.fromJson(json);

      expect(req.clientName, 'Gemini');
      expect(req.clientUri, 'https://gemini.google.com');
      expect(req.scopes, ['buoyai:read']);
      expect(req.scopeDescriptions, ['Read your data']);
    });

    test('tolerates missing optional fields', () {
      final req = ConsentRequest.fromJson(<String, dynamic>{
        'clientName': 'X',
      });

      expect(req.clientName, 'X');
      expect(req.clientUri, isNull);
      expect(req.logoUri, isNull);
      expect(req.scopes, isEmpty);
      expect(req.scopeDescriptions, isEmpty);
    });
  });
}
