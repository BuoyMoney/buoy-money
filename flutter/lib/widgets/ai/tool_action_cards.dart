import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import '../../l10n/generated/app_localizations.dart';
import '../../services/ai_service.dart';
import '../../utils/formatters.dart';

const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _confirmBg = Color(0x1A7B61FF);
const _confirmBorder = Color(0x407B61FF);
const _destructiveBg = Color(0x1AF87171);
const _destructiveBorder = Color(0x40F87171);

const _toolDisplayNames = <String, String>{
  'get_accounts': 'Retrieved Accounts',
  'get_transactions': 'Retrieved Transactions',
  'get_spending_summary': 'Analyzed Spending',
  'get_budgets': 'Retrieved Budgets',
  'get_budget_overview': 'Analyzed Budgets',
  'get_countup_categories': 'Count-Up Categories',
  'get_savings_goals': 'Retrieved Savings',
  'get_savings_overview': 'Analyzed Savings',
  'get_subscriptions': 'Retrieved Subscriptions',
  'get_subscription_summary': 'Analyzed Subscriptions',
  'get_income_summary': 'Analyzed Income',
  'get_recurring_income': 'Recurring Income',
  'get_user_preferences': 'Retrieved Preferences',
  'get_user_context': 'Gathered Context',
  'get_user_profile': 'Retrieved Profile',
  'create_budget_category': 'Create Budget Category',
  'update_budget_amount': 'Update Budget Amount',
  'toggle_category_settings': 'Update Category Settings',
  'create_savings_goal': 'Create Savings Goal',
  'add_to_savings_goal': 'Add to Savings Goal',
  'update_savings_goal': 'Update Savings Goal',
  'categorize_transaction': 'Categorize Transaction',
  'delete_transaction': 'Delete Transaction',
  'update_transaction_date': 'Change Transaction Date',
};

/// Read-only tools whose names don't start with `get_`.
const _otherReadTools = <String>{
  'search_help',
  'search_chat_history',
  'recall_memory',
  'detect_subscriptions',
  'analyze_merchant_for_subscription',
};

String formatToolName(String toolName) {
  if (_toolDisplayNames.containsKey(toolName)) {
    return _toolDisplayNames[toolName]!;
  }
  return toolName
      .replaceAll(RegExp(r'^(get|create|update|add|toggle|delete)_'), '')
      .split('_')
      .map((w) => w.isEmpty ? '' : '${w[0].toUpperCase()}${w.substring(1)}')
      .join(' ');
}

bool isReadTool(String toolName) =>
    toolName.startsWith('get_') || _otherReadTools.contains(toolName);

Map<String, dynamic>? _decodeObject(String? text) {
  if (text == null || text.isEmpty) return null;
  try {
    final decoded = jsonDecode(text);
    return decoded is Map<String, dynamic> ? decoded : null;
  } catch (_) {
    return null;
  }
}

double? _number(Object? value) => value is num ? value.toDouble() : null;

/// A write tool's preview: the JSON the server returns, with
/// `RequiresConfirmation: true`, before the user has said yes.
class ToolPreview {
  /// The server's plain-English summary, e.g. "Change 'Dining Out' from
  /// $450.00 to $300.00/month".
  final String? description;
  final double? before;
  final double? after;
  final String? warning;

  const ToolPreview({this.description, this.before, this.after, this.warning});

  factory ToolPreview.parse(String result) {
    final json = _decodeObject(result);
    if (json == null) return const ToolPreview();
    final preview = json['Preview'];
    final fields =
        preview is Map<String, dynamic> ? preview : const <String, dynamic>{};
    return ToolPreview(
      description: json['Description'] as String? ?? json['Message'] as String?,
      before: _number(fields['CurrentAmount']),
      // A budget change has NewAmount; money added to a goal has NewTotal.
      after: _number(fields['NewAmount']) ?? _number(fields['NewTotal']),
      warning: json['Warning'] as String?,
    );
  }

  /// True when [result] is a preview waiting for the user, not something that ran.
  static bool isPreview(String result) =>
      _decodeObject(result)?['RequiresConfirmation'] == true;
}

/// What a confirmed write returned. The execute endpoint answers 200 even when
/// the tool itself refused (e.g. "Budget category not found"), so this reads
/// the tool's own JSON.
class ToolOutcome {
  final bool success;
  final String message;

  const ToolOutcome({required this.success, required this.message});

