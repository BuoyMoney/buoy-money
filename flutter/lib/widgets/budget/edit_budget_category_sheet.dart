import 'dart:ui';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../models/budget_models.dart';
import '../../models/savings_models.dart';
import '../../utils/currency.dart';
import '../../services/budget_service.dart';
import '../../services/savings_service.dart';
import '../../utils/formatters.dart';
import '../app_toast.dart';
import 'sheet_primitives.dart';

Future<BudgetCategory?> showEditBudgetCategorySheet(
  BuildContext context,
  BudgetCategory category, {
  Budget? budget,
}) {
  return showModalBottomSheet<BudgetCategory>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _EditBudgetCategorySheet(category: category, budget: budget),
  );
}

class _EditBudgetCategorySheet extends StatefulWidget {
  final BudgetCategory category;
  final Budget? budget;
  const _EditBudgetCategorySheet({required this.category, this.budget});

  @override
  State<_EditBudgetCategorySheet> createState() => _EditBudgetCategorySheetState();
}

class _EditBudgetCategorySheetState extends State<_EditBudgetCategorySheet> {
  late TextEditingController _amountController;
  late bool _allowRollover;
  late bool _isCountUpEnabled;
  CountUpRolloverMode _countUpRolloverMode = CountUpRolloverMode.rolloverToSelf;
  double? _maxRolloverMultiplier;
  int? _rolloverOverflowSavingsGoalId;
  int? _savingsGoalId;
  bool _applyToFuturePeriods = false;
  bool _saving = false;

  List<SavingsGoal> _savingsGoals = [];
  bool _loadingGoals = true;

  // Rollover multiplier presets (logarithmic scale)
  static const _rolloverPresets = [0.1, 0.25, 0.5, 0.75, 1.0, 2.0, 3.0, 5.0, 10.0];

  @override
  void initState() {
    super.initState();
    final c = widget.category;
    _amountController = TextEditingController(text: c.allocatedAmount.toStringAsFixed(2));
    _allowRollover = c.allowRollover;
    _isCountUpEnabled = c.isCountUpEnabled;
    _countUpRolloverMode = c.countUpRolloverMode ?? CountUpRolloverMode.rolloverToSelf;
    _maxRolloverMultiplier = c.maxRolloverMultiplier;
    _rolloverOverflowSavingsGoalId = c.rolloverOverflowSavingsGoalId;
    _savingsGoalId = c.savingsGoalId;
    _loadSavingsGoals();
  }

