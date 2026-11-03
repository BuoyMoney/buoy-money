import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/services/review_prompt_service.dart';

/// Fixed clock shared by the service under test and the fixtures.
final kNow = DateTime(2026, 9, 2, 12);

class _FakeReviewer implements StoreReviewer {
  bool available = true;
  int requests = 0;
  int listings = 0;

  @override
  Future<bool> isAvailable() async => available;

  @override
  Future<void> requestReview() async => requests++;

  @override
  Future<void> openStoreListing() async => listings++;
}

/// Builds a service whose clock we control and whose device looks like it was
/// first seen [daysAgo] days ago with [sessions] sessions already logged.
Future<(ReviewPromptService, _FakeReviewer, DateTime)> _service({
  int daysAgo = 30,
  int sessions = 10,
  List<DateTime> priorPrompts = const [],
  DateTime? lastNegative,
}) async {
  final now = kNow;
  SharedPreferences.setMockInitialValues({
    'rp_first_seen': now.subtract(Duration(days: daysAgo)).toIso8601String(),
    'rp_sessions': sessions,
    'rp_last_session': now.toIso8601String(), // debounce: this call adds no session
    if (priorPrompts.isNotEmpty)
      'rp_prompt_times':
          '[${priorPrompts.map((t) => '"${t.toIso8601String()}"').join(',')}]',
    if (lastNegative != null) 'rp_last_negative': lastNegative.toIso8601String(),
  });
  final reviewer = _FakeReviewer();
  final svc = ReviewPromptService(
    reviewer: reviewer,
    now: () => now,
    settle: Duration.zero,
    isWeb: false,
    prefs: await SharedPreferences.getInstance(),
  );
  return (svc, reviewer, now);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('ReviewPromptService eligibility', () {
    test('asks after a win on a seasoned device', () async {
      final (svc, reviewer, _) = await _service();
      expect(await svc.maybeRequest(ReviewMoment.badgeUnlocked), isTrue);
      expect(reviewer.requests, 1);
    });

    test('never on web', () async {
      SharedPreferences.setMockInitialValues({});
      final reviewer = _FakeReviewer();
      final svc = ReviewPromptService(
          reviewer: reviewer, isWeb: true, settle: Duration.zero,
          prefs: await SharedPreferences.getInstance());
      expect(await svc.evaluate(), ReviewSkipReason.web);
    });

    test('too new: under 7 days since first seen', () async {
      final (svc, _, _) = await _service(daysAgo: 3);
      expect(await svc.evaluate(), ReviewSkipReason.tooNew);
    });

    test('too few sessions', () async {
      final (svc, _, _) = await _service(sessions: 2);
      expect(await svc.evaluate(), ReviewSkipReason.tooFewSessions);
    });

    test('90-day cooldown after a prompt', () async {
      final (svc, _, _) = await _service(
          priorPrompts: [kNow.subtract(const Duration(days: 30))]);
      expect(await svc.evaluate(), ReviewSkipReason.cooldown);
    });

    test('at most three prompts per rolling year', () async {
      final (svc, _, _) = await _service(priorPrompts: [
        kNow.subtract(const Duration(days: 300)),
        kNow.subtract(const Duration(days: 200)),
        kNow.subtract(const Duration(days: 100)),
      ]);
      expect(await svc.evaluate(), ReviewSkipReason.yearlyQuota);
    });

    test('quiet period after an oops moment', () async {
      final (svc, _, _) = await _service(
          lastNegative: kNow.subtract(const Duration(hours: 2)));
      expect(await svc.evaluate(), ReviewSkipReason.recentNegativeMoment);
    });

    test('quiet period expires after 24h', () async {
      final (svc, _, _) = await _service(
          lastNegative: kNow.subtract(const Duration(hours: 30)));
      expect(await svc.evaluate(), isNull);
    });

    test('platform unavailable: no request, no state change', () async {
      final (svc, reviewer, _) = await _service();
      reviewer.available = false;
      expect(await svc.maybeRequest(ReviewMoment.wrappedFinished), isFalse);
      expect(reviewer.requests, 0);
      expect(await svc.evaluate(), isNull, reason: 'nothing was recorded');
    });

    test('a request starts the cooldown', () async {
      final (svc, reviewer, _) = await _service();
      await svc.maybeRequest(ReviewMoment.badgeUnlocked);
      expect(await svc.evaluate(), ReviewSkipReason.cooldown);
      expect(await svc.maybeRequest(ReviewMoment.wrappedFinished), isFalse);
      expect(reviewer.requests, 1);
    });

    test('recordNegativeMoment suppresses an otherwise-eligible ask', () async {
      final (svc, reviewer, _) = await _service();
      await svc.recordNegativeMoment();
      expect(await svc.maybeRequest(ReviewMoment.badgeUnlocked), isFalse);
      expect(reviewer.requests, 0);
    });

    test('openStoreListing is quota-free', () async {
      final (svc, reviewer, _) = await _service(sessions: 0, daysAgo: 0);
      await svc.openStoreListing();
      await svc.openStoreListing();
      expect(reviewer.listings, 2);
    });
  });
}
