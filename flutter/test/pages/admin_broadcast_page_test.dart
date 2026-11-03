import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/admin_broadcast_page.dart';
import 'package:buoy_ai/services/broadcast_service.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeService extends BroadcastService {
  _FakeService({this.previewError, this.sendFailures = 0});

  int sendFailures;

  final int count = 42;
  final int skipped = 3;
  final Object? previewError;
  final List<Map<String, Object?>> previews = [];
  final List<Map<String, Object?>> sends = [];

  @override
  Future<BroadcastResult> preview({
    required String subject,
    required String bodyMarkdown,
    required String audience,
    required String category,
  }) async {
    previews.add({
      'subject': subject,
      'body': bodyMarkdown,
      'audience': audience,
      'category': category
    });
    if (previewError != null) throw previewError!;
    return BroadcastResult(
        dryRun: true,
        status: 'Preview',
        recipientCount: count,
        skippedCount: skipped);
  }

  @override
  Future<BroadcastResult> send({
    required String subject,
    required String bodyMarkdown,
    required String audience,
    required String category,
    required int confirmRecipientCount,
    String? idempotencyKey,
  }) async {
    sends.add({
      'key': idempotencyKey,
      'audience': audience,
      'category': category,
      'confirm': confirmRecipientCount
    });
    if (sendFailures > 0) {
      sendFailures--;
      throw _http(504);
    }
    return BroadcastResult(
        dryRun: false,
        status: 'Queued',
        recipientCount: count,
        skippedCount: skipped);
  }
}

DioException _http(int status, [Object? data]) => DioException(
      requestOptions: RequestOptions(path: '/admin/broadcast'),
      response: Response(
          requestOptions: RequestOptions(path: '/admin/broadcast'),
          statusCode: status,
          data: data),
    );

/// A tall screen so the whole form is built (a ListView only builds what is on screen).
void _tall(WidgetTester tester) {
  tester.view.physicalSize = const Size(900, 3200);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

Widget _wrap(BroadcastService service) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: AdminBroadcastPage(service: service),
    );

Future<void> _fill(WidgetTester tester) async {
  await tester.enterText(
      find.widgetWithText(TextField, 'Subject'), 'Maintenance tonight');
  await tester.enterText(find.widgetWithText(TextField, 'Body (Markdown)'),
      'We will be **down** briefly.');
  await tester.pump();
}

Finder get _previewButton =>
    find.widgetWithText(FilledButton, 'Preview recipients');
Finder get _sendButton => find.widgetWithText(FilledButton, 'Send now');

