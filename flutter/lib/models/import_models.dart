import 'dart:typed_data';

/// A file the user picked, held in memory so it uploads the same way on web and mobile.
class ImportPickedFile {
  final String name;
  final Uint8List bytes;

  const ImportPickedFile({required this.name, required this.bytes});

  String get extension {
    final dot = name.lastIndexOf('.');
    return dot < 0 ? '' : name.substring(dot + 1).toLowerCase();
  }
}

/// Thrown by the file picker when a chosen file is over the size limit.
class ImportFileTooLarge implements Exception {
  final String fileName;
  const ImportFileTooLarge(this.fileName);
}

// The backend writes camelCase; accept PascalCase too (project rule for all models).
dynamic _get(Map<String, dynamic> json, String key) {
  if (json.containsKey(key)) return json[key];
  final pascal = key[0].toUpperCase() + key.substring(1);
  return json[pascal];
}

String _str(Map<String, dynamic> json, String key, [String fallback = '']) =>
    (_get(json, key) as String?) ?? fallback;

int _int(Map<String, dynamic> json, String key, [int fallback = 0]) =>
    (_get(json, key) as num?)?.toInt() ?? fallback;

double _dbl(Map<String, dynamic> json, String key) =>
    (_get(json, key) as num?)?.toDouble() ?? 0;

bool _bool(Map<String, dynamic> json, String key) => (_get(json, key) as bool?) ?? false;

List<T> _list<T>(Map<String, dynamic> json, String key, T Function(Map<String, dynamic>) map) =>
    ((_get(json, key) as List<dynamic>?) ?? const [])
        .map((e) => map(e as Map<String, dynamic>))
        .toList();

/// How day, month and year are ordered in a date column.
enum ImportDateOrder {
  auto('Auto'),
  monthFirst('MonthFirst'),
  dayFirst('DayFirst'),
  yearFirst('YearFirst');

  final String apiValue;
  const ImportDateOrder(this.apiValue);

  static ImportDateOrder fromString(String? value) => ImportDateOrder.values.firstWhere(
        (e) => e.apiValue.toLowerCase() == (value ?? '').toLowerCase(),
        orElse: () => ImportDateOrder.auto,
      );

  String get displayName => switch (this) {
        ImportDateOrder.auto => 'Auto',
        ImportDateOrder.monthFirst => 'Month first',
        ImportDateOrder.dayFirst => 'Day first',
        ImportDateOrder.yearFirst => 'Year first',
      };
}

/// How numbers write thousands and decimals.
enum ImportNumberFormat {
  auto('Auto'),
  us('Us'),
  european('European');

  final String apiValue;
  const ImportNumberFormat(this.apiValue);

  static ImportNumberFormat fromString(String? value) => ImportNumberFormat.values.firstWhere(
        (e) => e.apiValue.toLowerCase() == (value ?? '').toLowerCase(),
        orElse: () => ImportNumberFormat.auto,
      );

  String get displayName => switch (this) {
        ImportNumberFormat.auto => 'Auto',
        ImportNumberFormat.us => '1,234.56',
        ImportNumberFormat.european => '1.234,56',
      };
}

/// What the preview decided about a row.
class ImportRowClass {
  static const newRow = 'new';
  static const duplicateExact = 'duplicateExact';
  static const duplicateLikely = 'duplicateLikely';
  static const possibleDuplicate = 'possibleDuplicate';
  static const invalid = 'invalid';
}

/// How to read a file. Column numbers are positions in the header row.
class ImportMapping {
  final String? delimiter;
  final ImportDateOrder dateOrder;
  final ImportNumberFormat numberFormat;
  final bool flipSign;
  final int? dateColumn;
  final int? amountColumn;
  final int? debitColumn;
  final int? creditColumn;
  final int? descriptionColumn;
  final int? memoColumn;
  final int? categoryColumn;
  final int? accountColumn;
  final int? idColumn;

  const ImportMapping({
    this.delimiter,
    this.dateOrder = ImportDateOrder.auto,
    this.numberFormat = ImportNumberFormat.auto,
    this.flipSign = false,
    this.dateColumn,
    this.amountColumn,
    this.debitColumn,
    this.creditColumn,
    this.descriptionColumn,
    this.memoColumn,
    this.categoryColumn,
    this.accountColumn,
    this.idColumn,
  });

