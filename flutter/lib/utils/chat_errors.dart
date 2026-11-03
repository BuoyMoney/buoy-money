/// Why an AI chat message failed to send, and what to tell the user.
///
/// The chat page hand-rolls its SSE request, so failures arrive as exceptions. These map them to
/// honest messages: a plan refusal offers an upgrade (retrying never helps), and the per-minute
/// 429 says to slow down. The hourly and daily caps are not here: the server sends those inside
/// the stream and the bubble shows its text.
library;

/// Thrown when the server refuses chat because the account's plan does not include it
/// (403 with error code `PremiumRequired`).
class ChatUpgradeRequired implements Exception {
  const ChatUpgradeRequired();

  @override
  String toString() => 'ChatUpgradeRequired';
}

/// The message to show for a failed send, and whether to offer the plans page.
class ChatSendError {
  final String message;
  final bool offerUpgrade;
  const ChatSendError(this.message, {this.offerUpgrade = false});
}

ChatSendError describeChatSendError(Object error, {bool isRetry = false}) {
  if (error is ChatUpgradeRequired) {
    return const ChatSendError('AI chat is part of Premium.', offerUpgrade: true);
  }
  if (error.toString().contains('429')) {
    return const ChatSendError(
        "You're sending messages quickly. Wait a minute, then tap retry.");
  }
  return ChatSendError(isRetry
      ? 'Retry failed. Please try again.'
      : 'Failed to send message. Tap retry to try again.');
}
