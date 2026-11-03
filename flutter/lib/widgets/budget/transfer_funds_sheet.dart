import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../models/budget_models.dart';
import '../../utils/currency.dart';
import '../../services/budget_service.dart';
import '../../utils/formatters.dart';
import '../app_toast.dart';
import 'sheet_primitives.dart';

Future<bool?> showTransferFundsSheet(
  BuildContext context, {
  required BudgetCategory targetCategory,
  required Budget budget,
}) {
  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _TransferFundsSheet(
      targetCategory: targetCategory,
      budget: budget,
    ),
  );
}

class _TransferFundsSheet extends StatefulWidget {
  final BudgetCategory targetCategory;
  final Budget budget;
  const _TransferFundsSheet({required this.targetCategory, required this.budget});

  @override
  State<_TransferFundsSheet> createState() => _TransferFundsSheetState();
}

class _TransferFundsSheetState extends State<_TransferFundsSheet> {
  final _amountController = TextEditingController();
  final _noteController = TextEditingController();
  int? _sourceCategoryId;
  bool _saving = false;

  List<BudgetCategory> get _sourceCandidates {
    return widget.budget.categories.where((c) =>
        c.id != widget.targetCategory.id &&
        !c.isIncome &&
        c.remainingAmount > 0).toList();
  }

  double get _sourceAvailable {
    if (_sourceCategoryId == null) return 0;
    final src = _sourceCategory;
    return src?.remainingAmount ?? 0;
  }

  /// The currently-selected source category, or null if none is selected or the
  /// selection no longer maps to a category (e.g. after a reload).
  BudgetCategory? get _sourceCategory {
    for (final c in widget.budget.categories) {
      if (c.id == _sourceCategoryId) return c;
    }
    return null;
  }

  double get _deficit {
    return (widget.targetCategory.spentAmount - widget.targetCategory.allocatedAmount)
        .clamp(0, double.infinity);
  }

  double get _amount =>
      parseFlexibleAmount(_amountController.text) ?? 0;

  bool get _isValid =>
      _sourceCategoryId != null &&
      _amount > 0 &&
      _amount <= _sourceAvailable;