  Future<void> _loadSavingsGoals() async {
    try {
      final goals = await savingsService.getGoals();
      if (mounted) {
        setState(() {
          _savingsGoals = goals.where((g) => g.isActive && !g.isCompleted).toList();
          _loadingGoals = false;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _loadingGoals = false);
    }
  }

  double get _currentAmount =>
      parseFlexibleAmount(_amountController.text) ?? widget.category.allocatedAmount;

  String _formatMultiplier(double? v) {
    if (v == null) return '∞';
    if (v == v.roundToDouble()) return '${v.toInt()}x';
    return '${v}x';
  }

  Future<void> _save() async {
    final amount = parseFlexibleAmount(_amountController.text);
    if (amount == null || amount < 0) return;
    setState(() => _saving = true);
    try {
      // Toggle count-up if changed
      if (_isCountUpEnabled != widget.category.isCountUpEnabled) {
        await budgetService.enableCountUp(widget.category.id, _isCountUpEnabled);
      }

      final updated = await budgetService.updateBudgetCategory(
        widget.category.id,
        UpdateBudgetCategoryRequest(
          allocatedAmount: amount,
          allowRollover: _isCountUpEnabled ? true : _allowRollover,
          maxRolloverMultiplier: _isCountUpEnabled ? null : _maxRolloverMultiplier,
          rolloverOverflowSavingsGoalId: _isCountUpEnabled ? null : _rolloverOverflowSavingsGoalId,
          countUpRolloverMode: _isCountUpEnabled ? _countUpRolloverMode : null,
          savingsGoalId: _countUpRolloverMode == CountUpRolloverMode.transferToSavings ? _savingsGoalId : null,
          applyToFuturePeriods: _applyToFuturePeriods,
        ),
      );
      if (mounted) Navigator.of(context).pop(updated);
    } catch (e) {
      setState(() => _saving = false);
      if (mounted) {
        // Surface the backend's validation message (e.g. the amount bound) instead
        // of a raw DioException string.
        final data = e is DioException ? e.response?.data : null;
        final msg = (data is Map && data['message'] != null)
            ? data['message'].toString()
            : 'Failed to update category. Please check the amount and try again.';
        showAppToast(context, msg, type: AppToastType.error);
      }
    }
  }

  @override
  void dispose() {
    _amountController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.category;
    final pctUsed = c.percentageUsed.clamp(0, 200).toDouble();

    return DraggableScrollableSheet(
      initialChildSize: 0.75,
      minChildSize: 0.4,
      maxChildSize: 0.95,
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
                      Text(c.categoryName,
                          style: GoogleFonts.sora(
                              fontSize: 18,
                              fontWeight: FontWeight.bold,
                              color: sheetTextGhost)),
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
                      // Current spending context
                      SheetGlassCard(
                        child: Column(
                          children: [
                            Row(
                              mainAxisAlignment: MainAxisAlignment.spaceBetween,
                              children: [
                                SheetSummaryItem(c.isIncome ? 'Expected' : 'Budgeted', formatCurrency(c.allocatedAmount), sheetTextGhost),
                                SheetSummaryItem(c.isIncome ? 'Earned' : 'Spent', formatCurrency(c.spentAmount),
                                    c.isIncome ? sheetIncome : sheetExpense),
                                SheetSummaryItem(
                                  'Left',
                                  formatCurrency(c.remainingAmount),
                                  c.isIncome
                                      ? (c.remainingAmount <= 0 ? sheetIncome : sheetTextGhost)
                                      : (c.remainingAmount >= 0 ? sheetIncome : sheetExpense),
                                ),
                              ],
                            ),
                            const SizedBox(height: 12),
                            ClipRRect(
                              borderRadius: BorderRadius.circular(4),
                              child: LinearProgressIndicator(
                                value: (pctUsed / 100).clamp(0.0, 1.0),
                                backgroundColor: sheetCardBg,
                                color: pctUsed > 100 ? sheetExpense : (pctUsed > 80 ? sheetPending : sheetAccent),
                                minHeight: 6,
                              ),
                            ),
                            const SizedBox(height: 4),
                            Text(AppLocalizations.of(context).budPercentUsed(pctUsed.toStringAsFixed(0)),
                                style: GoogleFonts.firaCode(fontSize: 11, color: sheetTextMuted)),
                          ],
                        ),
                      ),
                      const SizedBox(height: 20),

                      // Budget amount
                      SheetTextField(
                        controller: _amountController,
                        label: 'Budget Amount',
                        prefix: primaryCurrencySymbol,
                        keyboardType: const TextInputType.numberWithOptions(decimal: true),
                        onChanged: (_) => setState(() {}),
                      ),

                      // Unallocated amount indicator
                      if (widget.budget != null) ...[
                        const SizedBox(height: 8),
                        Builder(builder: (context) {
                          final budget = widget.budget!;
                          final otherAllocated = budget.categories
                              .where((c) => !c.isIncome && c.id != widget.category.id)
                              .fold(0.0, (sum, c) => sum + c.allocatedAmount);
                          final unallocated = budget.totalIncome - otherAllocated - _currentAmount;
                          final isOver = unallocated < 0;
                          return Container(
                            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                            decoration: BoxDecoration(
                              color: (isOver ? sheetExpense : sheetIncome).withValues(alpha: 0.08),
                              borderRadius: BorderRadius.circular(8),
                              border: Border.all(
                                color: (isOver ? sheetExpense : sheetIncome).withValues(alpha: 0.2),
                              ),
                            ),
                            child: Row(
                              mainAxisAlignment: MainAxisAlignment.spaceBetween,
                              children: [
                                Text(
                                  AppLocalizations.of(context).budRemainingToAllocate,
                                  style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted),
                                ),
                                Text(
                                  formatCurrency(unallocated.abs()),
                                  style: GoogleFonts.firaCode(
                                    fontSize: 13,
                                    fontWeight: FontWeight.w600,
                                    color: isOver ? sheetExpense : sheetIncome,
                                  ),
                                ),
                              ],
                            ),
                          );
                        }),
                      ],
                      const SizedBox(height: 16),

                      // Count-Up toggle
                      SheetToggleRow(
                        label: 'Count-Up Budgeting',
                        subtitle: 'Earn your budget daily instead of all at once',
                        value: _isCountUpEnabled,
                        onChanged: (v) => setState(() {
                          _isCountUpEnabled = v;
                          if (v) _allowRollover = true;
                        }),
                      ),
                      const SizedBox(height: 12),

                      if (_isCountUpEnabled) ...[
                        // Count-Up rollover modes
                        Text(AppLocalizations.of(context).budAtPeriodEnd, style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
                        const SizedBox(height: 8),
                        SheetRadioOption(
                          label: 'Roll Over',
                          subtitle: 'Carry surplus forward to next period',
                          value: CountUpRolloverMode.rolloverToSelf,
                          groupValue: _countUpRolloverMode,
                          onChanged: (v) => setState(() => _countUpRolloverMode = v!),
                        ),
                        SheetRadioOption<CountUpRolloverMode>(
                          label: 'Transfer to Savings',
                          subtitle: 'Auto-transfer surplus to savings goal',
                          value: CountUpRolloverMode.transferToSavings,
                          groupValue: _countUpRolloverMode,
                          onChanged: (v) => setState(() {
                            if (v != null && (_savingsGoals.isNotEmpty || _loadingGoals)) {
                              _countUpRolloverMode = v;
                            }
                          }),
                        ),
                        // Savings goal picker for count-up transfer mode
                        if (_countUpRolloverMode == CountUpRolloverMode.transferToSavings) ...[
                          if (_loadingGoals)
                            const Padding(
                              padding: EdgeInsets.only(left: 40, top: 4, bottom: 8),
                              child: SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2, color: sheetAccent)),
                            )
                          else if (_savingsGoals.isEmpty)
                            Padding(
                              padding: const EdgeInsets.only(left: 40, top: 4, bottom: 8),
                              child: Text(AppLocalizations.of(context).budCreateSavingsGoalFirst,
                                  style: GoogleFonts.sora(fontSize: 11, color: sheetPending)),
                            )
                          else
                            Padding(
                              padding: const EdgeInsets.only(left: 40, top: 4, bottom: 8),
                              child: SheetDropdown<int>(
                                value: _savingsGoalId,
                                hint: 'Select savings goal',
                                items: _savingsGoals.map((g) => DropdownMenuItem(
                                  value: g.id,
                                  child: Text(g.name, style: GoogleFonts.sora(fontSize: 13, color: sheetTextGhost)),
                                )).toList(),
                                onChanged: (v) => setState(() => _savingsGoalId = v),
                              ),
                            ),
                        ],
                        SheetRadioOption(
                          label: 'Start Fresh',
                          subtitle: 'Reset to ${primaryCurrencySymbol}0 (surplus is lost)',
                          value: CountUpRolloverMode.resetToZero,
                          groupValue: _countUpRolloverMode,
                          onChanged: (v) => setState(() => _countUpRolloverMode = v!),
                          color: sheetPending,
                        ),
                        const SizedBox(height: 12),
                      ] else ...[
                        // Standard rollover
                        SheetToggleRow(
                          label: 'Allow Rollover',
                          subtitle: 'Unused funds carry to next period',
                          value: _allowRollover,
                          onChanged: (v) => setState(() {
                            _allowRollover = v;
                            if (!v) {
                              _maxRolloverMultiplier = null;
                              _rolloverOverflowSavingsGoalId = null;
                            }
                          }),
                        ),

                        // Max rollover multiplier (shown when rollover enabled)
                        if (_allowRollover) ...[
                          const SizedBox(height: 12),
                          // Header row with label + current value badge
                          Row(
                            children: [
                              Text(AppLocalizations.of(context).budMaxRollover, style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
                              const Spacer(),
                              Container(
                                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                                decoration: BoxDecoration(
                                  color: _maxRolloverMultiplier == null
                                      ? sheetTextMuted.withValues(alpha: 0.1)
                                      : _maxRolloverMultiplier! <= 1.0
                                          ? sheetIncome.withValues(alpha: 0.15)
                                          : sheetPending.withValues(alpha: 0.15),
                                  borderRadius: BorderRadius.circular(8),
                                ),
                                child: Text(
                                  _formatMultiplier(_maxRolloverMultiplier),
                                  style: GoogleFonts.firaCode(
                                    fontSize: 12,
                                    fontWeight: FontWeight.w600,
                                    color: _maxRolloverMultiplier == null
                                        ? sheetTextMuted
                                        : _maxRolloverMultiplier! <= 1.0 ? sheetIncome : sheetPending,
                                  ),
                                ),
                              ),
                            ],
                          ),
                          const SizedBox(height: 8),
                          // Preset chips
                          Wrap(
                            spacing: 6,
                            runSpacing: 6,
                            children: [
                              ..._rolloverPresets.map((v) => SheetChoiceChip(
                                label: '${v == v.roundToDouble() ? v.toInt().toString() : v.toString()}x',
                                selected: _maxRolloverMultiplier == v,
                                onSelected: () => setState(() => _maxRolloverMultiplier = v),
                              )),
                              SheetChoiceChip(
                                label: '∞',
                                selected: _maxRolloverMultiplier == null,
                                onSelected: () => setState(() {
                                  _maxRolloverMultiplier = null;
                                  _rolloverOverflowSavingsGoalId = null;
                                }),
                              ),
                            ],
                          ),
                          // Max rollover amount helper text
                          if (_maxRolloverMultiplier != null) ...[
                            const SizedBox(height: 6),
                            Text(
                              AppLocalizations.of(context).budMaxRolloverAmount(
                                  formatCurrency(_currentAmount * _maxRolloverMultiplier!)),
                              style: GoogleFonts.firaCode(fontSize: 11, color: sheetTextMuted),
                            ),
                          ],
                          // Overflow savings goal (when max is set, not unlimited)
                          if (_maxRolloverMultiplier != null && !_loadingGoals && _savingsGoals.isNotEmpty) ...[
                            const SizedBox(height: 12),
                            Text(AppLocalizations.of(context).budOverflowTo, style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
                            const SizedBox(height: 4),
                            Text(AppLocalizations.of(context).budOverflowHint,
                                style: GoogleFonts.sora(fontSize: 10, color: sheetTextMuted.withValues(alpha: 0.6))),
                            const SizedBox(height: 6),
                            SheetDropdown<int>(
                              value: _rolloverOverflowSavingsGoalId,
                              hint: 'Select savings goal for overflow',
                              items: _savingsGoals.map((g) => DropdownMenuItem(
                                value: g.id,
                                child: Text(g.name, style: GoogleFonts.sora(fontSize: 13, color: sheetTextGhost)),
                              )).toList(),
                              onChanged: (v) => setState(() => _rolloverOverflowSavingsGoalId = v),
                            ),
                          ],
                          // Nudge to connect an overflow goal when a cap is set but none
                          // is chosen — otherwise surplus beyond the cap is silently
                          // untracked ("overflow lost", QE #615). Works with or without
                          // existing goals.
                          if (_maxRolloverMultiplier != null && _rolloverOverflowSavingsGoalId == null) ...[
                            const SizedBox(height: 10),
                            Container(
                              padding: const EdgeInsets.all(10),
                              decoration: BoxDecoration(
                                color: sheetPending.withValues(alpha: 0.1),
                                borderRadius: BorderRadius.circular(10),
                                border: Border.all(color: sheetPending.withValues(alpha: 0.3)),
                              ),
                              child: Row(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  const Icon(Icons.info_outline, size: 16, color: sheetPending),
                                  const SizedBox(width: 8),
                                  Expanded(
                                    child: Text(
                                      _savingsGoals.isEmpty
                                          ? AppLocalizations.of(context)
                                              .budOverflowNudgeNoGoals
                                          : AppLocalizations.of(context)
                                              .budOverflowNudgePickGoal,
                                      style: GoogleFonts.sora(fontSize: 11, color: sheetPending),
                                    ),
                                  ),
                                ],
                              ),
                            ),
                          ],
                        ],
                        const SizedBox(height: 12),
                      ],

                      // Rollover balance display
                      if (c.rolloverBalance != 0)
                        Padding(
                          padding: const EdgeInsets.only(bottom: 12),
                          child: SheetGlassCard(
                            child: Row(
                              mainAxisAlignment: MainAxisAlignment.spaceBetween,
                              children: [
                                Text(AppLocalizations.of(context).budCurrentRollover,
                                    style: GoogleFonts.sora(fontSize: 13, color: sheetTextMuted)),
                                Text(formatCurrency(c.rolloverBalance),
                                    style: GoogleFonts.firaCode(
                                        fontSize: 14,
                                        color: c.rolloverBalance > 0 ? sheetIncome : sheetExpense,
                                        fontWeight: FontWeight.w600)),
                              ],
                            ),
                          ),
                        ),

                      // Apply to future
                      SheetToggleRow(
                        label: 'Apply to Future Periods',
                        subtitle: 'Set as default for upcoming resets',
                        value: _applyToFuturePeriods,
                        onChanged: (v) => setState(() => _applyToFuturePeriods = v),
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
                        label: _saving ? 'Saving...' : 'Save Changes',
                        onPressed: !_saving ? _save : null,
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
