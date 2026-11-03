import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/covering_transfer_models.dart';
import '../models/transaction_models.dart';
import '../models/savings_models.dart';
import '../models/dashboard_models.dart';
import '../models/budget_models.dart';
import '../utils/currency.dart';
import '../services/api_client.dart';
import '../services/accounts_service.dart';
import '../services/budget_service.dart';
import '../services/categories_service.dart';
import '../services/categorization_rules_service.dart';
import '../services/savings_service.dart';
import '../services/transactions_service.dart';
import '../utils/api_error.dart';
import '../utils/formatters.dart';
import '../utils/savings_earmark.dart';
import 'always_categorize_offer.dart';
import 'app_toast.dart';
import 'receipt_split_card.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _pending = Color(0xFFFBBF24);
const _sheetBg = Color(0xFF0C0C18);

// ═════════════════════════════════════════════════════════════
//  TRANSACTION DETAIL BOTTOM SHEET
// ═════════════════════════════════════════════════════════════

/// What a detail-sheet session did to the transaction, so the caller can update
/// its list in place instead of reloading the whole thing.
///
/// The sheet can change a transaction's category/savings goal, name, notes and
/// tags, or delete it — but it has no way to change the **amount or date**. That
/// is what makes splicing safe: an edit here can never move a row's position in
/// a date-ordered list, so there is nothing to re-sort. Splitting is the one
/// exception, because it creates child rows the caller doesn't have; that sets
/// [restructured] and the caller does need a reload.
class TransactionDetailResult {
  /// Id of the transaction the sheet was opened on. Always set, including for
  /// deletes, so the caller can find the row without holding onto the original.
  final int transactionId;

  /// The transaction as it now stands, or null when it was [deleted].
  final Transaction? transaction;

  /// The transaction was deleted; remove the row.
  final bool deleted;

  /// The edit changed which rows exist (a split creates children), so the caller
  /// has to reload rather than splice.
  final bool restructured;

  const TransactionDetailResult({
    required this.transactionId,
    this.transaction,
    this.deleted = false,
    this.restructured = false,
  });
}

/// Shows a full-featured transaction detail sheet.
///
/// [onChanged] fires at most once, when the sheet closes after an edit. It is a
/// callback rather than a return value because the sheet can also be dismissed
/// by dragging or tapping the barrier, and those paths complete the route with
/// null — a returned result would be silently lost exactly when the user made an
/// edit and then swiped the sheet away.
Future<void> showTransactionDetailSheet(
  BuildContext context,
  Transaction transaction, {
  ValueChanged<TransactionDetailResult>? onChanged,
}) {
  return showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _TransactionDetailSheet(
      transaction: transaction,
      onChanged: onChanged,
    ),
  );
}

class _TransactionDetailSheet extends StatefulWidget {
  final Transaction transaction;
  final ValueChanged<TransactionDetailResult>? onChanged;

  const _TransactionDetailSheet({
    required this.transaction,
    this.onChanged,
  });

  @override
  State<_TransactionDetailSheet> createState() =>
      _TransactionDetailSheetState();
}

class _TransactionDetailSheetState extends State<_TransactionDetailSheet> {
  late Transaction _tx;
  bool _editingNotes = false;
  bool _savingNotes = false;
  bool _deleting = false;
  bool _renaming = false;
  final _notesController = TextEditingController();
  bool _modified = false;
  bool _deleted = false;
  bool _restructured = false;

  // Semantic tags
  late List<String> _tags;
  final _tagController = TextEditingController();
  bool _savingTags = false;

  @override
  void initState() {
    super.initState();
    _tx = widget.transaction;
    _notesController.text = _tx.userNotes ?? '';
    _tags = _parseTags(_tx.semanticTags);
  }

  static List<String> _parseTags(String? raw) {
    if (raw == null || raw.isEmpty) return [];
    return raw.split(',').map((t) => t.trim()).where((t) => t.isNotEmpty).toList();
  }

  @override
  void dispose() {
    _notesController.dispose();
    _tagController.dispose();
    super.dispose();
  }

  Future<void> _saveNotes() async {
    setState(() => _savingNotes = true);
    try {
      final updated = await transactionsService.updateTransactionNotes(
        _tx.id,
        _notesController.text.isEmpty ? null : _notesController.text,
      );
      setState(() {
        _tx = updated;
        _editingNotes = false;
        _savingNotes = false;
        _modified = true;
      });
    } catch (_) {
      setState(() => _savingNotes = false);
      if (mounted) {
        showAppToast(context, 'Failed to save notes', type: AppToastType.error);
      }
    }
  }