  factory ImportMapping.fromJson(Map<String, dynamic> json) => ImportMapping(
        delimiter: _get(json, 'delimiter') as String?,
        dateOrder: ImportDateOrder.fromString(_get(json, 'dateOrder') as String?),
        numberFormat: ImportNumberFormat.fromString(_get(json, 'numberFormat') as String?),
        flipSign: _bool(json, 'flipSign'),
        dateColumn: (_get(json, 'dateColumn') as num?)?.toInt(),
        amountColumn: (_get(json, 'amountColumn') as num?)?.toInt(),
        debitColumn: (_get(json, 'debitColumn') as num?)?.toInt(),
        creditColumn: (_get(json, 'creditColumn') as num?)?.toInt(),
        descriptionColumn: (_get(json, 'descriptionColumn') as num?)?.toInt(),
        memoColumn: (_get(json, 'memoColumn') as num?)?.toInt(),
        categoryColumn: (_get(json, 'categoryColumn') as num?)?.toInt(),
        accountColumn: (_get(json, 'accountColumn') as num?)?.toInt(),
        idColumn: (_get(json, 'idColumn') as num?)?.toInt(),
      );

  Map<String, dynamic> toJson() => {
        if (delimiter != null) 'delimiter': delimiter,
        'dateOrder': dateOrder.apiValue,
        'numberFormat': numberFormat.apiValue,
        'flipSign': flipSign,
        if (dateColumn != null) 'dateColumn': dateColumn,
        if (amountColumn != null) 'amountColumn': amountColumn,
        if (debitColumn != null) 'debitColumn': debitColumn,
        if (creditColumn != null) 'creditColumn': creditColumn,
        if (descriptionColumn != null) 'descriptionColumn': descriptionColumn,
        if (memoColumn != null) 'memoColumn': memoColumn,
        if (categoryColumn != null) 'categoryColumn': categoryColumn,
        if (accountColumn != null) 'accountColumn': accountColumn,
        if (idColumn != null) 'idColumn': idColumn,
      };

  /// Replaces one column. Passing `null` clears it (the sentinel keeps "leave as is" distinct).
  ImportMapping copyWith({
    String? delimiter,
    ImportDateOrder? dateOrder,
    ImportNumberFormat? numberFormat,
    bool? flipSign,
    Object? dateColumn = _keep,
    Object? amountColumn = _keep,
    Object? debitColumn = _keep,
    Object? creditColumn = _keep,
    Object? descriptionColumn = _keep,
    Object? memoColumn = _keep,
    Object? categoryColumn = _keep,
    Object? accountColumn = _keep,
    Object? idColumn = _keep,
  }) {
    return ImportMapping(
      delimiter: delimiter ?? this.delimiter,
      dateOrder: dateOrder ?? this.dateOrder,
      numberFormat: numberFormat ?? this.numberFormat,
      flipSign: flipSign ?? this.flipSign,
      dateColumn: identical(dateColumn, _keep) ? this.dateColumn : dateColumn as int?,
      amountColumn: identical(amountColumn, _keep) ? this.amountColumn : amountColumn as int?,
      debitColumn: identical(debitColumn, _keep) ? this.debitColumn : debitColumn as int?,
      creditColumn: identical(creditColumn, _keep) ? this.creditColumn : creditColumn as int?,
      descriptionColumn:
          identical(descriptionColumn, _keep) ? this.descriptionColumn : descriptionColumn as int?,
      memoColumn: identical(memoColumn, _keep) ? this.memoColumn : memoColumn as int?,
      categoryColumn:
          identical(categoryColumn, _keep) ? this.categoryColumn : categoryColumn as int?,
      accountColumn: identical(accountColumn, _keep) ? this.accountColumn : accountColumn as int?,
      idColumn: identical(idColumn, _keep) ? this.idColumn : idColumn as int?,
    );
  }
}

const Object _keep = Object();

class ImportDetected {
  final String source;
  final String? delimiter;
  final String? encoding;
  final String? dateOrder;
  final bool dateOrderAmbiguous;
  final String? numberFormat;
  final List<String> headers;
  final List<List<String>> sampleRows;
  final ImportMapping proposedMapping;

