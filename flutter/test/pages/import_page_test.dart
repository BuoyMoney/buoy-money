import 'dart:typed_data';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/import_models.dart';
import 'package:buoy_ai/pages/import_page.dart';
import 'package:buoy_ai/providers/import_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import '../providers/import_fakes.dart';

Widget _host(ImportController controller) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: ImportPage(controller: controller),
    );

ImportController _controller(FakeImportService service) => ImportController(
      service: service,
      accounts: FakeAccountsService(),
      categories: FakeCategoriesService(),
      picker: () async => [ImportPickedFile(name: 'bank.csv', bytes: Uint8List.fromList([1, 2]))],
    );

void main() {
  setUpAll(() {
    GoogleFonts.config.allowRuntimeFetching = false;
  });

  testWidgets('the first step explains where a file comes from and offers the picker', (tester) async {
    tester.view.physicalSize = const Size(900, 2000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(_host(_controller(FakeImportService(previewResult: samplePreview()))));
    await tester.pump();

    expect(find.text('Import data'), findsOneWidget);
    expect(find.text('Choose file'), findsOneWidget);
    expect(find.text('Bank file (CSV, OFX or QFX)'), findsOneWidget);
    expect(find.text('Actual Budget'), findsOneWidget);
    expect(find.text('YNAB'), findsOneWidget);
  });

  testWidgets('pick, review the counts, confirm, see the result and undo', (tester) async {
    tester.view.physicalSize = const Size(900, 2400);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final service = FakeImportService(
      previewResult: samplePreview(
        newCount: 3,
        likely: 1,
        rows: [row(1, ImportRowClass.duplicateLikely)],
      ),
    )..rowsByClass[ImportRowClass.duplicateLikely] = [row(1, ImportRowClass.duplicateLikely)];
    final controller = _controller(service);
    await tester.pumpWidget(_host(controller));
    await tester.pump();

    await tester.tap(find.byKey(const ValueKey('import-choose-file')));
    await tester.pumpAndSettle();

    // Review step: counts, the duplicate, and an import button with the number.
    expect(find.byKey(const ValueKey('import-count-new')), findsOneWidget);
    expect(find.descendant(of: find.byKey(const ValueKey('import-count-new')), matching: find.text('3')), findsOneWidget);
    expect(find.byKey(const ValueKey('import-count-likely')), findsOneWidget);
    expect(find.text('Import 3 transactions'), findsOneWidget);
    expect(find.textContaining('Coffee 1'), findsOneWidget);

    // Turning on likely duplicates raises the number.
    await tester.tap(find.byKey(const ValueKey('import-include-likely')));
    await tester.pumpAndSettle();
    expect(find.text('Import 4 transactions'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('import-include-likely')));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const ValueKey('import-confirm')));
    await tester.pumpAndSettle();

    expect(service.commits, hasLength(1));
    expect(find.text('Import finished'), findsOneWidget);
    expect(find.text('3 transactions imported'), findsOneWidget);
    expect(find.text('1 account created'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('import-undo')));
    await tester.pumpAndSettle();

    expect(service.undone, ['b1']);
    expect(find.text('Import undone'), findsOneWidget);
  });

  testWidgets('an ambiguous date order blocks the import button until one is chosen', (tester) async {
    tester.view.physicalSize = const Size(900, 2400);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final service = FakeImportService(previewResult: samplePreview(needsDateOrder: true))
      ..reparseResult = samplePreview();
    await tester.pumpWidget(_host(_controller(service)));
    await tester.pump();
    await tester.tap(find.byKey(const ValueKey('import-choose-file')));
    await tester.pumpAndSettle();

    expect(find.text('Which date order does this file use?'), findsOneWidget);
    final blocked = tester.widget<FilledButton>(find.byKey(const ValueKey('import-confirm')));
    expect(blocked.onPressed, isNull);

    await tester.tap(find.byKey(const ValueKey('import-date-day-first')));
    await tester.pumpAndSettle();

    expect(service.reparseCalls.single.mapping.dateOrder, ImportDateOrder.dayFirst);
    expect(find.text('Which date order does this file use?'), findsNothing);
    final ready = tester.widget<FilledButton>(find.byKey(const ValueKey('import-confirm')));
    expect(ready.onPressed, isNotNull);
  });

  testWidgets('a file with several accounts shows one row per account and the category choices', (tester) async {
    tester.view.physicalSize = const Size(900, 3000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final service = FakeImportService(
      previewResult: ImportPreview(
        importId: 'imp1',
        detected: const ImportDetected(source: 'ynab'),
        accounts: const [
          ImportAccountPreview(sourceKey: 'Checking', sourceName: 'Checking', proposedAction: 'match', matchedAccountId: 7, rowCount: 5, newCount: 5),
          ImportAccountPreview(sourceKey: 'Visa', sourceName: 'Visa', proposedAction: 'create', rowCount: 2, newCount: 2),
        ],
        categories: const [
          ImportCategoryPreview(
              sourceGroup: 'Everyday', sourceName: 'Groceries', proposedCategoryId: 20, proposedCategoryName: 'Groceries', proposedNewName: 'Groceries'),
        ],
        budget: const ImportBudgetPreview(latestMonth: '2026-01', categoriesWithAmounts: 4),
        summary: const ImportSummary(total: 7, newCount: 7),
      ),
    );
    final controller = _controller(service);
    await controller.loadChoices();
    await tester.pumpWidget(_host(controller));
    await tester.pump();
    await tester.tap(find.byKey(const ValueKey('import-choose-file')));
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('import-account-Checking')), findsOneWidget);
    expect(find.byKey(const ValueKey('import-account-Visa')), findsOneWidget);
    expect(find.byKey(const ValueKey('import-category-Everyday|Groceries')), findsOneWidget);
    expect(find.byKey(const ValueKey('import-create-budget')), findsOneWidget);
    expect(find.text('Import 7 transactions'), findsOneWidget);

    // Skipping the Visa account takes its two rows out of the count.
    await tester.tap(find.byKey(const ValueKey('import-account-Visa')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Skip this account').last);
    await tester.pumpAndSettle();
    expect(find.text('Import 5 transactions'), findsOneWidget);
  });

  testWidgets('a read failure shows the server message on the first step', (tester) async {
    tester.view.physicalSize = const Size(900, 2000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final service = FakeImportService(previewResult: samplePreview())..previewError = StateError('bad');
    await tester.pumpWidget(_host(_controller(service)));
    await tester.pump();

    await tester.tap(find.byKey(const ValueKey('import-choose-file')));
    await tester.pumpAndSettle();

    expect(find.textContaining('We could not read that file'), findsOneWidget);
    expect(find.byKey(const ValueKey('import-choose-file')), findsOneWidget);
  });
}
