import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/receipt_models.dart';
import 'package:buoy_ai/models/transaction_models.dart';
import 'package:buoy_ai/pages/receipts_page.dart';
import 'package:buoy_ai/services/receipts_service.dart';
import 'package:buoy_ai/widgets/receipt_scan_sheet.dart';
import 'package:buoy_ai/widgets/receipt_split_card.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeReceipts extends ReceiptsService {
  _FakeReceipts({this.statusInfo = const ReceiptScanStatusInfo(), this.scans = const [], this.match, this.scanResult, this.scanError});

  ReceiptScanStatusInfo statusInfo;
  List<ReceiptScan> scans;
  ReceiptScan? match;
  ReceiptScan? scanResult;
  ReceiptScanException? scanError;
  final applied = <int>[];
  final dismissed = <int>[];
  final sent = <List<ReceiptUpload>>[];
  int? sentTransactionId;

  @override
  Future<ReceiptScanStatusInfo> status() async => statusInfo;

  @override
  Future<List<ReceiptScan>> list() async => scans;

  @override
  Future<ReceiptScan> scan(List<ReceiptUpload> files, {int? transactionId}) async {
    sent.add(files);
    sentTransactionId = transactionId;
    if (scanError != null) throw scanError!;
    return scanResult!;
  }

  @override
  Future<ReceiptScan?> forTransaction(int transactionId) async => match;

  @override
  Future<ReceiptScan> apply(int id) async {
    applied.add(id);
    return ReceiptScan(id: id, status: ReceiptStatus.applied, retailer: 'Walmart');
  }

  @override
  Future<ReceiptScan> dismiss(int id) async {
    dismissed.add(id);
    return ReceiptScan(id: id, status: ReceiptStatus.dismissed);
  }
}

Widget wrap(Widget child) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: child,
    );

ReceiptScan _ready({int id = 7, int transactionId = 100}) => ReceiptScan.fromJson({
      'id': id,
      'status': 'ready',
      'retailer': 'Walmart',
      'total': 60.0,
      'itemCount': 3,
      'proposal': {
        'method': 'exact',
        'confident': true,
        'charges': [
          {
            'transactionId': transactionId,
            'amount': 60.0,
            'date': '2026-10-01T00:00:00Z',
            'splits': [
              {'categoryId': 20, 'categoryName': 'Groceries', 'amount': 40.0, 'items': ['Milk', 'Bananas']},
              {'categoryId': 24, 'categoryName': 'Shopping', 'amount': 20.0, 'items': ['Paper towels']},
            ],
          },
        ],
      },
    });

Transaction _walmartCharge({bool split = false}) => Transaction(
      id: 100,
      accountId: 1,
      amount: -60,
      type: TransactionType.debit,
      description: 'WALMART SUPERCENTER #1234',
      merchantName: 'Walmart',
      categoryName: 'Groceries',
      date: DateTime(2026, 10, 1),
      accountName: 'Checking',
      isSplit: split,
    );

// A real 1x1 PNG, so the preview thumbnail decodes.
const _png = <int>[
  0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
  0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
  0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0xF8, 0x0F, 0x00, 0x00,
  0x01, 0x01, 0x00, 0x05, 0x18, 0xD8, 0x4D, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
  0x42, 0x60, 0x82,
];

Future<void> _openSheet(WidgetTester tester, _FakeReceipts fake, {int? transactionId}) async {
  await tester.pumpWidget(wrap(Builder(
    builder: (context) => Scaffold(
      body: Center(
        child: TextButton(
          onPressed: () => showReceiptScanSheet(context, const [ReceiptUpload(_png, 'shot.png')],
              transactionId: transactionId, service: fake),
          child: const Text('open'),
        ),
      ),
    ),
  )));
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
}

