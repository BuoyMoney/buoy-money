import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../l10n/generated/app_localizations.dart';
import '../models/import_models.dart';
import '../providers/import_controller.dart';
import '../services/categories_service.dart';
import '../utils/formatters.dart';
import '../utils/import_file_picker.dart';
import '../widgets/app_toast.dart';

/// Settings > Import data. Choose a file, see exactly what would be added, then confirm.
///
/// Works for bank files (CSV, OFX, QFX) and for Actual Budget and YNAB exports. Nothing is written
/// until the user confirms on the review step, and a finished import can be undone for a day.
class ImportPage extends StatefulWidget {
  const ImportPage({super.key, this.controller});

  /// Injected in tests; the app builds its own with the real services and picker.
  final ImportController? controller;

  @override
  State<ImportPage> createState() => _ImportPageState();
}

class _ImportPageState extends State<ImportPage> {
  late final ImportController _controller;
  late final bool _ownsController;

  @override
  void initState() {
    super.initState();
    _ownsController = widget.controller == null;
    _controller = widget.controller ?? ImportController(picker: pickImportFiles);
    _controller.addListener(_onChanged);
    _controller.loadChoices();
  }

  @override
  void dispose() {
    _controller.removeListener(_onChanged);
    if (_ownsController) _controller.dispose();
    super.dispose();
  }

  void _onChanged() {
    if (mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = _controller;

    final Widget body = switch (c.step) {
      ImportStep.choose => _ChooseStep(controller: c),
      ImportStep.reading => _Busy(label: l10n.impReading),
      ImportStep.review => _ReviewStep(controller: c),
      ImportStep.importing => _Busy(label: l10n.impImporting),
      ImportStep.done => _DoneStep(controller: c),
    };

    return Scaffold(
      appBar: AppBar(title: Text(l10n.impTitle)),
      body: SafeArea(child: body),
    );
  }
}

String _failureText(AppLocalizations l10n, ImportFailure failure, String fallback) {
  if (failure.tooLarge) return l10n.impFileTooLarge;
  return failure.serverMessage ?? fallback;
}

class _Busy extends StatelessWidget {
  const _Busy({required this.label});
  final String label;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        const CircularProgressIndicator(),
        const SizedBox(height: 16),
        Text(label),
      ]),
    );
  }
}

/// A rounded tinted box used for notes and warnings.
class _Note extends StatelessWidget {
  const _Note({required this.child, this.error = false});
  final Widget child;
  final bool error;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: (error ? scheme.errorContainer : scheme.primaryContainer).withValues(alpha: 0.3),
        borderRadius: BorderRadius.circular(12),
      ),
      child: child,
    );
  }
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle(this.text);
  final String text;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: 24, bottom: 8),
      child: Text(text, style: Theme.of(context).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700)),
    );
  }
}

// ───────────────────────── Step 1: choose a file ─────────────────────────

class _ChooseStep extends StatelessWidget {
  const _ChooseStep({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final failure = controller.failure;

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text(l10n.impIntro, style: theme.textTheme.bodyMedium),
        const SizedBox(height: 16),
        if (failure != null) ...[
          _Note(error: true, child: Text(_failureText(l10n, failure, l10n.impReadFailed))),
          const SizedBox(height: 16),
        ],
        FilledButton.icon(
          key: const ValueKey('import-choose-file'),
          onPressed: controller.pickFiles,
          icon: const Icon(Icons.upload_file_rounded),
          label: Text(l10n.impChooseButton),
        ),
        const SizedBox(height: 8),
        Text(l10n.impPrivacyNote,
            style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurface.withValues(alpha: 0.6))),
        _SectionTitle(l10n.impWhereFrom),
        _SourceHelp(title: l10n.impSourceBank, body: l10n.impSourceBankHowTo, icon: Icons.account_balance_outlined),
        _SourceHelp(title: l10n.impSourceActual, body: l10n.impSourceActualHowTo, icon: Icons.savings_outlined),
        _SourceHelp(title: l10n.impSourceYnab, body: l10n.impSourceYnabHowTo, icon: Icons.account_balance_wallet_outlined),
      ],
    );
  }
}