  factory ToolOutcome.fromResult(String? result) {
    final json = _decodeObject(result);
    if (json == null) return const ToolOutcome(success: true, message: 'Done.');
    final error = json['Error'] as String?;
    if (error != null) return ToolOutcome(success: false, message: error);
    final message = json['Message'] as String?;
    if (json['Success'] == false) {
      return ToolOutcome(
          success: false, message: message ?? 'It didn\'t go through.');
    }
    return ToolOutcome(success: true, message: message ?? 'Done.');
  }
}

/// The bubble text once a confirmation is settled: the server's "Confirmation
/// Required" header and its "Would you like me to proceed" question give way to
/// [outcome], so the message no longer asks a question that has been answered.
String settleConfirmationText(String content, String outcome) {
  final kept = content
      .split('\n')
      .where((line) {
        final t = line.trim();
        return t != '📋 **Confirmation Required**' &&
            !t.startsWith('Would you like me to proceed');
      })
      .join('\n')
      .trim();
  return kept.isEmpty ? outcome : '$kept\n\n$outcome';
}

/// "ACTIONS TAKEN" above an assistant message: read tools as chips, and each
/// write that actually ran as a Complete row. A preview still waiting for the
/// user's Confirm tap is not an action taken, so it isn't listed.
class ToolActionSummary extends StatelessWidget {
  final List<ToolCallResult> toolResults;
  const ToolActionSummary({super.key, required this.toolResults});