  Future<void> _deleteTransaction() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => _GlassDialog(
        title: AppLocalizations.of(ctx).txnDeleteTransactionTitle,
        message: AppLocalizations.of(ctx).txnDeleteConfirmBody(
            AppLocalizations.of(ctx).txnDeleteNamedPrompt(_tx.displayTitle)),
        confirmLabel: AppLocalizations.of(ctx).txnDelete,
        confirmColor: _expense,
      ),
    );
    if (confirmed != true) return;

    setState(() => _deleting = true);
    try {
      await transactionsService.deleteTransaction(_tx.id);
      _modified = true;
      _deleted = true;
      if (mounted) Navigator.of(context).pop();
    } catch (_) {
      setState(() => _deleting = false);
      if (mounted) {
        showAppToast(context, 'Failed to delete transaction',
            type: AppToastType.error);
      }
    }
  }

  /// A draw from a goal that lives in a real account is either still in flight
  /// (the money has not left that account yet) or settled by a transfer. Both
  /// states are shown here so the user can see why the goal and the bank
  /// disagree, and tie the purchase to the transfer once it lands.
  Widget _buildCoveringTransferRow() {
    final l10n = AppLocalizations.of(context);
    final covered = _tx.coveringTransfer;
    final settled = _tx.isCoveredByTransfer;
    final tone = settled ? _income : _pending;
    final text = settled
        ? (covered != null
            ? l10n.txnCoveredByTransfer(
                covered.accountName, formatMediumDate(covered.date))
            : l10n.txnCoveredByTransferShort)
        : l10n.txnWaitingForTransfer(
            _tx.savingsGoalName ?? l10n.txnSavingsGoalFallback);
    return Padding(
      padding: const EdgeInsets.only(top: 8, left: 4, right: 4),
      child: Row(
        children: [
          Icon(settled ? Icons.swap_horiz_rounded : Icons.hourglass_top_rounded,
              size: 14, color: tone),
          const SizedBox(width: 6),
          Expanded(
            child: Text(text,
                style: GoogleFonts.sora(fontSize: 11, color: tone)),
          ),
          GestureDetector(
            onTap: settled ? _unlinkCoveringTransfer : _openCoveringTransferSheet,
            behavior: HitTestBehavior.opaque,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
              child: Text(
                settled ? l10n.txnUnlinkTransfer : l10n.txnLinkTransfer,
                style: GoogleFonts.sora(
                    fontSize: 11, fontWeight: FontWeight.w600, color: _accent),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _openCoveringTransferSheet() async {
    final updated = await showCoveringTransferSheet(context, _tx);
    if (updated != null && mounted) {
      setState(() {
        _tx = updated;
        _modified = true;
      });
    }
  }

  Future<void> _unlinkCoveringTransfer() async {
    try {
      final updated = await transactionsService.unlinkCoveringTransfer(_tx.id);
      if (!mounted) return;
      setState(() {
        _tx = updated;
        _modified = true;
      });
    } catch (e) {
      if (mounted) {
        showAppToast(context,
            apiErrorMessage(e, fallback: AppLocalizations.of(context).txnCoverLinkFailed),
            type: AppToastType.error);
      }
    }
  }

  void _openEditCategory() async {
    final result = await showEditCategorySheet(context, _tx);
    if (result != null && mounted) {
      setState(() {
        _tx = result;
        _tags = _parseTags(_tx.semanticTags);
        _modified = true;
      });
    }
  }

  void _addTag() {
    final tag = _tagController.text.trim().toLowerCase().replaceAll(RegExp(r'[^a-z0-9_]'), '_');
    if (tag.isEmpty || _tags.contains(tag)) {
      _tagController.clear();
      return;
    }
    setState(() => _tags.add(tag));
    _tagController.clear();
    _saveTags();
  }

  void _removeTag(String tag) {
    setState(() => _tags.remove(tag));
    _saveTags();
  }

  Future<void> _saveTags() async {
    setState(() => _savingTags = true);
    try {
      final tagsString = _tags.isEmpty ? null : _tags.join(', ');
      final updated = await transactionsService.updateTransactionCategory(
        _tx.id,
        categoryId: _tx.categoryId,
        semanticTags: tagsString ?? '',
      );
      if (mounted) {
        setState(() {
          _tx = updated;
          _savingTags = false;
          _modified = true;
        });
      }
    } catch (_) {
      setState(() => _savingTags = false);
      if (mounted) {
        showAppToast(context, 'Failed to update tags',
            type: AppToastType.error);
      }
    }
  }

  /// A forwarded receipt's split was applied from the card: same effect as
  /// splitting by hand (new child rows), so the caller reloads.
  Future<void> _onReceiptApplied() async {
    setState(() {
      _modified = true;
      _restructured = true;
    });
    try {
      final updated = await transactionsService.getTransaction(_tx.id);
      if (mounted) setState(() => _tx = updated);
    } catch (_) {}
  }

  void _openSplitTransaction() async {
    final result = await showSplitTransactionSheet(context, _tx);
    if (result == true && mounted) {
      setState(() {
        _modified = true;
        // A split creates child rows the caller's list has never seen, so this
        // is the one edit here it cannot resolve by splicing a single row.
        _restructured = true;
      });
      // Refresh the transaction
      try {
        final updated = await transactionsService.getTransaction(_tx.id);
        setState(() => _tx = updated);
      } catch (_) {}
    }
  }

  @override
  Widget build(BuildContext context) {
    final isIncome = _tx.isIncome;
    final amountColor = isIncome ? _income : _expense;
    final prefix = isIncome ? '+' : '-';

    return PopScope(
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop || !_modified) return;
        // Single notification channel for every close path — the delete's
        // programmatic pop, a drag-dismiss, and a barrier tap all land here, so
        // the caller is told exactly once regardless of how the sheet went away.
        widget.onChanged?.call(TransactionDetailResult(
          transactionId: _tx.id,
          transaction: _deleted ? null : _tx,
          deleted: _deleted,
          restructured: _restructured,
        ));
      },
      child: DraggableScrollableSheet(
      initialChildSize: 0.85,
      minChildSize: 0.5,
      maxChildSize: 0.95,
      builder: (_, scrollController) => ClipRRect(
        borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
          child: Container(
            decoration: BoxDecoration(
              color: _sheetBg.withValues(alpha: 0.95),
              borderRadius:
                  const BorderRadius.vertical(top: Radius.circular(24)),
              border: Border.all(color: _cardBorder),
            ),
            child: Column(
              children: [
                // Drag handle
                Padding(
                  padding: const EdgeInsets.only(top: 12, bottom: 8),
                  child: Container(
                    width: 40,
                    height: 4,
                    decoration: BoxDecoration(
                      color: _textMuted.withValues(alpha: 0.3),
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                ),
                // Content
                Expanded(
                  child: ListView(
                    controller: scrollController,
                    padding: const EdgeInsets.fromLTRB(24, 8, 24, 32),
                    children: [
                      _buildAmountHeader(prefix, amountColor),
                      const SizedBox(height: 24),
                      _buildInfoSection(),
                      const SizedBox(height: 20),
                      // Single destination section: a transaction goes to EITHER
                      // a budget category OR a savings goal. The picker opened
                      // from here handles both (and reversing between them).
                      _buildDestinationSection(),
                      if (!_tx.isIncome && _tx.parentTransactionId == null)
                        ReceiptSplitCard(
                          transaction: _tx,
                          onApplied: _onReceiptApplied,
                        ),
                      const SizedBox(height: 12),
                      _buildTagsSection(),
                      const SizedBox(height: 20),
                      _buildNotesSection(),
                      if (_tx.isSplit) ...[
                        const SizedBox(height: 20),
                        _buildSplitsSection(),
                      ],
                      if (_tx.merchantName != null) ...[
                        const SizedBox(height: 20),
                        _buildMerchantDetails(),
                      ],
                      const SizedBox(height: 28),
                      _buildActions(),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
      ),
    );
  }

  /// Rename the transaction. Clearing the field restores whatever the bank called it, so the
  /// rename is always reversible — the bank's own text is stored separately and never
  /// overwritten.
  Future<void> _openRenameDialog() async {
    final controller = TextEditingController(text: _tx.nickname ?? '');
    final bankName = _tx.hasBankName ? _tx.bankName : null;

    final result = await showDialog<String?>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: _sheetBg,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: _cardBorder),
        ),
        title: Text(AppLocalizations.of(dialogContext).txnRenameTransaction,
            style: GoogleFonts.sora(color: _textGhost, fontSize: 17)),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            TextField(
              controller: controller,
              autofocus: true,
              maxLength: 200,
              style: GoogleFonts.sora(color: _textGhost),
              decoration: InputDecoration(
                hintText: bankName ?? 'Name this transaction',
                hintStyle: GoogleFonts.sora(color: _textMuted),
                counterStyle: GoogleFonts.sora(color: _textMuted, fontSize: 11),
                enabledBorder: UnderlineInputBorder(
                    borderSide: BorderSide(color: _cardBorder)),
                focusedBorder: const UnderlineInputBorder(
                    borderSide: BorderSide(color: _accent)),
              ),
              onSubmitted: (v) => Navigator.pop(dialogContext, v),
            ),
            const SizedBox(height: 8),
            Text(
              bankName != null
                  ? AppLocalizations.of(dialogContext)
                      .txnBankCallsThis(bankName)
                  : AppLocalizations.of(dialogContext).txnBankSentNoName,
              style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: Text(AppLocalizations.of(dialogContext).txnCancel,
                style: GoogleFonts.sora(color: _textMuted)),
          ),
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, controller.text),
            child: Text(AppLocalizations.of(dialogContext).txnSave,
                style: GoogleFonts.sora(color: _accent)),
          ),
        ],
      ),
    );

    if (result == null || !mounted) return;

    setState(() => _renaming = true);
    try {
      final updated =
          await transactionsService.updateTransactionNickname(_tx.id, result);
      if (mounted) {
        setState(() {
          _tx = updated;
          _modified = true;
        });
      }
    } catch (_) {
      if (mounted) {
        showAppToast(context, "Couldn't rename that transaction. Please try again.",
            type: AppToastType.error);
      }
    } finally {
      if (mounted) setState(() => _renaming = false);
    }
  }

  Widget _buildAmountHeader(String prefix, Color amountColor) {
    return Column(
      children: [
        Text(
          '$prefix${formatCurrency(_tx.amount.abs())}',
          style: GoogleFonts.firaCode(
            fontSize: 36,
            fontWeight: FontWeight.w700,
            color: amountColor,
          ),
        ),
        const SizedBox(height: 4),
        // Tap to rename. Banks leave plenty of transactions with no usable name at all
        // (Plaid sends no merchant for checks and transfers), so this is the only way to
        // make those rows readable.
        InkWell(
          onTap: _renaming ? null : _openRenameDialog,
          borderRadius: BorderRadius.circular(8),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Flexible(
                  child: Text(
                    _tx.displayTitle,
                    style: GoogleFonts.sora(
                      fontSize: 16,
                      color: _textGhost,
                      fontWeight: FontWeight.w500,
                      fontStyle: _tx.hasName ? FontStyle.normal : FontStyle.italic,
                    ),
                    textAlign: TextAlign.center,
                  ),
                ),
                const SizedBox(width: 6),
                _renaming
                    ? const SizedBox(
                        width: 12,
                        height: 12,
                        child: CircularProgressIndicator(strokeWidth: 1.5, color: _textMuted),
                      )
                    : const Icon(Icons.edit_outlined, size: 14, color: _textMuted),
              ],
            ),
          ),
        ),
        const SizedBox(height: 4),
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Text(
              formatMediumDate(_tx.date),
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
            ),
            if (_tx.isPending) ...[
              const SizedBox(width: 8),
              _Badge(label: 'Pending', color: _pending),
            ],
            if (_tx.isRecurring) ...[
              const SizedBox(width: 8),
              _Badge(label: 'Recurring', color: _accent),
            ],
          ],
        ),
      ],
    );
  }

  Widget _buildInfoSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _InfoRow(label: 'Account', value: _tx.accountName),
          _InfoRow(label: 'Description', value: _tx.description),
          if (_tx.paymentChannel != null)
            _InfoRow(label: 'Channel', value: _tx.paymentChannel!),
          if (_tx.location != null)
            _InfoRow(label: 'Location', value: _tx.location!),
          _InfoRow(
            label: 'Source',
            value: _tx.categorySource ?? 'Unknown',
            trailing: _buildSourceBadge(),
          ),
        ],
      ),
    );
  }

  Widget? _buildSourceBadge() {
    final source = _tx.categorySource?.toLowerCase();
    if (source == 'user') {
      return const _Badge(label: 'Verified', color: _income);
    }
    if (source == 'ai' && _tx.aiConfidence != null) {
      final conf = _tx.aiConfidence!;
      final color = conf >= 0.8
          ? _income
          : conf >= 0.5
              ? _pending
              : _expense;
      return _Badge(
        label: 'AI ${(conf * 100).toInt()}%',
        color: color,
      );
    }
    return null;
  }

  /// Single "destination" section — a transaction goes to EITHER a budget
  /// category OR a savings goal (never both). "Change" opens the unified picker
  /// which handles switching between the two and reversing an earmark.
  Widget _buildDestinationSection() {
    final isEarmarked = _tx.savingsGoalId != null;
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(isEarmarked ? Icons.savings_outlined : Icons.local_offer_outlined,
                  size: 14, color: _textMuted),
              const SizedBox(width: 6),
              Text(
                  isEarmarked
                      ? AppLocalizations.of(context).txnSavingsGoalFallback
                      : AppLocalizations.of(context).txnCategory,
                  style: GoogleFonts.sora(
                      fontSize: 12, color: _textMuted, fontWeight: FontWeight.w500)),
              const Spacer(),
              GestureDetector(
                onTap: _openEditCategory,
                child: Row(
                  children: [
                    Icon(Icons.edit_outlined, size: 14, color: _accent),
                    const SizedBox(width: 4),
                    Text(AppLocalizations.of(context).txnChange,
                        style: GoogleFonts.sora(
                            fontSize: 12, color: _accent, fontWeight: FontWeight.w500)),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          if (isEarmarked) ...[
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              decoration: BoxDecoration(
                color: _income.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(color: _income.withValues(alpha: 0.15)),
              ),
              child: Row(
                children: [
                  const Icon(Icons.check_circle, size: 16, color: _income),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      _tx.savingsGoalName ??
                          AppLocalizations.of(context)
                              .txnSavingsGoalNumber(_tx.savingsGoalId!),
                      style: GoogleFonts.sora(fontSize: 14, color: _textGhost),
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 8),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 4),
              child: Text(
                _tx.isIncome
                    ? AppLocalizations.of(context).txnDepositedIntoGoal
                    : AppLocalizations.of(context).txnPaidFromGoal,
                style: GoogleFonts.sora(
                    fontSize: 11, color: _textMuted, fontStyle: FontStyle.italic),
              ),
            ),
            if (_tx.isDrawFromLinkedGoal) _buildCoveringTransferRow(),
          ] else ...[
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
                border: Border.all(color: _accent.withValues(alpha: 0.15)),
              ),
              child: Row(
                children: [
                  Container(
                    width: 8,
                    height: 8,
                    decoration: BoxDecoration(
                      color: _parseCategoryColor(_tx.categoryColor),
                      shape: BoxShape.circle,
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      _tx.categoryName,
                      style: GoogleFonts.sora(fontSize: 14, color: _textGhost),
                    ),
                  ),
                  if (_tx.personalCategoryName != null)
                    Text(
                      _tx.personalCategoryName!,
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                    ),
                ],
              ),
            ),
            if (_tx.isAICategorized) ...[
              const SizedBox(height: 8),
              Row(
                children: [
                  const Icon(Icons.auto_awesome, size: 14, color: _accent),
                  const SizedBox(width: 4),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context).txnAiSuggested(
                          _tx.aiSuggestedCategoryName ??
                              AppLocalizations.of(context).txnUnknown),
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                    ),
                  ),
                ],
              ),
            ],
          ],
        ],
      ),
    );
  }

  Widget _buildTagsSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.label_outline, size: 14, color: _accent),
              const SizedBox(width: 6),
              Text(AppLocalizations.of(context).txnSemanticTags,
                  style: GoogleFonts.sora(
                      fontSize: 12, color: _textMuted, fontWeight: FontWeight.w500)),
              const Spacer(),
              if (_savingTags)
                const SizedBox(
                  width: 12,
                  height: 12,
                  child: CircularProgressIndicator(strokeWidth: 1.5, color: _accent),
                ),
            ],
          ),
          const SizedBox(height: 8),
          if (_tags.isNotEmpty)
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: _tags.map((tag) => Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: 0.08),
                  borderRadius: BorderRadius.circular(6),
                  border: Border.all(color: _accent.withValues(alpha: 0.15)),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(tag,
                        style: GoogleFonts.firaCode(fontSize: 11, color: _textGhost)),
                    const SizedBox(width: 4),
                    GestureDetector(
                      onTap: () => _removeTag(tag),
                      child: Icon(Icons.close, size: 12, color: _textMuted.withValues(alpha: 0.7)),
                    ),
                  ],
                ),
              )).toList(),
            ),
          if (_tags.isEmpty)
            Text(AppLocalizations.of(context).txnNoTags,
                style: GoogleFonts.sora(
                    fontSize: 12,
                    color: _textMuted.withValues(alpha: 0.5),
                    fontStyle: FontStyle.italic)),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: Container(
                  height: 34,
                  decoration: BoxDecoration(
                    color: Colors.white.withValues(alpha: 0.03),
                    borderRadius: BorderRadius.circular(8),
                    border: Border.all(color: _cardBorder),
                  ),
                  child: TextField(
                    controller: _tagController,
                    style: GoogleFonts.firaCode(fontSize: 12, color: _textGhost),
                    onSubmitted: (_) => _addTag(),
                    decoration: InputDecoration(
                      hintText: 'Add tag...',
                      hintStyle: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                      border: InputBorder.none,
                      contentPadding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
                      isDense: true,
                    ),
                  ),
                ),
              ),
              const SizedBox(width: 6),
              GestureDetector(
                onTap: _addTag,
                child: Container(
                  width: 34,
                  height: 34,
                  decoration: BoxDecoration(
                    color: _accent.withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: const Icon(Icons.add, size: 16, color: _accent),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _buildNotesSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text(AppLocalizations.of(context).txnNotes,
                  style: GoogleFonts.sora(
                      fontSize: 12, color: _textMuted, fontWeight: FontWeight.w500)),
              const Spacer(),
              if (!_editingNotes)
                GestureDetector(
                  onTap: () => setState(() => _editingNotes = true),
                  child: Icon(Icons.edit_outlined, size: 14, color: _accent),
                ),
            ],
          ),
          const SizedBox(height: 8),
          if (_editingNotes) ...[
            TextField(
              controller: _notesController,
              maxLines: 3,
              style: GoogleFonts.sora(fontSize: 14, color: _textGhost),
              decoration: InputDecoration(
                hintText: 'Add a note...',
                hintStyle: GoogleFonts.sora(fontSize: 14, color: _textMuted),
                filled: true,
                fillColor: Colors.white.withValues(alpha: 0.03),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(8),
                  borderSide: BorderSide(color: _cardBorder),
                ),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(8),
                  borderSide: BorderSide(color: _cardBorder),
                ),
                focusedBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(8),
                  borderSide: BorderSide(color: _accent.withValues(alpha: 0.5)),
                ),
              ),
            ),
            const SizedBox(height: 8),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                TextButton(
                  onPressed: () {
                    _notesController.text = _tx.userNotes ?? '';
                    setState(() => _editingNotes = false);
                  },
                  child: Text(AppLocalizations.of(context).txnCancel,
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                ),
                const SizedBox(width: 8),
                _SmallAccentButton(
                  onPressed: _savingNotes ? null : _saveNotes,
                  label: _savingNotes ? 'Saving...' : 'Save',
                ),
              ],
            ),
          ] else ...[
            Text(
              _tx.userNotes?.isNotEmpty == true
                  ? _tx.userNotes!
                  : AppLocalizations.of(context).txnNoNotes,
              style: GoogleFonts.sora(
                fontSize: 14,
                color: _tx.userNotes?.isNotEmpty == true
                    ? _textGhost
                    : _textMuted.withValues(alpha: 0.5),
                fontStyle: _tx.userNotes?.isNotEmpty == true
                    ? FontStyle.normal
                    : FontStyle.italic,
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildSplitsSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.call_split, size: 14, color: _accent),
              const SizedBox(width: 6),
              Text(AppLocalizations.of(context).txnSplitDetails,
                  style: GoogleFonts.sora(
                      fontSize: 12, color: _textMuted, fontWeight: FontWeight.w500)),
            ],
          ),
          const SizedBox(height: 8),
          ...(_tx.splits ?? []).map((s) => Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Row(
                  children: [
                    Container(
                      width: 8,
                      height: 8,
                      decoration: BoxDecoration(
                        color: _parseCategoryColor(s.categoryColor),
                        shape: BoxShape.circle,
                      ),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(s.categoryName,
                              style: GoogleFonts.sora(
                                  fontSize: 13, color: _textGhost)),
                          if (s.notes != null && s.notes!.isNotEmpty)
                            Text(s.notes!,
                                maxLines: 2,
                                overflow: TextOverflow.ellipsis,
                                style: GoogleFonts.sora(
                                    fontSize: 11, color: _textMuted)),
                        ],
                      ),
                    ),
                    Text(
                      formatCurrency(s.amount),
                      style: GoogleFonts.firaCode(
                          fontSize: 13, color: _textMuted),
                    ),
                  ],
                ),
              )),
        ],
      ),
    );
  }

  Widget _buildMerchantDetails() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(AppLocalizations.of(context).txnMerchant,
              style: GoogleFonts.sora(
                  fontSize: 12, color: _textMuted, fontWeight: FontWeight.w500)),
          const SizedBox(height: 8),
          if (_tx.merchantLogoUrl != null) ...[
            ClipRRect(
              borderRadius: BorderRadius.circular(8),
              child: Image.network(
                _tx.merchantLogoUrl!,
                width: 32,
                height: 32,
                errorBuilder: (_, __, ___) => const SizedBox.shrink(),
              ),
            ),
            const SizedBox(height: 8),
          ],
          _InfoRow(label: 'Name', value: _tx.merchantName!),
          if (_tx.merchantWebsite != null)
            _InfoRow(label: 'Website', value: _tx.merchantWebsite!),
          if (_tx.plaidCategoryDetailed != null)
            _InfoRow(label: 'Plaid Category', value: _tx.plaidCategoryDetailed!),
        ],
      ),
    );
  }

  Widget _buildActions() {
    return Column(
      children: [
        Row(
          children: [
            Expanded(
              child: _ActionButton(
                icon: Icons.edit_outlined,
                label: 'Edit Category',
                onTap: _openEditCategory,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _ActionButton(
                icon: Icons.call_split,
                label: _tx.isSplit ? 'Edit Split' : 'Split',
                onTap: _openSplitTransaction,
              ),
            ),
          ],
        ),
        const SizedBox(height: 10),
        SizedBox(
          width: double.infinity,
          child: _ActionButton(
            icon: Icons.delete_outline,
            label: _deleting ? 'Deleting...' : 'Delete Transaction',
            color: _expense,
            onTap: _deleting ? null : _deleteTransaction,
          ),
        ),
      ],
    );
  }
}