class _SourceHelp extends StatelessWidget {
  const _SourceHelp({required this.title, required this.body, required this.icon});
  final String title;
  final String body;
  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: ExpansionTile(
        leading: Icon(icon),
        title: Text(title, style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w600)),
        childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        expandedAlignment: Alignment.centerLeft,
        children: [Text(body)],
      ),
    );
  }
}

// ───────────────────────── Step 2: review ─────────────────────────

class _ReviewStep extends StatelessWidget {
  const _ReviewStep({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final preview = c.preview!;
    final summary = preview.summary;
    final failure = c.failure;
    final inOutLine = '${l10n.impMoneyIn}: ${formatCurrency(summary.totalInflow)}   '
        '${l10n.impMoneyOut}: ${formatCurrency(summary.totalOutflow)}';

    return Column(
      children: [
        Expanded(
          child: ListView(
            padding: const EdgeInsets.all(16),
            children: [
              Text(c.files.map((f) => f.name).join(', '),
                  style: Theme.of(context).textTheme.bodySmall, maxLines: 2, overflow: TextOverflow.ellipsis),
              const SizedBox(height: 12),
              if (failure != null) ...[
                _Note(error: true, child: Text(_failureText(l10n, failure, l10n.impImportFailed))),
                const SizedBox(height: 12),
              ],
              for (final w in preview.warnings) ...[
                _Note(child: Text(w)),
                const SizedBox(height: 8),
              ],
              if (preview.needsDateOrderChoice) _DateOrderChoice(controller: c),
              _SectionTitle(l10n.impWhereTitle),
              _AccountsSection(controller: c),
              _SectionTitle(l10n.impSummaryTitle),
              _SummaryCards(summary: summary),
              if (summary.firstDate != null && summary.lastDate != null) ...[
                const SizedBox(height: 8),
                Text(l10n.impSummaryRange(summary.firstDate!, summary.lastDate!)),
              ],
              const SizedBox(height: 4),
              Text(inOutLine),
              if (summary.duplicateExact > 0) ...[
                const SizedBox(height: 8),
                Text(l10n.impAlreadyHereNote, style: Theme.of(context).textTheme.bodySmall),
              ],
              if (summary.duplicateLikely > 0) _LikelySection(controller: c),
              if (summary.possibleDuplicate > 0) _PossibleSection(controller: c),
              if (summary.invalid > 0) _ProblemsSection(controller: c),
              if (preview.categories.isNotEmpty) _CategoriesSection(controller: c),
              if (preview.budget != null) _BudgetSection(controller: c),
              if (preview.detected.isCsv) _ReadingSection(controller: c),
              const SizedBox(height: 24),
            ],
          ),
        ),
        _ConfirmBar(controller: c),
      ],
    );
  }
}

class _ConfirmBar extends StatelessWidget {
  const _ConfirmBar({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final count = controller.willImportCount;
    return Material(
      elevation: 8,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
        child: Row(children: [
          TextButton(
            key: const ValueKey('import-start-over'),
            onPressed: controller.reset,
            child: Text(l10n.impStartOver),
          ),
          const Spacer(),
          FilledButton(
            key: const ValueKey('import-confirm'),
            onPressed: controller.canCommit ? controller.commit : null,
            child: Text(count == 0 ? l10n.impNothingToImport : l10n.impImportButton(count)),
          ),
        ]),
      ),
    );
  }
}

class _DateOrderChoice extends StatelessWidget {
  const _DateOrderChoice({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Padding(
      padding: const EdgeInsets.only(top: 4),
      child: _Note(
        error: true,
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(l10n.impDateOrderTitle, style: const TextStyle(fontWeight: FontWeight.w700)),
          const SizedBox(height: 4),
          Text(l10n.impDateOrderBody),
          const SizedBox(height: 8),
          Wrap(spacing: 8, children: [
            OutlinedButton(
              key: const ValueKey('import-date-month-first'),
              onPressed: controller.busy ? null : () => controller.setDateOrder(ImportDateOrder.monthFirst),
              child: Text(l10n.impDateMonthFirst),
            ),
            OutlinedButton(
              key: const ValueKey('import-date-day-first'),
              onPressed: controller.busy ? null : () => controller.setDateOrder(ImportDateOrder.dayFirst),
              child: Text(l10n.impDateDayFirst),
            ),
          ]),
        ]),
      ),
    );
  }
}

