import 'dart:ui';
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

Future<bool?> showTransferToSavingsSheet(
  BuildContext context, {
  required BudgetCategory category,
}) {
  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _TransferToSavingsSheet(category: category),
  );
}

class _TransferToSavingsSheet extends StatefulWidget {
  final BudgetCategory category;
  const _TransferToSavingsSheet({required this.category});

  @override
  State<_TransferToSavingsSheet> createState() => _TransferToSavingsSheetState();
}

class _TransferToSavingsSheetState extends State<_TransferToSavingsSheet> {
  final _amountController = TextEditingController();
  final _noteController = TextEditingController();
  int? _selectedGoalId;
  bool _saving = false;

  List<SavingsGoal> _goals = [];
  bool _loadingGoals = true;

  @override
  void initState() {
    super.initState();
    _loadGoals();
  }

  Future<void> _loadGoals() async {
    try {
      final goals = await savingsService.getGoals();
      if (mounted) {
        setState(() {
          _goals = goals.where((g) => g.isActive && !g.isCompleted).toList();
          _loadingGoals = false;
          if (_goals.isNotEmpty) _selectedGoalId = _goals.first.id;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _loadingGoals = false);
    }
  }

  double get _available => widget.category.remainingAmount.clamp(0, double.infinity);

  double get _amount =>
      parseFlexibleAmount(_amountController.text) ?? 0;

  bool get _isValid =>
      _selectedGoalId != null && _amount > 0 && _amount <= _available;

  SavingsGoal? get _selectedGoal {
    if (_selectedGoalId == null) return null;
    for (final g in _goals) {
      if (g.id == _selectedGoalId) return g;
    }
    return null;
  }

  Future<void> _submit() async {
    if (!_isValid) return;
    setState(() => _saving = true);
    try {
      await budgetService.transferToSavings(
        categoryId: widget.category.id,
        savingsGoalId: _selectedGoalId!,
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
      initialChildSize: 0.7,
      minChildSize: 0.4,
      maxChildSize: 0.9,
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
                      Text(AppLocalizations.of(context).budTransferToSavings,
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
                      // Source category info
                      SheetGlassCard(
                        child: Row(
                          mainAxisAlignment: MainAxisAlignment.spaceBetween,
                          children: [
                            Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(widget.category.categoryName,
                                    style: GoogleFonts.sora(fontSize: 14, color: sheetTextGhost, fontWeight: FontWeight.w500)),
                                const SizedBox(height: 2),
                                Text(AppLocalizations.of(context).budAvailableToTransfer,
                                    style: GoogleFonts.sora(fontSize: 11, color: sheetTextMuted)),
                              ],
                            ),
                            Text(formatCurrency(_available),
                                style: GoogleFonts.firaCode(
                                    fontSize: 16, color: sheetIncome, fontWeight: FontWeight.w600)),
                          ],
                        ),
                      ),
                      const SizedBox(height: 16),

                      // Savings goal selector
                      Text(AppLocalizations.of(context).budSavingsGoal, style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
                      const SizedBox(height: 8),
                      if (_loadingGoals)
                        const Center(child: CircularProgressIndicator(color: sheetAccent))
                      else if (_goals.isEmpty)
                        Container(
                          padding: const EdgeInsets.all(16),
                          decoration: BoxDecoration(
                            color: sheetPending.withValues(alpha: 0.1),
                            borderRadius: BorderRadius.circular(12),
                            border: Border.all(color: sheetPending.withValues(alpha: 0.3)),
                          ),
                          child: Row(
                            children: [
                              const Icon(Icons.info_outline, color: sheetPending, size: 18),
                              const SizedBox(width: 8),
                              Expanded(
                                child: Text(AppLocalizations.of(context).budNoActiveSavingsGoals,
                                    style: GoogleFonts.sora(fontSize: 13, color: sheetPending)),
                              ),
                            ],
                          ),
                        )
                      else ...[
                        SheetDropdown<int>(
                          value: _selectedGoalId,
                          hint: 'Select savings goal',
                          items: _goals.map((g) => DropdownMenuItem(
                            value: g.id,
                            child: Row(
                              mainAxisAlignment: MainAxisAlignment.spaceBetween,
                              children: [
                                Flexible(child: Text(g.name, style: GoogleFonts.sora(fontSize: 14, color: sheetTextGhost))),
                                Text('${g.progressPercentage.toStringAsFixed(0)}%',
                                    style: GoogleFonts.firaCode(fontSize: 12, color: sheetAccent)),
                              ],
                            ),
                          )).toList(),
                          onChanged: (v) => setState(() => _selectedGoalId = v),
                        ),
                        // Goal progress
                        if (_selectedGoal != null) ...[
                          const SizedBox(height: 12),
                          SheetGlassCard(
                            child: Column(
                              children: [
                                Row(
                                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                                  children: [
                                    Text(_selectedGoal!.name,
                                        style: GoogleFonts.sora(fontSize: 13, color: sheetTextGhost)),
                                    Text('${formatCurrency(_selectedGoal!.currentAmount)} / ${formatCurrency(_selectedGoal!.targetAmount)}',
                                        style: GoogleFonts.firaCode(fontSize: 11, color: sheetTextMuted)),
                                  ],
                                ),
                                const SizedBox(height: 8),
                                ClipRRect(
                                  borderRadius: BorderRadius.circular(4),
                                  child: LinearProgressIndicator(
                                    value: (_selectedGoal!.progressPercentage / 100).clamp(0.0, 1.0),
                                    backgroundColor: sheetCardBg,
                                    color: sheetIncome,
                                    minHeight: 6,
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ],
                      ],
                      const SizedBox(height: 16),

                      // Amount
                      SheetTextField(
                        controller: _amountController,
                        label: 'Amount',
                        prefix: primaryCurrencySymbol,
                        keyboardType: const TextInputType.numberWithOptions(decimal: true),
                        onChanged: (_) => setState(() {}),
                      ),
                      if (_available > 0)
                        Align(
                          alignment: Alignment.centerLeft,
                          child: TextButton(
                            onPressed: () {
                              _amountController.text = _available.toStringAsFixed(2);
                              setState(() {});
                            },
                            child: Text(AppLocalizations.of(context).budTransferMax(formatCurrency(_available)),
                                style: GoogleFonts.sora(fontSize: 12, color: sheetAccent)),
                          ),
                        ),
                      const SizedBox(height: 16),

                      // Note
                      SheetTextField(
                        controller: _noteController,
                        label: 'Note (optional)',
                        hint: 'Reason for transfer',
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
                        label: _saving ? 'Transferring...' : 'Transfer to Savings',
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