  const ImportDetected({
    this.source = '',
    this.delimiter,
    this.encoding,
    this.dateOrder,
    this.dateOrderAmbiguous = false,
    this.numberFormat,
    this.headers = const [],
    this.sampleRows = const [],
    this.proposedMapping = const ImportMapping(),
  });

  factory ImportDetected.fromJson(Map<String, dynamic> json) => ImportDetected(
        source: _str(json, 'source'),
        delimiter: _get(json, 'delimiter') as String?,
        encoding: _get(json, 'encoding') as String?,
        dateOrder: _get(json, 'dateOrder') as String?,
        dateOrderAmbiguous: _bool(json, 'dateOrderAmbiguous'),
        numberFormat: _get(json, 'numberFormat') as String?,
        headers: ((_get(json, 'headers') as List<dynamic>?) ?? const []).map((e) => '$e').toList(),
        sampleRows: ((_get(json, 'sampleRows') as List<dynamic>?) ?? const [])
            .map((r) => (r as List<dynamic>).map((c) => '$c').toList())
            .toList(),
        proposedMapping: _get(json, 'proposedMapping') is Map<String, dynamic>
            ? ImportMapping.fromJson(_get(json, 'proposedMapping') as Map<String, dynamic>)
            : const ImportMapping(),
      );

  /// True for a plain bank CSV, where the user may need to say which column is which.
  bool get isCsv => source == 'csv';
}

class ImportAccountPreview {
  final String sourceKey;
  final String sourceName;
  final String proposedAction; // create | match
  final int? matchedAccountId;
  final String proposedType;
  final bool closed;
  final int rowCount;
  final int newCount;
  final int duplicateLikelyCount;
  final int possibleDuplicateCount;

  const ImportAccountPreview({
    required this.sourceKey,
    required this.sourceName,
    required this.proposedAction,
    this.matchedAccountId,
    this.proposedType = 'Checking',
    this.closed = false,
    this.rowCount = 0,
    this.newCount = 0,
    this.duplicateLikelyCount = 0,
    this.possibleDuplicateCount = 0,
  });

  factory ImportAccountPreview.fromJson(Map<String, dynamic> json) => ImportAccountPreview(
        sourceKey: _str(json, 'sourceKey'),
        sourceName: _str(json, 'sourceName'),
        proposedAction: _str(json, 'proposedAction', 'create'),
        matchedAccountId: (_get(json, 'matchedAccountId') as num?)?.toInt(),
        proposedType: _str(json, 'proposedType', 'Checking'),
        closed: _bool(json, 'closed'),
        rowCount: _int(json, 'rowCount'),
        newCount: _int(json, 'newCount'),
        duplicateLikelyCount: _int(json, 'duplicateLikelyCount'),
        possibleDuplicateCount: _int(json, 'possibleDuplicateCount'),
      );
}

class ImportCategoryPreview {
  final String sourceGroup;
  final String sourceName;
  final bool isIncome;
  final int? proposedCategoryId;
  final String? proposedCategoryName;
  final String proposedNewName;
  final int rowCount;

  const ImportCategoryPreview({
    required this.sourceGroup,
    required this.sourceName,
    this.isIncome = false,
    this.proposedCategoryId,
    this.proposedCategoryName,
    this.proposedNewName = '',
    this.rowCount = 0,
  });

  factory ImportCategoryPreview.fromJson(Map<String, dynamic> json) => ImportCategoryPreview(
        sourceGroup: _str(json, 'sourceGroup'),
        sourceName: _str(json, 'sourceName'),
        isIncome: _bool(json, 'isIncome'),
        proposedCategoryId: (_get(json, 'proposedCategoryId') as num?)?.toInt(),
        proposedCategoryName: _get(json, 'proposedCategoryName') as String?,
        proposedNewName: _str(json, 'proposedNewName'),
        rowCount: _int(json, 'rowCount'),
      );

  /// Stable key for choices made in the UI.
  String get key => '$sourceGroup|$sourceName';
}

class ImportBudgetPreview {
  final List<String> months;
  final String latestMonth;
  final int categoriesWithAmounts;
  final double latestMonthTotal;

  const ImportBudgetPreview({
    this.months = const [],
    this.latestMonth = '',
    this.categoriesWithAmounts = 0,
    this.latestMonthTotal = 0,
  });

