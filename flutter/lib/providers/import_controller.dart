import 'package:flutter/foundation.dart' show ChangeNotifier;

import '../models/dashboard_models.dart';
import '../models/import_models.dart';
import '../services/accounts_service.dart';
import '../services/categories_service.dart';
import '../services/import_service.dart';
import '../utils/api_error.dart';

/// Opens the platform file picker and returns the chosen files' bytes. Empty when cancelled.
typedef ImportFilePicker = Future<List<ImportPickedFile>> Function();

enum ImportStep { choose, reading, review, importing, done }

/// A failure the page can show: the server's own message when it sent one.
class ImportFailure {
  final String? serverMessage;

  /// The chosen file is over the size limit (checked before anything is uploaded).
  final bool tooLarge;
  const ImportFailure(this.serverMessage, {this.tooLarge = false});
}

/// The state machine behind the import screen. All decisions the user makes (target account,
/// date order, column mapping, duplicates, categories) live here so they are testable without
/// pumping widgets.
class ImportController extends ChangeNotifier {
  ImportController({
    ImportService? service,
    AccountsService? accounts,
    CategoriesService? categories,
    required ImportFilePicker picker,
  })  : _service = service ?? importService,
        _accounts = accounts ?? accountsService,
        _categories = categories ?? categoriesService,
        _picker = picker;

  final ImportService _service;
  final AccountsService _accounts;
  final CategoriesService _categories;
  final ImportFilePicker _picker;

  ImportStep _step = ImportStep.choose;
  ImportFailure? _failure;
  List<ImportPickedFile> _files = const [];
  ImportPreview? _preview;
  ImportMapping _mapping = const ImportMapping();
  int? _targetAccountId;
  bool _includeLikely = false;
  bool _createBudget = false;
  bool _undone = false;
  ImportCommitResult? _result;
  ImportUndoResult? _undoResult;
  List<Account> _userAccounts = const [];
  List<Category> _knownCategories = const [];
  final Map<String, ImportAccountChoice> _accountChoices = {};
  final Map<String, ImportCategoryChoice> _categoryChoices = {};
  final Map<int, String> _overrides = {};
  final Map<int, String> _rowClassById = {};
  final Map<String, List<ImportRowPreview>> _rowsByClass = {};
  bool _busy = false;

  ImportStep get step => _step;
  bool get busy => _busy;
  ImportFailure? get failure => _failure;
  List<ImportPickedFile> get files => _files;
  ImportPreview? get preview => _preview;
  ImportMapping get mapping => _mapping;
  int? get targetAccountId => _targetAccountId;
  bool get includeLikely => _includeLikely;
  bool get createBudget => _createBudget;
  bool get undone => _undone;
  ImportCommitResult? get result => _result;
  ImportUndoResult? get undoResult => _undoResult;
  List<Account> get userAccounts => _userAccounts;
  List<Category> get knownCategories => _knownCategories;
  Map<int, String> get overrides => Map.unmodifiable(_overrides);

  ImportAccountChoice? accountChoice(String sourceKey) => _accountChoices[sourceKey];
  ImportCategoryChoice? categoryChoice(ImportCategoryPreview category) => _categoryChoices[category.key];

  /// Loads what the pickers need (the user's accounts and categories). Failures are ignored:
  /// the page still works, the user just cannot pick an existing one.
  Future<void> loadChoices() async {
    try {
      _userAccounts = (await _accounts.getAccounts()).where((a) => a.isActive).toList();
    } catch (_) {}
    try {
      _knownCategories = (await _categories.getAll()).allCategories;
    } catch (_) {}
    notifyListeners();
  }

  // ---- Step 1: choose and read ----

  Future<void> pickFiles() async {
    final List<ImportPickedFile> picked;
    try {
      picked = await _picker();
    } catch (e) {
      _fail(e);
      return;
    }
    if (picked.isEmpty) return;
    await readFiles(picked);
  }

  Future<void> readFiles(List<ImportPickedFile> files) async {
    _files = files;
    _mapping = const ImportMapping();
    _targetAccountId = null;
    _includeLikely = false;
    _createBudget = false;
    _overrides.clear();
    _rowsByClass.clear();
    _step = ImportStep.reading;
    _failure = null;
    notifyListeners();
    try {
      final preview = await _service.preview(files);
      _adopt(preview, resetChoices: true);
      _step = ImportStep.review;
    } catch (e) {
      _failure = ImportFailure(_serverMessage(e));
      _step = ImportStep.choose;
    }
    notifyListeners();
  }