void main() {
  group('receipt models', () {
    test('read camelCase and PascalCase', () {
      final status = ReceiptScanStatusInfo.fromJson({'Blocker': 'premium_required', 'ReadyCount': 2, 'Stores': ['Walmart']});
      expect(status.blocker, ReceiptBlocker.premiumRequired);
      expect(status.readyCount, 2);
      final r = _ready();
      expect(r.status, ReceiptStatus.ready);
      expect(r.categoryCount, 2);
      expect(r.chargedTotal, 60.0);
      expect(ReceiptStatusX.fromString('not_a_receipt'), ReceiptStatus.notAReceipt);
      expect(ReceiptStatusX.fromString('???'), ReceiptStatus.unknown);
      expect(const ReceiptUpload([0x25, 0x50, 0x44, 0x46, 0x2D], 'x.bin').isPdf, isTrue);
    });

    test('store charges are recognised by merchant or description', () {
      expect(isReceiptStore(_walmartCharge()), isTrue);
      final coffee = Transaction(
          id: 1, accountId: 1, amount: -5, type: TransactionType.debit, description: 'BLUE BOTTLE',
          categoryName: 'Dining Out', date: DateTime(2026, 10, 1), accountName: 'Checking');
      expect(isReceiptStore(coffee), isFalse);
    });
  });

  group('ReceiptScanSheet', () {
    testWidgets('shows what will be sent and sends nothing until Send', (tester) async {
      final fake = _FakeReceipts(scanResult: _ready());
      await _openSheet(tester, fake, transactionId: 100);
      expect(find.byKey(const Key('rcpt-thumb-0')), findsOneWidget);
      expect(find.textContaining('Google Gemini'), findsOneWidget);
      expect(fake.sent, isEmpty);

      await tester.tap(find.byKey(const Key('rcpt-send')));
      await tester.pumpAndSettle();
      expect(fake.sent, hasLength(1));
      expect(fake.sentTransactionId, 100);
      expect(find.text('Groceries'), findsOneWidget);
      expect(find.text('Milk, Bananas'), findsOneWidget);

      await tester.tap(find.byKey(const Key('rcpt-apply')));
      await tester.pumpAndSettle();
      expect(fake.applied, [7]);
    });

    testWidgets('several screenshots are announced as one receipt', (tester) async {
      final fake = _FakeReceipts(scanResult: _ready());
      await tester.pumpWidget(wrap(Builder(
        builder: (context) => Scaffold(
          body: TextButton(
            onPressed: () => showReceiptScanSheet(
                context, const [ReceiptUpload(_png, 'a.png'), ReceiptUpload(_png, 'b.png'), ReceiptUpload(_png, 'c.png')],
                service: fake),
            child: const Text('open'),
          ),
        ),
      )));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      expect(find.textContaining('3 screenshots, read as one receipt'), findsOneWidget);
      await tester.tap(find.byKey(const Key('rcpt-send')));
      await tester.pumpAndSettle();
      expect(fake.sent.single, hasLength(3));
    });

    testWidgets('a receipt still waiting for its charge says so', (tester) async {
      final fake = _FakeReceipts(
          scanResult: const ReceiptScan(id: 3, status: ReceiptStatus.unmatched, message: "The charge isn't in from your bank yet."));
      await _openSheet(tester, fake);
      await tester.tap(find.byKey(const Key('rcpt-send')));
      await tester.pumpAndSettle();
      expect(find.textContaining("isn't in from your bank yet"), findsOneWidget);
      expect(find.byKey(const Key('rcpt-apply')), findsNothing);
    });

    testWidgets('a Premium block offers Premium instead of retrying', (tester) async {
      final fake = _FakeReceipts(
          scanError: const ReceiptScanException('Receipt scanning is part of Premium.', blocker: ReceiptBlocker.premiumRequired));
      await _openSheet(tester, fake);
      await tester.tap(find.byKey(const Key('rcpt-send')));
      await tester.pumpAndSettle();
      expect(find.text('Receipt scanning is part of Premium.'), findsOneWidget);
      expect(find.text('See Premium'), findsOneWidget);
      expect(find.text('Try again'), findsNothing);
    });

    testWidgets('a failed read can be retried', (tester) async {
      final fake = _FakeReceipts(scanError: const ReceiptScanException(null));
      await _openSheet(tester, fake);
      await tester.tap(find.byKey(const Key('rcpt-send')));
      await tester.pumpAndSettle();
      expect(find.textContaining("couldn't be read"), findsOneWidget);
      fake.scanError = null;
      fake.scanResult = _ready();
      await tester.tap(find.text('Try again'));
      await tester.pumpAndSettle();
      expect(fake.sent, hasLength(2));
      expect(find.byKey(const Key('rcpt-apply')), findsOneWidget);
    });
  });

  group('ReceiptsPage', () {
    testWidgets('explains how to send a receipt and lists ready ones', (tester) async {
      final fake = _FakeReceipts(scans: [_ready()]);
      await tester.pumpWidget(wrap(ReceiptsPage(service: fake)));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('rcpt-scan')), findsOneWidget);
      await tester.scrollUntilVisible(find.text('Apply split'), 300, scrollable: find.byType(Scrollable).first);
      expect(find.text('2 categories ready'), findsOneWidget);
      await tester.ensureVisible(find.text('Apply split'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Apply split'));
      await tester.pumpAndSettle();
      expect(fake.applied, [7]);
    });

    testWidgets('blockers explain themselves and hide the scan button', (tester) async {
      await tester.pumpWidget(wrap(ReceiptsPage(
          service: _FakeReceipts(statusInfo: const ReceiptScanStatusInfo(blocker: ReceiptBlocker.premiumRequired)))));
      await tester.pumpAndSettle();
      expect(find.text('Receipt scanning is part of Premium.'), findsOneWidget);
      expect(find.byKey(const Key('rcpt-scan')), findsNothing);

      await tester.pumpWidget(wrap(ReceiptsPage(
          key: UniqueKey(),
          service: _FakeReceipts(statusInfo: const ReceiptScanStatusInfo(blocker: ReceiptBlocker.aiConsentRequired)))));
      await tester.pumpAndSettle();
      expect(find.text('Turn on AI features'), findsOneWidget);
    });
  });

  group('ReceiptSplitCard', () {
    testWidgets('offers the split for a matched charge', (tester) async {
      final fake = _FakeReceipts(match: _ready());
      var appliedCallbacks = 0;
      await tester.pumpWidget(wrap(Scaffold(
        body: ReceiptSplitCard(transaction: _walmartCharge(), service: fake, onApplied: () async => appliedCallbacks++),
      )));
      await tester.pumpAndSettle();
      expect(find.text('Receipt from Walmart'), findsOneWidget);
      await tester.tap(find.text('Apply split'));
      await tester.pumpAndSettle();
      expect(fake.applied, [7]);
      expect(appliedCallbacks, 1);
    });

    testWidgets('offers Scan receipt when no receipt matched', (tester) async {
      await tester.pumpWidget(wrap(Scaffold(
        body: ReceiptSplitCard(transaction: _walmartCharge(), service: _FakeReceipts(), onApplied: () async {}),
      )));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('rcpt-card-scan')), findsOneWidget);
    });

    testWidgets('shows nothing for a charge already split by hand', (tester) async {
      await tester.pumpWidget(wrap(Scaffold(
        body: ReceiptSplitCard(transaction: _walmartCharge(split: true), service: _FakeReceipts(), onApplied: () async {}),
      )));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('rcpt-card-scan')), findsNothing);
    });
  });
}