class _AccountsSection extends StatelessWidget {
  const _AccountsSection({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final preview = c.preview!;

    // One account in the file: the user picks where it goes.
    if (!preview.isMultiAccount) {
      final source = preview.accounts.isEmpty ? null : preview.accounts.first;
      return DropdownButtonFormField<int?>(
        key: const ValueKey('import-target-account'),
        initialValue: c.targetAccountId ?? (source?.proposedAction == 'match' ? source?.matchedAccountId : null),
        decoration: InputDecoration(labelText: l10n.impImportInto, border: const OutlineInputBorder()),
        isExpanded: true,
        items: [
          DropdownMenuItem<int?>(
            value: null,
            child: Text(l10n.impNewAccountNamed(source?.sourceName ?? l10n.impNewAccount)),
          ),
          for (final a in c.userAccounts)
            DropdownMenuItem<int?>(value: a.id, child: Text(a.displayName, overflow: TextOverflow.ellipsis)),
        ],
        onChanged: c.busy ? null : c.setTargetAccount,
      );
    }

    // Several accounts (Actual, YNAB, a multi-statement OFX): one row each.
    return Column(
      children: [
        for (final account in preview.accounts) _AccountRow(controller: c, account: account),
      ],
    );
  }
}

class _AccountRow extends StatelessWidget {
  const _AccountRow({required this.controller, required this.account});
  final ImportController controller;
  final ImportAccountPreview account;

  static const _createValue = -1;
  static const _skipValue = -2;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final choice = controller.accountChoice(account.sourceKey);
    final current = switch (choice?.action) {
      'match' => choice?.accountId ?? _createValue,
      'skip' => _skipValue,
      _ => _createValue,
    };
    final known = controller.userAccounts.any((a) => a.id == current);
    final value = (current == _createValue || current == _skipValue || known) ? current : _createValue;

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: DropdownButtonFormField<int>(
        key: ValueKey('import-account-${account.sourceKey}'),
        initialValue: value,
        isExpanded: true,
        decoration: InputDecoration(
          labelText: '${account.sourceName} (${l10n.impRowsCount(account.rowCount)})',
          border: const OutlineInputBorder(),
        ),
        items: [
          DropdownMenuItem(value: _createValue, child: Text(l10n.impAccountCreate)),
          for (final a in controller.userAccounts)
            DropdownMenuItem(value: a.id, child: Text(l10n.impAccountAddTo(a.displayName), overflow: TextOverflow.ellipsis)),
          DropdownMenuItem(value: _skipValue, child: Text(l10n.impAccountSkip)),
        ],
        onChanged: (v) {
          if (v == null) return;
          controller.setAccountChoice(ImportAccountChoice(
            sourceKey: account.sourceKey,
            action: v == _skipValue ? 'skip' : v == _createValue ? 'create' : 'match',
            accountId: v >= 0 ? v : null,
            name: account.sourceName,
            type: account.proposedType,
          ));
        },
      ),
    );
  }
}