  void _adopt(ImportPreview preview, {required bool resetChoices}) {
    _preview = preview;
    _rowClassById
      ..clear()
      ..addEntries(preview.rows.map((r) => MapEntry(r.rowId, r.rowClass)));
    _rowsByClass.clear();
    if (resetChoices) {
      _accountChoices.clear();
      _categoryChoices.clear();
      _createBudget = preview.budget != null;
    }
    for (final account in preview.accounts) {
      _accountChoices.putIfAbsent(
        account.sourceKey,
        () => ImportAccountChoice(
          sourceKey: account.sourceKey,
          action: account.proposedAction,
          accountId: account.matchedAccountId,
          name: account.sourceName,
          type: account.proposedType,
        ),
      );
    }
    // Keep choices the user already made; give new categories their proposed default.
    for (final category in preview.categories) {
      _categoryChoices.putIfAbsent(
        category.key,
        () => category.proposedCategoryId != null
            ? ImportCategoryChoice(
                sourceGroup: category.sourceGroup,
                sourceName: category.sourceName,
                action: 'map',
                categoryId: category.proposedCategoryId)
            : ImportCategoryChoice(
                sourceGroup: category.sourceGroup, sourceName: category.sourceName, action: 'create'),
      );
    }
  }

  // ---- Step 2: adjust ----

  Future<void> _reparse() async {
    final preview = _preview;
    if (preview == null) return;
    _busy = true;
    _failure = null;
    notifyListeners();
    try {
      final updated = await _service.reparse(
        preview.importId,
        accountId: _targetAccountId,
        mapping: _mapping,
      );
      _adopt(updated, resetChoices: false);
    } catch (e) {
      _failure = ImportFailure(_serverMessage(e));
    }
    _busy = false;
    notifyListeners();
  }

  Future<void> setDateOrder(ImportDateOrder order) {
    _mapping = _withEffective(dateOrder: order);
    return _reparse();
  }

  Future<void> setNumberFormat(ImportNumberFormat format) {
    _mapping = _withEffective(numberFormat: format);
    return _reparse();
  }

  Future<void> setFlipSign(bool flip) {
    _mapping = _withEffective(flipSign: flip);
    return _reparse();
  }

  /// Which column holds [field]. Pass `null` to clear it. Fields: date, amount, debit, credit,
  /// description, memo, category.
  Future<void> setColumn(String field, int? column) {
    final base = _withEffective();
    _mapping = switch (field) {
      'date' => base.copyWith(dateColumn: column),
      'amount' => base.copyWith(amountColumn: column, debitColumn: null, creditColumn: null),
      'debit' => base.copyWith(debitColumn: column, amountColumn: null),
      'credit' => base.copyWith(creditColumn: column, amountColumn: null),
      'description' => base.copyWith(descriptionColumn: column),
      'memo' => base.copyWith(memoColumn: column),
      'category' => base.copyWith(categoryColumn: column),
      _ => base,
    };
    return _reparse();
  }

  /// The mapping the server is using now, with the user's own choices on top.
  ImportMapping _withEffective({
    ImportDateOrder? dateOrder,
    ImportNumberFormat? numberFormat,
    bool? flipSign,
  }) {
    final shown = _preview?.detected.proposedMapping ?? const ImportMapping();
    return shown.copyWith(
      delimiter: _mapping.delimiter,
      dateOrder: dateOrder ?? _mapping.dateOrder,
      numberFormat: numberFormat ?? _mapping.numberFormat,
      flipSign: flipSign ?? _mapping.flipSign,
    );
  }

  /// Points a single-account file at an existing account (or `null` for a new one).
  Future<void> setTargetAccount(int? accountId) async {
    // The single-account choice follows the target: drop it so the reparse rebuilds it from the
    // new proposal, and put it back if the reparse fails (the old target stays in force).
    final previousTarget = _targetAccountId;
    final previousChoice = _accountChoices.remove('__target__');
    _targetAccountId = accountId;
    await _reparse();
    if (_failure != null) {
      _targetAccountId = previousTarget;
      if (previousChoice != null) _accountChoices['__target__'] = previousChoice;
      notifyListeners();
    }
  }

  void setAccountChoice(ImportAccountChoice choice) {
    _accountChoices[choice.sourceKey] = choice;
    notifyListeners();
  }

  void setCategoryChoice(ImportCategoryChoice choice) {
    _categoryChoices['${choice.sourceGroup}|${choice.sourceName}'] = choice;
    notifyListeners();
  }

  void setIncludeLikely(bool value) {
    _includeLikely = value;
    notifyListeners();
  }

  void setCreateBudget(bool value) {
    _createBudget = value;
    notifyListeners();
  }

  /// `skip`, `import`, or `null` to go back to the default for the row's class.
  void setRowOverride(int rowId, String? choice) {
    if (choice == null) {
      _overrides.remove(rowId);
    } else {
      _overrides[rowId] = choice;
    }
    notifyListeners();
  }

  /// The rows of one class, loaded on demand (the preview carries only the first few).
  List<ImportRowPreview> rowsOfClass(String rowClass) =>
      _rowsByClass[rowClass] ??
      (_preview?.rows.where((r) => r.rowClass == rowClass).toList() ?? const []);

