import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:in_app_review/in_app_review.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'analytics_service.dart';

/// The moments that may earn a store-review prompt. Every one of these is a
/// WIN the user just experienced (Hooked: ask right after the variable reward
/// lands), never a task, a launch, or a stress point.
enum ReviewMoment { badgeUnlocked, wrappedFinished }

/// Thin seam over the platform review APIs so the timing logic is testable.
abstract class StoreReviewer {
  Future<bool> isAvailable();
  Future<void> requestReview();
  Future<void> openStoreListing();
}

class InAppStoreReviewer implements StoreReviewer {
  /// Apple app ID (numeric) for openStoreListing on iOS. Empty until the App
  /// Store listing exists; the Settings "Rate BuoyAI" row hides on iOS while
  /// it is empty.
  static const appStoreId = '';

  final InAppReview _review = InAppReview.instance;

  @override
  Future<bool> isAvailable() => _review.isAvailable();

  @override
  Future<void> requestReview() => _review.requestReview();

  @override
  Future<void> openStoreListing() =>
      _review.openStoreListing(appStoreId: appStoreId.isEmpty ? null : appStoreId);
}

/// Why a prompt was NOT shown; surfaced to analytics so trigger tuning is
/// data-driven rather than guessed.
enum ReviewSkipReason {
  web,
  tooNew,
  tooFewSessions,
  cooldown,
  yearlyQuota,
  recentNegativeMoment,
  unavailable,
}

/// Store-review prompting done by the book, both the platforms' and ours.
///
/// Platform rules: Google Play's In-App Review guidelines forbid asking
/// questions before the prompt ("Do you like the app?" is their own example)
/// and Apple 1.1.7 disallows custom review prompts. So there is NO pre-screen
/// here. The filter is behavioral instead: only users who have demonstrably
/// stuck around (days + sessions) get asked, and only in the seconds after a
/// win they just felt. That is also the Mom Test answer: "are you enjoying
/// it?" is compliment-fishing; returning for a week and earning a badge is
/// the real answer.
///
/// The OS owns the dialog and its outcome; we never learn whether a review
/// was written, so the only levers are eligibility and timing. All state is
/// per-device (SharedPreferences), which matches how the OS quotas work.
class ReviewPromptService {
  static const minDaysSinceFirstSeen = 7;
  static const minSessions = 5;
  static const cooldown = Duration(days: 90);
  static const maxPromptsPerYear = 3; // iOS enforces this itself; mirrored so Android matches
  static const negativeQuietPeriod = Duration(hours: 24);
  static const sessionDebounce = Duration(minutes: 30);

  static const _kFirstSeen = 'rp_first_seen';
  static const _kSessions = 'rp_sessions';
  static const _kLastSession = 'rp_last_session';
  static const _kPromptTimes = 'rp_prompt_times';
  static const _kLastNegative = 'rp_last_negative';

  final StoreReviewer _reviewer;
  final DateTime Function() _now;
  final Duration _settle;
  final bool _isWeb;
  SharedPreferences? _prefs;
  bool _launchCounted = false;

  ReviewPromptService({
    StoreReviewer? reviewer,
    DateTime Function()? now,
    Duration settle = const Duration(milliseconds: 1500),
    bool? isWeb,
    SharedPreferences? prefs,
  })  : _reviewer = reviewer ?? InAppStoreReviewer(),
        _now = now ?? DateTime.now,
        _settle = settle,
        _isWeb = isWeb ?? kIsWeb,
        _prefs = prefs;

  Future<SharedPreferences> _load() async {
    final p = _prefs ??= await SharedPreferences.getInstance();
    if (!p.containsKey(_kFirstSeen)) {
      await p.setString(_kFirstSeen, _now().toIso8601String());
    }
    if (!_launchCounted) {
      _launchCounted = true;
      await _bumpSession(p);
    }
    return p;
  }

