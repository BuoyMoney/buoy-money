import 'package:buoy_ai/utils/chat_errors.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('describeChatSendError', () {
    test('a plan refusal offers the plans page instead of a retry', () {
      final d = describeChatSendError(const ChatUpgradeRequired());
      expect(d.offerUpgrade, isTrue);
      expect(d.message, contains('Premium'));
      expect(d.message, isNot(contains('retry')));
    });

    test('the per-minute 429 says to slow down, not that a daily limit was hit', () {
      final d = describeChatSendError(Exception('429: Rate limit exceeded'));
      expect(d.offerUpgrade, isFalse);
      expect(d.message, contains('Wait a minute'));
      expect(d.message.toLowerCase(), isNot(contains('daily')));
    });

    test('anything else keeps the generic retry message', () {
      expect(describeChatSendError(Exception('HTTP 500: boom')).message,
          'Failed to send message. Tap retry to try again.');
      expect(describeChatSendError(Exception('HTTP 500: boom'), isRetry: true).message,
          'Retry failed. Please try again.');
    });
  });
}
