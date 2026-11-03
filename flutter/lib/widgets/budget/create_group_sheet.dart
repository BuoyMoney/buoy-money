import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../models/budget_models.dart';
import '../../services/budget_service.dart';
import '../app_toast.dart';
import 'sheet_primitives.dart';

Future<CategoryGroup?> showCreateGroupSheet(
  BuildContext context, {
  required int budgetId,
}) {
  return showModalBottomSheet<CategoryGroup>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _CreateGroupSheet(budgetId: budgetId),
  );
}

class _CreateGroupSheet extends StatefulWidget {
  final int budgetId;
  const _CreateGroupSheet({required this.budgetId});

  @override
  State<_CreateGroupSheet> createState() => _CreateGroupSheetState();
}

class _CreateGroupSheetState extends State<_CreateGroupSheet> {
  final _nameController = TextEditingController();
  final _iconController = TextEditingController();
  bool _saving = false;

  bool get _isValid => _nameController.text.trim().isNotEmpty;

  Future<void> _submit() async {
    if (!_isValid) return;
    setState(() => _saving = true);
    try {
      final group = await budgetService.createCategoryGroup(
        widget.budgetId,
        name: _nameController.text.trim(),
        icon: _iconController.text.isNotEmpty ? _iconController.text.trim() : null,
      );
      if (mounted) Navigator.of(context).pop(group);
    } catch (e) {
      setState(() => _saving = false);
      if (mounted) {
        showAppToast(context, 'Failed to create group: $e',
            type: AppToastType.error);
      }
    }
  }

  @override
  void dispose() {
    _nameController.dispose();
    _iconController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.4,
      minChildSize: 0.25,
      maxChildSize: 0.6,
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
                      Text(AppLocalizations.of(context).budNewCategoryGroup,
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
                      SheetTextField(
                        controller: _nameController,
                        label: 'Group Name',
                        hint: 'e.g., Bills & Utilities',
                        onChanged: (_) => setState(() {}),
                      ),
                      const SizedBox(height: 16),
                      SheetTextField(
                        controller: _iconController,
                        label: 'Icon (emoji, optional)',
                        hint: '\u{1F3E0}',
                        onChanged: (_) => setState(() {}),
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
                        label: _saving ? 'Creating...' : 'Create Group',
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