// ═════════════════════════════════════════════════════════════
//  EDIT CATEGORY BOTTOM SHEET
// ═════════════════════════════════════════════════════════════

/// Shows category selection sheet.
/// Returns updated Transaction if category was changed, null if cancelled.
Future<Transaction?> showEditCategorySheet(
  BuildContext context,
  Transaction transaction,
) {
  return showModalBottomSheet<Transaction>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _EditCategorySheet(transaction: transaction),
  );
}

class _EditCategorySheet extends StatefulWidget {
  final Transaction transaction;
  const _EditCategorySheet({required this.transaction});

  @override
  State<_EditCategorySheet> createState() => _EditCategorySheetState();
}

class _EditCategorySheetState extends State<_EditCategorySheet> {
  List<Category> _allCategories = [];
  List<SavingsGoal> _savingsGoals = [];
  bool _loading = true;
  bool _saving = false;
  int? _selectedCategoryId;
  int? _selectedSavingsGoalId;
  String _searchTerm = '';

  // RFC #793 — F4. Personal sub-categories from the member's family-funded
  // personal budget(s). When present, the member can privately re-bucket a
  // claimed transaction; the picked id is sent as personalCategoryId.
  List<BudgetCategory> _personalSubcategories = [];
  int? _selectedPersonalCategoryId;

  // Create new category inline form
  bool _showCreateForm = false;
  bool _creatingCategory = false;
  final _newCatNameController = TextEditingController();
  String _newCatColor = '#3B82F6';
  bool _newCatIsIncome = false;

  static const _colorPalette = [
    '#3B82F6', '#8B5CF6', '#EC4899', '#EF4444',
    '#F97316', '#EAB308', '#22C55E', '#14B8A6',
    '#06B6D4', '#6366F1', '#A855F7', '#F43F5E',
    '#10B981', '#0EA5E9', '#84CC16', '#F59E0B',
  ];

  @override
  void initState() {
    super.initState();
    // Pre-select the current destination: an earmarked goal takes precedence
    // over the category, and the two are mutually exclusive.
    if (widget.transaction.savingsGoalId != null) {
      _selectedSavingsGoalId = widget.transaction.savingsGoalId;
    } else {
      _selectedCategoryId = widget.transaction.categoryId;
    }
    _selectedPersonalCategoryId = widget.transaction.personalCategoryId;
    _loadCategories();
    _loadSavingsGoals();
    _loadPersonalSubcategories();
  }

  /// RFC #793 — F4. Load the expense categories of the user's family-funded
  /// personal budget(s); these are the private sub-categories a member can
  /// assign to a claimed transaction. Non-fatal: the picker is simply hidden.
  Future<void> _loadPersonalSubcategories() async {
    try {
      final budgets = await budgetService.getBudgets();
      final subs = <BudgetCategory>[];
      for (final b in budgets.where((b) => b.isFamilyFunded)) {
        subs.addAll(b.categories.where((c) => !c.isIncome && !c.isVirtual));
      }
      if (!mounted) return;
      setState(() => _personalSubcategories = subs);
    } catch (_) {
      // Non-fatal — no personal sub-category picker is shown.
    }
  }

  @override
  void dispose() {
    _newCatNameController.dispose();
    super.dispose();
  }