class _SummaryCards extends StatelessWidget {
  const _SummaryCards({required this.summary});
  final ImportSummary summary;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Wrap(spacing: 8, runSpacing: 8, children: [
      _Count(label: l10n.impSummaryNew, value: summary.newCount, key: const ValueKey('import-count-new')),
      if (summary.duplicateExact > 0)
        _Count(label: l10n.impSummaryAlready, value: summary.duplicateExact, key: const ValueKey('import-count-exact')),
      if (summary.duplicateLikely > 0)
        _Count(label: l10n.impSummaryLikely, value: summary.duplicateLikely, key: const ValueKey('import-count-likely')),
      if (summary.possibleDuplicate > 0)
        _Count(label: l10n.impSummaryPossible, value: summary.possibleDuplicate, key: const ValueKey('import-count-possible')),
      if (summary.invalid > 0)
        _Count(label: l10n.impSummaryProblems, value: summary.invalid, key: const ValueKey('import-count-invalid')),
    ]);
  }
}

class _Count extends StatelessWidget {
  const _Count({super.key, required this.label, required this.value});
  final String label;
  final int value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      width: 150,
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: theme.colorScheme.outlineVariant.withValues(alpha: 0.6)),
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text(value.toString(), style: theme.textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        Text(label, style: theme.textTheme.bodySmall),
      ]),
    );
  }
}

class _RowTile extends StatelessWidget {
  const _RowTile({required this.row, required this.included, required this.onChanged, required this.switchLabel});
  final ImportRowPreview row;
  final bool included;
  final ValueChanged<bool> onChanged;
  final String switchLabel;

  @override
  Widget build(BuildContext context) {
    final title = '${row.date ?? ''}  ${row.payee}';
    final subtitle = '${formatCurrency(row.amount)}${row.reason == null ? '' : '  ·  ${row.reason}'}';
    return SwitchListTile(
      key: ValueKey('import-row-${row.rowId}'),
      contentPadding: EdgeInsets.zero,
      dense: true,
      value: included,
      onChanged: onChanged,
      title: Text(title, maxLines: 1, overflow: TextOverflow.ellipsis),
      subtitle: Text(subtitle, maxLines: 2, overflow: TextOverflow.ellipsis),
      secondary: Text(switchLabel, style: Theme.of(context).textTheme.labelSmall),
    );
  }
}

class _LikelySection extends StatefulWidget {
  const _LikelySection({required this.controller});
  final ImportController controller;

  @override
  State<_LikelySection> createState() => _LikelySectionState();
}

class _LikelySectionState extends State<_LikelySection> {
  @override
  void initState() {
    super.initState();
    widget.controller.loadRows(ImportRowClass.duplicateLikely);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = widget.controller;
    final rows = c.rowsOfClass(ImportRowClass.duplicateLikely);
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      _SectionTitle(l10n.impLikelyTitle),
      Text(l10n.impLikelyBody),
      SwitchListTile(
        key: const ValueKey('import-include-likely'),
        contentPadding: EdgeInsets.zero,
        value: c.includeLikely,
        onChanged: c.setIncludeLikely,
        title: Text(l10n.impImportLikely),
      ),
      for (final row in rows)
        _RowTile(
          row: row,
          switchLabel: l10n.impRowImport,
          included: c.overrides[row.rowId] == 'import' ||
              (c.includeLikely && c.overrides[row.rowId] != 'skip'),
          onChanged: (v) => c.setRowOverride(row.rowId, v ? 'import' : 'skip'),
        ),
    ]);
  }
}

class _PossibleSection extends StatefulWidget {
  const _PossibleSection({required this.controller});
  final ImportController controller;

  @override
  State<_PossibleSection> createState() => _PossibleSectionState();
}

class _PossibleSectionState extends State<_PossibleSection> {
  @override
  void initState() {
    super.initState();
    widget.controller.loadRows(ImportRowClass.possibleDuplicate);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = widget.controller;
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      _SectionTitle(l10n.impPossibleTitle),
      Text(l10n.impPossibleBody),
      for (final row in c.rowsOfClass(ImportRowClass.possibleDuplicate))
        _RowTile(
          row: row,
          switchLabel: l10n.impRowImport,
          included: c.overrides[row.rowId] != 'skip',
          onChanged: (v) => c.setRowOverride(row.rowId, v ? null : 'skip'),
        ),
    ]);
  }
}