  @override
  Widget build(BuildContext context) {
    final ran =
        toolResults.where((r) => !ToolPreview.isPreview(r.result)).toList();
    if (ran.isEmpty) return const SizedBox.shrink();
    final readOps = ran.where((r) => isReadTool(r.toolName)).toList();
    final writeOps = ran.where((r) => !isReadTool(r.toolName)).toList();

    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _cardBorder),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            AppLocalizations.of(context).aiChatActionsTaken,
            style: GoogleFonts.sora(
              color: _textMuted,
              fontSize: 10,
              fontWeight: FontWeight.w700,
              letterSpacing: 1.2,
            ),
          ),
          if (readOps.isNotEmpty) ...[
            const SizedBox(height: 8),
            Wrap(
              spacing: 6,
              runSpacing: 4,
              children: readOps.map((r) {
                return Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                  decoration: BoxDecoration(
                    color: const Color(0xFF60A5FA).withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    formatToolName(r.toolName),
                    style: GoogleFonts.sora(
                      color: const Color(0xFF60A5FA),
                      fontSize: 10,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                );
              }).toList(),
            ),
          ],
          for (final w in writeOps) ...[
            const SizedBox(height: 6),
            Row(
              children: [
                const Icon(Icons.check_circle, size: 14, color: _income),
                const SizedBox(width: 6),
                Text(
                  formatToolName(w.toolName),
                  style: GoogleFonts.sora(
                    color: _textGhost,
                    fontSize: 12,
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const Spacer(),
                Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(
                    color: _income.withValues(alpha: 0.15),
                    borderRadius: BorderRadius.circular(6),
                  ),
                  child: Text(
                    AppLocalizations.of(context).aiChatComplete,
                    style: GoogleFonts.sora(
                      color: _income,
                      fontSize: 9,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

/// The Confirm / Cancel card for a write the assistant wants to make. It shows
/// the change itself (the server's description and, where there is one, the
/// amount before and after), not just the tool's name.
class ToolConfirmationCard extends StatefulWidget {
  final ToolCallResult toolResult;
  final bool isLoading;
  final VoidCallback onConfirm;
  final VoidCallback onCancel;

  const ToolConfirmationCard({
    super.key,
    required this.toolResult,
    required this.isLoading,
    required this.onConfirm,
    required this.onCancel,
  });

  @override
  State<ToolConfirmationCard> createState() => _ToolConfirmationCardState();
}

class _ToolConfirmationCardState extends State<ToolConfirmationCard> {
  bool _showDetails = false;

  bool get _isDestructive => widget.toolResult.toolName == 'delete_transaction';

  Color get _borderCol => _isDestructive ? _destructiveBorder : _confirmBorder;
  Color get _bgCol => _isDestructive ? _destructiveBg : _confirmBg;
  Color get _accentCol => _isDestructive ? _expense : _accent;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final displayName = formatToolName(widget.toolResult.toolName);
    final preview = ToolPreview.parse(widget.toolResult.result);
    final details = widget.toolResult.arguments.entries
        .where((e) => e.key.toLowerCase() != 'confirmed')
        .toList();

    return Container(
      margin: const EdgeInsets.only(top: 10),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: _bgCol,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _borderCol, width: 2),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.info_outline, color: _accentCol, size: 18),
              const SizedBox(width: 8),
              Text(
                l10n.aiChatConfirmAction,
                style: GoogleFonts.sora(
                  color: _textGhost,
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                ),
              ),
              const Spacer(),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                decoration: BoxDecoration(
                  color: _accentCol.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  displayName,
                  style: GoogleFonts.sora(
                    color: _accentCol,
                    fontSize: 10,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          Text(
            preview.description ?? l10n.aiChatAiWouldLikeTo(displayName),
            style: GoogleFonts.sora(
              color: _textGhost,
              fontSize: 13,
              fontWeight: FontWeight.w500,
              height: 1.4,
            ),
          ),
          if (preview.before != null && preview.after != null) ...[
            const SizedBox(height: 10),
            Row(
              children: [
                Text(
                  formatCurrency(preview.before!),
                  style: GoogleFonts.sora(
                    color: _textMuted,
                    fontSize: 16,
                    fontWeight: FontWeight.w500,
                    decoration: TextDecoration.lineThrough,
                    decorationColor: _textMuted,
                  ),
                ),
                const Padding(
                  padding: EdgeInsets.symmetric(horizontal: 8),
                  child: Icon(Icons.arrow_forward, size: 16, color: _textMuted),
                ),
                Text(
                  formatCurrency(preview.after!),
                  style: GoogleFonts.sora(
                    color: _textGhost,
                    fontSize: 18,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ],
            ),
          ],
          if (preview.warning != null) ...[
            const SizedBox(height: 8),
            Text(
              preview.warning!,
              style: GoogleFonts.sora(
                color: _expense,
                fontSize: 11,
                fontWeight: FontWeight.w600,
              ),
            ),
          ],
          if (details.isNotEmpty)
            GestureDetector(
              onTap: () => setState(() => _showDetails = !_showDetails),
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Row(
                  children: [
                    Text(
                      _showDetails
                          ? l10n.aiChatHideDetails
                          : l10n.aiChatShowDetails,
                      style: GoogleFonts.sora(
                          color: _textMuted,
                          fontSize: 11,
                          fontWeight: FontWeight.w500),
                    ),
                    Icon(
                      _showDetails
                          ? Icons.keyboard_arrow_up
                          : Icons.keyboard_arrow_down,
                      color: _textMuted,
                      size: 16,
                    ),
                  ],
                ),
              ),
            ),
          if (_showDetails && details.isNotEmpty)
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: const Color(0x08FFFFFF),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Column(
                children: details.map((e) {
                  return Padding(
                    padding: const EdgeInsets.symmetric(vertical: 2),
                    child: Row(
                      mainAxisAlignment: MainAxisAlignment.spaceBetween,
                      children: [
                        Text(
                          e.key,
                          style:
                              GoogleFonts.sora(color: _textMuted, fontSize: 11),
                        ),
                        Text(
                          '${e.value}',
                          style: GoogleFonts.sora(
                            color: _textGhost,
                            fontSize: 11,
                            fontWeight: FontWeight.w500,
                          ),
                        ),
                      ],
                    ),
                  );
                }).toList(),
              ),
            ),
          const SizedBox(height: 10),
          Row(
            mainAxisAlignment: MainAxisAlignment.end,
            children: [
              GestureDetector(
                onTap: widget.isLoading ? null : widget.onCancel,
                child: Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 14, vertical: 7),
                  decoration: BoxDecoration(
                    borderRadius: BorderRadius.circular(8),
                    border: Border.all(color: _cardBorder),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(Icons.close, size: 14, color: _textMuted),
                      const SizedBox(width: 4),
                      Text(
                        l10n.aiChatCancel,
                        style:
                            GoogleFonts.sora(color: _textMuted, fontSize: 12),
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(width: 8),
              GestureDetector(
                onTap: widget.isLoading ? null : widget.onConfirm,
                child: Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 14, vertical: 7),
                  decoration: BoxDecoration(
                    color: _accentCol,
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: widget.isLoading
                      ? const SizedBox(
                          width: 14,
                          height: 14,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: _textGhost,
                          ),
                        )
                      : Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            const Icon(Icons.check,
                                size: 14, color: _textGhost),
                            const SizedBox(width: 4),
                            Text(
                              l10n.aiChatConfirm,
                              style: GoogleFonts.sora(
                                color: _textGhost,
                                fontSize: 12,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                          ],
                        ),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