  Future<void> _loadCategories() async {
    try {
      final response = await categoriesService.getAll();
      if (!mounted) return;
      setState(() {
        _allCategories = response.allCategories;
        _loading = false;
      });
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _loadSavingsGoals() async {
    // Splits can't be earmarked to a goal, and neither can a still-pending hold
    // (the backend rejects both) — skip loading entirely.
    if (!canEarmarkToSavings(widget.transaction)) return;
    try {
      final goals = await savingsService.getGoals();
      if (!mounted) return;
      setState(() {
        _savingsGoals = goals.where((g) => g.canContribute).toList();
      });
    } catch (_) {
      // Non-fatal: the Savings Goals group is simply hidden.
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
        isIncome: _newCatIsIncome,
      );
      if (!mounted) return;
      setState(() {
        _allCategories.insert(0, created);
        _selectedCategoryId = created.id;
        _selectedSavingsGoalId = null;
        _showCreateForm = false;
        _creatingCategory = false;
        _newCatNameController.clear();
        _newCatColor = '#3B82F6';
        _newCatIsIncome = false;
      });
    } catch (_) {
      setState(() => _creatingCategory = false);
      if (mounted) {
        showAppToast(context, 'Failed to create category',
            type: AppToastType.error);
      }
    }
  }

  Future<void> _save() async {
    final tx = widget.transaction;

    // ── Savings goal selected → earmark the transaction ──
    if (_selectedSavingsGoalId != null) {
      // Already earmarked to this same goal — nothing to do.
      if (_selectedSavingsGoalId == tx.savingsGoalId) {
        Navigator.of(context).pop();
        return;
      }
      final goal =
          _savingsGoals.firstWhere((g) => g.id == _selectedSavingsGoalId);
      final confirmed = await _confirmEarmark(goal);
      if (confirmed != true || !mounted) return;

      setState(() => _saving = true);
      try {
        // Backend handles re-linking automatically (reverses any prior goal).
        final updated = await transactionsService.markSavingsWithdrawal(
          tx.id,
          _selectedSavingsGoalId!,
        );
        if (mounted) Navigator.of(context).pop(updated);
      } catch (e) {
        setState(() => _saving = false);
        if (mounted) {
          // Show the backend's own explanation (e.g. "This transaction is still
          // pending…") — a fixed string here left the user with no idea why.
          showAppToast(
            context,
            apiErrorMessage(e, fallback: 'Failed to earmark to savings goal'),
            type: AppToastType.error,
          );
        }
      }
      return;
    }

    // ── Normal category selected ──
    // Nothing to do only if the category is unchanged AND the user had already confirmed it
    // (and it isn't earmarked). A low-confidence transaction already *displays* a category —
    // the AI auto-applies its guess while leaving IsUserCategorized false — so picking that
    // same category here is the user confirming the guess, not a no-op. Treating it as one
    // dropped the confirmation on the floor and the review queue kept asking.
    final personalChanged =
        _selectedPersonalCategoryId != tx.personalCategoryId;
    final alreadyConfirmed = _selectedCategoryId == tx.categoryId &&
        tx.isUserCategorized &&
        !tx.needsReview;
    if (alreadyConfirmed && tx.savingsGoalId == null && !personalChanged) {
      Navigator.of(context).pop();
      return;
    }
    setState(() => _saving = true);
    try {
      // The backend auto-reverses any existing earmark before setting the
      // category, so this also covers "move back into the budget".
      final updated = await transactionsService.updateTransactionCategory(
        tx.id,
        categoryId: _selectedCategoryId,
        // RFC #793 — F4: private personal sub-category (member re-bucketing).
        personalCategoryId: _selectedPersonalCategoryId,
      );
      if (mounted) await _maybeOfferAlwaysRule(tx, updated);
      if (mounted) Navigator.of(context).pop(updated);
    } catch (e) {
      setState(() => _saving = false);
      if (mounted) {
        showAppToast(
          context,
          apiErrorMessage(e, fallback: 'Failed to update category'),
          type: AppToastType.error,
        );
      }
    }
  }

  /// One tap to turn a correction into a rule. Rules run before Plaid and before
  /// AI on every new transaction, and a merchant with a rule never needs review
  /// again; the learned-habit shortcut would get there after a few repeats, this
  /// gets there now. Asked only on a real correction (a different category than
  /// the one shown), only for named merchants, and never again for a merchant
  /// the user answered "just this one" for.
  Future<void> _maybeOfferAlwaysRule(Transaction tx, Transaction updated) async {
    final categoryId = _selectedCategoryId;
    final merchant = tx.merchantName?.trim() ?? '';
    if (categoryId == null || merchant.isEmpty) return;
    if (categoryId == tx.categoryId) return;
    if (!await categorizationRulesService.shouldOfferRule(merchant)) return;
    if (!mounted) return;

    var categoryName = updated.categoryName;
    for (final c in _allCategories) {
      if (c.id == categoryId) {
        categoryName = c.name;
        break;
      }
    }

    final l10n = AppLocalizations.of(context);
    final always = await showAlwaysCategorizeOffer(
      context,
      merchant: merchant,
      category: categoryName,
    );
    if (!mounted) return;
    if (always != true) {
      await categorizationRulesService.markOfferDeclined(merchant);
      return;
    }
    try {
      await categorizationRulesService.createRuleFromCategorization(
        transactionId: tx.id,
        categoryId: categoryId,
      );
      if (mounted) {
        showAppToast(context, l10n.txnRuleSaved(merchant, categoryName),
            type: AppToastType.success);
      }
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          apiErrorMessage(e, fallback: l10n.txnRuleSaveFailed),
          type: AppToastType.error,
        );
      }
    }
  }

  /// Direction-aware confirmation before earmarking to a savings goal.
  /// Income deposits into the goal; expense withdraws from it.
  Future<bool?> _confirmEarmark(SavingsGoal goal) {
    final tx = widget.transaction;
    final amount = formatCurrency(tx.amount.abs());
    final String title;
    final String message;
    if (tx.isIncome) {
      title = AppLocalizations.of(context)
          .txnEarmarkDepositTitle(amount, goal.name);
      message =
          AppLocalizations.of(context).txnEarmarkDepositBody(goal.name);
    } else {
      title =
          AppLocalizations.of(context).txnEarmarkPayTitle(amount, goal.name);
      message = AppLocalizations.of(context)
          .txnEarmarkPayBody(tx.categoryName, goal.name);
    }
    return showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: _sheetBg,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: _cardBorder),
        ),
        title: Text(
          title,
          style: GoogleFonts.sora(
              fontSize: 16, color: _textGhost, fontWeight: FontWeight.w600),
        ),
        content: Text(
          message,
          style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.4),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(AppLocalizations.of(ctx).txnCancel,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(
                tx.isIncome
                    ? AppLocalizations.of(ctx).txnDeposit
                    : AppLocalizations.of(ctx).txnPay,
                style: GoogleFonts.sora(
                    fontSize: 13,
                    color: _accent,
                    fontWeight: FontWeight.w600)),
          ),
        ],
      ),
    );
  }

  List<Category> get _filteredCategories {
    if (_searchTerm.isEmpty) return _allCategories;
    final lower = _searchTerm.toLowerCase();
    return _allCategories.where((c) => c.name.toLowerCase().contains(lower)).toList();
  }

  // Group categories by type
  Map<String, List<Category>> get _groupedCategories {
    final cats = _filteredCategories;
    final groups = <String, List<Category>>{};
    for (final c in cats) {
      final group = c.scope == 'personal'
          ? '📁 My Categories'
          : c.scope == 'family'
              ? '👨‍👩‍👧 Family'
              : c.isIncome
                  ? '💰 Income'
                  : c.id >= 10 && c.id <= 19
                      ? '🏠 Fixed Expenses'
                      : c.id >= 20 && c.id <= 29
                          ? '🛒 Variable Expenses'
                          : c.id >= 30 && c.id <= 39
                              ? '💳 Transfers'
                              : '📋 Other';
      groups.putIfAbsent(group, () => []).add(c);
    }
    return groups;
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.75,
      minChildSize: 0.4,
      maxChildSize: 0.9,
      builder: (_, scrollController) => ClipRRect(
        borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
          child: Container(
            decoration: BoxDecoration(
              color: _sheetBg.withValues(alpha: 0.95),
              borderRadius:
                  const BorderRadius.vertical(top: Radius.circular(24)),
              border: Border.all(color: _cardBorder),
            ),
            child: Column(
              children: [
                _DragHandle(),
                // Header
                Padding(
                  padding: const EdgeInsets.fromLTRB(24, 0, 24, 12),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(AppLocalizations.of(context).txnEditCategory,
                          style: GoogleFonts.sora(
                              fontSize: 20,
                              fontWeight: FontWeight.bold,
                              color: _textGhost)),
                      const SizedBox(height: 4),
                      Text(
                        '${widget.transaction.displayTitle} — ${formatCurrency(widget.transaction.amount.abs())}',
                        style: GoogleFonts.sora(
                            fontSize: 13, color: _textMuted),
                      ),
                      const SizedBox(height: 12),
                      // Search
                      _SheetSearchBar(
                        hint: 'Search categories...',
                        onChanged: (v) => setState(() => _searchTerm = v),
                      ),
                    ],
                  ),
                ),
                // Categories list
                Expanded(
                  child: _loading
                      ? const Center(
                          child:
                              CircularProgressIndicator(color: _accent))
                      : ListView(
                          controller: scrollController,
                          padding: const EdgeInsets.symmetric(horizontal: 24),
                          children: [
                            // Create New Category button / inline form
                            _buildCreateCategorySection(),
                            ..._buildCategoryGroups(),
                            // RFC #793 — F4: personal sub-category picker.
                            ..._buildPersonalSubcategorySection(),
                          ],
                        ),
                ),
                // Save button
                Padding(
                  padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
                  child: SizedBox(
                    width: double.infinity,
                    child: _AccentButton(
                      onPressed: _saving ? null : _save,
                      label: _saving ? 'Saving...' : 'Save Category',
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

  Widget _buildCreateCategorySection() {
    if (!_showCreateForm) {
      return GestureDetector(
        onTap: () => setState(() => _showCreateForm = true),
        child: Container(
          margin: const EdgeInsets.only(bottom: 8),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: _accent.withValues(alpha: 0.06),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(color: _accent.withValues(alpha: 0.15)),
          ),
          child: Row(
            children: [
              Icon(Icons.add_circle_outline, size: 18, color: _accent),
              const SizedBox(width: 8),
              Text(AppLocalizations.of(context).txnCreateNewCategory,
                  style: GoogleFonts.sora(
                      fontSize: 14, color: _accent, fontWeight: FontWeight.w500)),
            ],
          ),
        ),
      );
    }

    return Container(
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _accent.withValues(alpha: 0.25)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.add_circle, size: 16, color: _accent),
              const SizedBox(width: 6),
              Text(AppLocalizations.of(context).txnNewCategory,
                  style: GoogleFonts.sora(
                      fontSize: 13, color: _accent, fontWeight: FontWeight.w600)),
              const Spacer(),
              GestureDetector(
                onTap: () => setState(() {
                  _showCreateForm = false;
                  _newCatNameController.clear();
                }),
                child: Icon(Icons.close, size: 16, color: _textMuted),
              ),
            ],
          ),
          const SizedBox(height: 10),
          // Name field
          _SheetTextField(
            controller: _newCatNameController,
            hint: 'Category name',
          ),
          const SizedBox(height: 10),
          // Color palette
          Text(AppLocalizations.of(context).txnColor,
              style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
          const SizedBox(height: 6),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: _colorPalette.map((hex) {
              final isSelected = hex == _newCatColor;
              return GestureDetector(
                onTap: () => setState(() => _newCatColor = hex),
                child: Container(
                  width: 24,
                  height: 24,
                  decoration: BoxDecoration(
                    color: _parseCategoryColor(hex),
                    shape: BoxShape.circle,
                    border: isSelected
                        ? Border.all(color: _textGhost, width: 2)
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
          // Income toggle
          GestureDetector(
            onTap: () => setState(() => _newCatIsIncome = !_newCatIsIncome),
            child: Row(
              children: [
                Container(
                  width: 18,
                  height: 18,
                  decoration: BoxDecoration(
                    color: _newCatIsIncome
                        ? _income.withValues(alpha: 0.2)
                        : Colors.transparent,
                    borderRadius: BorderRadius.circular(4),
                    border: Border.all(
                      color: _newCatIsIncome ? _income : _textMuted,
                    ),
                  ),
                  child: _newCatIsIncome
                      ? const Icon(Icons.check, size: 14, color: _income)
                      : null,
                ),
                const SizedBox(width: 8),
                Text(AppLocalizations.of(context).txnIncomeCategory,
                    style: GoogleFonts.sora(fontSize: 13, color: _textGhost)),
              ],
            ),
          ),
          const SizedBox(height: 12),
          // Create button
          SizedBox(
            width: double.infinity,
            child: _AccentButton(
              onPressed: _creatingCategory ? null : _createCategory,
              label: _creatingCategory ? 'Creating...' : 'Create Category',
            ),
          ),
        ],
      ),
    );
  }

  /// RFC #793 — F4. A private picker (member only) that re-buckets a claimed
  /// transaction into a sub-category of their family-funded personal budget.
  /// Sent as personalCategoryId; leaves the shared family category unchanged.
  List<Widget> _buildPersonalSubcategorySection() {
    if (_personalSubcategories.isEmpty) return const [];
    return [
      const SizedBox(height: 20),
      Row(
        children: [
          const Icon(Icons.lock_outline, size: 14, color: _accent),
          const SizedBox(width: 6),
          Text(AppLocalizations.of(context).txnPersonalSubcategory,
              style: GoogleFonts.sora(
                  fontSize: 12,
                  color: _textGhost,
                  fontWeight: FontWeight.w600)),
        ],
      ),
      const SizedBox(height: 4),
      Text(
        AppLocalizations.of(context).txnPersonalSubcategoryHint,
        style: GoogleFonts.sora(fontSize: 11, color: _textMuted, height: 1.4),
      ),
      const SizedBox(height: 10),
      Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [
          _PersonalSubChip(
            label: 'None',
            selected: _selectedPersonalCategoryId == null,
            onTap: () => setState(() => _selectedPersonalCategoryId = null),
          ),
          ..._personalSubcategories.map((c) => _PersonalSubChip(
                label: c.categoryName,
                selected: _selectedPersonalCategoryId == c.categoryId,
                onTap: () => setState(
                    () => _selectedPersonalCategoryId = c.categoryId),
              )),
        ],
      ),
      const SizedBox(height: 8),
    ];
  }

  List<Widget> _buildCategoryGroups() {
    final groups = _groupedCategories;
    final widgets = <Widget>[];

    for (final entry in groups.entries) {
      widgets.add(Padding(
        padding: const EdgeInsets.only(top: 12, bottom: 6),
        child: Text(entry.key,
            style: GoogleFonts.sora(
                fontSize: 12,
                color: _textMuted,
                fontWeight: FontWeight.w600,
                letterSpacing: 0.5)),
      ));
      for (final cat in entry.value) {
        final isSelected = cat.id == _selectedCategoryId;
        widgets.add(GestureDetector(
          onTap: () => setState(() {
            // Selecting a category is mutually exclusive with a savings goal.
            _selectedCategoryId = cat.id;
            _selectedSavingsGoalId = null;
          }),
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 150),
            margin: const EdgeInsets.only(bottom: 2),
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            decoration: BoxDecoration(
              color: isSelected
                  ? _accent.withValues(alpha: 0.12)
                  : Colors.transparent,
              borderRadius: BorderRadius.circular(8),
              border: isSelected
                  ? Border.all(color: _accent.withValues(alpha: 0.3))
                  : null,
            ),
            child: Row(
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: _parseCategoryColor(cat.color),
                    shape: BoxShape.circle,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(cat.name,
                      style: GoogleFonts.sora(
                        fontSize: 14,
                        color: isSelected ? _textGhost : _textMuted,
                        fontWeight:
                            isSelected ? FontWeight.w600 : FontWeight.w400,
                      )),
                ),
                if (isSelected)
                  const Icon(Icons.check_circle, size: 18, color: _accent),
              ],
            ),
          ),
        ));
      }
    }

    widgets.addAll(_buildSavingsGoalGroup());
    return widgets;
  }

  /// ── Savings Goals group (bottom of the picker) ──
  ///
  /// Sits below the category groups so the two pickers (here and the AI review
  /// card) present goals in the same place. When the transaction can't be
  /// earmarked at all, the reason is shown instead of an empty gap — previously
  /// the group just vanished for a pending hold with no explanation, and picking
  /// a goal on one only surfaced a generic "Failed to earmark" banner.
  List<Widget> _buildSavingsGoalGroup() {
    final blockedReason = savingsEarmarkBlockedReason(widget.transaction);

    // Nothing to say when the user simply has no goals yet.
    if (blockedReason == null && _savingsGoals.isEmpty) return const [];

    final lower = _searchTerm.toLowerCase();
    final goals = _searchTerm.isEmpty
        ? _savingsGoals
        : _savingsGoals
            .where((g) => g.name.toLowerCase().contains(lower))
            .toList();
    if (blockedReason == null && goals.isEmpty) return const [];

    final widgets = <Widget>[
      Padding(
        padding: const EdgeInsets.only(top: 12, bottom: 6),
        child: Text(AppLocalizations.of(context).txnSavingsGoalsGroup,
            style: GoogleFonts.sora(
                fontSize: 12,
                color: _textMuted,
                fontWeight: FontWeight.w600,
                letterSpacing: 0.5)),
      ),
    ];

    if (blockedReason != null) {
      widgets.add(Padding(
        padding: const EdgeInsets.only(bottom: 4, left: 2, right: 2),
        child: Text(blockedReason,
            style: GoogleFonts.sora(
                fontSize: 12, color: _textMuted, height: 1.4)),
      ));
      return widgets;
    }

    for (final goal in goals) {
      final isSelected = goal.id == _selectedSavingsGoalId;
      widgets.add(GestureDetector(
        onTap: () => setState(() {
          // Selecting a goal is mutually exclusive with a category.
          _selectedSavingsGoalId = goal.id;
          _selectedCategoryId = null;
        }),
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          margin: const EdgeInsets.only(bottom: 2),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: isSelected
                ? _accent.withValues(alpha: 0.12)
                : Colors.transparent,
            borderRadius: BorderRadius.circular(8),
            border: isSelected
                ? Border.all(color: _accent.withValues(alpha: 0.3))
                : null,
          ),
          child: Row(
            children: [
              const Icon(Icons.savings_outlined, size: 16, color: _accent),
              const SizedBox(width: 8),
              Expanded(
                child: Text(goal.name,
                    style: GoogleFonts.sora(
                      fontSize: 14,
                      color: isSelected ? _textGhost : _textMuted,
                      fontWeight:
                          isSelected ? FontWeight.w600 : FontWeight.w400,
                    )),
              ),
              if (isSelected)
                const Icon(Icons.check_circle, size: 18, color: _accent),
            ],
          ),
        ),
      ));
    }
    return widgets;
  }
}

// ═════════════════════════════════════════════════════════════
//  ADVANCED FILTERS BOTTOM SHEET
// ═════════════════════════════════════════════════════════════

/// Shows advanced filters sheet.
/// Returns updated TransactionFilter if applied, null if cancelled.
Future<TransactionFilter?> showAdvancedFiltersSheet(
  BuildContext context,
  TransactionFilter currentFilter,
) {
  return showModalBottomSheet<TransactionFilter>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _AdvancedFiltersSheet(filter: currentFilter),
  );
}

class _AdvancedFiltersSheet extends StatefulWidget {
  final TransactionFilter filter;
  const _AdvancedFiltersSheet({required this.filter});