  factory ImportBudgetPreview.fromJson(Map<String, dynamic> json) => ImportBudgetPreview(
        months: ((_get(json, 'months') as List<dynamic>?) ?? const []).map((e) => '$e').toList(),
        latestMonth: _str(json, 'latestMonth'),
        categoriesWithAmounts: _int(json, 'categoriesWithAmounts'),
        latestMonthTotal: _dbl(json, 'latestMonthTotal'),
      );
}

class ImportSummary {
  final int total;
  final int newCount;
  final int duplicateExact;
  final int duplicateLikely;
  final int possibleDuplicate;
  final int invalid;
  final String? firstDate;
  final String? lastDate;
  final double totalInflow;
  final double totalOutflow;

  const ImportSummary({
    this.total = 0,
    this.newCount = 0,
    this.duplicateExact = 0,
    this.duplicateLikely = 0,
    this.possibleDuplicate = 0,
    this.invalid = 0,
    this.firstDate,
    this.lastDate,
    this.totalInflow = 0,
    this.totalOutflow = 0,
  });

  factory ImportSummary.fromJson(Map<String, dynamic> json) => ImportSummary(
        total: _int(json, 'total'),
        newCount: _int(json, 'new'),
        duplicateExact: _int(json, 'duplicateExact'),
        duplicateLikely: _int(json, 'duplicateLikely'),
        possibleDuplicate: _int(json, 'possibleDuplicate'),
        invalid: _int(json, 'invalid'),
        firstDate: _get(json, 'firstDate') as String?,
        lastDate: _get(json, 'lastDate') as String?,
        totalInflow: _dbl(json, 'totalInflow'),
        totalOutflow: _dbl(json, 'totalOutflow'),
      );
}

class ImportRowPreview {
  final int rowId;
  final String? accountKey;
  final String? date;
  final double amount;
  final String payee;
  final String? memo;
  final String? category;
  final String rowClass;
  final String? reason;

  const ImportRowPreview({
    required this.rowId,
    this.accountKey,
    this.date,
    required this.amount,
    required this.payee,
    this.memo,
    this.category,
    this.rowClass = ImportRowClass.newRow,
    this.reason,
  });

  factory ImportRowPreview.fromJson(Map<String, dynamic> json) => ImportRowPreview(
        rowId: _int(json, 'rowId'),
        accountKey: _get(json, 'accountKey') as String?,
        date: _get(json, 'date') as String?,
        amount: _dbl(json, 'amount'),
        payee: _str(json, 'payee'),
        memo: _get(json, 'memo') as String?,
        category: _get(json, 'category') as String?,
        rowClass: _str(json, 'class', ImportRowClass.newRow),
        reason: _get(json, 'reason') as String?,
      );
}

class ImportPreview {
  final String importId;
  final DateTime? expiresAt;
  final ImportDetected detected;
  final List<ImportAccountPreview> accounts;
  final List<ImportCategoryPreview> categories;
  final ImportBudgetPreview? budget;
  final ImportSummary summary;
  final List<ImportRowPreview> rows;
  final List<String> warnings;
  final bool needsDateOrderChoice;

  const ImportPreview({
    required this.importId,
    this.expiresAt,
    this.detected = const ImportDetected(),
    this.accounts = const [],
    this.categories = const [],
    this.budget,
    this.summary = const ImportSummary(),
    this.rows = const [],
    this.warnings = const [],
    this.needsDateOrderChoice = false,
  });

  factory ImportPreview.fromJson(Map<String, dynamic> json) => ImportPreview(
        importId: _str(json, 'importId'),
        expiresAt: DateTime.tryParse(_str(json, 'expiresAt')),
        detected: _get(json, 'detected') is Map<String, dynamic>
            ? ImportDetected.fromJson(_get(json, 'detected') as Map<String, dynamic>)
            : const ImportDetected(),
        accounts: _list(json, 'accounts', ImportAccountPreview.fromJson),
        categories: _list(json, 'categories', ImportCategoryPreview.fromJson),
        budget: _get(json, 'budget') is Map<String, dynamic>
            ? ImportBudgetPreview.fromJson(_get(json, 'budget') as Map<String, dynamic>)
            : null,
        summary: _get(json, 'summary') is Map<String, dynamic>
            ? ImportSummary.fromJson(_get(json, 'summary') as Map<String, dynamic>)
            : const ImportSummary(),
        rows: _list(json, 'rows', ImportRowPreview.fromJson),
        warnings: ((_get(json, 'warnings') as List<dynamic>?) ?? const []).map((e) => '$e').toList(),
        needsDateOrderChoice: _bool(json, 'needsDateOrderChoice'),
      );