class _ProblemsSection extends StatefulWidget {
  const _ProblemsSection({required this.controller});
  final ImportController controller;

  @override
  State<_ProblemsSection> createState() => _ProblemsSectionState();
}

class _ProblemsSectionState extends State<_ProblemsSection> {
  @override
  void initState() {
    super.initState();
    widget.controller.loadRows(ImportRowClass.invalid);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final rows = widget.controller.rowsOfClass(ImportRowClass.invalid).take(20);
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      _SectionTitle(l10n.impProblemsTitle),
      Text(l10n.impProblemsBody),
      const SizedBox(height: 4),
      for (final row in rows)
        ListTile(
          contentPadding: EdgeInsets.zero,
          dense: true,
          title: Text(row.payee.isEmpty ? '-' : row.payee, maxLines: 1, overflow: TextOverflow.ellipsis),
          subtitle: Text(row.reason ?? ''),
        ),
    ]);
  }
}

class _CategoriesSection extends StatelessWidget {
  const _CategoriesSection({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final preview = controller.preview!;
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      _SectionTitle(l10n.impCategoriesTitle),
      Text(l10n.impCategoriesBody),
      const SizedBox(height: 12),
      for (final category in preview.categories)
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: _CategoryRow(controller: controller, category: category),
        ),
    ]);
  }
}

class _CategoryRow extends StatelessWidget {
  const _CategoryRow({required this.controller, required this.category});
  final ImportController controller;
  final ImportCategoryPreview category;

  static const _createValue = -1;
  static const _noneValue = -2;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final choice = controller.categoryChoice(category);
    final known = <Category>[...controller.knownCategories];
    final proposedId = category.proposedCategoryId;
    // The proposal may not be in the list if the categories failed to load; keep it selectable.
    if (proposedId != null && !known.any((k) => k.id == proposedId)) {
      known.add(Category(
        id: proposedId,
        name: category.proposedCategoryName ?? category.sourceName,
        color: '#6B7280',
        isIncome: category.isIncome,
        isSystem: true,
        sortOrder: 0,
        scope: 'system',
        canEdit: false,
      ));
    }

    var value = switch (choice?.action) {
      'none' => _noneValue,
      'map' => choice?.categoryId ?? _noneValue,
      _ => _createValue,
    };
    if (value >= 0 && !known.any((k) => k.id == value)) value = _createValue;

    final label = category.sourceGroup.isEmpty
        ? category.sourceName
        : '${category.sourceGroup}: ${category.sourceName}';
    return DropdownButtonFormField<int>(
      key: ValueKey('import-category-${category.key}'),
      initialValue: value,
      isExpanded: true,
      decoration: InputDecoration(labelText: label, border: const OutlineInputBorder()),
      items: [
        DropdownMenuItem(value: _createValue, child: Text(l10n.impCategoryCreate(category.proposedNewName))),
        DropdownMenuItem(value: _noneValue, child: Text(l10n.impCategoryNone)),
        for (final k in known) DropdownMenuItem(value: k.id, child: Text(k.name, overflow: TextOverflow.ellipsis)),
      ],
      onChanged: (v) {
        if (v == null) return;
        controller.setCategoryChoice(ImportCategoryChoice(
          sourceGroup: category.sourceGroup,
          sourceName: category.sourceName,
          action: v == _createValue ? 'create' : v == _noneValue ? 'none' : 'map',
          categoryId: v >= 0 ? v : null,
        ));
      },
    );
  }
}

