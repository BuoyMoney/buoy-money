import 'dart:ui';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../models/budget_models.dart';
import '../../models/family_models.dart';
import '../../utils/currency.dart';
import '../../services/budget_service.dart';
import '../../utils/formatters.dart';
import '../app_toast.dart';
import 'sheet_primitives.dart';

/// Shows the cinematic sheet that turns an existing family expense category into
/// a member's personal budget funded by the family (RFC #793). Owner/BudgetAdmin
/// only (gated by the caller). Returns `true` when the personal budget was created.
Future<bool?> showSpawnPersonalBudgetSheet(
  BuildContext context, {
  required BudgetCategory category,
  required List<FamilyMember> familyMembers,
  required int currentUserId,
}) {
  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _SpawnPersonalBudgetSheet(
      category: category,
      familyMembers: familyMembers,
      currentUserId: currentUserId,
    ),
  );
}

class _SpawnPersonalBudgetSheet extends StatefulWidget {
  final BudgetCategory category;
  final List<FamilyMember> familyMembers;
  final int currentUserId;

  const _SpawnPersonalBudgetSheet({
    required this.category,
    required this.familyMembers,
    required this.currentUserId,
  });

  @override
  State<_SpawnPersonalBudgetSheet> createState() =>
      _SpawnPersonalBudgetSheetState();
}

class _SpawnPersonalBudgetSheetState extends State<_SpawnPersonalBudgetSheet> {
  final _nameController = TextEditingController();
  final _amountController = TextEditingController();
  int? _assignedUserId;
  bool _nameEdited = false;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    // Default the recipient to the caller when they're in the family list.
    final hasCaller =
        widget.familyMembers.any((m) => m.userId == widget.currentUserId);
    _assignedUserId = hasCaller
        ? widget.currentUserId
        : (widget.familyMembers.isNotEmpty
            ? widget.familyMembers.first.userId
            : null);
    _amountController.text = widget.category.allocatedAmount.toStringAsFixed(0);
    _nameController.text = _defaultName();
  }

  String _firstNameFor(int? userId) {
    final matches = widget.familyMembers.where((m) => m.userId == userId);
    if (matches.isEmpty) return 'My';
    final m = matches.first;
    return m.firstName ?? m.displayName;
  }

  String _defaultName() => "${_firstNameFor(_assignedUserId)}'s Budget";

  double get _amount => parseFlexibleAmount(_amountController.text.trim()) ?? 0;

  String? get _amountError {
    if (_amountController.text.trim().isEmpty) return null;
    if (_amount <= 0) return 'Amount must be greater than 0';
    return null;
  }

  bool get _isValid =>
      _nameController.text.trim().isNotEmpty &&
      _assignedUserId != null &&
      _amount > 0;

  void _onMemberChanged(int? v) {
    setState(() {
      _assignedUserId = v;
      // Keep the name in sync with the recipient until the user hand-edits it.
      if (!_nameEdited) _nameController.text = _defaultName();
    });
  }

  Future<void> _submit() async {
    if (!_isValid) return;
    setState(() => _saving = true);
    try {
      await budgetService.spawnPersonalBudget(
        widget.category.id,
        assignedUserId: _assignedUserId,
        allocatedAmount: _amount,
        budgetName: _nameController.text.trim(),
      );
      if (!mounted) return;
      showAppToast(
        context,
        '${_nameController.text.trim()} created',
        type: AppToastType.success,
      );
      Navigator.of(context).pop(true);
    } catch (e) {
      if (!mounted) return;
      setState(() => _saving = false);
      showAppToast(context, _spawnError(e), type: AppToastType.error);
    }
  }

  String _spawnError(Object e) {
    if (e is DioException) {
      final data = e.response?.data;
      if (data is Map && data['message'] != null) {
        return data['message'].toString();
      }
    }
    return 'Failed to create personal budget';
  }

  @override
  void dispose() {
    _nameController.dispose();
    _amountController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final amountError = _amountError;
    return DraggableScrollableSheet(
      initialChildSize: 0.72,
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
                      Flexible(
                        child: Text(AppLocalizations.of(context).budMakePersonalBudget,
                            style: GoogleFonts.sora(
                                fontSize: 20,
                                fontWeight: FontWeight.bold,
                                color: sheetTextGhost)),
                      ),
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
                    padding: const EdgeInsets.fromLTRB(24, 0, 24, 16),
                    children: [
                      // Explainer note
                      Container(
                        padding: const EdgeInsets.all(12),
                        decoration: BoxDecoration(
                          color: sheetAccent.withValues(alpha: 0.08),
                          borderRadius: BorderRadius.circular(12),
                          border:
                              Border.all(color: sheetAccent.withValues(alpha: 0.25)),
                        ),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            const Icon(Icons.savings_outlined,
                                color: sheetAccent, size: 18),
                            const SizedBox(width: 8),
                            Expanded(
                              child: Text(
                                AppLocalizations.of(context)
                                    .budSpawnExplainer(widget.category.categoryName),
                                style: GoogleFonts.sora(
                                    fontSize: 12,
                                    color: sheetTextGhost.withValues(alpha: 0.8),
                                    height: 1.4),
                              ),
                            ),
                          ],
                        ),
                      ),
                      const SizedBox(height: 20),

                      // Assign to
                      Text(AppLocalizations.of(context).budFor,
                          style: GoogleFonts.sora(
                              fontSize: 12, color: sheetTextMuted)),
                      const SizedBox(height: 6),
                      SheetDropdown<int>(
                        value: _assignedUserId,
                        hint: 'Select family member',
                        items: widget.familyMembers
                            .map((m) => DropdownMenuItem<int>(
                                  value: m.userId,
                                  child: Text(
                                    m.isCurrentUser
                                        ? AppLocalizations.of(context)
                                            .budMemberYou(m.displayName)
                                        : m.displayName,
                                    style: GoogleFonts.sora(
                                        fontSize: 14, color: sheetTextGhost),
                                  ),
                                ))
                            .toList(),
                        onChanged: _onMemberChanged,
                      ),
                      const SizedBox(height: 16),

                      // Budget name
                      SheetTextField(
                        controller: _nameController,
                        label: 'Budget Name',
                        hint: "e.g., Alex's Budget",
                        onChanged: (_) => setState(() => _nameEdited = true),
                      ),
                      const SizedBox(height: 16),

                      // Allocated amount
                      SheetTextField(
                        controller: _amountController,
                        label: 'Allocation',
                        hint: '0.00',
                        prefix: primaryCurrencySymbol,
                        keyboardType: const TextInputType.numberWithOptions(
                            decimal: true),
                        onChanged: (_) => setState(() {}),
                      ),
                      const SizedBox(height: 6),
                      Text(
                        amountError ??
                            AppLocalizations.of(context)
                                .budDefaultsToCurrentAllocation,
                        style: GoogleFonts.sora(
                          fontSize: 11,
                          color: amountError != null ? sheetExpense : sheetTextMuted,
                        ),
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
                        label: _saving ? 'Creating...' : 'Create Personal Budget',
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