  @override
  State<_AdvancedFiltersSheet> createState() => _AdvancedFiltersSheetState();
}

class _AdvancedFiltersSheetState extends State<_AdvancedFiltersSheet> {
  late final TextEditingController _minAmountController;
  late final TextEditingController _maxAmountController;
  int? _selectedAccountId;
  int? _selectedCategoryId;
  DateTime? _startDate;
  DateTime? _endDate;

  List<Category> _categories = [];
  List<_SimpleAccount> _accounts = [];
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _minAmountController = TextEditingController(
      text: widget.filter.minAmount?.toStringAsFixed(2) ?? '',
    );
    _maxAmountController = TextEditingController(
      text: widget.filter.maxAmount?.toStringAsFixed(2) ?? '',
    );
    _selectedAccountId = widget.filter.accountId;
    _selectedCategoryId = widget.filter.categoryId;
    _startDate = widget.filter.startDate;
    _endDate = widget.filter.endDate;
    _loadData();
  }

  @override
  void dispose() {
    _minAmountController.dispose();
    _maxAmountController.dispose();
    super.dispose();
  }

  Future<void> _loadData() async {
    try {
      final cats = await categoriesService.getAll();
      // Load the user's accounts, whichever provider they came from
      List<_SimpleAccount> accounts = [];
      try {
        final accountsData = await fetchAccountsJson(apiClient);
        accounts = accountsData
            .map((e) => _SimpleAccount.fromJson(e as Map<String, dynamic>))
            .toList();
      } catch (_) {}
      if (!mounted) return;
      setState(() {
        _categories = cats.allCategories;
        _accounts = accounts;
        _loading = false;
      });
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  void _applyAmountPreset(double? min, double? max) {
    setState(() {
      _minAmountController.text = min?.toStringAsFixed(0) ?? '';
      _maxAmountController.text = max?.toStringAsFixed(0) ?? '';
    });
  }

  void _clearAll() {
    setState(() {
      _minAmountController.clear();
      _maxAmountController.clear();
      _selectedAccountId = null;
      _selectedCategoryId = null;
      _startDate = null;
      _endDate = null;
    });
  }

  void _apply() {
    final minAmt = parseFlexibleAmount(_minAmountController.text);
    final maxAmt = parseFlexibleAmount(_maxAmountController.text);

    // Validation
    if (minAmt != null && maxAmt != null && minAmt > maxAmt) {
      showAppToast(context, 'Min amount must be less than max',
          type: AppToastType.error);
      return;
    }
    if (_startDate != null && _endDate != null && _startDate!.isAfter(_endDate!)) {
      showAppToast(context, 'Start date must be before end date',
          type: AppToastType.error);
      return;
    }

    final result = widget.filter.copyWith(
      minAmount: minAmt,
      maxAmount: maxAmt,
      accountId: _selectedAccountId,
      categoryId: _selectedCategoryId,
      startDate: _startDate,
      endDate: _endDate,
      clearMinAmount: minAmt == null,
      clearMaxAmount: maxAmt == null,
      clearAccountId: _selectedAccountId == null,
      clearCategoryId: _selectedCategoryId == null,
      clearStartDate: _startDate == null,
      clearEndDate: _endDate == null,
      page: 1,
    );
    Navigator.of(context).pop(result);
  }

  Future<void> _pickDate(bool isStart) async {
    final initial = isStart ? _startDate : _endDate;
    final date = await showDatePicker(
      context: context,
      initialDate: initial ?? DateTime.now(),
      firstDate: DateTime(2020),
      lastDate: DateTime.now(),
      builder: (context, child) {
        return Theme(
          data: Theme.of(context).copyWith(
            colorScheme: const ColorScheme.dark(
              primary: _accent,
              onPrimary: _textGhost,
              surface: _sheetBg,
              onSurface: _textGhost,
            ),
          ),
          child: child!,
        );
      },
    );
    if (date != null) {
      setState(() {
        if (isStart) {
          _startDate = date;
        } else {
          _endDate = date;
        }
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.8,
      minChildSize: 0.5,
      maxChildSize: 0.95,
      builder: (_, scrollController) => ClipRRect(
        borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
          child: Container(
            decoration: BoxDecoration(
              color: _sheetBg.withValues(alpha: 0.95),
              borderRadius:
                  const BorderRadius.vertical(top: Radius.circular(24)),
              border: Border.all(color: _cardBorder),
            ),
            child: Column(
              children: [
                _DragHandle(),
                // Header
                Padding(
                  padding: const EdgeInsets.fromLTRB(24, 0, 24, 12),
                  child: Row(
                    children: [
                      Text(AppLocalizations.of(context).txnAdvancedFilters,
                          style: GoogleFonts.sora(
                              fontSize: 20,
                              fontWeight: FontWeight.bold,
                              color: _textGhost)),
                      const Spacer(),
                      GestureDetector(
                        onTap: _clearAll,
                        child: Text(
                            AppLocalizations.of(context).txnClearAllFilters,
                            style: GoogleFonts.sora(
                                fontSize: 13, color: _accent)),
                      ),
                    ],
                  ),
                ),
                Expanded(
                  child: _loading
                      ? const Center(
                          child:
                              CircularProgressIndicator(color: _accent))
                      : ListView(
                          controller: scrollController,
                          padding: const EdgeInsets.symmetric(horizontal: 24),
                          children: [
                            _buildAmountSection(),
                            const SizedBox(height: 20),
                            _buildDateSection(),
                            const SizedBox(height: 20),
                            _buildAccountSection(),
                            const SizedBox(height: 20),
                            _buildCategorySection(),
                            const SizedBox(height: 24),
                          ],
                        ),
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
                  child: SizedBox(
                    width: double.infinity,
                    child: _AccentButton(
                      onPressed: _apply,
                      label: 'Apply Filters',
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

  Widget _buildAmountSection() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(AppLocalizations.of(context).txnAmountRange,
            style: GoogleFonts.sora(
                fontSize: 13, color: _textMuted, fontWeight: FontWeight.w600)),
        const SizedBox(height: 10),
        Row(
          children: [
            Expanded(
              child: _SheetTextField(
                controller: _minAmountController,
                hint: 'Min $primaryCurrencySymbol',
                keyboardType: TextInputType.number,
              ),
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              child: Text('—',
                  style: GoogleFonts.sora(color: _textMuted, fontSize: 16)),
            ),
            Expanded(
              child: _SheetTextField(
                controller: _maxAmountController,
                hint: 'Max $primaryCurrencySymbol',
                keyboardType: TextInputType.number,
              ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          children: [
            _PresetChip(label: '< ${primaryCurrencySymbol}50', onTap: () => _applyAmountPreset(null, 50)),
            _PresetChip(
                label: '${primaryCurrencySymbol}50–${primaryCurrencySymbol}200', onTap: () => _applyAmountPreset(50, 200)),
            _PresetChip(
                label: '${primaryCurrencySymbol}200–${primaryCurrencySymbol}1K',
                onTap: () => _applyAmountPreset(200, 1000)),
            _PresetChip(
                label: '> ${primaryCurrencySymbol}1K', onTap: () => _applyAmountPreset(1000, null)),
            _PresetChip(label: 'Clear', onTap: () => _applyAmountPreset(null, null)),
          ],
        ),
      ],
    );
  }

  Widget _buildDateSection() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(AppLocalizations.of(context).txnCustomDateRange,
            style: GoogleFonts.sora(
                fontSize: 13, color: _textMuted, fontWeight: FontWeight.w600)),
        const SizedBox(height: 10),
        Row(
          children: [
            Expanded(
              child: _DatePickerButton(
                label: _startDate != null
                    ? formatShortDate(_startDate!)
                    : 'Start Date',
                onTap: () => _pickDate(true),
              ),
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              child: Text('—',
                  style: GoogleFonts.sora(color: _textMuted, fontSize: 16)),
            ),
            Expanded(
              child: _DatePickerButton(
                label: _endDate != null
                    ? formatShortDate(_endDate!)
                    : 'End Date',
                onTap: () => _pickDate(false),
              ),
            ),
          ],
        ),
        if (_startDate != null || _endDate != null) ...[
          const SizedBox(height: 6),
          GestureDetector(
            onTap: () => setState(() {
              _startDate = null;
              _endDate = null;
            }),
            child: Text(AppLocalizations.of(context).txnClearDates,
                style: GoogleFonts.sora(fontSize: 12, color: _accent)),
          ),
        ],
      ],
    );
  }

  Widget _buildAccountSection() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(AppLocalizations.of(context).txnAccount,
            style: GoogleFonts.sora(
                fontSize: 13, color: _textMuted, fontWeight: FontWeight.w600)),
        const SizedBox(height: 10),
        _SheetDropdown<int?>(
          value: _selectedAccountId,
          hint: 'All Accounts',
          items: [
            DropdownMenuItem(
                value: null,
                child: Text(AppLocalizations.of(context).txnAllAccounts)),
            ..._accounts.map((a) => DropdownMenuItem(
                  value: a.id,
                  child: Text('${a.name} (${a.mask ?? ''})'),
                )),
          ],
          onChanged: (v) => setState(() => _selectedAccountId = v),
        ),
      ],
    );
  }

  Widget _buildCategorySection() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(AppLocalizations.of(context).txnCategory,
            style: GoogleFonts.sora(
                fontSize: 13, color: _textMuted, fontWeight: FontWeight.w600)),
        const SizedBox(height: 10),
        _SheetDropdown<int?>(
          value: _selectedCategoryId,
          hint: 'All Categories',
          items: [
            DropdownMenuItem(
                value: null,
                child: Text(AppLocalizations.of(context).txnAllCategories)),
            ..._categories.map((c) => DropdownMenuItem(
                  value: c.id,
                  child: Text(c.name),
                )),
          ],
          onChanged: (v) => setState(() => _selectedCategoryId = v),
        ),
      ],
    );
  }
}

class _SimpleAccount {
  final int id;
  final String name;
  final String? mask;
  _SimpleAccount({required this.id, required this.name, this.mask});
  factory _SimpleAccount.fromJson(Map<String, dynamic> json) {
    return _SimpleAccount(
      id: json['id'] as int,
      name: json['name'] as String? ?? json['accountName'] as String? ?? '',
      mask: json['mask'] as String?,
    );
  }
}

// ═════════════════════════════════════════════════════════════
//  SPLIT TRANSACTION BOTTOM SHEET
// ═════════════════════════════════════════════════════════════

/// Shows split transaction sheet.
/// Returns true if the transaction was split/unsplit, null otherwise.
Future<bool?> showSplitTransactionSheet(
  BuildContext context,
  Transaction transaction,
) {
  return showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _SplitTransactionSheet(transaction: transaction),
  );
}

class _SplitTransactionSheet extends StatefulWidget {
  final Transaction transaction;
  const _SplitTransactionSheet({required this.transaction});

  @override
  State<_SplitTransactionSheet> createState() =>
      _SplitTransactionSheetState();
}

class _SplitTransactionSheetState extends State<_SplitTransactionSheet> {
  final _splits = <_SplitEntry>[];
  List<Category> _categories = [];
  bool _loading = true;
  bool _saving = false;

  double get _totalSplit =>
      _splits.fold(0.0, (sum, s) => sum + (parseFlexibleAmount(s.amountController.text) ?? 0));

  double get _originalAmount => widget.transaction.amount.abs();

  double get _remaining => _originalAmount - _totalSplit;

  // Accept a one-cent rounding difference: an even split of an amount that
  // doesn't divide cleanly (e.g. $100 / 3) leaves a residue that must not
  // permanently disable Save. Tolerance is inclusive of a single cent.
  bool get _isBalanced => (_remaining).abs() <= 0.01;

  @override
  void initState() {
    super.initState();
    _loadCategories();
    // Initialize with 2 splits
    if (widget.transaction.isSplit && widget.transaction.splits != null) {
      for (final s in widget.transaction.splits!) {
        _splits.add(_SplitEntry(
          amountController:
              TextEditingController(text: s.amount.toStringAsFixed(2)),
          categoryId: s.categoryId,
          note: s.notes,
        ));
      }
    } else {
      _splits.add(_SplitEntry(amountController: TextEditingController()));
      _splits.add(_SplitEntry(amountController: TextEditingController()));
      // Seed an even split that sums back to the original to the cent (the
      // last part absorbs any rounding residue), so Save isn't disabled on
      // open for amounts that don't halve cleanly (e.g. $10.01).
      _applyEvenSplit(_splits);
    }
  }

  @override
  void dispose() {
    for (final s in _splits) {
      s.amountController.dispose();
    }
    super.dispose();
  }

  Future<void> _loadCategories() async {
    try {
      final cats = await categoriesService.getAll();
      if (!mounted) return;
      setState(() {
        _categories = cats.allCategories;
        _loading = false;
      });
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  void _addSplit() {
    setState(() {
      _splits.add(_SplitEntry(amountController: TextEditingController()));
    });
  }

  void _removeSplit(int index) {
    if (_splits.length <= 2) return;
    setState(() {
      _splits[index].amountController.dispose();
      _splits.removeAt(index);
    });
  }

  void _distributeEvenly() {
    setState(() {
      _applyEvenSplit(_splits);
    });
  }

  /// Distributes [_originalAmount] evenly across [entries], absorbing any
  /// rounding remainder into the last entry so the parts always sum back to
  /// the original to the cent (e.g. $100 / 3 → 33.33, 33.33, 33.34).
  void _applyEvenSplit(List<_SplitEntry> entries) {
    if (entries.isEmpty) return;
    final each = double.parse((_originalAmount / entries.length).toStringAsFixed(2));
    double assigned = 0;
    for (var i = 0; i < entries.length; i++) {
      final isLast = i == entries.length - 1;
      final value = isLast
          ? double.parse((_originalAmount - assigned).toStringAsFixed(2))
          : each;
      assigned += each;
      entries[i].amountController.text = value.toStringAsFixed(2);
    }
  }

  Future<void> _save() async {
    if (!_isBalanced) {
      showAppToast(context, 'Split amounts must equal the original',
          type: AppToastType.error);
      return;
    }

    setState(() => _saving = true);
    try {
      final items = _splits
          .map((s) => TransactionSplitItem(
                amount: parseFlexibleAmount(s.amountController.text) ?? 0,
                categoryId: s.categoryId,
                note: s.note,
              ))
          .toList();
      await transactionsService.splitTransaction(
          widget.transaction.id, items);
      if (mounted) Navigator.of(context).pop(true);
    } catch (_) {
      setState(() => _saving = false);
      if (mounted) {
        showAppToast(context, 'Failed to split transaction',
            type: AppToastType.error);
      }
    }
  }

  Future<void> _unsplit() async {
    setState(() => _saving = true);
    try {
      await transactionsService.unsplitTransaction(widget.transaction.id);
      if (mounted) Navigator.of(context).pop(true);
    } catch (_) {
      setState(() => _saving = false);
      if (mounted) {
        showAppToast(context, 'Failed to unsplit transaction',
            type: AppToastType.error);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.8,
      minChildSize: 0.5,
      maxChildSize: 0.95,
      builder: (_, scrollController) => ClipRRect(
        borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
          child: Container(
            decoration: BoxDecoration(
              color: _sheetBg.withValues(alpha: 0.95),
              borderRadius:
                  const BorderRadius.vertical(top: Radius.circular(24)),
              border: Border.all(color: _cardBorder),
            ),
            child: Column(
              children: [
                _DragHandle(),
                Padding(
                  padding: const EdgeInsets.fromLTRB(24, 0, 24, 12),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(AppLocalizations.of(context).txnSplitTransaction,
                          style: GoogleFonts.sora(
                              fontSize: 20,
                              fontWeight: FontWeight.bold,
                              color: _textGhost)),
                      const SizedBox(height: 4),
                      Text(
                        '${widget.transaction.displayTitle} — ${formatCurrency(_originalAmount)}',
                        style: GoogleFonts.sora(
                            fontSize: 13, color: _textMuted),
                      ),
                    ],
                  ),
                ),
                Expanded(
                  child: _loading
                      ? const Center(
                          child:
                              CircularProgressIndicator(color: _accent))
                      : ListView(
                          controller: scrollController,
                          padding: const EdgeInsets.symmetric(horizontal: 24),
                          children: [
                            ..._splits.asMap().entries.map(
                                  (e) => _buildSplitRow(e.key, e.value),
                                ),
                            const SizedBox(height: 12),
                            // Add + Distribute
                            Row(
                              children: [
                                GestureDetector(
                                  onTap: _addSplit,
                                  child: Row(
                                    children: [
                                      const Icon(Icons.add_circle_outline,
                                          size: 16, color: _accent),
                                      const SizedBox(width: 4),
                                      Text(
                                          AppLocalizations.of(context)
                                              .txnAddSplit,
                                          style: GoogleFonts.sora(
                                              fontSize: 13,
                                              color: _accent,
                                              fontWeight: FontWeight.w500)),
                                    ],
                                  ),
                                ),
                                const Spacer(),
                                GestureDetector(
                                  onTap: _distributeEvenly,
                                  child: Text(
                                      AppLocalizations.of(context)
                                          .txnDistributeEvenly,
                                      style: GoogleFonts.sora(
                                          fontSize: 13,
                                          color: _accent,
                                          fontWeight: FontWeight.w500)),
                                ),
                              ],
                            ),
                            const SizedBox(height: 16),
                            // Running total
                            _buildRunningTotal(),
                            if (widget.transaction.isSplit) ...[
                              const SizedBox(height: 16),
                              SizedBox(
                                width: double.infinity,
                                child: TextButton(
                                  onPressed: _saving ? null : _unsplit,
                                  child: Text(
                                      AppLocalizations.of(context)
                                          .txnUnsplitTransaction,
                                      style: GoogleFonts.sora(
                                          color: _expense, fontSize: 14)),
                                ),
                              ),
                            ],
                          ],
                        ),
                ),
                Padding(
                  padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
                  child: SizedBox(
                    width: double.infinity,
                    child: _AccentButton(
                      onPressed: (_saving || !_isBalanced) ? null : _save,
                      label: _saving ? 'Saving...' : 'Save Split',
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

  Widget _buildSplitRow(int index, _SplitEntry entry) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: _GlassCard(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Text(AppLocalizations.of(context).txnPartNumber(index + 1),
                    style: GoogleFonts.sora(
                        fontSize: 12,
                        color: _textMuted,
                        fontWeight: FontWeight.w600)),
                const Spacer(),
                if (_splits.length > 2)
                  GestureDetector(
                    onTap: () => _removeSplit(index),
                    child: const Icon(Icons.close, size: 16, color: _expense),
                  ),
              ],
            ),
            const SizedBox(height: 8),
            _SheetTextField(
              controller: entry.amountController,
              hint: 'Amount',
              keyboardType:
                  const TextInputType.numberWithOptions(decimal: true),
              prefix: primaryCurrencySymbol,
              onChanged: (_) => setState(() {}),
            ),
            const SizedBox(height: 8),
            _SheetDropdown<int?>(
              value: entry.categoryId,
              hint: 'Category',
              items: [
                DropdownMenuItem(
                    value: null,
                    child:
                        Text(AppLocalizations.of(context).txnUncategorized)),
                ..._categories.map((c) => DropdownMenuItem(
                      value: c.id,
                      child: Text(c.name),
                    )),
              ],
              onChanged: (v) => setState(() => entry.categoryId = v),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildRunningTotal() {
    final balanceColor = _isBalanced
        ? _income
        : _remaining > 0
            ? _pending
            : _expense;
    final balanceText = _isBalanced
        ? AppLocalizations.of(context).txnSplitMatch
        : _remaining > 0
            ? AppLocalizations.of(context)
                .txnSplitRemaining(formatCurrency(_remaining))
            : AppLocalizations.of(context)
                .txnSplitOver(formatCurrency(_remaining.abs()));

    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: balanceColor.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: balanceColor.withValues(alpha: 0.2)),
      ),
      child: Row(
        children: [
          Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(AppLocalizations.of(context).txnSplitTotal,
                  style: GoogleFonts.sora(
                      fontSize: 11, color: _textMuted)),
              Text(formatCurrency(_totalSplit),
                  style: GoogleFonts.firaCode(
                      fontSize: 15,
                      color: _textGhost,
                      fontWeight: FontWeight.w600)),
            ],
          ),
          const Spacer(),
          Column(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Text(AppLocalizations.of(context).txnOriginal,
                  style: GoogleFonts.sora(
                      fontSize: 11, color: _textMuted)),
              Text(formatCurrency(_originalAmount),
                  style: GoogleFonts.firaCode(
                      fontSize: 15,
                      color: _textGhost,
                      fontWeight: FontWeight.w600)),
            ],
          ),
          const SizedBox(width: 12),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
            decoration: BoxDecoration(
              color: balanceColor.withValues(alpha: 0.15),
              borderRadius: BorderRadius.circular(6),
            ),
            child: Text(balanceText,
                style: GoogleFonts.sora(
                    fontSize: 12,
                    color: balanceColor,
                    fontWeight: FontWeight.w600)),
          ),
        ],
      ),
    );
  }
}

class _SplitEntry {
  final TextEditingController amountController;
  int? categoryId;

  /// Carried through an edit so a receipt split's item notes survive it.
  final String? note;
  _SplitEntry({required this.amountController, this.categoryId, this.note});
}

// ═════════════════════════════════════════════════════════════
//  SHARED CINEMATIC WIDGETS
// ═════════════════════════════════════════════════════════════

// ═════════════════════════════════════════════════════════════
//  COVERING TRANSFER PICKER
// ═════════════════════════════════════════════════════════════

/// "Which transfer covered this?" for a purchase paid from a goal that lives in
/// a real account. Returns the updated transaction when a link was made.
Future<Transaction?> showCoveringTransferSheet(
    BuildContext context, Transaction transaction) {
  return showModalBottomSheet<Transaction?>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _CoveringTransferSheet(transaction: transaction),
  );
}

class _CoveringTransferSheet extends StatefulWidget {
  const _CoveringTransferSheet({required this.transaction});
  final Transaction transaction;

  @override
  State<_CoveringTransferSheet> createState() => _CoveringTransferSheetState();
}

class _CoveringTransferSheetState extends State<_CoveringTransferSheet> {
  CoveringTransferCandidates? _data;
  String? _error;
  bool _includeOthers = true;
  bool _linking = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final data = await transactionsService
          .getCoveringTransferCandidates(widget.transaction.id);
      if (!mounted) return;
      setState(() {
        _data = data;
        // Pre-tick "also cover the others" only when one transfer plausibly
        // moved everything at once; otherwise the user is likely matching one.
        _includeOthers = data.pendingDraws.length > 1 &&
            data.candidates.any((c) => c.matchesPendingTotal);
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = apiErrorMessage(e,
          fallback: AppLocalizations.of(context).txnCoverLinkFailed));
    }
  }

  Future<void> _pick(CoveringTransferCandidate c) async {
    if (_linking) return;
    setState(() => _linking = true);
    try {
      final updated = await transactionsService.linkCoveringTransfer(
        widget.transaction.id,
        c.transactionId,
        includeOtherPending: _includeOthers && (_data?.pendingDraws.length ?? 0) > 1,
      );
      if (!mounted) return;
      showAppToast(context, AppLocalizations.of(context).txnCoverLinked);
      Navigator.of(context).pop(updated);
    } catch (e) {
      if (!mounted) return;
      setState(() => _linking = false);
      showAppToast(context,
          apiErrorMessage(e, fallback: AppLocalizations.of(context).txnCoverLinkFailed),
          type: AppToastType.error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final data = _data;
    final others = (data?.pendingDraws.length ?? 1) - 1;
    final othersAmount = data == null
        ? 0.0
        : data.pendingTotal - widget.transaction.amount.abs();

    return DraggableScrollableSheet(
      initialChildSize: 0.7,
      minChildSize: 0.4,
      maxChildSize: 0.95,
      builder: (_, scrollController) => ClipRRect(
        borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
          child: Container(
            decoration: const BoxDecoration(
              color: _sheetBg,
              border: Border(top: BorderSide(color: _cardBorder)),
            ),
            child: Column(
              children: [
                _DragHandle(),
                Padding(
                  padding: const EdgeInsets.fromLTRB(20, 4, 20, 8),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(l10n.txnCoverSheetTitle,
                          style: GoogleFonts.sora(
                              fontSize: 18,
                              fontWeight: FontWeight.w600,
                              color: _textGhost)),
                      const SizedBox(height: 4),
                      Text(
                        data == null
                            ? ''
                            : l10n.txnCoverSheetSubtitle(data.accountName),
                        style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                      ),
                    ],
                  ),
                ),
                if (data != null && others > 0)
                  Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 12),
                    child: CheckboxListTile(
                      value: _includeOthers,
                      onChanged: (v) => setState(() => _includeOthers = v ?? false),
                      dense: true,
                      activeColor: _accent,
                      controlAffinity: ListTileControlAffinity.leading,
                      title: Text(
                        l10n.txnCoverAlsoOthers(
                            others, formatCurrency(othersAmount)),
                        style: GoogleFonts.sora(fontSize: 12, color: _textGhost),
                      ),
                    ),
                  ),
                Expanded(
                  child: _error != null
                      ? Center(
                          child: Padding(
                            padding: const EdgeInsets.all(24),
                            child: Text(_error!,
                                textAlign: TextAlign.center,
                                style: GoogleFonts.sora(
                                    fontSize: 13, color: _expense)),
                          ),
                        )
                      : data == null
                          ? const Center(
                              child: SizedBox(
                                  width: 22,
                                  height: 22,
                                  child: CircularProgressIndicator(
                                      strokeWidth: 2, color: _accent)))
                          : data.candidates.isEmpty
                              ? Center(
                                  child: Padding(
                                    padding: const EdgeInsets.all(24),
                                    child: Text(
                                      l10n.txnCoverNone(data.accountName),
                                      textAlign: TextAlign.center,
                                      style: GoogleFonts.sora(
                                          fontSize: 13, color: _textMuted),
                                    ),
                                  ),
                                )
                              : ListView.separated(
                                  controller: scrollController,
                                  padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                                  itemCount: data.candidates.length,
                                  separatorBuilder: (_, __) =>
                                      const SizedBox(height: 8),
                                  itemBuilder: (_, i) =>
                                      _candidateRow(data.candidates[i], l10n),
                                ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _candidateRow(CoveringTransferCandidate c, AppLocalizations l10n) {
    final badge = c.matchesPendingTotal
        ? l10n.txnCoverMatchesTotal
        : c.matchesThis
            ? l10n.txnCoverMatchesThis
            : null;
    return GestureDetector(
      onTap: _linking ? null : () => _pick(c),
      child: _GlassCard(
        child: Row(
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(c.title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: GoogleFonts.sora(
                          fontSize: 14, color: _textGhost)),
                  const SizedBox(height: 2),
                  Text(
                    [formatMediumDate(c.date), if (c.categoryName != null) c.categoryName!]
                        .join(' · '),
                    style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 10),
            Column(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(formatCurrency(-c.amount),
                    style: GoogleFonts.firaCode(
                        fontSize: 14,
                        fontWeight: FontWeight.w600,
                        color: _textGhost)),
                if (badge != null) ...[
                  const SizedBox(height: 4),
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                    decoration: BoxDecoration(
                      color: _income.withValues(alpha: 0.12),
                      borderRadius: BorderRadius.circular(999),
                    ),
                    child: Text(badge,
                        style: GoogleFonts.sora(
                            fontSize: 10,
                            fontWeight: FontWeight.w600,
                            color: _income)),
                  ),
                ],
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _DragHandle extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: 12, bottom: 8),
      child: Container(
        width: 40,
        height: 4,
        decoration: BoxDecoration(
          color: _textMuted.withValues(alpha: 0.3),
          borderRadius: BorderRadius.circular(2),
        ),
      ),
    );
  }
}

class _GlassCard extends StatelessWidget {
  final Widget child;
  const _GlassCard({required this.child});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _cardBorder),
      ),
      child: child,
    );
  }
}

class _InfoRow extends StatelessWidget {
  final String label;
  final String value;
  final Widget? trailing;
  const _InfoRow({required this.label, required this.value, this.trailing});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 100,
            child: Text(label,
                style: GoogleFonts.sora(
                    fontSize: 12, color: _textMuted, fontWeight: FontWeight.w500)),
          ),
          Expanded(
            child: Text(value,
                style: GoogleFonts.sora(fontSize: 13, color: _textGhost)),
          ),
          if (trailing != null) trailing!,
        ],
      ),
    );
  }
}

class _Badge extends StatelessWidget {
  final String label;
  final Color color;
  const _Badge({required this.label, required this.color});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Text(label,
          style: GoogleFonts.sora(
              fontSize: 10, color: color, fontWeight: FontWeight.w600)),
    );
  }
}

