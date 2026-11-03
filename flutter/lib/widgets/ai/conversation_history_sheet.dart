import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import '../../l10n/generated/app_localizations.dart';
import '../../services/ai_service.dart';
import '../app_toast.dart';

const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBorder = Color(0x14FFFFFF);
const _sheetBg = Color(0xFF0C0C18);
const _expense = Color(0xFFF87171);

/// What the owner picked in the history sheet. A null [sessionId] means start a
/// new conversation.
class ConversationHistoryChoice {
  final String? sessionId;
  const ConversationHistoryChoice.open(String this.sessionId);
  const ConversationHistoryChoice.newChat() : sessionId = null;
}

/// The AI chat's conversation history. It loads the list itself, so the list
/// appears when it arrives and a delete updates it in place. (The chat page used
/// to hand it a snapshot taken while still loading, and a modal route doesn't
/// rebuild on the page's setState, so the sheet spun forever.)
///
/// It closes itself with its own context and returns a [ConversationHistoryChoice];
/// it never calls back into the chat page's context, which may already be gone.
class ConversationHistorySheet extends StatefulWidget {
  final String? currentSessionId;
  final Future<List<AiConversation>> Function() loadConversations;
  final Future<void> Function(String sessionId) deleteConversation;

  /// Called after a conversation is deleted, so the page can reset if it was open.
  final void Function(String sessionId)? onDeleted;

  const ConversationHistorySheet({
    super.key,
    this.currentSessionId,
    required this.loadConversations,
    required this.deleteConversation,
    this.onDeleted,
  });

  @override
  State<ConversationHistorySheet> createState() =>
      _ConversationHistorySheetState();
}

class _ConversationHistorySheetState extends State<ConversationHistorySheet> {
  List<AiConversation>? _conversations; // null while loading

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    List<AiConversation> list;
    try {
      list = await widget.loadConversations();
    } catch (_) {
      list = const [];
    }
    if (!mounted) return;
    setState(() => _conversations = list);
  }

  Future<void> _delete(String sessionId) async {
    try {
      await widget.deleteConversation(sessionId);
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to delete conversation',
            type: AppToastType.error);
      }
      return;
    }
    widget.onDeleted?.call(sessionId);
    if (!mounted) return;
    setState(() => _conversations =
        _conversations?.where((c) => c.sessionId != sessionId).toList());
    showAppToast(context, 'Conversation deleted',
        type: AppToastType.success, duration: const Duration(seconds: 2));
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final conversations = _conversations;
    return Container(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.of(context).size.height * 0.75,
      ),
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        border: Border(top: BorderSide(color: _cardBorder)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Center(
            child: Container(
              margin: const EdgeInsets.only(top: 12),
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: _textMuted.withValues(alpha: 0.3),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          // Header
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 12),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(
                  l10n.aiChatHistoryTitle,
                  style: GoogleFonts.instrumentSerif(
                    color: _textGhost,
                    fontSize: 22,
                    fontWeight: FontWeight.bold,
                  ),
                ),
                GestureDetector(
                  onTap: () => Navigator.of(context)
                      .pop(const ConversationHistoryChoice.newChat()),
                  child: Container(
                    padding:
                        const EdgeInsets.symmetric(horizontal: 14, vertical: 7),
                    decoration: BoxDecoration(
                      gradient: const LinearGradient(
                        colors: [_accent, Color(0xFF9F7AEA)],
                      ),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: Text(
                      l10n.aiChatNewChat,
                      style: GoogleFonts.sora(
                        color: _textGhost,
                        fontSize: 12,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                ),
              ],
            ),
          ),
          const Divider(color: _cardBorder, height: 1),
          // List
          Flexible(
            // ListTile paints its tileColor on the nearest Material; without one here
            // it paints under the sheet's own background and the current row never shows.
            child: Material(
              type: MaterialType.transparency,
              child: conversations == null
                  ? const Center(
                      child: Padding(
                        padding: EdgeInsets.all(32),
                        child: CircularProgressIndicator(color: _accent),
                      ),
                    )
                  : conversations.isEmpty
                      ? Padding(
                          padding: const EdgeInsets.all(32),
                          child: Text(
                            l10n.aiChatNoPreviousConversations,
                            style: GoogleFonts.sora(
                                color: _textMuted, fontSize: 14),
                          ),
                        )
                      : ListView.separated(
                          shrinkWrap: true,
                          padding: const EdgeInsets.symmetric(vertical: 8),
                          itemCount: conversations.length,
                          separatorBuilder: (_, __) =>
                              const Divider(color: _cardBorder, height: 1),
                          itemBuilder: (_, i) {
                            final conv = conversations[i];
                            final isCurrent =
                                conv.sessionId == widget.currentSessionId;
                            return ListTile(
                              tileColor: isCurrent
                                  ? _accent.withValues(alpha: 0.08)
                                  : Colors.transparent,
                              title: Text(
                                conv.title ?? l10n.aiChatUntitledConversation,
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: GoogleFonts.sora(
                                  color: _textGhost,
                                  fontSize: 13,
                                  fontWeight: FontWeight.w500,
                                ),
                              ),
                              subtitle: Text(
                                l10n.aiChatConversationSubtitle(
                                    _formatDate(conv.lastMessageAt),
                                    conv.messageCount),
                                style: GoogleFonts.sora(
                                    color: _textMuted, fontSize: 11),
                              ),
                              trailing: IconButton(
                                icon: const Icon(Icons.delete_outline,
                                    size: 18, color: _expense),
                                onPressed: () => _delete(conv.sessionId),
                              ),
                              onTap: () => Navigator.of(context).pop(
                                  ConversationHistoryChoice.open(
                                      conv.sessionId)),
                            );
                          },
                        ),
            ),
          ),
        ],
      ),
    );
  }

  String _formatDate(DateTime dt) {
    const months = [
      'Jan',
      'Feb',
      'Mar',
      'Apr',
      'May',
      'Jun',
      'Jul',
      'Aug',
      'Sep',
      'Oct',
      'Nov',
      'Dec',
    ];
    return '${months[dt.month - 1]} ${dt.day}';
  }
}
