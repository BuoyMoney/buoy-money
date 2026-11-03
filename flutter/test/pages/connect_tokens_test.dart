import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/connect_page.dart';
import 'package:buoy_ai/services/connect_service.dart';

DioException _http(int status, [Object? data]) {
  final options = RequestOptions(path: '/connect/tokens');
  return DioException(
    requestOptions: options,
    type: DioExceptionType.badResponse,
    response: Response(requestOptions: options, statusCode: status, data: data),
  );
}

class _FakeTokens extends ConnectService {
  _FakeTokens({List<ConnectToken>? tokens, this.listError, this.createError})
      : tokens = tokens ?? [];

  final List<ConnectToken> tokens;
  final Object? listError;
  final Object? createError;
  final List<String> created = [];
  final List<String> revoked = [];

  @override
  Future<List<ConnectToken>> listTokens() async {
    if (listError != null) throw listError!;
    return List.of(tokens);
  }

  @override
  Future<CreatedConnectToken> createToken(String name) async {
    if (createError != null) throw createError!;
    created.add(name);
    final info = ConnectToken(id: 'new-${created.length}', name: name, createdAt: DateTime(2026, 10, 31));
    tokens.add(info);
    return CreatedConnectToken(
      info: info,
      token: 'bm_secret_123',
      mcpUrl: 'https://buoy.example.ts.net/mcp',
    );
  }

  @override
  Future<void> revokeToken(String id) async {
    revoked.add(id);
    tokens.removeWhere((t) => t.id == id);
  }
}

/// Settings > Tools for AI assistants on a self-hosted server: list, create, revoke, and the
/// "turned off" answer.
void main() {
  late ConnectService realService;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realService = connectService;
    ServerEdition.current.value = ServerCapabilities.fromJson({'edition': 'selfhost'});
  });

  tearDown(() {
    connectService = realService;
    ServerEdition.current.value = ServerCapabilities.hosted;
  });

  Future<void> pumpPage(WidgetTester tester, _FakeTokens fake) async {
    connectService = fake;
    tester.view.physicalSize = const Size(900, 1800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: const ConnectPage(requestId: null, animateBackground: false),
    ));
    await tester.pumpAndSettle();
  }

  testWidgets('lists tokens with name, created and last used', (tester) async {
    await pumpPage(
        tester,
        _FakeTokens(tokens: [
          ConnectToken(id: '1', name: 'Open WebUI', createdAt: DateTime(2026, 10, 1)),
          ConnectToken(
              id: '2',
              name: 'Laptop',
              createdAt: DateTime(2026, 9, 1),
              lastUsedAt: DateTime(2026, 10, 20)),
        ]));

    expect(find.text('Access tokens'), findsOneWidget);
    expect(find.textContaining('Read-only for money'), findsOneWidget);
    expect(find.text('Open WebUI'), findsOneWidget);
    expect(find.text('Laptop'), findsOneWidget);
    expect(find.text('Never used'), findsOneWidget);
    expect(find.textContaining('Last used'), findsOneWidget);
    expect(find.textContaining('Created'), findsNWidgets(2));
  });

  testWidgets('empty list says so and offers New token', (tester) async {
    await pumpPage(tester, _FakeTokens());
    expect(find.text('No tokens yet.'), findsOneWidget);
    expect(find.byKey(const Key('connectTokenNew')), findsOneWidget);
  });

  testWidgets('404: tells the owner how to turn tools on', (tester) async {
    await pumpPage(tester, _FakeTokens(listError: _http(404)));
    expect(find.byKey(const Key('connectTokensOff')), findsOneWidget);
    expect(find.textContaining('BUOY_MCP_ENABLED=true'), findsOneWidget);
    expect(find.byKey(const Key('connectTokenNew')), findsNothing);
  });

  testWidgets('create: default name, secret shown once with the MCP address, list refreshed',
      (tester) async {
    final fake = _FakeTokens();
    await pumpPage(tester, fake);

    await tester.tap(find.byKey(const Key('connectTokenNew')));
    await tester.pumpAndSettle();
    final field = tester.widget<TextField>(find.byKey(const Key('connectTokenName')));
    expect(field.controller!.text, 'Open WebUI');

    await tester.tap(find.byKey(const Key('connectTokenCreate')));
    await tester.pumpAndSettle();

    expect(fake.created, ['Open WebUI']);
    expect(find.text('Copy it now. You will not see it again.'), findsOneWidget);
    expect(find.text('bm_secret_123'), findsOneWidget);
    expect(find.text('https://buoy.example.ts.net/mcp'), findsOneWidget);
    expect(find.byKey(const Key('connectTokenCopy')), findsOneWidget);
    expect(find.byKey(const Key('connectTokenCopyMcp')), findsOneWidget);

    await tester.tap(find.byKey(const Key('connectTokenDone')));
    await tester.pumpAndSettle();
    expect(find.text('bm_secret_123'), findsNothing);
    expect(find.text('Open WebUI'), findsOneWidget);
  });

  testWidgets('create refused (409): the server message is shown, no secret dialog',
      (tester) async {
    await pumpPage(
        tester, _FakeTokens(createError: _http(409, {'message': 'You have too many tokens.'})));

    await tester.tap(find.byKey(const Key('connectTokenNew')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('connectTokenCreate')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));

    expect(find.text('You have too many tokens.'), findsOneWidget);
    expect(find.byKey(const Key('connectTokenOnceNote')), findsNothing);
    await tester.pump(const Duration(seconds: 5));
    await tester.pumpAndSettle();
  });

  testWidgets('revoke asks first, then removes the token', (tester) async {
    final fake = _FakeTokens(tokens: [ConnectToken(id: '7', name: 'Old box')]);
    await pumpPage(tester, fake);

    await tester.tap(find.byKey(const ValueKey('connectTokenRevoke-7')));
    await tester.pumpAndSettle();
    expect(find.text('Revoke Old box?'), findsOneWidget);

    await tester.tap(find.byKey(const Key('connectTokenRevokeConfirm')));
    await tester.pumpAndSettle();

    expect(fake.revoked, ['7']);
    expect(find.text('Old box'), findsNothing);
    expect(find.text('No tokens yet.'), findsOneWidget);
    await tester.pump(const Duration(seconds: 5));
    await tester.pumpAndSettle();
  });

  testWidgets('revoke cancelled: nothing is sent', (tester) async {
    final fake = _FakeTokens(tokens: [ConnectToken(id: '7', name: 'Old box')]);
    await pumpPage(tester, fake);

    await tester.tap(find.byKey(const ValueKey('connectTokenRevoke-7')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    expect(fake.revoked, isEmpty);
    expect(find.text('Old box'), findsOneWidget);
  });

  testWidgets('hosted server: no tokens view, the consent page stays as it was',
      (tester) async {
    ServerEdition.current.value = ServerCapabilities.hosted;
    connectService = _FakeTokens();
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: const ConnectPage(requestId: null, animateBackground: false),
    ));
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.text('Access tokens'), findsNothing);
    expect(find.textContaining('expired'), findsOneWidget);
  });
}