class _ActionButton extends StatelessWidget {
  final IconData icon;
  final String label;
  final Color color;
  final VoidCallback? onTap;

  const _ActionButton({
    required this.icon,
    required this.label,
    this.color = _accent,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(vertical: 12),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.08),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: color.withValues(alpha: 0.2)),
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, size: 16, color: color),
            const SizedBox(width: 6),
            Text(label,
                style: GoogleFonts.sora(
                    fontSize: 13, color: color, fontWeight: FontWeight.w500)),
          ],
        ),
      ),
    );
  }
}

class _AccentButton extends StatelessWidget {
  final VoidCallback? onPressed;
  final String label;
  const _AccentButton({required this.onPressed, required this.label});

  @override
  Widget build(BuildContext context) {
    return ElevatedButton(
      onPressed: onPressed,
      style: ElevatedButton.styleFrom(
        backgroundColor: _accent,
        foregroundColor: _textGhost,
        disabledBackgroundColor: _accent.withValues(alpha: 0.3),
        disabledForegroundColor: _textGhost.withValues(alpha: 0.5),
        padding: const EdgeInsets.symmetric(vertical: 14),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
        textStyle: GoogleFonts.sora(fontSize: 14, fontWeight: FontWeight.w600),
      ),
      child: Text(label),
    );
  }
}