class _BudgetSection extends StatelessWidget {
  const _BudgetSection({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final budget = controller.preview!.budget!;
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      _SectionTitle(l10n.impBudgetTitle),
      CheckboxListTile(
        key: const ValueKey('import-create-budget'),
        contentPadding: EdgeInsets.zero,
        controlAffinity: ListTileControlAffinity.leading,
        value: controller.createBudget,
        onChanged: (v) => controller.setCreateBudget(v ?? false),
        title: Text(l10n.impBudgetBody(budget.latestMonth, budget.categoriesWithAmounts)),
      ),
      Text(l10n.impBudgetNote, style: Theme.of(context).textTheme.bodySmall),
    ]);
  }
}

/// For a plain CSV: which column is which, date order, number format, sign.
class _ReadingSection extends StatelessWidget {
  const _ReadingSection({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final detected = c.preview!.detected;
    final shown = detected.proposedMapping;
    final headers = detected.headers;

    String headerLabel(int i) {
      final h = i < headers.length ? headers[i].trim() : '';
      return h.isEmpty ? l10n.impColumnNumber(i + 1) : h;
    }

    Widget columnPicker(String field, String label, int? current) {
      return Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: DropdownButtonFormField<int?>(
          key: ValueKey('import-column-$field'),
          initialValue: (current != null && current < headers.length) ? current : null,
          isExpanded: true,
          decoration: InputDecoration(labelText: label, border: const OutlineInputBorder()),
          items: [
            DropdownMenuItem<int?>(value: null, child: Text(l10n.impColumnNone)),
            for (var i = 0; i < headers.length; i++)
              DropdownMenuItem<int?>(value: i, child: Text(headerLabel(i), overflow: TextOverflow.ellipsis)),
          ],
          onChanged: c.busy ? null : (v) => c.setColumn(field, v),
        ),
      );
    }

    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      _SectionTitle(l10n.impReadingTitle),
      if (headers.isNotEmpty) ...[
        columnPicker('date', l10n.impColumnDate, shown.dateColumn),
        columnPicker('amount', l10n.impColumnAmount, shown.amountColumn),
        if (shown.amountColumn == null) ...[
          columnPicker('debit', l10n.impColumnDebit, shown.debitColumn),
          columnPicker('credit', l10n.impColumnCredit, shown.creditColumn),
        ],
        columnPicker('description', l10n.impColumnDescription, shown.descriptionColumn),
        columnPicker('memo', l10n.impColumnMemo, shown.memoColumn),
        columnPicker('category', l10n.impColumnCategory, shown.categoryColumn),
      ],
      Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: DropdownButtonFormField<ImportDateOrder>(
          key: const ValueKey('import-date-order'),
          initialValue: ImportDateOrder.fromString(detected.dateOrder) == ImportDateOrder.auto
              ? ImportDateOrder.monthFirst
              : ImportDateOrder.fromString(detected.dateOrder),
          isExpanded: true,
          decoration: InputDecoration(labelText: l10n.impDateOrderLabel, border: const OutlineInputBorder()),
          items: [
            DropdownMenuItem(value: ImportDateOrder.monthFirst, child: Text(l10n.impDateMonthFirst)),
            DropdownMenuItem(value: ImportDateOrder.dayFirst, child: Text(l10n.impDateDayFirst)),
            DropdownMenuItem(value: ImportDateOrder.yearFirst, child: Text(l10n.impDateYearFirst)),
          ],
          onChanged: c.busy ? null : (v) => v == null ? null : c.setDateOrder(v),
        ),
      ),
      Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: DropdownButtonFormField<ImportNumberFormat>(
          key: const ValueKey('import-number-format'),
          initialValue: ImportNumberFormat.fromString(detected.numberFormat) == ImportNumberFormat.auto
              ? ImportNumberFormat.us
              : ImportNumberFormat.fromString(detected.numberFormat),
          isExpanded: true,
          decoration: InputDecoration(labelText: l10n.impNumberFormatLabel, border: const OutlineInputBorder()),
          items: [
            DropdownMenuItem(value: ImportNumberFormat.us, child: Text(ImportNumberFormat.us.displayName)),
            DropdownMenuItem(value: ImportNumberFormat.european, child: Text(ImportNumberFormat.european.displayName)),
          ],
          onChanged: c.busy ? null : (v) => v == null ? null : c.setNumberFormat(v),
        ),
      ),
      SwitchListTile(
        key: const ValueKey('import-flip-sign'),
        contentPadding: EdgeInsets.zero,
        value: shown.flipSign,
        onChanged: c.busy ? null : c.setFlipSign,
        title: Text(l10n.impFlipSign),
      ),
      if (detected.sampleRows.isNotEmpty) ...[
        const SizedBox(height: 8),
        Text(l10n.impSampleRows, style: Theme.of(context).textTheme.labelLarge),
        for (final row in detected.sampleRows.take(3))
          Text(row.join('  |  '), maxLines: 1, overflow: TextOverflow.ellipsis, style: Theme.of(context).textTheme.bodySmall),
      ],
    ]);
  }
}

