import 'dart:typed_data';

import 'package:buoy_ai/models/dashboard_models.dart';
import 'package:buoy_ai/models/import_models.dart';
import 'package:buoy_ai/providers/import_controller.dart';
import 'package:buoy_ai/services/accounts_service.dart';
import 'package:buoy_ai/services/categories_service.dart';
import 'package:buoy_ai/services/import_service.dart';

class FakeImportService extends ImportService {
  FakeImportService({required this.previewResult});

  ImportPreview previewResult;
  ImportPreview? reparseResult;
  Object? previewError;
  Object? commitError;
  final reparseCalls = <({int? accountId, ImportMapping mapping})>[];
  final commits = <ImportCommitRequest>[];
  final undone = <String>[];
  final rowsByClass = <String, List<ImportRowPreview>>{};

  @override
  Future<ImportPreview> preview(
    List<ImportPickedFile> files, {
    String source = 'auto',
    int? accountId,
    ImportMapping mapping = const ImportMapping(),
  }) async {
    if (previewError != null) throw previewError!;
    return previewResult;
  }

  @override
  Future<ImportPreview> reparse(
    String importId, {
    String source = 'auto',
    int? accountId,
    ImportMapping mapping = const ImportMapping(),
  }) async {
    reparseCalls.add((accountId: accountId, mapping: mapping));
    return reparseResult ?? previewResult;
  }

  @override
  Future<List<ImportRowPreview>> rows(String importId, {String? rowClass, int page = 0, int pageSize = 50}) async {
    final all = rowsByClass[rowClass] ?? const <ImportRowPreview>[];
    final start = page * pageSize;
    if (start >= all.length) return const [];
    final end = start + pageSize > all.length ? all.length : start + pageSize;
    return all.sublist(start, end);
  }

  @override
  Future<ImportCommitResult> commit(String importId, ImportCommitRequest request) async {
    if (commitError != null) throw commitError!;
    commits.add(request);
    return const ImportCommitResult(batchId: 'b1', imported: 3, accountsCreated: 1);
  }

  @override
  Future<ImportUndoResult> undo(String batchId) async {
    undone.add(batchId);
    return const ImportUndoResult(transactionsRemoved: 3, accountsRemoved: 1);
  }
}

class FakeAccountsService extends AccountsService {
  @override
  Future<List<Account>> getAccounts() async => [
        Account(
            id: 7,
            plaidAccountId: 'p7',
            name: 'Checking',
            type: 0,
            subtype: 'checking',
            currentBalance: 10),
      ];
}

class FakeCategoriesService extends CategoriesService {
  @override
  Future<CategoriesResponse> getAll({bool forceRefresh = false}) async => CategoriesResponse(
        systemCategories: const [],
        personalCategories: const [],
        familyCategories: const [],
        allCategories: [
          Category(
              id: 20, name: 'Groceries', color: '#fff', isIncome: false, isSystem: true, sortOrder: 0, scope: 'system', canEdit: false),
        ],
      );
}

ImportPreview samplePreview({
  int newCount = 3,
  int likely = 0,
  int possible = 0,
  bool needsDateOrder = false,
  List<ImportRowPreview> rows = const [],
  List<ImportAccountPreview>? accounts,
  List<ImportCategoryPreview> categories = const [],
  ImportBudgetPreview? budget,
}) =>
    ImportPreview(
      importId: 'imp1',
      detected: const ImportDetected(source: 'csv', headers: ['Date', 'Description', 'Amount']),
      accounts: accounts ??
          const [
            ImportAccountPreview(sourceKey: '', sourceName: 'bank', proposedAction: 'create', rowCount: 3),
          ],
      categories: categories,
      budget: budget,
      summary: ImportSummary(
        total: newCount + likely + possible,
        newCount: newCount,
        duplicateLikely: likely,
        possibleDuplicate: possible,
      ),
      rows: rows,
      needsDateOrderChoice: needsDateOrder,
    );

ImportRowPreview row(int id, String cls) => ImportRowPreview(
    rowId: id, date: '2026-01-0$id', amount: -4.5, payee: 'Coffee $id', rowClass: cls, reason: 'Looks like Coffee');

ImportController build(FakeImportService service, {List<ImportPickedFile>? picked}) => ImportController(
      service: service,
      accounts: FakeAccountsService(),
      categories: FakeCategoriesService(),
      picker: () async => picked ?? [ImportPickedFile(name: 'bank.csv', bytes: Uint8List.fromList([1]))],
    );

