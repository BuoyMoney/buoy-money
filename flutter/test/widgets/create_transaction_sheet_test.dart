import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/dashboard_models.dart';
import 'package:buoy_ai/models/transaction_models.dart';
import 'package:buoy_ai/services/accounts_service.dart';
import 'package:buoy_ai/services/transactions_service.dart';
import 'package:buoy_ai/widgets/transaction_modals.dart';

/// Widget coverage for the add-transaction sheet (#645).
///
/// `flutter drive` / `integration_test` can't reliably open a `showModalBottomSheet`, so the
/// add-transaction flow had no automated coverage. A widget test drives the sheet directly:
/// modal routes render fine under the test binding (unlike the live-binding integration_test),
/// and the two service globals are swapped for fakes so nothing touches the network.
class _FakeAccountsService extends AccountsService {
  final List<Account> _accounts;
  _FakeAccountsService(this._accounts);

  @override
  Future<List<Account>> getAccounts() async => _accounts;
}

class _FakeTransactionsService extends TransactionsService {
  int calls = 0;
  double? lastAmount;
  String? lastDescription;
  int? lastAccountId;

  @override
  Future<Transaction> createTransaction({
    int? accountId,
    required double amount,
    required String description,
    String? merchantName,
    DateTime? date,
    int? categoryId,
    String? userNotes,
  }) async {
    calls++;
    lastAmount = amount;
    lastDescription = description;
    lastAccountId = accountId;
    return Transaction(
      id: 999,
      accountId: accountId ?? 0,
      amount: amount,
      type: TransactionType.debit,
      description: description,
      categoryName: 'Uncategorized',
      date: date ?? DateTime(2026, 1, 1),
      accountName: 'Premium Checking',
    );
  }
}

void main() {
  late AccountsService realAccounts;
  late TransactionsService realTransactions;
  late _FakeTransactionsService fakeTx;

  setUp(() {
    // GoogleFonts would try to fetch over the network under test; keep it offline.
    GoogleFonts.config.allowRuntimeFetching = false;

    realAccounts = accountsService;
    realTransactions = transactionsService;

    accountsService = _FakeAccountsService(<Account>[
      Account.fromJson(<String, dynamic>{
        'id': 7,
        'name': 'Premium Checking',
        'type': 0,
        'isActive': true,
      }),
    ]);
    fakeTx = _FakeTransactionsService();
    transactionsService = fakeTx;
  });

  tearDown(() {
    accountsService = realAccounts;
    transactionsService = realTransactions;
  });

  /// Open the sheet on a generous surface (it's scrollable; a tall surface keeps the
  /// Add-transaction button hittable) and let the entrance animation + account load settle.
  /// Avoids pumpAndSettle: the account-loading spinner schedules frames forever.
  Future<void> openSheet(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1200, 2600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Builder(
            builder: (ctx) => Center(
              child: ElevatedButton(
                onPressed: () => showCreateTransactionSheet(ctx),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    await tester.pump(); // push the modal route
    await tester.pump(const Duration(milliseconds: 500)); // finish anim + fake getAccounts
  }

  testWidgets('renders the amount + description fields and the submit button', (tester) async {
    await openSheet(tester);

    expect(find.byKey(const Key('addtx_amount')), findsOneWidget);
    expect(find.byKey(const Key('addtx_description')), findsOneWidget);
    expect(find.byKey(const Key('addtx_submit')), findsOneWidget);
  });

  testWidgets('blocks submit with a missing description and never calls the service', (tester) async {
    await openSheet(tester);

    await tester.tap(find.byKey(const Key('addtx_submit')));
    await tester.pump();

    expect(find.text('Please enter a description.'), findsOneWidget);
    expect(fakeTx.calls, 0);
  });

  testWidgets('blocks submit when the amount is missing or not positive', (tester) async {
    await openSheet(tester);

    await tester.enterText(find.byKey(const Key('addtx_description')), 'Coffee');
    await tester.tap(find.byKey(const Key('addtx_submit')));
    await tester.pump();

    expect(find.text('Please enter an amount greater than zero.'), findsOneWidget);
    expect(fakeTx.calls, 0);
  });

  testWidgets('submits a valid expense with a signed (negative) amount', (tester) async {
    await openSheet(tester);

    await tester.enterText(find.byKey(const Key('addtx_description')), 'Coffee');
    await tester.enterText(find.byKey(const Key('addtx_amount')), '4.50');
    await tester.tap(find.byKey(const Key('addtx_submit')));
    await tester.pump(); // run _submit → fake createTransaction → pop
    await tester.pump(const Duration(milliseconds: 400));

    expect(fakeTx.calls, 1);
    expect(fakeTx.lastDescription, 'Coffee');
    // Default is an expense, so the amount is stored negative.
    expect(fakeTx.lastAmount, -4.50);
    // Sheet popped on success.
    expect(find.byKey(const Key('addtx_submit')), findsNothing);
  });
}
