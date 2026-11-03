import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../models/budget_models.dart';
import '../../services/budget_service.dart';
import '../../utils/formatters.dart';
import '../app_toast.dart';
import 'sheet_primitives.dart';

Future<bool?> showResetRolloversSheet(
  BuildContext context, {
  required Budget budget,
  BudgetCategory? singleCategory,
}) {
  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _ResetRolloversSheet(
      budget: budget,
      singleCategory: singleCategory,
    ),
  );
}

class _ResetRolloversSheet extends StatefulWidget {
  final Budget budget;
  final BudgetCategory? singleCategory;
  const _ResetRolloversSheet({required this.budget, this.singleCategory});

  @override
  State<_ResetRolloversSheet> createState() => _ResetRolloversSheetState();
}

class _ResetRolloversSheetState extends State<_ResetRolloversSheet> {
  bool _resetting = false;

  List<BudgetCategory> get _affectedCategories {
    if (widget.singleCategory != null) return [widget.singleCategory!];
    return widget.budget.categories.where((c) => c.rolloverBalance != 0).toList();
  }

  double get _totalRollover =>
      _affectedCategories.fold(0.0, (s, c) => s + c.rolloverBalance);

  Future<void> _reset() async {
    setState(() => _resetting = true);
    try {
      if (widget.singleCategory != null) {
        await budgetService.resetCategoryRollover(widget.singleCategory!.id);
      } else {
        await budgetService.resetAllRollovers(widget.budget.id);
      }
      if (mounted) Navigator.of(context).pop(true);
    } catch (e) {
      setState(() => _resetting = false);
      if (mounted) {
        showAppToast(context, 'Reset failed: $e', type: AppToastType.error);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final isSingle = widget.singleCategory != null;
    return DraggableScrollableSheet(
      initialChildSize: 0.55,
      minChildSize: 0.3,
      maxChildSize: 0.8,
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
                      Text(
                          isSingle
                              ? AppLocalizations.of(context).budResetRollover
                              : AppLocalizations.of(context).budResetAllRollovers,
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
                      // Warning
                      Container(
                        padding: const EdgeInsets.all(12),
                        margin: const EdgeInsets.only(bottom: 16),
                        decoration: BoxDecoration(
                          color: sheetPending.withValues(alpha: 0.1),
                          borderRadius: BorderRadius.circular(12),
                          border: Border.all(color: sheetPending.withValues(alpha: 0.3)),
                        ),
                        child: Row(
                          children: [
                            const Icon(Icons.warning_amber_rounded, color: sheetPending, size: 20),
                            const SizedBox(width: 8),
                            Expanded(
                              child: Text(
                                AppLocalizations.of(context).budResetWarning,
                                style: GoogleFonts.sora(fontSize: 13, color: sheetPending),
                              ),
                            ),
                          ],
                        ),
                      ),

                      // Affected categories
                      if (_affectedCategories.isNotEmpty) ...[
                        Text(AppLocalizations.of(context).budAffectedCategories,
                            style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
                        const SizedBox(height: 8),
                        ..._affectedCategories.map((c) => Padding(
                          padding: const EdgeInsets.only(bottom: 8),
                          child: SheetGlassCard(
                            child: Row(
                              mainAxisAlignment: MainAxisAlignment.spaceBetween,
                              children: [
                                Text(c.categoryName,
                                    style: GoogleFonts.sora(fontSize: 14, color: sheetTextGhost)),
                                Text(formatCurrency(c.rolloverBalance),
                                    style: GoogleFonts.firaCode(
                                        fontSize: 14,
                                        color: c.rolloverBalance > 0 ? sheetIncome : sheetExpense)),
                              ],
                            ),
                          ),
                        )),
                        const SizedBox(height: 8),
                        SheetGlassCard(
                          child: Row(
                            mainAxisAlignment: MainAxisAlignment.spaceBetween,
                            children: [
                              Text(AppLocalizations.of(context).budTotalToClear,
                                  style: GoogleFonts.sora(
                                      fontSize: 14, fontWeight: FontWeight.w600, color: sheetTextGhost)),
                              Text(formatCurrency(_totalRollover),
                                  style: GoogleFonts.firaCode(
                                      fontSize: 16, fontWeight: FontWeight.bold, color: sheetExpense)),
                            ],
                          ),
                        ),
                      ] else
                        Center(
                          child: Padding(
                            padding: const EdgeInsets.all(24),
                            child: Text(AppLocalizations.of(context).budNoRolloversToReset,
                                style: GoogleFonts.sora(fontSize: 14, color: sheetTextMuted)),
                          ),
                        ),
                    ],
                  ),
                ),
                if (_affectedCategories.isNotEmpty)
                  SafeArea(
                    child: Padding(
                      padding: const EdgeInsets.fromLTRB(24, 8, 24, 16),
                      child: SizedBox(
                        width: double.infinity,
                        height: 48,
                        child: ElevatedButton(
                          onPressed: !_resetting ? _reset : null,
                          style: ElevatedButton.styleFrom(
                            backgroundColor: sheetExpense.withValues(alpha: 0.2),
                            foregroundColor: sheetExpense,
                            shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(12),
                              side: BorderSide(color: sheetExpense.withValues(alpha: 0.4)),
                            ),
                          ),
                          child: Text(
                              _resetting
                                  ? AppLocalizations.of(context).budResetting
                                  : AppLocalizations.of(context).budResetRollovers,
                              style: GoogleFonts.sora(fontWeight: FontWeight.w600)),
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