  Future<void> _bumpSession(SharedPreferences p) async {
    final last = DateTime.tryParse(p.getString(_kLastSession) ?? '');
    final now = _now();
    if (last != null && now.difference(last) < sessionDebounce) return;
    await p.setInt(_kSessions, (p.getInt(_kSessions) ?? 0) + 1);
    await p.setString(_kLastSession, now.toIso8601String());
  }

  /// Call on app resume; the first call per process also counts the launch.
  Future<void> recordSession() async {
    final p = await _load();
    await _bumpSession(p);
  }

  /// A moment we must NOT follow with an ask (an oops badge, an error, a
  /// failed bank link). Suppresses prompts for [negativeQuietPeriod].
  Future<void> recordNegativeMoment() async {
    final p = await _load();
    await p.setString(_kLastNegative, _now().toIso8601String());
  }

  /// Pure eligibility check, exposed for tests and for analytics reasons.
  Future<ReviewSkipReason?> evaluate() async {
    if (_isWeb) return ReviewSkipReason.web;
    final p = await _load();
    final now = _now();

    final firstSeen = DateTime.tryParse(p.getString(_kFirstSeen) ?? '') ?? now;
    if (now.difference(firstSeen).inDays < minDaysSinceFirstSeen) {
      return ReviewSkipReason.tooNew;
    }
    if ((p.getInt(_kSessions) ?? 0) < minSessions) {
      return ReviewSkipReason.tooFewSessions;
    }

    final negative = DateTime.tryParse(p.getString(_kLastNegative) ?? '');
    if (negative != null && now.difference(negative) < negativeQuietPeriod) {
      return ReviewSkipReason.recentNegativeMoment;
    }

    final prompts = _promptTimes(p);
    if (prompts.isNotEmpty && now.difference(prompts.last) < cooldown) {
      return ReviewSkipReason.cooldown;
    }
    final lastYear = prompts.where((t) => now.difference(t).inDays < 365).length;
    if (lastYear >= maxPromptsPerYear) {
      return ReviewSkipReason.yearlyQuota;
    }
    return null;
  }

  /// Ask for a review if, and only if, this device has earned the ask.
  /// Returns true when the OS prompt was requested (the OS may still decline
  /// to show it; that is invisible to us by design).
  Future<bool> maybeRequest(ReviewMoment moment) async {
    final skip = await evaluate();
    if (skip != null) {
      _track('review_prompt_skipped', moment, {'reason': skip.name});
      return false;
    }
    if (!await _reviewer.isAvailable()) {
      _track('review_prompt_skipped', moment, {'reason': ReviewSkipReason.unavailable.name});
      return false;
    }

    // Let the win land before the ask; interrupting the celebration reads as
    // a shakedown, a beat of stillness reads as a natural "by the way".
    if (_settle > Duration.zero) {
      await Future<void>.delayed(_settle);
    }

    final p = await _load();
    final times = _promptTimes(p)..add(_now());
    await p.setString(_kPromptTimes, jsonEncode(times.map((t) => t.toIso8601String()).toList()));
    _track('review_prompt_requested', moment, {
      'sessions': p.getInt(_kSessions) ?? 0,
      'prompt_number': times.length,
    });
    await _reviewer.requestReview();
    return true;
  }

  /// Quota-free store listing, for a persistent "Rate BuoyAI" entry point.
  Future<void> openStoreListing() => _reviewer.openStoreListing();

  List<DateTime> _promptTimes(SharedPreferences p) {
    final raw = p.getString(_kPromptTimes);
    if (raw == null) return [];
    try {
      return (jsonDecode(raw) as List)
          .map((e) => DateTime.tryParse(e.toString()))
          .whereType<DateTime>()
          .toList()
        ..sort();
    } catch (_) {
      return [];
    }
  }

  void _track(String event, ReviewMoment moment, Map<String, Object> extra) {
    unawaited(analytics.track(event, {'moment': moment.name, ...extra}));
  }
}

/// App-wide instance (mirrors the `analytics` global).
final ReviewPromptService reviewPrompts = ReviewPromptService();
