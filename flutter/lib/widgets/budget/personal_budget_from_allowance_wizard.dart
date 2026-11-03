import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../models/budget_models.dart';
import '../../utils/currency.dart';
import '../../services/analytics_service.dart';
import '../../services/budget_service.dart';
import '../../services/categories_service.dart';
import '../../utils/formatters.dart';
import '../app_toast.dart';
import 'sheet_primitives.dart';

Future<Budget?> showPersonalBudgetFromAllowanceWizard(
  BuildContext context, {
  required List<BudgetCategory> userAllowances,
  Budget? familyBudget,
}) {
  return showModalBottomSheet<Budget>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _PersonalBudgetFromAllowanceWizard(
      userAllowances: userAllowances,
      familyBudget: familyBudget,
    ),
  );
}

class _PersonalBudgetFromAllowanceWizard extends StatefulWidget {
  final List<BudgetCategory> userAllowances;
  final Budget? familyBudget;
  const _PersonalBudgetFromAllowanceWizard({
    required this.userAllowances,
    this.familyBudget,
  });

  @override
  State<_PersonalBudgetFromAllowanceWizard> createState() =>
      _PersonalBudgetFromAllowanceWizardState();
}

class _PersonalBudgetFromAllowanceWizardState
    extends State<_PersonalBudgetFromAllowanceWizard> {
  int _step = 0; // 0=Welcome, 1=Categories, 2=Review

  // Category selection
  List<Category>? _personalCategories;
  final Set<int> _selectedCategoryIds = {};
  final Map<int, double> _allocations = {};
  bool _loadingCategories = true;
  bool _saving = false;

  double get _totalAllowance =>
      widget.userAllowances.fold(0.0, (s, c) => s + c.allocatedAmount);

  double get _totalAllocated =>
      _allocations.values.fold(0.0, (s, v) => s + v);

  double get _remaining => _totalAllowance - _totalAllocated;

  @override
  void initState() {
    super.initState();
    _loadCategories();
  }

  Future<void> _loadCategories() async {
    try {
      final cats = await categoriesService.getAll();
      if (mounted) {
        final personal = cats.allCategories.where((c) => !c.isIncome).toList();
        setState(() {
          _personalCategories = personal;
          _loadingCategories = false;
          // Pre-select first 4 categories as suggested defaults
          for (var i = 0; i < personal.length && i < 4; i++) {
            _selectedCategoryIds.add(personal[i].id);
          }
        });
      }
    } catch (_) {
      if (mounted) setState(() => _loadingCategories = false);
    }
  }

  void _autoDistribute() {
    if (_selectedCategoryIds.isEmpty) return;
    final perCategory = _totalAllowance / _selectedCategoryIds.length;
    setState(() {
      for (final id in _selectedCategoryIds) {
        _allocations[id] = double.parse(perCategory.toStringAsFixed(2));
      }
    });
  }

  Future<void> _submit() async {
    setState(() => _saving = true);
    try {
      final categories = _selectedCategoryIds.map((catId) {
        return CreateBudgetCategoryRequest(
          categoryId: catId,
          allocatedAmount: _allocations[catId] ?? 0,
          allowRollover: false,
          isIncome: false,
        );
      }).where((c) => c.allocatedAmount > 0).toList();

      final budget = await budgetService.createBudget(CreateBudgetRequest(
        name: 'My Budget',
        period: widget.familyBudget?.period ?? BudgetPeriod.monthly,
        totalIncome: _totalAllowance,
        categories: categories,
        type: BudgetType.personal,
        parentBudgetId: widget.familyBudget?.id,
        allocatedAmount: _totalAllowance,
      ));

      analytics.track(AnalyticsEvents.budgetCreated, {
        'category_count': categories.length,
        'from_family': widget.familyBudget != null,
      });
      if (mounted) Navigator.of(context).pop(budget);
    } catch (e) {
      setState(() => _saving = false);
      if (mounted) {
        showAppToast(context, 'Failed to create budget: $e',
            type: AppToastType.error);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.9,
      minChildSize: 0.5,
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
                // Step indicator
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 24),
                  child: Row(
                    children: [
                      Text(
                        _step == 0
                            ? AppLocalizations.of(context).budFamilyFunding
                            : _step == 1
                                ? AppLocalizations.of(context).budChooseCategories
                                : AppLocalizations.of(context).budReview,
                        style: GoogleFonts.sora(
                            fontSize: 20, fontWeight: FontWeight.bold, color: sheetTextGhost),
                      ),
                      const Spacer(),
                      // Step dots
                      Row(
                        children: List.generate(3, (i) => Container(
                          width: i == _step ? 20 : 8,
                          height: 8,
                          margin: const EdgeInsets.only(left: 4),
                          decoration: BoxDecoration(
                            color: i <= _step ? sheetAccent : sheetCardBg,
                            borderRadius: BorderRadius.circular(4),
                            border: Border.all(
                              color: i <= _step ? sheetAccent.withValues(alpha: 0.5) : sheetCardBorder,
                            ),
                          ),
                        )),
                      ),
                      const SizedBox(width: 8),
                      IconButton(
                        icon: const Icon(Icons.close, color: sheetTextMuted, size: 20),
                        onPressed: () => Navigator.of(context).pop(),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 8),
                Expanded(
                  child: ListView(
                    controller: scrollController,
                    padding: const EdgeInsets.fromLTRB(24, 0, 24, 32),
                    children: [
                      if (_step == 0) ..._buildWelcomeStep(),
                      if (_step == 1) ..._buildCategoriesStep(),
                      if (_step == 2) ..._buildReviewStep(),
                    ],
                  ),
                ),
                // Navigation buttons
                SafeArea(
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(24, 8, 24, 16),
                    child: Row(
                      children: [
                        if (_step > 0)
                          Expanded(
                            child: OutlinedButton(
                              onPressed: () => setState(() => _step--),
                              style: OutlinedButton.styleFrom(
                                foregroundColor: sheetTextMuted,
                                side: BorderSide(color: sheetCardBorder),
                                padding: const EdgeInsets.symmetric(vertical: 14),
                                shape: RoundedRectangleBorder(
                                    borderRadius: BorderRadius.circular(12)),
                              ),
                              child: Text(AppLocalizations.of(context).budBack, style: GoogleFonts.sora()),
                            ),
                          ),
                        if (_step > 0) const SizedBox(width: 12),
                        Expanded(
                          child: SizedBox(
                            height: 48,
                            child: SheetAccentButton(
                              label: _step == 2
                                  ? (_saving ? 'Creating...' : 'Create Budget')
                                  : 'Continue',
                              onPressed: _step == 2
                                  ? (_totalAllocated > 0 && _totalAllocated <= _totalAllowance && !_saving ? _submit : null)
                                  : (_step == 1 && _selectedCategoryIds.isEmpty ? null : () => setState(() => _step++)),
                            ),
                          ),
                        ),
                      ],
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

  List<Widget> _buildWelcomeStep() {
    return [
      const SizedBox(height: 16),
      // Allowance info card
      Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          gradient: LinearGradient(
            colors: [sheetAccent.withValues(alpha: 0.15), sheetIncome.withValues(alpha: 0.08)],
          ),
          borderRadius: BorderRadius.circular(16),
          border: Border.all(color: sheetAccent.withValues(alpha: 0.3)),
        ),
        child: Column(
          children: [
            const Text('\u{1F4B0}', style: TextStyle(fontSize: 36)),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context)
                  .budFamilyAllocationsAssigned(widget.userAllowances.length),
              style: GoogleFonts.sora(fontSize: 14, color: sheetTextMuted),
            ),
            const SizedBox(height: 4),
            Text(
              formatCurrency(_totalAllowance),
              style: GoogleFonts.firaCode(fontSize: 28, color: sheetIncome, fontWeight: FontWeight.bold),
            ),
            Text(AppLocalizations.of(context).budPerMonth, style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
          ],
        ),
      ),
      const SizedBox(height: 20),
      Text(AppLocalizations.of(context).budYourFamilyAllocations,
          style: GoogleFonts.sora(fontSize: 14, fontWeight: FontWeight.w600, color: sheetTextGhost)),
      const SizedBox(height: 8),
      ...widget.userAllowances.map((a) => Padding(
        padding: const EdgeInsets.only(bottom: 6),
        child: SheetGlassCard(
          child: Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text(a.categoryName, style: GoogleFonts.sora(fontSize: 14, color: sheetTextGhost)),
              Text(formatCurrency(a.allocatedAmount),
                  style: GoogleFonts.firaCode(fontSize: 14, color: sheetIncome, fontWeight: FontWeight.w600)),
            ],
          ),
        ),
      )),
      const SizedBox(height: 16),
      Text(
        AppLocalizations.of(context).budAllowanceWizardIntro,
        style: GoogleFonts.sora(fontSize: 13, color: sheetTextMuted, height: 1.5),
      ),
    ];
  }

  List<Widget> _buildCategoriesStep() {
    if (_loadingCategories) {
      return [
        const SizedBox(height: 40),
        const Center(child: CircularProgressIndicator(color: sheetAccent)),
      ];
    }
    if (_personalCategories == null) {
      return [
        const SizedBox(height: 40),
        Center(child: Text(AppLocalizations.of(context).budFailedToLoadCategories, style: GoogleFonts.sora(color: sheetTextMuted))),
      ];
    }

    return [
      // Summary bar
      SheetGlassCard(
        child: Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            SheetSummaryItem('Funding', formatCurrency(_totalAllowance), sheetIncome),
            SheetSummaryItem('Allocated', formatCurrency(_totalAllocated), sheetAccent),
            SheetSummaryItem('Remaining', formatCurrency(_remaining),
                _remaining >= 0 ? sheetIncome : sheetExpense),
          ],
        ),
      ),
      const SizedBox(height: 8),
      // Auto-distribute button
      Align(
        alignment: Alignment.centerRight,
        child: TextButton.icon(
          icon: const Icon(Icons.auto_fix_high, size: 14, color: sheetAccent),
          label: Text(AppLocalizations.of(context).budAutoDistribute, style: GoogleFonts.sora(fontSize: 12, color: sheetAccent)),
          onPressed: _autoDistribute,
        ),
      ),
      const SizedBox(height: 4),
      // Category list
      ..._personalCategories!.map((cat) {
        final selected = _selectedCategoryIds.contains(cat.id);
        return Padding(
          padding: const EdgeInsets.only(bottom: 6),
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            decoration: BoxDecoration(
              color: selected ? sheetAccent.withValues(alpha: 0.06) : sheetCardBg,
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: selected ? sheetAccent.withValues(alpha: 0.2) : sheetCardBorder),
            ),
            child: Row(
              children: [
                Checkbox(
                  value: selected,
                  onChanged: (v) => setState(() {
                    if (v == true) {
                      _selectedCategoryIds.add(cat.id);
                    } else {
                      _selectedCategoryIds.remove(cat.id);
                      _allocations.remove(cat.id);
                    }
                  }),
                  activeColor: sheetAccent,
                  side: BorderSide(color: sheetTextMuted.withValues(alpha: 0.4)),
                ),
                Expanded(
                  child: Text(cat.name,
                      style: GoogleFonts.sora(
                          fontSize: 13,
                          color: selected ? sheetTextGhost : sheetTextMuted)),
                ),
                if (selected)
                  SizedBox(
                    width: 100,
                    child: TextFormField(
                      initialValue: _allocations[cat.id]?.toStringAsFixed(0) ?? '',
                      keyboardType: const TextInputType.numberWithOptions(decimal: true),
                      style: GoogleFonts.firaCode(fontSize: 13, color: sheetTextGhost),
                      onChanged: (v) => setState(() {
                        final amount = parseFlexibleAmount(v);
                        if (amount != null && amount > 0) {
                          _allocations[cat.id] = amount;
                        } else {
                          _allocations.remove(cat.id);
                        }
                      }),
                      decoration: InputDecoration(
                        hintText: '${primaryCurrencySymbol}0',
                        hintStyle: GoogleFonts.firaCode(fontSize: 13, color: sheetTextMuted.withValues(alpha: 0.3)),
                        isDense: true,
                        contentPadding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
                        border: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(6),
                          borderSide: BorderSide(color: sheetCardBorder),
                        ),
                        enabledBorder: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(6),
                          borderSide: BorderSide(color: sheetCardBorder),
                        ),
                        focusedBorder: OutlineInputBorder(
                          borderRadius: BorderRadius.circular(6),
                          borderSide: const BorderSide(color: sheetAccent),
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
        );
      }),
    ];
  }

  List<Widget> _buildReviewStep() {
    final allocatedCategories = _selectedCategoryIds
        .where((id) => (_allocations[id] ?? 0) > 0)
        .toList();

    return [
      const SizedBox(height: 16),
      SheetGlassCard(
        child: Column(
          children: [
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(AppLocalizations.of(context).budTotalFunding, style: GoogleFonts.sora(fontSize: 13, color: sheetTextMuted)),
                Text(formatCurrency(_totalAllowance),
                    style: GoogleFonts.firaCode(fontSize: 14, color: sheetIncome, fontWeight: FontWeight.w600)),
              ],
            ),
            const SizedBox(height: 8),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(AppLocalizations.of(context).budTotalAllocated, style: GoogleFonts.sora(fontSize: 13, color: sheetTextMuted)),
                Text(formatCurrency(_totalAllocated),
                    style: GoogleFonts.firaCode(fontSize: 14, color: sheetAccent, fontWeight: FontWeight.w600)),
              ],
            ),
            const SizedBox(height: 8),
            const Divider(color: sheetCardBorder),
            const SizedBox(height: 8),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(AppLocalizations.of(context).budUnallocated, style: GoogleFonts.sora(fontSize: 13, color: sheetTextMuted)),
                Text(formatCurrency(_remaining),
                    style: GoogleFonts.firaCode(
                        fontSize: 14,
                        color: _remaining >= 0 ? sheetIncome : sheetExpense,
                        fontWeight: FontWeight.w600)),
              ],
            ),
          ],
        ),
      ),
      const SizedBox(height: 20),
      Text(AppLocalizations.of(context).budCategories,
          style: GoogleFonts.sora(fontSize: 14, fontWeight: FontWeight.w600, color: sheetTextGhost)),
      const SizedBox(height: 8),
      ...allocatedCategories.map((catId) {
        final cat = _personalCategories?.cast<Category?>().firstWhere((c) => c?.id == catId, orElse: () => null);
        if (cat == null) return const SizedBox.shrink();
        return Padding(
          padding: const EdgeInsets.only(bottom: 6),
          child: SheetGlassCard(
            child: Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(cat.name, style: GoogleFonts.sora(fontSize: 13, color: sheetTextGhost)),
                Text(formatCurrency(_allocations[catId] ?? 0),
                    style: GoogleFonts.firaCode(fontSize: 13, color: sheetAccent, fontWeight: FontWeight.w600)),
              ],
            ),
          ),
        );
      }),
      if (_remaining < 0) ...[
        const SizedBox(height: 12),
        Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: sheetExpense.withValues(alpha: 0.1),
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: sheetExpense.withValues(alpha: 0.3)),
          ),
          child: Row(
            children: [
              const Icon(Icons.warning_amber_rounded, color: sheetExpense, size: 18),
              const SizedBox(width: 8),
              Expanded(
                child: Text(AppLocalizations.of(context).budOverAllocatedBy(formatCurrency(_remaining.abs())),
                    style: GoogleFonts.sora(fontSize: 13, color: sheetExpense)),
              ),
            ],
          ),
        ),
      ],
    ];
  }
}