void main() {
  testWidgets('the body shows as rendered Markdown while typing',
      (tester) async {
    _tall(tester);
    await tester.pumpWidget(_wrap(_FakeService()));
    await _fill(tester);

    expect(find.text('Maintenance tonight'), findsWidgets);
    final preview = find.byType(MarkdownBody);
    expect(
        find.descendant(
            of: preview,
            matching: find.textContaining('down', findRichText: true)),
        findsOneWidget);
    expect(
        find.descendant(
            of: preview,
            matching: find.textContaining('**', findRichText: true)),
        findsNothing);
  });

  testWidgets('a pipe table in the body renders as a table', (tester) async {
    _tall(tester);
    await tester.pumpWidget(_wrap(_FakeService()));
    await tester.enterText(find.widgetWithText(TextField, 'Body (Markdown)'),
        '| Plan | Price |\n| --- | --- |\n| Standard | 5 |');
    await tester.pump();

    expect(find.byType(Table), findsOneWidget);
    expect(find.text('Standard', findRichText: true), findsOneWidget);
  });

  testWidgets(
      'preview asks first, shows the count, and send needs that count typed',
      (tester) async {
    _tall(tester);
    final service = _FakeService();
    await tester.pumpWidget(_wrap(service));
    await _fill(tester);
    await tester.tap(find.text('Incident'));
    await tester.tap(find.text('Active users'));
    await tester.pump();

    expect(_sendButton, findsNothing);
    await tester.tap(_previewButton);
    await tester.pumpAndSettle();

    expect(service.previews.single, containsPair('audience', 'active'));
    expect(service.previews.single, containsPair('category', 'incident'));
    expect(find.text('42 people will get this email.'), findsOneWidget);
    expect(find.text('3 skipped because they opted out of announcements.'),
        findsOneWidget);

    // Disabled until the exact count is typed.
    expect(tester.widget<FilledButton>(_sendButton).onPressed, isNull);
    await tester.enterText(
        find.widgetWithText(TextField, 'Type 42 to confirm'), '41');
    await tester.pump();
    expect(tester.widget<FilledButton>(_sendButton).onPressed, isNull);

    await tester.enterText(
        find.widgetWithText(TextField, 'Type 42 to confirm'), '42');
    await tester.pump();
    await tester.tap(_sendButton);
    await tester.pumpAndSettle();

    expect(service.sends.single, {
      'key': isNotNull,
      'audience': 'active',
      'category': 'incident',
      'confirm': 42
    });
    expect(find.text('Queued for delivery to 42 recipients.'), findsOneWidget);
  });

  testWidgets('editing after a preview throws the preview away',
      (tester) async {
    _tall(tester);
    await tester.pumpWidget(_wrap(_FakeService()));
    await _fill(tester);
    await tester.tap(_previewButton);
    await tester.pumpAndSettle();
    expect(_sendButton, findsOneWidget);

    await tester.enterText(
        find.widgetWithText(TextField, 'Subject'), 'Different subject');
    await tester.pump();

    expect(_sendButton, findsNothing);
    expect(
        find.text(
            'You changed the email. Preview the recipients again before sending.'),
        findsOneWidget);
  });

  testWidgets('moving the cursor after a preview keeps the preview',
      (tester) async {
    _tall(tester);
    await tester.pumpWidget(_wrap(_FakeService()));
    await _fill(tester);
    await tester.tap(_previewButton);
    await tester.pumpAndSettle();
    expect(_sendButton, findsOneWidget);

    final subject = find.widgetWithText(TextField, 'Subject');
    final controller = tester.widget<TextField>(subject).controller!;
    controller.selection = const TextSelection.collapsed(offset: 2);
    await tester.pump();

    expect(_sendButton, findsOneWidget);
  });

  testWidgets('missing MFA (403) says how to fix it', (tester) async {
    _tall(tester);
    await tester.pumpWidget(_wrap(_FakeService(
        previewError: _http(403, {
      'message':
          'Phishing-resistant MFA (TOTP or passkey) is required to broadcast.'
    }))));
    await _fill(tester);
    await tester.tap(_previewButton);
    await tester.pumpAndSettle();

    expect(find.textContaining('two-step sign-in'), findsOneWidget);
    expect(_sendButton, findsNothing);
  });

  testWidgets("a 400 shows the server's own message", (tester) async {
    _tall(tester);
    await tester.pumpWidget(_wrap(_FakeService(
        previewError: _http(400, {'message': 'Too many recipients.'}))));
    await _fill(tester);
    await tester.tap(_previewButton);
    await tester.pumpAndSettle();

    expect(find.text('Too many recipients.'), findsOneWidget);
  });

  testWidgets('an empty subject or body is stopped before the server',
      (tester) async {
    _tall(tester);
    final service = _FakeService();
    await tester.pumpWidget(_wrap(service));
    await tester.tap(_previewButton);
    await tester.pump();

    expect(find.text('Add a subject and a body first.'), findsOneWidget);
    expect(service.previews, isEmpty);
  });

  testWidgets('a retried send reuses the same idempotency key', (tester) async {
    _tall(tester);
    final service = _FakeService(sendFailures: 1);
    await tester.pumpWidget(_wrap(service));
    await _fill(tester);
    await tester.tap(_previewButton);
    await tester.pumpAndSettle();
    await tester.enterText(
        find.widgetWithText(TextField, 'Type 42 to confirm'), '42');
    await tester.pump();
    await tester.tap(_sendButton);
    await tester.pumpAndSettle();
    expect(find.text('Queued for delivery to 42 recipients.'), findsNothing);
    await tester.tap(_sendButton);
    await tester.pumpAndSettle();

    expect(service.sends, hasLength(2));
    expect(service.sends[0]['key'], isNotNull);
    expect(service.sends[1]['key'], service.sends[0]['key']);
    expect(find.text('Queued for delivery to 42 recipients.'), findsOneWidget);
  });

  testWidgets('a role 403 does not blame MFA', (tester) async {
    _tall(tester);
    await tester.pumpWidget(_wrap(_FakeService(previewError: _http(403))));
    await _fill(tester);
    await tester.tap(_previewButton);
    await tester.pumpAndSettle();
    expect(find.textContaining('two-step sign-in'), findsNothing);
  });

  testWidgets('strikethrough is not rendered, matching the server',
      (tester) async {
    _tall(tester);
    await tester.pumpWidget(_wrap(_FakeService()));
    await tester.enterText(
        find.widgetWithText(TextField, 'Body (Markdown)'), '~~gone~~');
    await tester.pump();
    expect(
        find.descendant(
            of: find.byType(MarkdownBody),
            matching: find.textContaining('~~gone~~', findRichText: true)),
        findsOneWidget);
  });
}