  /// More than one account in the file (Actual, YNAB, multi-statement OFX).
  bool get isMultiAccount => accounts.length > 1;
}

/// What the user decided for one account in the file.
class ImportAccountChoice {
  final String sourceKey;
  final String action; // create | match | skip
  final int? accountId;
  final String? name;
  final String? type;

  const ImportAccountChoice({
    required this.sourceKey,
    required this.action,
    this.accountId,
    this.name,
    this.type,
  });

  Map<String, dynamic> toJson() => {
        'sourceKey': sourceKey,
        'action': action,
        if (accountId != null) 'accountId': accountId,
        if (name != null) 'name': name,
        if (type != null) 'type': type,
      };
}

/// What the user decided for one category in the file.
class ImportCategoryChoice {
  final String sourceGroup;
  final String sourceName;
  final String action; // map | create | none
  final int? categoryId;

  const ImportCategoryChoice({
    required this.sourceGroup,
    required this.sourceName,
    required this.action,
    this.categoryId,
  });

  Map<String, dynamic> toJson() => {
        'sourceGroup': sourceGroup,
        'sourceName': sourceName,
        'action': action,
        if (categoryId != null) 'categoryId': categoryId,
      };
}

class ImportCommitRequest {
  final List<ImportAccountChoice> accountMappings;
  final List<ImportCategoryChoice> categoryMappings;
  final List<String> includeClasses;
  final Map<int, String> overrides;
  final bool createBudget;

  const ImportCommitRequest({
    this.accountMappings = const [],
    this.categoryMappings = const [],
    this.includeClasses = const [ImportRowClass.newRow, ImportRowClass.possibleDuplicate],
    this.overrides = const {},
    this.createBudget = false,
  });

  Map<String, dynamic> toJson() => {
        'accountMappings': accountMappings.map((e) => e.toJson()).toList(),
        'categoryMappings': categoryMappings.map((e) => e.toJson()).toList(),
        'includeClasses': includeClasses,
        'overrides': {for (final e in overrides.entries) '${e.key}': e.value},
        'createBudget': createBudget,
      };
}

class ImportCommitResult {
  final String batchId;
  final int imported;
  final int skippedDuplicates;
  final int skippedInvalid;
  final int skippedByChoice;
  final int accountsCreated;
  final int categoriesCreated;
  final bool budgetCreated;
  final List<String> warnings;
  final DateTime? undoableUntil;

  const ImportCommitResult({
    required this.batchId,
    this.imported = 0,
    this.skippedDuplicates = 0,
    this.skippedInvalid = 0,
    this.skippedByChoice = 0,
    this.accountsCreated = 0,
    this.categoriesCreated = 0,
    this.budgetCreated = false,
    this.warnings = const [],
    this.undoableUntil,
  });

  factory ImportCommitResult.fromJson(Map<String, dynamic> json) => ImportCommitResult(
        batchId: _str(json, 'batchId'),
        imported: _int(json, 'imported'),
        skippedDuplicates: _int(json, 'skippedDuplicates'),
        skippedInvalid: _int(json, 'skippedInvalid'),
        skippedByChoice: _int(json, 'skippedByChoice'),
        accountsCreated: _int(json, 'accountsCreated'),
        categoriesCreated: _int(json, 'categoriesCreated'),
        budgetCreated: _bool(json, 'budgetCreated'),
        warnings: ((_get(json, 'warnings') as List<dynamic>?) ?? const []).map((e) => '$e').toList(),
        undoableUntil: DateTime.tryParse(_str(json, 'undoableUntil')),
      );
}

class ImportUndoResult {
  final int transactionsRemoved;
  final int accountsRemoved;

  const ImportUndoResult({this.transactionsRemoved = 0, this.accountsRemoved = 0});

  factory ImportUndoResult.fromJson(Map<String, dynamic> json) => ImportUndoResult(
        transactionsRemoved: _int(json, 'transactionsRemoved'),
        accountsRemoved: _int(json, 'accountsRemoved'),
      );
}
