import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/services/ai_service.dart';
import 'package:buoy_ai/widgets/ai/conversation_history_sheet.dart';

/// The AI chat history sheet used to receive a snapshot of the conversation list
/// taken while it was still loading, so it showed a spinner forever (reproduced on
/// the Android emulator, 2026-10-05). These tests drive the sheet the way the chat
/// page opens it: a modal bottom sheet whose result is the owner's choice.

AiConversation _conv(String id, String title, {int messages = 4}) =>
    AiConversation(
      id: id.hashCode,
      sessionId: id,
      context: 'chat',
      title: title,
      startedAt: DateTime(2026, 10, 1, 9),
      lastMessageAt: DateTime(2026, 10, 3, 18),
      isActive: true,
      messageCount: messages,
    );

void main() {
  setUp(() {
    // GoogleFonts would try to fetch over the network under test; keep it offline.
    GoogleFonts.config.allowRuntimeFetching = false;
  });

  /// Pumps a page with a button that opens the sheet, and returns a holder for the
  /// sheet's result once it closes.
  Future<List<ConversationHistoryChoice?>> pumpHost(
    WidgetTester tester, {
    required Future<List<AiConversation>> Function() load,
    Future<void> Function(String)? delete,
    void Function(String)? onDeleted,
    String? current,
  }) async {
    final results = <ConversationHistoryChoice?>[];
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(
        body: Builder(
          builder: (context) => Center(
            child: TextButton(
              onPressed: () async {
                results
                    .add(await showModalBottomSheet<ConversationHistoryChoice>(
                  context: context,
                  isScrollControlled: true,
                  builder: (_) => ConversationHistorySheet(
                    currentSessionId: current,
                    loadConversations: load,
                    deleteConversation: delete ?? (_) async {},
                    onDeleted: onDeleted,
                  ),
                ));
              },
              child: const Text('open history'),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open history'));
    await tester.pump(); // route pushed
    await tester.pump(const Duration(milliseconds: 400)); // sheet animated in
    return results;
  }

  testWidgets('shows a spinner, then the conversations once they load',
      (tester) async {
    final pending = Completer<List<AiConversation>>();
    await pumpHost(tester, load: () => pending.future);

    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    expect(find.text('House Fund plan'), findsNothing);

    pending
        .complete([_conv('s1', 'House Fund plan'), _conv('s2', 'Dining out')]);
    await tester.pump();

    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(find.text('House Fund plan'), findsOneWidget);
    expect(find.text('Dining out'), findsOneWidget);
  });

  testWidgets('shows the empty message when there are no conversations',
      (tester) async {
    await pumpHost(tester, load: () async => []);
    await tester.pump();

    expect(find.text('No previous conversations'), findsOneWidget);
  });

  testWidgets('deleting a conversation removes its row and tells the page',
      (tester) async {
    final deleted = <String>[];
    final notified = <String>[];
    await pumpHost(
      tester,
      load: () async =>
          [_conv('s1', 'House Fund plan'), _conv('s2', 'Dining out')],
      delete: (id) async => deleted.add(id),
      onDeleted: notified.add,
    );
    await tester.pump();

    await tester.tap(find.byIcon(Icons.delete_outline).first);
    await tester.pump();

    expect(deleted, ['s1']);
    expect(notified, ['s1']);
    expect(find.text('House Fund plan'), findsNothing);
    expect(find.text('Dining out'), findsOneWidget);
    await tester.pump(const Duration(seconds: 3)); // let the toast time out
  });

  testWidgets('a failed delete keeps the row', (tester) async {
    final notified = <String>[];
    await pumpHost(
      tester,
      load: () async => [_conv('s1', 'House Fund plan')],
      delete: (_) async => throw Exception('offline'),
      onDeleted: notified.add,
    );
    await tester.pump();

    await tester.tap(find.byIcon(Icons.delete_outline));
    await tester.pump();

    expect(find.text('House Fund plan'), findsOneWidget);
    expect(notified, isEmpty);
    await tester.pump(const Duration(seconds: 5)); // let the toast time out
  });

  testWidgets('tapping a conversation closes the sheet with its session id',
      (tester) async {
    final results = await pumpHost(
      tester,
      load: () async =>
          [_conv('s1', 'House Fund plan'), _conv('s2', 'Dining out')],
    );
    await tester.pump();

    await tester.tap(find.text('Dining out'));
    await tester.pumpAndSettle();

    expect(results, hasLength(1));
    expect(results.single?.sessionId, 's2');
    expect(find.byType(ConversationHistorySheet), findsNothing);
  });

  testWidgets('New Chat closes the sheet asking for a new conversation',
      (tester) async {
    final results = await pumpHost(tester,
        load: () async => [_conv('s1', 'House Fund plan')]);
    await tester.pump();

    await tester.tap(find.text('New Chat'));
    await tester.pumpAndSettle();

    expect(results, hasLength(1));
    expect(results.single, isNotNull);
    expect(results.single?.sessionId, isNull);
  });
}