  void _showSourcePicker(BuildContext context) {
    showModalBottomSheet(
      context: context,
      backgroundColor: Colors.transparent,
      isScrollControlled: true,
      builder: (_) => SafeArea(
        top: false,
        child: Container(
          decoration: BoxDecoration(
            color: sheetBg.withValues(alpha: 0.98),
            borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
            border: Border.all(color: sheetCardBorder),
          ),
          padding: const EdgeInsets.fromLTRB(24, 16, 24, 24),
          constraints: BoxConstraints(
            maxHeight: MediaQuery.of(context).size.height * 0.75,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Center(child: SheetDragHandle()),
              const SizedBox(height: 12),
              Text(AppLocalizations.of(context).budSelectSourceCategory,
                  style: GoogleFonts.sora(fontSize: 16, fontWeight: FontWeight.w600, color: sheetTextGhost)),
              const SizedBox(height: 12),
              if (_sourceCandidates.isNotEmpty)
                Flexible(
                  child: ListView(
                    shrinkWrap: true,
                    children: _sourceCandidates.map((c) => ListTile(
                          contentPadding: EdgeInsets.zero,
                          title: Text(c.categoryName, style: GoogleFonts.sora(fontSize: 14, color: sheetTextGhost)),
                          trailing: Text(formatCurrency(c.remainingAmount),
                              style: GoogleFonts.firaCode(fontSize: 12, color: sheetIncome)),
                          selected: c.id == _sourceCategoryId,
                          selectedTileColor: sheetAccent.withValues(alpha: 0.08),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
                          onTap: () {
                            setState(() => _sourceCategoryId = c.id);
                            Navigator.pop(context);
                          },
                        )).toList(),
                  ),
                ),
              if (_sourceCandidates.isEmpty)
                Padding(
                  padding: const EdgeInsets.all(24),
                  child: Text(AppLocalizations.of(context).budNoCategoriesWithFunds,
                      style: GoogleFonts.sora(fontSize: 14, color: sheetTextMuted)),
                ),
            ],
          ),
        ),
      ),
    );
  }

  Future<void> _submit() async {
    if (!_isValid) return;
    setState(() => _saving = true);
    try {
      await budgetService.transferFunds(
        targetCategoryId: widget.targetCategory.id,
        sourceCategoryId: _sourceCategoryId!,
        amount: _amount,
        note: _noteController.text.isNotEmpty ? _noteController.text : null,
      );
      if (mounted) Navigator.of(context).pop(true);
    } catch (e) {
      setState(() => _saving = false);
      if (mounted) {
        showAppToast(context, 'Transfer failed: $e', type: AppToastType.error);
      }
    }
  }

  @override
  void dispose() {
    _amountController.dispose();
    _noteController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.65,
      minChildSize: 0.4,
      maxChildSize: 0.85,
      builder: (_, scrollController) => ClipRRect(
        borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
          child: Container(
            decoration: BoxDecoration(
              color: sheetBg.withValues(alpha: 0.95),
              borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
              border: Border.all(color: sheetCardBorder),
            ),
            child: Column(
              children: [
                SheetDragHandle(),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 24),
                  child: Row(
                    children: [
                      Text(AppLocalizations.of(context).budTransferFunds,
                          style: GoogleFonts.sora(
                              fontSize: 18, fontWeight: FontWeight.bold, color: sheetTextGhost)),
                      const Spacer(),
                      IconButton(
                        icon: const Icon(Icons.close, color: sheetTextMuted, size: 20),
                        onPressed: () => Navigator.of(context).pop(),
                      ),
                    ],
                  ),
                ),
                Expanded(
                  child: ListView(
                    controller: scrollController,
                    padding: const EdgeInsets.fromLTRB(24, 8, 24, 16),
                    children: [
                      // Target category alert
                      if (_deficit > 0)
                        Container(
                          padding: const EdgeInsets.all(12),
                          margin: const EdgeInsets.only(bottom: 16),
                          decoration: BoxDecoration(
                            color: sheetExpense.withValues(alpha: 0.1),
                            borderRadius: BorderRadius.circular(12),
                            border: Border.all(color: sheetExpense.withValues(alpha: 0.3)),
                          ),
                          child: Row(
                            children: [
                              const Icon(Icons.warning_amber_rounded, color: sheetExpense, size: 20),
                              const SizedBox(width: 8),
                              Expanded(
                                child: Text(
                                  AppLocalizations.of(context).budCategoryOverBy(
                                      widget.targetCategory.categoryName,
                                      formatCurrency(_deficit)),
                                  style: GoogleFonts.sora(fontSize: 13, color: sheetExpense),
                                ),
                              ),
                            ],
                          ),
                        ),

                      // From → To visualization
                      SheetGlassCard(
                        child: Row(
                          children: [
                            Expanded(
                              child: GestureDetector(
                                onTap: () => _showSourcePicker(context),
                                child: Column(
                                  children: [
                                    Text(AppLocalizations.of(context).budFromLabel, style: GoogleFonts.sora(fontSize: 10, color: sheetTextMuted, letterSpacing: 1)),
                                    const SizedBox(height: 4),
                                    Text(
                                      _sourceCategoryId != null
                                          ? widget.budget.categories
                                              .firstWhere((c) => c.id == _sourceCategoryId)
                                              .categoryName
                                          : AppLocalizations.of(context).budSourceCategory,
                                      style: GoogleFonts.sora(
                                        fontSize: 14,
                                        color: _sourceCategoryId != null ? sheetTextGhost : sheetTextMuted,
                                      ),
                                      textAlign: TextAlign.center,
                                    ),
                                  ],
                                ),
                              ),
                            ),
                            const Icon(Icons.arrow_forward, color: sheetAccent, size: 20),
                            Expanded(
                              child: Column(
                                children: [
                                  Text(AppLocalizations.of(context).budToLabel, style: GoogleFonts.sora(fontSize: 10, color: sheetTextMuted, letterSpacing: 1)),
                                  const SizedBox(height: 4),
                                  Text(widget.targetCategory.categoryName,
                                      style: GoogleFonts.sora(fontSize: 14, color: sheetAccent),
                                      textAlign: TextAlign.center),
                                ],
                              ),
                            ),
                          ],
                        ),
                      ),
                      const SizedBox(height: 16),

                      // Count-up category warning
                      if (_sourceCategory?.isCountUpEnabled ?? false)
                        Builder(builder: (_) {
                          final src = _sourceCategory!;
                          final days = widget.budget.daysInCurrentPeriod > 0
                              ? widget.budget.daysInCurrentPeriod
                              : 30;
                          final currentDaily = src.allocatedAmount / days;
                          final newDaily = _amount > 0
                              ? (src.allocatedAmount - _amount) / days
                              : currentDaily;
                          return Container(
                            padding: const EdgeInsets.all(12),
                            margin: const EdgeInsets.only(bottom: 16),
                            decoration: BoxDecoration(
                              color: sheetPending.withValues(alpha: 0.1),
                              borderRadius: BorderRadius.circular(12),
                              border: Border.all(color: sheetPending.withValues(alpha: 0.3)),
                            ),
                            child: Row(
                              children: [
                                const Icon(Icons.trending_up, color: sheetPending, size: 18),
                                const SizedBox(width: 8),
                                Expanded(
                                  child: Text(
                                    AppLocalizations.of(context)
                                            .budCountUpDailyAllowance(
                                                formatCurrency(currentDaily)) +
                                        (_amount > 0
                                            ? ' → ${formatCurrency(newDaily)}'
                                            : ''),
                                    style: GoogleFonts.sora(fontSize: 12, color: sheetPending),
                                  ),
                                ),
                              ],
                            ),
                          );
                        }),

                      // Amount
                      SheetTextField(
                        controller: _amountController,
                        label: 'Amount',
                        prefix: primaryCurrencySymbol,
                        keyboardType: const TextInputType.numberWithOptions(decimal: true),
                        onChanged: (_) => setState(() {}),
                      ),
                      if (_deficit > 0 && _sourceCategoryId != null)
                        Align(
                          alignment: Alignment.centerLeft,
                          child: TextButton(
                            onPressed: () {
                              final fillAmount = _deficit.clamp(0, _sourceAvailable);
                              _amountController.text = fillAmount.toStringAsFixed(2);
                              setState(() {});
                            },
                            child: Text(AppLocalizations.of(context).budFillExactDeficit(formatCurrency(_deficit.clamp(0, _sourceAvailable))),
                                style: GoogleFonts.sora(fontSize: 12, color: sheetAccent)),
                          ),
                        ),
                      const SizedBox(height: 16),

                      // Note
                      SheetTextField(
                        controller: _noteController,
                        label: 'Note (optional)',
                        hint: 'Why are you transferring?',
                      ),
                    ],
                  ),
                ),

                SafeArea(
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(24, 8, 24, 16),
                    child: SizedBox(
                      width: double.infinity,
                      height: 48,
                      child: SheetAccentButton(
                        label: _saving ? 'Transferring...' : 'Transfer ${_amount > 0 ? formatCurrency(_amount) : ""}',
                        onPressed: _isValid && !_saving ? _submit : null,
                      ),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
