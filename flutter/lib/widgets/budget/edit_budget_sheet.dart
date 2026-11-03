import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../models/budget_models.dart';
import '../../services/analytics_service.dart';
import '../../services/budget_service.dart';
import '../../services/categories_service.dart';
import '../../utils/formatters.dart';
import '../app_toast.dart';
import 'sheet_primitives.dart';

Future<Budget?> showEditBudgetSheet(BuildContext context, Budget budget) {
  return showModalBottomSheet<Budget>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _EditBudgetSheet(budget: budget),
  );
}

class _EditBudgetSheet extends StatefulWidget {
  final Budget budget;
  const _EditBudgetSheet({required this.budget});

  @override
  State<_EditBudgetSheet> createState() => _EditBudgetSheetState();
}

class _EditBudgetSheetState extends State<_EditBudgetSheet> {
  late TextEditingController _nameController;
  late BudgetPeriod _period;
  bool _saving = false;

  // category allocations from existing budget
  final Map<int, double> _allocations = {};
  final Map<int, bool> _rolloverEnabled = {};
  final Map<int, bool> _countUpEnabled = {};

  List<Category>? _allCategories;
  bool _loadingCategories = true;

  // Inline category creation
  bool _showCreateForm = false;
  bool _creatingCategory = false;
  final _newCatNameController = TextEditingController();
  String? _newCatIcon;
  String _newCatColor = '#3B82F6';
  bool _newCatIsIncome = false;

  @override
  void initState() {
    super.initState();
    final b = widget.budget;
    _nameController = TextEditingController(text: b.name);
    _period = b.period;
    for (final c in b.categories) {
      _allocations[c.categoryId] = c.allocatedAmount;
      _rolloverEnabled[c.categoryId] = c.allowRollover;
      _countUpEnabled[c.categoryId] = c.isCountUpEnabled;
    }
    _loadCategories();
  }

  Future<void> _loadCategories() async {
    try {
      final cats = await categoriesService.getAll();
      if (mounted) setState(() { _allCategories = cats.allCategories; _loadingCategories = false; });
    } catch (_) {
      if (mounted) setState(() => _loadingCategories = false);
    }
  }

