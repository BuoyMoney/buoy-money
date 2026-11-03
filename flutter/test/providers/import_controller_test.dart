import 'package:buoy_ai/models/import_models.dart';
import 'package:buoy_ai/providers/import_controller.dart';
import 'package:flutter_test/flutter_test.dart';

import 'import_fakes.dart';

void main() {
  test('picking a file reads it and moves to review', () async {
    final service = FakeImportService(previewResult: samplePreview());
    final c = build(service);

    await c.pickFiles();

    expect(c.step, ImportStep.review);
    expect(c.preview!.summary.newCount, 3);
    expect(c.willImportCount, 3);
    expect(c.canCommit, isTrue);
  });

  test('cancelling the picker changes nothing', () async {
    final c = build(FakeImportService(previewResult: samplePreview()), picked: const []);
    await c.pickFiles();
    expect(c.step, ImportStep.choose);
  });

  test('a read failure returns to the first step with the server message', () async {
    final service = FakeImportService(previewResult: samplePreview())..previewError = StateError('boom');
    final c = build(service);

    await c.pickFiles();

    expect(c.step, ImportStep.choose);
    expect(c.failure, isNotNull);
  });

  test('a file over the limit is reported without calling the server', () async {
    final service = FakeImportService(previewResult: samplePreview());
    final c = ImportController(
      service: service,
      accounts: FakeAccountsService(),
      categories: FakeCategoriesService(),
      picker: () async => throw const ImportFileTooLarge('huge.zip'),
    );

    await c.pickFiles();

    expect(c.failure!.tooLarge, isTrue);
    expect(c.step, ImportStep.choose);
  });

  test('likely duplicates are skipped by default and counted when turned on', () async {
    final service = FakeImportService(
      previewResult: samplePreview(newCount: 2, likely: 3, possible: 1, rows: [row(1, ImportRowClass.duplicateLikely)]),
    );
    final c = build(service);
    await c.pickFiles();

    expect(c.willImportCount, 3); // 2 new + 1 possible
    c.setIncludeLikely(true);
    expect(c.willImportCount, 6);
  });

  test('per-row choices adjust the count both ways', () async {
    final service = FakeImportService(
      previewResult: samplePreview(
        newCount: 0,
        likely: 2,
        possible: 2,
        rows: [
          row(1, ImportRowClass.duplicateLikely),
          row(2, ImportRowClass.duplicateLikely),
          row(3, ImportRowClass.possibleDuplicate),
          row(4, ImportRowClass.possibleDuplicate),
        ],
      ),
    );
    final c = build(service);
    await c.pickFiles();
    expect(c.willImportCount, 2);

    c.setRowOverride(1, 'import'); // a likely duplicate the user wants
    c.setRowOverride(3, 'skip'); // a possible duplicate the user does not
    expect(c.willImportCount, 2);
    c.setRowOverride(3, null);
    expect(c.willImportCount, 3);
  });

  test('commit sends the account, category, class and override choices', () async {
    final service = FakeImportService(
      previewResult: samplePreview(
        newCount: 1,
        likely: 1,
        rows: [row(1, ImportRowClass.duplicateLikely)],
        categories: const [
          ImportCategoryPreview(
              sourceGroup: 'Everyday', sourceName: 'Groceries', proposedCategoryId: 20, proposedNewName: 'Groceries'),
          ImportCategoryPreview(sourceGroup: '', sourceName: 'Hobbies', proposedNewName: 'Hobbies'),
        ],
        budget: const ImportBudgetPreview(latestMonth: '2026-01', categoriesWithAmounts: 2),
      ),
    );
    final c = build(service);
    await c.pickFiles();
    c.setRowOverride(1, 'import');
    c.setCategoryChoice(const ImportCategoryChoice(sourceGroup: '', sourceName: 'Hobbies', action: 'none'));

    await c.commit();

    expect(c.step, ImportStep.done);
    expect(c.result!.imported, 3);
    final sent = service.commits.single;
    expect(sent.includeClasses, [ImportRowClass.newRow, ImportRowClass.possibleDuplicate]);
    expect(sent.overrides, {1: 'import'});
    expect(sent.createBudget, isTrue);
    expect(sent.accountMappings.single.action, 'create');
    final groceries = sent.categoryMappings.firstWhere((m) => m.sourceName == 'Groceries');
    expect(groceries.action, 'map');
    expect(groceries.categoryId, 20);
    expect(sent.categoryMappings.firstWhere((m) => m.sourceName == 'Hobbies').action, 'none');
  });

  test('an unmatched category defaults to creating it', () async {
    final service = FakeImportService(
      previewResult: samplePreview(categories: const [
        ImportCategoryPreview(sourceGroup: '', sourceName: 'Hobbies', proposedNewName: 'Hobbies'),
      ]),
    );
    final c = build(service);
    await c.pickFiles();
    expect(c.categoryChoice(c.preview!.categories.single)!.action, 'create');
  });

  test('an ambiguous date order blocks commit until a reparse resolves it', () async {
    final service = FakeImportService(previewResult: samplePreview(needsDateOrder: true))
      ..reparseResult = samplePreview(needsDateOrder: false);
    final c = build(service);
    await c.pickFiles();
    expect(c.canCommit, isFalse);

    await c.setDateOrder(ImportDateOrder.dayFirst);

    expect(service.reparseCalls.single.mapping.dateOrder, ImportDateOrder.dayFirst);
    expect(c.canCommit, isTrue);
  });

  test('choosing a target account re-reads the file against it', () async {
    final service = FakeImportService(previewResult: samplePreview());
    final c = build(service);
    await c.pickFiles();

    await c.setTargetAccount(7);

    expect(service.reparseCalls.single.accountId, 7);
    expect(c.targetAccountId, 7);
  });

  test('changing the target account replaces the single-account choice', () async {
    ImportPreview targeted(int id) => samplePreview(accounts: [
          ImportAccountPreview(
              sourceKey: '__target__', sourceName: 'acct', proposedAction: 'match', matchedAccountId: id, rowCount: 3),
        ]);
    final service = FakeImportService(previewResult: targeted(1));
    final c = build(service);
    await c.pickFiles();
    expect(c.accountChoice('__target__')?.accountId, 1);

    service.reparseResult = targeted(2);
    await c.setTargetAccount(2);

    expect(c.accountChoice('__target__')?.accountId, 2);
  });

  test('changing a column keeps the other detected columns', () async {
    final preview = samplePreview();
    final withMapping = ImportPreview(
      importId: preview.importId,
      detected: const ImportDetected(
        source: 'csv',
        headers: ['Date', 'Description', 'Amount'],
        proposedMapping: ImportMapping(dateColumn: 0, descriptionColumn: 1, amountColumn: 2),
      ),
      accounts: preview.accounts,
      summary: preview.summary,
    );
    final service = FakeImportService(previewResult: withMapping);
    final c = build(service);
    await c.pickFiles();

    await c.setColumn('description', 0);

    final sent = service.reparseCalls.single.mapping;
    expect(sent.descriptionColumn, 0);
    expect(sent.dateColumn, 0);
    expect(sent.amountColumn, 2);
  });

  test('choosing separate money in and out columns clears the single amount column', () async {
    final withMapping = ImportPreview(
      importId: 'imp1',
      detected: const ImportDetected(
        source: 'csv',
        headers: ['Date', 'Out', 'In'],
        proposedMapping: ImportMapping(dateColumn: 0, amountColumn: 1),
      ),
      accounts: const [ImportAccountPreview(sourceKey: '', sourceName: 'bank', proposedAction: 'create')],
      summary: const ImportSummary(total: 1, newCount: 1),
    );
    final service = FakeImportService(previewResult: withMapping);
    final c = build(service);
    await c.pickFiles();

    await c.setColumn('debit', 1);

    final sent = service.reparseCalls.single.mapping;
    expect(sent.amountColumn, isNull);
    expect(sent.debitColumn, 1);
  });

  test('skipping an account removes its rows from the count', () async {
    final service = FakeImportService(
      previewResult: samplePreview(
        newCount: 5,
        accounts: const [
          ImportAccountPreview(sourceKey: 'A', sourceName: 'A', proposedAction: 'create', rowCount: 3, newCount: 3),
          ImportAccountPreview(sourceKey: 'B', sourceName: 'B', proposedAction: 'create', rowCount: 2, newCount: 2),
        ],
      ),
    );
    final c = build(service);
    await c.pickFiles();
    expect(c.preview!.isMultiAccount, isTrue);

    c.setAccountChoice(const ImportAccountChoice(sourceKey: 'B', action: 'skip'));

    expect(c.willImportCount, 3);
  });

  test('skipping an account only removes the classes that would have been imported', () async {
    // B holds 1 new row plus 3 likely duplicates and 2 invalid rows; A holds 2 new rows.
    final service = FakeImportService(
      previewResult: samplePreview(
        newCount: 3,
        likely: 3,
        accounts: const [
          ImportAccountPreview(sourceKey: 'A', sourceName: 'A', proposedAction: 'create', rowCount: 2, newCount: 2),
          ImportAccountPreview(
              sourceKey: 'B', sourceName: 'B', proposedAction: 'create', rowCount: 6, newCount: 1, duplicateLikelyCount: 3),
        ],
      ),
    );
    final c = build(service);
    await c.pickFiles();

    c.setAccountChoice(const ImportAccountChoice(sourceKey: 'B', action: 'skip'));

    expect(c.willImportCount, 2);
    expect(c.canCommit, isTrue);
  });

  test('loadRows walks the pages of a class, up to a cap', () async {
    final service = FakeImportService(previewResult: samplePreview(likely: 150));
    service.rowsByClass[ImportRowClass.duplicateLikely] = [
      for (var i = 1; i <= 150; i++) row(i, ImportRowClass.duplicateLikely),
    ];
    final c = build(service);
    await c.pickFiles();

    await c.loadRows(ImportRowClass.duplicateLikely);

    expect(c.rowsOfClass(ImportRowClass.duplicateLikely), hasLength(150));
  });

  test('a failed commit returns to review and keeps the choices', () async {
    final service = FakeImportService(previewResult: samplePreview())..commitError = StateError('nope');
    final c = build(service);
    await c.pickFiles();
    c.setIncludeLikely(true);

    await c.commit();

    expect(c.step, ImportStep.review);
    expect(c.failure, isNotNull);
    expect(c.includeLikely, isTrue);
  });

  test('undo calls the batch once and marks the import undone', () async {
    final service = FakeImportService(previewResult: samplePreview());
    final c = build(service);
    await c.pickFiles();
    await c.commit();

    await c.undo();
    await c.undo(); // a second tap does nothing

    expect(service.undone, ['b1']);
    expect(c.undone, isTrue);
    expect(c.undoResult!.transactionsRemoved, 3);
  });

  test('the user accounts and categories load for the pickers', () async {
    final c = build(FakeImportService(previewResult: samplePreview()));
    await c.loadChoices();
    expect(c.userAccounts.single.displayName, 'Checking');
    expect(c.knownCategories.single.name, 'Groceries');
  });

  group('models', () {
    test('ImportPreview reads camelCase and PascalCase', () {
      final camel = ImportPreview.fromJson({
        'importId': 'a',
        'summary': {'total': 2, 'new': 1, 'duplicateLikely': 1},
        'rows': [
          {'rowId': 1, 'date': '2026-01-02', 'amount': -4.5, 'payee': 'Coffee', 'class': 'duplicateLikely'}
        ],
        'needsDateOrderChoice': true,
      });
      expect(camel.importId, 'a');
      expect(camel.summary.newCount, 1);
      expect(camel.rows.single.rowClass, ImportRowClass.duplicateLikely);
      expect(camel.needsDateOrderChoice, isTrue);

      final pascal = ImportPreview.fromJson({
        'ImportId': 'b',
        'Summary': {'Total': 5, 'New': 4},
        'Accounts': [
          {'SourceKey': 'k', 'SourceName': 'n', 'ProposedAction': 'match', 'MatchedAccountId': 9}
        ],
      });
      expect(pascal.importId, 'b');
      expect(pascal.summary.newCount, 4);
      expect(pascal.accounts.single.matchedAccountId, 9);
    });

    test('ImportMapping round trips through json', () {
      const m = ImportMapping(dateOrder: ImportDateOrder.dayFirst, flipSign: true, dateColumn: 1, amountColumn: 3);
      final back = ImportMapping.fromJson(m.toJson());
      expect(back.dateOrder, ImportDateOrder.dayFirst);
      expect(back.flipSign, isTrue);
      expect(back.dateColumn, 1);
      expect(back.amountColumn, 3);
      expect(back.debitColumn, isNull);
    });

    test('ImportCommitRequest json uses string keys for overrides', () {
      const request = ImportCommitRequest(overrides: {4: 'skip'});
      expect(request.toJson()['overrides'], {'4': 'skip'});
    });
  });
}