class _SmallAccentButton extends StatelessWidget {
  final VoidCallback? onPressed;
  final String label;
  const _SmallAccentButton({required this.onPressed, required this.label});

  @override
  Widget build(BuildContext context) {
    return ElevatedButton(
      onPressed: onPressed,
      style: ElevatedButton.styleFrom(
        backgroundColor: _accent,
        foregroundColor: _textGhost,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        minimumSize: Size.zero,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
        textStyle: GoogleFonts.sora(fontSize: 12, fontWeight: FontWeight.w600),
      ),
      child: Text(label),
    );
  }
}

class _SheetSearchBar extends StatelessWidget {
  final String hint;
  final ValueChanged<String> onChanged;
  const _SheetSearchBar({required this.hint, required this.onChanged});

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: _cardBorder),
      ),
      child: TextField(
        onChanged: onChanged,
        style: GoogleFonts.sora(color: _textGhost, fontSize: 14),
        decoration: InputDecoration(
          hintText: hint,
          hintStyle: GoogleFonts.sora(color: _textMuted, fontSize: 14),
          prefixIcon: const Icon(Icons.search, color: _textMuted, size: 18),
          border: InputBorder.none,
          contentPadding:
              const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        ),
      ),
    );
  }
}

class _SheetTextField extends StatelessWidget {
  final TextEditingController controller;
  final String hint;
  final TextInputType? keyboardType;
  final String? prefix;
  final ValueChanged<String>? onChanged;

  const _SheetTextField({
    required this.controller,
    required this.hint,
    this.keyboardType,
    this.prefix,
    this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: _cardBorder),
      ),
      child: TextField(
        controller: controller,
        keyboardType: keyboardType,
        onChanged: onChanged,
        style: GoogleFonts.firaCode(color: _textGhost, fontSize: 14),
        decoration: InputDecoration(
          hintText: hint,
          hintStyle: GoogleFonts.sora(color: _textMuted, fontSize: 14),
          prefixText: prefix,
          prefixStyle: GoogleFonts.firaCode(color: _textMuted, fontSize: 14),
          border: InputBorder.none,
          contentPadding:
              const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        ),
      ),
    );
  }
}

class _SheetDropdown<T> extends StatelessWidget {
  final T value;
  final String hint;
  final List<DropdownMenuItem<T>> items;
  final ValueChanged<T?> onChanged;

  const _SheetDropdown({
    required this.value,
    required this.hint,
    required this.items,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: _cardBorder),
      ),
      child: DropdownButtonHideUnderline(
        child: DropdownButton<T>(
          value: value,
          hint: Text(hint,
              style: GoogleFonts.sora(color: _textMuted, fontSize: 14)),
          isExpanded: true,
          dropdownColor: _sheetBg,
          style: GoogleFonts.sora(color: _textGhost, fontSize: 14),
          icon: const Icon(Icons.keyboard_arrow_down, color: _textMuted),
          items: items,
          onChanged: onChanged,
        ),
      ),
    );
  }
}

class _PresetChip extends StatelessWidget {
  final String label;
  final VoidCallback onTap;
  const _PresetChip({required this.label, required this.onTap});

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: _cardBorder),
        ),
        child: Text(label,
            style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
      ),
    );
  }
}

/// Selectable chip for the RFC #793 personal sub-category picker (F4).
class _PersonalSubChip extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;
  const _PersonalSubChip({
    required this.label,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: BoxDecoration(
          color: selected ? _accent.withValues(alpha: 0.18) : _cardBg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: selected ? _accent : _cardBorder,
            width: selected ? 1.5 : 1,
          ),
        ),
        child: Text(
          label,
          style: GoogleFonts.sora(
            fontSize: 13,
            color: selected ? _textGhost : _textMuted,
            fontWeight: selected ? FontWeight.w600 : FontWeight.w400,
          ),
        ),
      ),
    );
  }
}

class _DatePickerButton extends StatelessWidget {
  final String label;
  final VoidCallback onTap;
  const _DatePickerButton({required this.label, required this.onTap});

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: _cardBorder),
        ),
        child: Row(
          children: [
            const Icon(Icons.calendar_today, size: 14, color: _textMuted),
            const SizedBox(width: 8),
            Expanded(
              child: Text(label,
                  style: GoogleFonts.sora(fontSize: 13, color: _textGhost)),
            ),
          ],
        ),
      ),
    );
  }
}

class _GlassDialog extends StatelessWidget {
  final String title;
  final String message;
  final String confirmLabel;
  final Color confirmColor;

  const _GlassDialog({
    required this.title,
    required this.message,
    required this.confirmLabel,
    required this.confirmColor,
  });