  Future<void> _createCategory() async {
    final name = _newCatNameController.text.trim();
    if (name.isEmpty) return;
    setState(() => _creatingCategory = true);
    try {
      final created = await categoriesService.create(
        name: name,
        color: _newCatColor,
        icon: _newCatIcon,
        isIncome: _newCatIsIncome,
      );
      if (!mounted) return;
      setState(() {
        _allCategories ??= [];
        _allCategories!.add(created);
        _showCreateForm = false;
        _creatingCategory = false;
        _newCatNameController.clear();
        _newCatIcon = null;
        _newCatColor = '#3B82F6';
        _newCatIsIncome = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _creatingCategory = false);
      showAppToast(context, 'Failed to create category',
          type: AppToastType.error);
    }
  }

  double get _totalAllocated {
    final incomeIds = _allCategories?.where((c) => c.isIncome).map((c) => c.id).toSet()
        ?? widget.budget.incomeCategories.map((c) => c.categoryId).toSet();
    return _allocations.entries
        .where((e) => !incomeIds.contains(e.key))
        .fold(0.0, (s, e) => s + e.value);
  }
  double get _totalIncome {
    final incomeIds = _allCategories?.where((c) => c.isIncome).map((c) => c.id).toSet()
        ?? widget.budget.incomeCategories.map((c) => c.categoryId).toSet();
    return _allocations.entries
        .where((e) => incomeIds.contains(e.key))
        .fold(0.0, (s, e) => s + e.value);
  }

  bool get _hasExpenseAllocations {
    final incomeIds = _allCategories?.where((c) => c.isIncome).map((c) => c.id).toSet()
        ?? widget.budget.incomeCategories.map((c) => c.categoryId).toSet();
    return _allocations.entries.any((e) => !incomeIds.contains(e.key) && e.value > 0);
  }

  bool get _isValid =>
      _nameController.text.trim().isNotEmpty &&
      _allocations.isNotEmpty &&
      _hasExpenseAllocations;

  Future<void> _save() async {
    if (!_isValid) return;
    setState(() => _saving = true);
    try {
      final categories = _allocations.entries.map((e) {
        final cat = _allCategories?.firstWhere((c) => c.id == e.key,
            orElse: () => _allCategories!.first);
        return CreateBudgetCategoryRequest(
          categoryId: e.key,
          allocatedAmount: e.value,
          allowRollover: _rolloverEnabled[e.key] ?? false,
          isIncome: cat?.isIncome ?? false,
          isCountUpEnabled: _countUpEnabled[e.key] ?? false,
        );
      }).toList();

      final updated = await budgetService.updateBudget(
        widget.budget.id,
        CreateBudgetRequest(
          name: _nameController.text.trim(),
          period: _period,
          totalIncome: _totalIncome,
          categories: categories,
          type: widget.budget.type,
        ),
      );
      analytics.track(AnalyticsEvents.budgetUpdated, {
        'category_count': categories.length,
      });
      if (mounted) Navigator.of(context).pop(updated);
    } catch (e) {
      setState(() => _saving = false);
      if (mounted) {
        showAppToast(context, 'Failed to update: $e',
            type: AppToastType.error);
      }
    }
  }

  Widget _buildCreateCategoryButton() {
    if (!_showCreateForm) {
      return GestureDetector(
        onTap: () => setState(() => _showCreateForm = true),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: sheetAccent.withValues(alpha: 0.06),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(color: sheetAccent.withValues(alpha: 0.15)),
          ),
          child: Row(
            children: [
              Icon(Icons.add_circle_outline, size: 18, color: sheetAccent),
              const SizedBox(width: 8),
              Text(AppLocalizations.of(context).budCreateNewCategory,
                  style: GoogleFonts.sora(
                      fontSize: 14, color: sheetAccent, fontWeight: FontWeight.w500)),
            ],
          ),
        ),
      );
    }

    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: sheetCardBg,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: sheetAccent.withValues(alpha: 0.25)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.add_circle, size: 16, color: sheetAccent),
              const SizedBox(width: 6),
              Text(AppLocalizations.of(context).budNewCategory,
                  style: GoogleFonts.sora(
                      fontSize: 13, color: sheetAccent, fontWeight: FontWeight.w600)),
              const Spacer(),
              GestureDetector(
                onTap: () => setState(() {
                  _showCreateForm = false;
                  _newCatNameController.clear();
                  _newCatIcon = null;
                }),
                child: const Icon(Icons.close, size: 16, color: sheetTextMuted),
              ),
            ],
          ),
          const SizedBox(height: 10),
          SheetTextField(
            controller: _newCatNameController,
            hint: 'Category name',
            onChanged: (_) {},
          ),
          const SizedBox(height: 10),
          Text(AppLocalizations.of(context).budIcon, style: GoogleFonts.sora(fontSize: 11, color: sheetTextMuted)),
          const SizedBox(height: 6),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: sheetIconPalette.map((emoji) {
              final isSelected = emoji == _newCatIcon;
              return GestureDetector(
                onTap: () => setState(() => _newCatIcon = isSelected ? null : emoji),
                child: Container(
                  width: 32,
                  height: 32,
                  decoration: BoxDecoration(
                    color: isSelected
                        ? sheetAccent.withValues(alpha: 0.2)
                        : Colors.white.withValues(alpha: 0.04),
                    borderRadius: BorderRadius.circular(8),
                    border: Border.all(
                      color: isSelected ? sheetAccent : sheetCardBorder,
                      width: isSelected ? 2 : 1,
                    ),
                  ),
                  alignment: Alignment.center,
                  child: Text(emoji, style: const TextStyle(fontSize: 16)),
                ),
              );
            }).toList(),
          ),
          const SizedBox(height: 10),
          Text(AppLocalizations.of(context).budColor, style: GoogleFonts.sora(fontSize: 11, color: sheetTextMuted)),
          const SizedBox(height: 6),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: sheetColorPalette.map((hex) {
              final isSelected = hex == _newCatColor;
              return GestureDetector(
                onTap: () => setState(() => _newCatColor = hex),
                child: Container(
                  width: 24,
                  height: 24,
                  decoration: BoxDecoration(
                    color: parseCategoryColor(hex),
                    shape: BoxShape.circle,
                    border: isSelected
                        ? Border.all(color: sheetTextGhost, width: 2)
                        : null,
                  ),
                  child: isSelected
                      ? const Icon(Icons.check, size: 14, color: Colors.white)
                      : null,
                ),
              );
            }).toList(),
          ),
          const SizedBox(height: 10),
          GestureDetector(
            onTap: () => setState(() => _newCatIsIncome = !_newCatIsIncome),
            child: Row(
              children: [
                Container(
                  width: 18,
                  height: 18,
                  decoration: BoxDecoration(
                    color: _newCatIsIncome
                        ? sheetIncome.withValues(alpha: 0.2)
                        : Colors.transparent,
                    borderRadius: BorderRadius.circular(4),
                    border: Border.all(
                      color: _newCatIsIncome ? sheetIncome : sheetTextMuted,
                    ),
                  ),
                  child: _newCatIsIncome
                      ? const Icon(Icons.check, size: 14, color: sheetIncome)
                      : null,
                ),
                const SizedBox(width: 8),
                Text(AppLocalizations.of(context).budIncomeCategory,
                    style: GoogleFonts.sora(fontSize: 13, color: sheetTextGhost)),
              ],
            ),
          ),
          const SizedBox(height: 12),
          SizedBox(
            width: double.infinity,
            child: SheetAccentButton(
              onPressed: _creatingCategory ? null : _createCategory,
              label: _creatingCategory ? 'Creating...' : 'Create Category',
            ),
          ),
        ],
      ),
    );
  }

  @override
  void dispose() {
    _nameController.dispose();
    _newCatNameController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.85,
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
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 24),
                  child: Row(
                    children: [
                      Text(AppLocalizations.of(context).budEditBudget,
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
                    padding: const EdgeInsets.fromLTRB(24, 8, 24, 32),
                    children: [
                      SheetTextField(
                        controller: _nameController,
                        label: 'Budget Name',
                        onChanged: (_) => setState(() {}),
                      ),
                      const SizedBox(height: 16),
                      if (widget.budget.type == BudgetType.family) ...[
                        Text(AppLocalizations.of(context).budPeriod, style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
                        const SizedBox(height: 8),
                        Wrap(
                          spacing: 8,
                          children: BudgetPeriod.values.map((p) => SheetChoiceChip(
                            label: p.displayName,
                            selected: _period == p,
                            onSelected: () => setState(() => _period = p),
                          )).toList(),
                        ),
                        const SizedBox(height: 16),
                      ],
                      // Summary (auto-calculated from income categories)
                      if (_totalIncome > 0 || _totalAllocated > 0) ...[
                        SheetGlassCard(
                          child: Row(
                            mainAxisAlignment: MainAxisAlignment.spaceBetween,
                            children: [
                              SheetSummaryItem('Income', formatCurrency(_totalIncome), sheetIncome),
                              SheetSummaryItem('Expenses', formatCurrency(_totalAllocated), sheetExpense),
                              SheetSummaryItem('Remaining',
                                  formatCurrency(_totalIncome - _totalAllocated),
                                  _totalIncome - _totalAllocated >= 0 ? sheetIncome : sheetExpense),
                            ],
                          ),
                        ),
                        const SizedBox(height: 20),
                      ],

                      Text(AppLocalizations.of(context).budCategories,
                          style: GoogleFonts.sora(
                              fontSize: 14, fontWeight: FontWeight.w600, color: sheetTextGhost)),
                      const SizedBox(height: 8),

                      if (_loadingCategories)
                        const Center(child: CircularProgressIndicator(color: sheetAccent))
                      else if (_allCategories != null) ...[
                        if (_allCategories!.any((c) => c.isIncome)) ...[
                          Padding(
                            padding: const EdgeInsets.only(top: 8, bottom: 4),
                            child: Text(AppLocalizations.of(context).budIncome, style: GoogleFonts.sora(fontSize: 11, color: sheetIncome, fontWeight: FontWeight.w600)),
                          ),
                          ..._allCategories!.where((c) => c.isIncome).map((cat) => SheetCategoryAllocationRow(
                            category: cat,
                            amount: _allocations[cat.id],
                            rollover: false,
                            countUp: false,
                            hideToggles: true,
                            onAmountChanged: (v) => setState(() {
                              if (v != null && v > 0) {
                                _allocations[cat.id] = v;
                              } else {
                                _allocations.remove(cat.id);
                              }
                            }),
                            onRolloverChanged: (_) {},
                            onCountUpChanged: (_) {},
                          )),
                        ],
                        Padding(
                          padding: const EdgeInsets.only(top: 12, bottom: 4),
                          child: Text(AppLocalizations.of(context).budExpenses, style: GoogleFonts.sora(fontSize: 11, color: sheetExpense, fontWeight: FontWeight.w600)),
                        ),
                        ..._allCategories!.where((c) => !c.isIncome).map((cat) => SheetCategoryAllocationRow(
                          category: cat,
                          amount: _allocations[cat.id],
                          rollover: _rolloverEnabled[cat.id] ?? false,
                          countUp: _countUpEnabled[cat.id] ?? false,
                          onAmountChanged: (v) => setState(() {
                            if (v != null && v > 0) {
                              _allocations[cat.id] = v;
                            } else {
                              _allocations.remove(cat.id);
                            }
                          }),
                          onRolloverChanged: (v) => setState(() => _rolloverEnabled[cat.id] = v),
                          onCountUpChanged: (v) => setState(() {
                            _countUpEnabled[cat.id] = v;
                            if (v) _rolloverEnabled[cat.id] = true;
                          }),
                        )),
                        const SizedBox(height: 12),
                        _buildCreateCategoryButton(),
                      ],
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
                        label: _saving ? 'Saving...' : 'Save Budget',
                        onPressed: _isValid && !_saving ? _save : null,
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
