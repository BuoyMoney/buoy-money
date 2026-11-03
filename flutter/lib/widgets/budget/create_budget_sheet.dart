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

enum _FundMode { none, existing, create }


Future<Budget?> showCreateBudgetSheet(
  BuildContext context, {
  bool hasPersonalBudget = false,
  bool hasFamilyBudget = false,
  bool hasFamily = false,
  // RFC #793 — F2. The family budget (for the "fund from" category picker) and
  // whether the caller can create/link the family allocation (Owner/BudgetAdmin).
  Budget? familyBudget,
  bool canManageFamily = false,
}) {
  return showModalBottomSheet<Budget>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _CreateBudgetSheet(
      hasPersonalBudget: hasPersonalBudget,
      hasFamilyBudget: hasFamilyBudget,
      hasFamily: hasFamily,
      familyBudget: familyBudget,
      canManageFamily: canManageFamily,
    ),
  );
}

class _CreateBudgetSheet extends StatefulWidget {
  final bool hasPersonalBudget;
  final bool hasFamilyBudget;
  final bool hasFamily;
  final Budget? familyBudget;
  final bool canManageFamily;
  const _CreateBudgetSheet({
    this.hasPersonalBudget = false,
    this.hasFamilyBudget = false,
    this.hasFamily = false,
    this.familyBudget,
    this.canManageFamily = false,
  });

  @override
  State<_CreateBudgetSheet> createState() => _CreateBudgetSheetState();
}

class _CreateBudgetSheetState extends State<_CreateBudgetSheet> {
  final _nameController = TextEditingController();
  BudgetPeriod _period = BudgetPeriod.monthly;
  BudgetType _budgetType = BudgetType.personal;
  bool _saving = false;

  // RFC #793 — F2 "fund from" (only for a Personal budget inside a family, Owner/BudgetAdmin).
  // _fundMode: none = standalone personal; existing = link a family category;
  // create = make a new family category to fund it.
  _FundMode _fundMode = _FundMode.none;
  int? _fundFromCategoryId;
  final _newFamilyCategoryNameController = TextEditingController();
  final _familyAllocationController = TextEditingController();

  // categoryId → allocated amount
  final Map<int, double> _allocations = {};
  final Map<int, bool> _rolloverEnabled = {};
  final Map<int, bool> _countUpEnabled = {};