// ───────────────────────── Step 3: done ─────────────────────────

class _DoneStep extends StatelessWidget {
  const _DoneStep({required this.controller});
  final ImportController controller;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final c = controller;
    final result = c.result!;
    final theme = Theme.of(context);

    final lines = <String>[
      l10n.impDoneImported(result.imported),
      if (result.skippedDuplicates > 0) l10n.impDoneSkippedDupes(result.skippedDuplicates),
      if (result.skippedByChoice > 0) l10n.impDoneSkippedChoice(result.skippedByChoice),
      if (result.skippedInvalid > 0) l10n.impDoneSkippedInvalid(result.skippedInvalid),
      if (result.accountsCreated > 0) l10n.impDoneAccounts(result.accountsCreated),
      if (result.categoriesCreated > 0) l10n.impDoneCategories(result.categoriesCreated),
      if (result.budgetCreated) l10n.impDoneBudget,
    ];

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Icon(c.undone ? Icons.undo_rounded : Icons.check_circle_outline_rounded,
            size: 48, color: theme.colorScheme.primary),
        const SizedBox(height: 12),
        Text(c.undone ? l10n.impUndoneTitle : l10n.impDoneTitle,
            style: theme.textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 12),
        if (c.undone)
          Text(l10n.impUndone(c.undoResult?.transactionsRemoved ?? 0))
        else ...[
          for (final line in lines)
            Padding(padding: const EdgeInsets.only(bottom: 4), child: Text(line, key: ValueKey(line))),
          const SizedBox(height: 12),
          Text(l10n.impDoneReviewNote, style: theme.textTheme.bodySmall),
          for (final w in result.warnings) ...[
            const SizedBox(height: 8),
            _Note(child: Text(w)),
          ],
        ],
        if (c.failure != null) ...[
          const SizedBox(height: 12),
          _Note(error: true, child: Text(_failureText(l10n, c.failure!, l10n.impUndoFailed))),
        ],
        const SizedBox(height: 24),
        if (!c.undone) ...[
          FilledButton(
            key: const ValueKey('import-review-transactions'),
            onPressed: () => context.go('/transactions'),
            child: Text(l10n.impDoneReview),
          ),
          const SizedBox(height: 8),
          OutlinedButton(
            key: const ValueKey('import-undo'),
            onPressed: c.busy ? null : () async {
              final messenger = context;
              await c.undo();
              if (messenger.mounted && c.undone) {
                showAppToast(messenger, l10n.impUndone(c.undoResult?.transactionsRemoved ?? 0),
                    type: AppToastType.success);
              }
            },
            child: Text(l10n.impDoneUndo),
          ),
          const SizedBox(height: 4),
          Text(l10n.impUndoHint, style: theme.textTheme.bodySmall),
          const SizedBox(height: 8),
        ],
        TextButton(
          key: const ValueKey('import-another'),
          onPressed: c.reset,
          child: Text(l10n.impAnother),
        ),
      ],
    );
  }
}