  Future<void> loadRows(String rowClass) async {
    final preview = _preview;
    if (preview == null || _rowsByClass.containsKey(rowClass)) return;
    try {
      // Per-row controls cover the first few pages; the rest follow the class default. Walking
      // every page would not scale to files near the 100,000-row limit.
      const pageSize = 100;
      const maxPages = 5;
      final rows = <ImportRowPreview>[];
      for (var page = 0; page < maxPages; page++) {
        final chunk = await _service.rows(preview.importId, rowClass: rowClass, page: page, pageSize: pageSize);
        rows.addAll(chunk);
        if (chunk.length < pageSize) break;
      }
      _rowsByClass[rowClass] = rows;
      for (final r in rows) {
        _rowClassById[r.rowId] = r.rowClass;
      }
      notifyListeners();
    } catch (_) {
      // The summary counts still hold; the list just stays short.
    }
  }

  /// How many transactions a commit would write with the current choices.
  int get willImportCount {
    final s = _preview?.summary;
    if (s == null) return 0;
    final skipped = _accountChoices.values.where((c) => c.action == 'skip').map((c) => c.sourceKey).toSet();
    var count = s.newCount + s.possibleDuplicate + (_includeLikely ? s.duplicateLikely : 0);
    // Rows on accounts the user chose to skip are not written: remove only the classes that
    // would have been included, not the account's whole row count.
    final skippedRows = <int>{};
    if (skipped.isNotEmpty) {
      for (final a in _preview!.accounts.where((a) => skipped.contains(a.sourceKey))) {
        count -= a.newCount + a.possibleDuplicateCount + (_includeLikely ? a.duplicateLikelyCount : 0);
      }
      for (final rows in [_preview!.rows, ..._rowsByClass.values]) {
        for (final r in rows) {
          if (skipped.contains(r.accountKey)) skippedRows.add(r.rowId);
        }
      }
    }
    for (final entry in _overrides.entries) {
      if (skippedRows.contains(entry.key)) continue;
      final cls = _rowClassById[entry.key];
      if (cls == null || cls == ImportRowClass.invalid || cls == ImportRowClass.duplicateExact) continue;
      final defaultIncluded = cls == ImportRowClass.newRow ||
          cls == ImportRowClass.possibleDuplicate ||
          (cls == ImportRowClass.duplicateLikely && _includeLikely);
      if (entry.value == 'skip' && defaultIncluded) count--;
      if (entry.value == 'import' && !defaultIncluded) count++;
    }
    return count < 0 ? 0 : count;
  }

  bool get canCommit =>
      _preview != null &&
      !_busy &&
      !(_preview!.needsDateOrderChoice) &&
      willImportCount > 0;

  // ---- Step 3: commit and undo ----

  Future<void> commit() async {
    final preview = _preview;
    if (preview == null || !canCommit) return;
    _step = ImportStep.importing;
    _failure = null;
    notifyListeners();
    try {
      final request = ImportCommitRequest(
        accountMappings: _accountChoices.values.toList(),
        categoryMappings: _categoryChoices.values.toList(),
        includeClasses: [
          ImportRowClass.newRow,
          ImportRowClass.possibleDuplicate,
          if (_includeLikely) ImportRowClass.duplicateLikely,
        ],
        overrides: Map.of(_overrides),
        createBudget: _createBudget && preview.budget != null,
      );
      _result = await _service.commit(preview.importId, request);
      _undone = false;
      _undoResult = null;
      _step = ImportStep.done;
    } catch (e) {
      _failure = ImportFailure(_serverMessage(e));
      _step = ImportStep.review;
    }
    notifyListeners();
  }

  Future<void> undo() async {
    final result = _result;
    if (result == null || _undone || _busy) return;
    _busy = true;
    _failure = null;
    notifyListeners();
    try {
      _undoResult = await _service.undo(result.batchId);
      _undone = true;
    } catch (e) {
      _failure = ImportFailure(_serverMessage(e));
    }
    _busy = false;
    notifyListeners();
  }

  void reset() {
    _step = ImportStep.choose;
    _failure = null;
    _files = const [];
    _preview = null;
    _result = null;
    _undoResult = null;
    _undone = false;
    _mapping = const ImportMapping();
    _targetAccountId = null;
    _overrides.clear();
    notifyListeners();
  }

  void dismissFailure() {
    _failure = null;
    notifyListeners();
  }

  void _fail(Object e) {
    _failure = e is ImportFileTooLarge
        ? const ImportFailure(null, tooLarge: true)
        : ImportFailure(_serverMessage(e));
    notifyListeners();
  }

  String? _serverMessage(Object e) {
    final message = apiErrorMessage(e, fallback: '');
    return message.isEmpty ? null : message;
  }
}