  @override
  Widget build(BuildContext context) {
    return Dialog(
      backgroundColor: Colors.transparent,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(20),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
          child: Container(
            padding: const EdgeInsets.all(24),
            decoration: BoxDecoration(
              color: _sheetBg.withValues(alpha: 0.95),
              borderRadius: BorderRadius.circular(20),
              border: Border.all(color: _cardBorder),
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(title,
                    style: GoogleFonts.sora(
                        fontSize: 18,
                        fontWeight: FontWeight.bold,
                        color: _textGhost)),
                const SizedBox(height: 12),
                Text(message,
                    style:
                        GoogleFonts.sora(fontSize: 14, color: _textMuted),
                    textAlign: TextAlign.center),
                const SizedBox(height: 24),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(
                        onPressed: () => Navigator.of(context).pop(false),
                        style: OutlinedButton.styleFrom(
                          foregroundColor: _textMuted,
                          side: BorderSide(color: _cardBorder),
                          shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(10)),
                        ),
                        child: Text(AppLocalizations.of(context).txnCancel,
                            style: GoogleFonts.sora(fontSize: 13)),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: ElevatedButton(
                        onPressed: () =>
                            Navigator.of(context).pop(true),
                        style: ElevatedButton.styleFrom(
                          backgroundColor: confirmColor,
                          foregroundColor: _textGhost,
                          shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(10)),
                        ),
                        child: Text(confirmLabel,
                            style: GoogleFonts.sora(fontSize: 13)),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

// ── Helper ──

Color _parseCategoryColor(String? hex) {
  if (hex == null || hex.isEmpty) return _accent;
  try {
    final clean = hex.replaceFirst('#', '');
    return Color(int.parse('FF$clean', radix: 16));
  } catch (_) {
    return _accent;
  }
}

// ═════════════════════════════════════════════════════════════
//  CREATE (MANUAL) TRANSACTION BOTTOM SHEET
// ═════════════════════════════════════════════════════════════

/// Shows a form to manually add a transaction. Returns the created [Transaction]
/// on success, or null if cancelled.
Future<Transaction?> showCreateTransactionSheet(
  BuildContext context, {
  int? defaultAccountId,
  VoidCallback? onCreated,
}) {
  return showModalBottomSheet<Transaction?>(
    context: context,
    isScrollControlled: true,
    backgroundColor: Colors.transparent,
    builder: (_) => _CreateTransactionSheet(
      defaultAccountId: defaultAccountId,
      onCreated: onCreated,
    ),
  );
}

class _CreateTransactionSheet extends StatefulWidget {
  final int? defaultAccountId;
  final VoidCallback? onCreated;

  const _CreateTransactionSheet({this.defaultAccountId, this.onCreated});

  @override
  State<_CreateTransactionSheet> createState() => _CreateTransactionSheetState();
}

class _CreateTransactionSheetState extends State<_CreateTransactionSheet> {
  final _descriptionController = TextEditingController();
  final _merchantController = TextEditingController();
  final _amountController = TextEditingController();
  final _notesController = TextEditingController();

  List<Account> _accounts = [];
  int? _selectedAccountId;
  DateTime _selectedDate = DateTime.now();
  TransactionCategory _selectedCategory = TransactionCategory.uncategorized;
  bool _isIncome = false;

  bool _loadingAccounts = true;
  bool _isSaving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadAccounts();
  }

  @override
  void dispose() {
    _descriptionController.dispose();
    _merchantController.dispose();
    _amountController.dispose();
    _notesController.dispose();
    super.dispose();
  }

  Future<void> _loadAccounts() async {
    try {
      final accounts = await accountsService.getAccounts();
      if (!mounted) return;
      setState(() {
        // Exclude the synthetic Cash account (type 5) — the "Cash (no account)" dropdown option
        // already represents it, so listing it too would be a confusing duplicate.
        _accounts = accounts.where((a) => a.isActive && a.type != 5).toList();
        _selectedAccountId = widget.defaultAccountId ??
            (_accounts.isNotEmpty ? _accounts.first.id : null);
        _loadingAccounts = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingAccounts = false;
        _error = AppLocalizations.of(context).txnCouldNotLoadAccounts;
      });
    }
  }

  Future<void> _submit() async {
    final desc = _descriptionController.text.trim();
    final amountStr = _amountController.text.trim().replaceAll(',', '');
    final amount = parseFlexibleAmount(amountStr);

    // No account check — a null selection means "Cash (no account)"; the backend files it under
    // the user's synthetic Cash account.
    if (desc.isEmpty) {
      setState(
          () => _error = AppLocalizations.of(context).txnEnterDescription);
      return;
    }
    if (amount == null || amount <= 0) {
      setState(() =>
          _error = AppLocalizations.of(context).txnEnterAmountGreaterZero);
      return;
    }

    setState(() {
      _isSaving = true;
      _error = null;
    });

    try {
      // Sign convention: income positive, expense negative.
      final signed = _isIncome ? amount.abs() : -amount.abs();
      final created = await transactionsService.createTransaction(
        accountId: _selectedAccountId, // null → the user's Cash account
        amount: signed,
        description: desc,
        merchantName: _merchantController.text.trim().isEmpty
            ? null
            : _merchantController.text.trim(),
        date: _selectedDate,
        categoryId: _selectedCategory.value,
        userNotes: _notesController.text.trim().isEmpty
            ? null
            : _notesController.text.trim(),
      );
      widget.onCreated?.call();
      if (mounted) Navigator.of(context).pop(created);
    } catch (e) {
      if (mounted) {
        setState(() {
          _isSaving = false;
          _error = AppLocalizations.of(context).txnCouldNotSaveTransaction;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return PopScope(
      canPop: !_isSaving,
      // NOTHING here reserves the keyboard inset. This sheet lives in the nav
      // shell's navigator, whose Scaffold already shrinks the viewport for the
      // keyboard — the root cause of the old keyboard-height void was the shell
      // re-injecting the raw inset below that resize, making sheets/pages
      // reserve it a second time (see stripShellOwnedInsets in
      // cinematic_nav_shell.dart, where the shell now strips it). The scrolling
      // ListView in _buildForm keeps viewInsets.bottom in its padding: it reads
      // 0 under the shell, and still lifts the focused field on any surface
      // that does NOT pre-shrink the viewport.
      child: ClipRRect(
        borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
          child: Container(
            // No maxHeight either: isScrollControlled already caps the route at the
            // available height and the ListView scrolls within whatever it gets.
            decoration: BoxDecoration(
              color: _sheetBg.withValues(alpha: 0.96),
              borderRadius:
                  const BorderRadius.vertical(top: Radius.circular(24)),
              border: Border.all(color: _cardBorder),
            ),
            child: SafeArea(
              top: false,
              child: _loadingAccounts
                  ? const Padding(
                      padding: EdgeInsets.all(48),
                      child: Center(
                        child: CircularProgressIndicator(color: _accent),
                      ),
                    )
                  : _buildForm(context),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildForm(BuildContext context) {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        // Grab handle
        Container(
          margin: const EdgeInsets.only(top: 12, bottom: 4),
          width: 40,
          height: 4,
          decoration: BoxDecoration(
            color: _textMuted.withValues(alpha: 0.3),
            borderRadius: BorderRadius.circular(2),
          ),
        ),
        Flexible(
          child: ListView(
            shrinkWrap: true,
            // The keyboard inset belongs HERE, on the scrollable's padding, not as a
            // fixed Padding wrapped around the sheet. Inside the scrollable it is space
            // the list can scroll through; outside it, it is a void that shrinks the
            // form — which is exactly what the blank block above the keyboard was.
            // On Android the viewport is already keyboard-adjusted so this is simply
            // extra scroll room; on platforms that don't resize it lets the focused
            // field scroll clear of the keyboard.
            padding: EdgeInsets.fromLTRB(
                24, 12, 24, 24 + MediaQuery.of(context).viewInsets.bottom),
            children: [
              Text(
                AppLocalizations.of(context).txnAddTransaction,
                style: GoogleFonts.sora(
                  fontSize: 20,
                  fontWeight: FontWeight.w600,
                  color: _textGhost,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                AppLocalizations.of(context).txnAddTransactionSubtitle,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
              ),
              const SizedBox(height: 20),

              ...[
                // A bank account is no longer required — a manual transaction can be filed under
                // the user's "Cash" account, so the form always shows.
                // Income / Expense toggle
                Row(
                  children: [
                    Expanded(
                      child: _TypeToggle(
                        label: 'Expense',
                        selected: !_isIncome,
                        color: _expense,
                        onTap: () => setState(() => _isIncome = false),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: _TypeToggle(
                        label: 'Income',
                        selected: _isIncome,
                        color: _income,
                        onTap: () => setState(() => _isIncome = true),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 16),

                _fieldLabel('Amount'),
                _textField(
                  fieldKey: const Key('addtx_amount'),
                  controller: _amountController,
                  hint: '0.00',
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  prefixText: _isIncome ? '+ ' : '- ',
                  prefixColor: _isIncome ? _income : _expense,
                  mono: true,
                ),
                const SizedBox(height: 14),

                _fieldLabel('Description'),
                _textField(
                  fieldKey: const Key('addtx_description'),
                  controller: _descriptionController,
                  hint: 'e.g. Farmers market',
                ),
                const SizedBox(height: 14),

                _fieldLabel('Merchant (optional)'),
                _textField(
                  controller: _merchantController,
                  hint: 'e.g. Downtown Market',
                ),
                const SizedBox(height: 14),

                _fieldLabel('Account'),
                _GlassCard(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 14),
                    child: DropdownButtonHideUnderline(
                      child: DropdownButton<int?>(
                        value: _selectedAccountId,
                        isExpanded: true,
                        dropdownColor: _sheetBg,
                        icon: const Icon(Icons.keyboard_arrow_down,
                            color: _textMuted),
                        style:
                            GoogleFonts.sora(fontSize: 15, color: _textGhost),
                        items: [
                          DropdownMenuItem<int?>(
                            value: null,
                            child: Text(
                                AppLocalizations.of(context).txnCashNoAccount,
                                overflow: TextOverflow.ellipsis),
                          ),
                          ..._accounts.map((a) {
                            final label = a.nickname?.isNotEmpty == true
                                ? a.nickname!
                                : a.name;
                            final mask =
                                a.mask != null ? ' ••${a.mask}' : '';
                            return DropdownMenuItem<int?>(
                              value: a.id,
                              child: Text('$label$mask',
                                  overflow: TextOverflow.ellipsis),
                            );
                          }),
                        ],
                        onChanged: (v) =>
                            setState(() => _selectedAccountId = v),
                      ),
                    ),
                  ),
                ),
                const SizedBox(height: 14),

                Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          _fieldLabel('Date'),
                          _GlassCard(
                            child: InkWell(
                              onTap: _pickDate,
                              child: Padding(
                                padding: const EdgeInsets.symmetric(
                                    horizontal: 14, vertical: 16),
                                child: Row(
                                  mainAxisAlignment:
                                      MainAxisAlignment.spaceBetween,
                                  children: [
                                    Text(
                                      formatMediumDate(_selectedDate),
                                      style: GoogleFonts.sora(
                                          fontSize: 15, color: _textGhost),
                                    ),
                                    const Icon(Icons.calendar_today_outlined,
                                        size: 16, color: _textMuted),
                                  ],
                                ),
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 14),

                _fieldLabel('Category'),
                _GlassCard(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 14),
                    child: DropdownButtonHideUnderline(
                      child: DropdownButton<TransactionCategory>(
                        value: _selectedCategory,
                        isExpanded: true,
                        dropdownColor: _sheetBg,
                        icon: const Icon(Icons.keyboard_arrow_down,
                            color: _textMuted),
                        style:
                            GoogleFonts.sora(fontSize: 15, color: _textGhost),
                        items: TransactionCategory.values.map((c) {
                          return DropdownMenuItem<TransactionCategory>(
                            value: c,
                            child: Text(c.displayName,
                                overflow: TextOverflow.ellipsis),
                          );
                        }).toList(),
                        onChanged: (c) => setState(() =>
                            _selectedCategory = c ?? _selectedCategory),
                      ),
                    ),
                  ),
                ),
                const SizedBox(height: 14),

                _fieldLabel('Notes (optional)'),
                _textField(
                  controller: _notesController,
                  hint: 'Anything worth remembering',
                  maxLines: 2,
                ),

                if (_error != null) ...[
                  const SizedBox(height: 16),
                  Text(
                    _error!,
                    style: GoogleFonts.sora(fontSize: 13, color: _expense),
                  ),
                ],

                const SizedBox(height: 24),
                SizedBox(
                  width: double.infinity,
                  child: ElevatedButton(
                    key: const Key('addtx_submit'),
                    onPressed: _isSaving ? null : _submit,
                    style: ElevatedButton.styleFrom(
                      backgroundColor: _accent,
                      disabledBackgroundColor: _accent.withValues(alpha: 0.4),
                      padding: const EdgeInsets.symmetric(vertical: 16),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(14),
                      ),
                    ),
                    child: _isSaving
                        ? const SizedBox(
                            height: 20,
                            width: 20,
                            child: CircularProgressIndicator(
                                strokeWidth: 2, color: Colors.white),
                          )
                        : Text(
                            AppLocalizations.of(context).txnAddTransaction,
                            style: GoogleFonts.sora(
                              fontSize: 15,
                              fontWeight: FontWeight.w600,
                              color: Colors.white,
                            ),
                          ),
                  ),
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }

  Future<void> _pickDate() async {
    final picked = await showDatePicker(
      context: context,
      initialDate: _selectedDate,
      firstDate: DateTime.now().subtract(const Duration(days: 365 * 5)),
      lastDate: DateTime.now(),
    );
    if (picked != null) setState(() => _selectedDate = picked);
  }

  Widget _fieldLabel(String text) => Padding(
        padding: const EdgeInsets.only(bottom: 6, left: 2),
        child: Text(
          text,
          style: GoogleFonts.sora(
            fontSize: 12,
            color: _textMuted,
            fontWeight: FontWeight.w500,
          ),
        ),
      );

  Widget _textField({
    required TextEditingController controller,
    required String hint,
    TextInputType? keyboardType,
    String? prefixText,
    Color? prefixColor,
    bool mono = false,
    int maxLines = 1,
    Key? fieldKey,
  }) {
    final baseStyle = mono
        ? GoogleFonts.firaCode(fontSize: 15, color: _textGhost)
        : GoogleFonts.sora(fontSize: 15, color: _textGhost);
    return TextField(
      key: fieldKey,
      controller: controller,
      keyboardType: keyboardType,
      maxLines: maxLines,
      style: baseStyle,
      decoration: InputDecoration(
        hintText: hint,
        hintStyle: (mono
                ? GoogleFonts.firaCode(fontSize: 15)
                : GoogleFonts.sora(fontSize: 15))
            .copyWith(color: _textMuted.withValues(alpha: 0.5)),
        prefixText: prefixText,
        prefixStyle: GoogleFonts.firaCode(
          fontSize: 15,
          color: prefixColor ?? _textGhost,
          fontWeight: FontWeight.w600,
        ),
        filled: true,
        fillColor: _cardBg,
        contentPadding:
            const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: const BorderSide(color: _cardBorder),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: _accent.withValues(alpha: 0.6)),
        ),
      ),
    );
  }
}

class _TypeToggle extends StatelessWidget {
  final String label;
  final bool selected;
  final Color color;
  final VoidCallback onTap;

  const _TypeToggle({
    required this.label,
    required this.selected,
    required this.color,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 160),
        padding: const EdgeInsets.symmetric(vertical: 13),
        decoration: BoxDecoration(
          borderRadius: BorderRadius.circular(12),
          color: selected ? color.withValues(alpha: 0.18) : _cardBg,
          border: Border.all(
            color: selected ? color : _cardBorder,
          ),
        ),
        child: Text(
          label,
          textAlign: TextAlign.center,
          style: GoogleFonts.sora(
            fontSize: 14,
            fontWeight: FontWeight.w600,
            color: selected ? color : _textMuted,
          ),
        ),
      ),
    );
  }
}