  List<Category>? _categories;
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
    _loadCategories();
  }

  Future<void> _loadCategories() async {
    try {
      final cats = await categoriesService.getAll();
      if (mounted) setState(() { _categories = cats.allCategories; _loadingCategories = false; });
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
        _categories ??= [];
        _categories!.add(created);
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
    final incomeIds = _categories?.where((c) => c.isIncome).map((c) => c.id).toSet() ?? {};
    return _allocations.entries
        .where((e) => !incomeIds.contains(e.key))
        .fold(0.0, (s, e) => s + e.value);
  }

  double get _totalIncome {
    final incomeIds = _categories?.where((c) => c.isIncome).map((c) => c.id).toSet() ?? {};
    return _allocations.entries
        .where((e) => incomeIds.contains(e.key))
        .fold(0.0, (s, e) => s + e.value);
  }

  double get _unallocated => _totalIncome - _totalAllocated;

  bool get _hasExpenseAllocations {
    final incomeIds = _categories?.where((c) => c.isIncome).map((c) => c.id).toSet() ?? {};
    return _allocations.entries.any((e) => !incomeIds.contains(e.key) && e.value > 0);
  }

  // RFC #793 — F2. The "fund from" selector is only meaningful for a Personal
  // budget inside a family whose family budget we could load. The allocation edit
  // itself is Owner/BudgetAdmin only; a plain member still creates a standalone
  // personal budget (the funding option is shown disabled with a note).
  bool get _familyFundContext =>
      _budgetType == BudgetType.personal &&
      widget.hasFamily &&
      widget.familyBudget != null;

  /// Family expense categories eligible to fund a personal budget (not already linked).
  List<BudgetCategory> get _fundableFamilyCategories =>
      widget.familyBudget?.expenseCategories
          .where((c) => !c.isAllowance && !c.isVirtual)
          .toList() ??
      const [];

  double get _familyAllocation =>
      parseFlexibleAmount(_familyAllocationController.text.trim()) ?? 0;

  bool get _fundSelectionValid {
    if (!_familyFundContext || !widget.canManageFamily) return true;
    switch (_fundMode) {
      case _FundMode.none:
        return true;
      case _FundMode.existing:
        return _fundFromCategoryId != null && _familyAllocation > 0;
      case _FundMode.create:
        return _newFamilyCategoryNameController.text.trim().isNotEmpty &&
            _familyAllocation > 0;
    }
  }

  bool get _isValid =>
      _nameController.text.trim().isNotEmpty &&
      _allocations.isNotEmpty &&
      _hasExpenseAllocations &&
      _fundSelectionValid;

  Future<void> _submit() async {
    if (!_isValid) return;
    setState(() => _saving = true);
    try {
      final categories = _allocations.entries.map((e) {
        final cat = _categories?.firstWhere((c) => c.id == e.key);
        return CreateBudgetCategoryRequest(
          categoryId: e.key,
          allocatedAmount: e.value,
          allowRollover: _rolloverEnabled[e.key] ?? false,
          isIncome: cat?.isIncome ?? false,
          isCountUpEnabled: _countUpEnabled[e.key] ?? false,
        );
      }).toList();

      // Resolve the family funding link (F2), if selected.
      final bool linkFamily = _familyFundContext &&
          widget.canManageFamily &&
          _fundMode != _FundMode.none;
      final budget = await budgetService.createBudget(CreateBudgetRequest(
        name: _nameController.text.trim(),
        period: _period,
        totalIncome: _totalIncome,
        categories: categories,
        type: _budgetType,
        parentBudgetId: linkFamily ? widget.familyBudget!.id : null,
        allocatedAmount: linkFamily ? _familyAllocation : null,
        fundFromCategoryId:
            linkFamily && _fundMode == _FundMode.existing ? _fundFromCategoryId : null,
        newFamilyCategoryName: linkFamily && _fundMode == _FundMode.create
            ? _newFamilyCategoryNameController.text.trim()
            : null,
      ));

      // Enable count-up for flagged categories after creation
      for (final catReq in categories) {
        if (catReq.isCountUpEnabled) {
          // Find the created budget category ID by matching categoryId
          final created = budget.categories.firstWhere(
            (bc) => bc.categoryId == catReq.categoryId,
            orElse: () => budget.categories.first,
          );
          await budgetService.enableCountUp(created.id, true);
        }
      }

      analytics.track(AnalyticsEvents.budgetCreated, {
        'category_count': categories.length,
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
  void dispose() {
    _nameController.dispose();
    _newCatNameController.dispose();
    _newFamilyCategoryNameController.dispose();
    _familyAllocationController.dispose();
    super.dispose();
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
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 24),
                  child: Row(
                    children: [
                      Text(AppLocalizations.of(context).budCreateBudget,
                          style: GoogleFonts.sora(
                              fontSize: 20,
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
                const SizedBox(height: 8),
                Expanded(
                  child: ListView(
                    controller: scrollController,
                    padding: const EdgeInsets.fromLTRB(24, 0, 24, 32),
                    children: [
                      // Budget Name
                      SheetTextField(
                        controller: _nameController,
                        label: 'Budget Name',
                        hint: 'e.g., Monthly Budget',
                        onChanged: (_) => setState(() {}),
                      ),
                      const SizedBox(height: 16),

                      // Period selector
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

                      // Budget type
                      Text(AppLocalizations.of(context).budType, style: GoogleFonts.sora(fontSize: 12, color: sheetTextMuted)),
                      const SizedBox(height: 8),
                      Wrap(
                        spacing: 8,
                        children: [
                          SheetChoiceChip(
                            label: 'Personal',
                            selected: _budgetType == BudgetType.personal,
                            onSelected: () => setState(() => _budgetType = BudgetType.personal),
                          ),
                          if (widget.hasFamily)
                            SheetChoiceChip(
                              label: 'Family',
                              selected: _budgetType == BudgetType.family,
                              onSelected: () => setState(() => _budgetType = BudgetType.family),
                            ),
                        ],
                      ),

                      // Override warning
                      if ((_budgetType == BudgetType.personal && widget.hasPersonalBudget) ||
                          (_budgetType == BudgetType.family && widget.hasFamilyBudget))
                        Container(
                          margin: const EdgeInsets.only(top: 12),
                          padding: const EdgeInsets.all(12),
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
                                  _budgetType == BudgetType.personal
                                      ? AppLocalizations.of(context)
                                          .budReplacePersonalWarning
                                      : AppLocalizations.of(context)
                                          .budReplaceFamilyWarning,
                                  style: GoogleFonts.sora(fontSize: 13, color: sheetPending),
                                ),
                              ),
                            ],
                          ),
                        ),

                      // RFC #793 — F2 "Fund from" selector.
                      if (_familyFundContext) ..._buildFundFromSection(),

                      const SizedBox(height: 20),

                      // Summary row (auto-calculated from income categories)
                      if (_totalIncome > 0 || _totalAllocated > 0) ...[
                        SheetGlassCard(
                          child: Row(
                            mainAxisAlignment: MainAxisAlignment.spaceBetween,
                            children: [
                              SheetSummaryItem('Income', formatCurrency(_totalIncome), sheetIncome),
                              SheetSummaryItem('Expenses', formatCurrency(_totalAllocated), sheetExpense),
                              SheetSummaryItem(
                                'Remaining',
                                formatCurrency(_unallocated),
                                _unallocated >= 0 ? sheetIncome : sheetExpense,
                              ),
                            ],
                          ),
                        ),
                        const SizedBox(height: 20),
                      ],

                      // Category allocation
                      Text(AppLocalizations.of(context).budAllocateCategories,
                          style: GoogleFonts.sora(
                              fontSize: 14,
                              fontWeight: FontWeight.w600,
                              color: sheetTextGhost)),
                      const SizedBox(height: 8),

                      if (_loadingCategories)
                        const Center(child: CircularProgressIndicator(color: sheetAccent))
                      else if (_categories != null)
                        ..._buildCategoryInputs(),
                    ],
                  ),
                ),

                // Submit button
                SafeArea(
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(24, 8, 24, 16),
                    child: SizedBox(
                      width: double.infinity,
                      height: 48,
                      child: SheetAccentButton(
                        label: _saving ? 'Creating...' : 'Create Budget',
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

  // RFC #793 — F2. Lets a Personal budget in a family be funded by a family
  // allocation: link an existing family category or create a new one. Only
  // Owner/BudgetAdmin may set the allocation; a member sees it disabled with a note.
  List<Widget> _buildFundFromSection() {
    final canManage = widget.canManageFamily;
    final fundable = _fundableFamilyCategories;
    return [
      const SizedBox(height: 20),
      Text(AppLocalizations.of(context).budFundFromFamily,
          style: GoogleFonts.sora(
              fontSize: 14, fontWeight: FontWeight.w600, color: sheetTextGhost)),
      const SizedBox(height: 4),
      Text(
        canManage
            ? AppLocalizations.of(context).budFundFromFamilyManagerHint
            : AppLocalizations.of(context).budFundFromFamilyMemberHint,
        style: GoogleFonts.sora(fontSize: 11, color: sheetTextMuted, height: 1.4),
      ),
      const SizedBox(height: 10),
      if (canManage) ...[
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            SheetChoiceChip(
              label: 'No family funding',
              selected: _fundMode == _FundMode.none,
              onSelected: () => setState(() => _fundMode = _FundMode.none),
            ),
            if (fundable.isNotEmpty)
              SheetChoiceChip(
                label: 'Existing category',
                selected: _fundMode == _FundMode.existing,
                onSelected: () =>
                    setState(() => _fundMode = _FundMode.existing),
              ),
            SheetChoiceChip(
              label: '+ New family category',
              selected: _fundMode == _FundMode.create,
              onSelected: () => setState(() => _fundMode = _FundMode.create),
            ),
          ],
        ),
        if (_fundMode == _FundMode.existing) ...[
          const SizedBox(height: 12),
          SheetDropdown<int>(
            value: _fundFromCategoryId,
            hint: 'Choose a family category',
            items: fundable
                .map((c) => DropdownMenuItem<int>(
                      value: c.id,
                      child: Text(
                        '${c.categoryName} · ${formatCurrency(c.allocatedAmount)}',
                        style: GoogleFonts.sora(
                            fontSize: 14, color: sheetTextGhost),
                      ),
                    ))
                .toList(),
            onChanged: (v) => setState(() {
              _fundFromCategoryId = v;
              final match = fundable.where((c) => c.id == v);
              if (match.isNotEmpty) {
                _familyAllocationController.text =
                    match.first.allocatedAmount.toStringAsFixed(0);
              }
            }),
          ),
        ],
        if (_fundMode == _FundMode.create) ...[
          const SizedBox(height: 12),
          SheetTextField(
            controller: _newFamilyCategoryNameController,
            label: 'New family category name',
            hint: "e.g., Alex's Allocation",
            onChanged: (_) => setState(() {}),
          ),
        ],
        if (_fundMode != _FundMode.none) ...[
          const SizedBox(height: 12),
          SheetTextField(
            controller: _familyAllocationController,
            label: 'Family allocation',
            hint: '0.00',
            prefix: primaryCurrencySymbol,
            keyboardType:
                const TextInputType.numberWithOptions(decimal: true),
            onChanged: (_) => setState(() {}),
          ),
        ],
      ],
    ];
  }

  List<Widget> _buildCategoryInputs() {
    final expense = _categories!.where((c) => !c.isIncome).toList();
    final income = _categories!.where((c) => c.isIncome).toList();

    return [
      if (income.isNotEmpty) ...[
        Padding(
          padding: const EdgeInsets.only(top: 8, bottom: 4),
          child: Text(AppLocalizations.of(context).budIncome, style: GoogleFonts.sora(fontSize: 11, color: sheetIncome, fontWeight: FontWeight.w600)),
        ),
        ...income.map((cat) => SheetCategoryAllocationRow(
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
      ...expense.map((cat) => SheetCategoryAllocationRow(
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
    ];
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
}
