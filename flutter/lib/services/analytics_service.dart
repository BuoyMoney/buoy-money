import 'package:flutter/foundation.dart' show debugPrint, kDebugMode;
import 'package:posthog_flutter/posthog_flutter.dart';
import 'package:shared_preferences/shared_preferences.dart';
import '../config/app_config.dart';
import 'consent_store.dart'
    if (dart.library.js_interop) 'consent_store_web.dart';

/// Centralized PostHog analytics service
///
/// Setup: Pass PostHog API key via --dart-define:
///   flutter run --dart-define=POSTHOG_KEY=phc_xxx
///
/// Auto-tracks screen views via PosthogObserver on GoRouter.
/// Call identify() after login, reset() on logout.

// ── Event names ────────
class AnalyticsEvents {
  AnalyticsEvents._();

  // Authentication
  static const signupStarted = 'signup_started';
  static const signupCompleted = 'signup_completed';
  static const loginCompleted = 'login_completed';
  static const logout = 'logout';

  // Onboarding
  static const onboardingStarted = 'onboarding_started';
  static const onboardingStepCompleted = 'onboarding_step_completed';
  static const onboardingCompleted = 'onboarding_completed';

  // Banking
  static const bankConnectionStarted = 'bank_connection_started';
  static const bankConnected = 'bank_connected';
  static const bankConnectionFailed = 'bank_connection_failed';
  static const bankDisconnected = 'bank_disconnected';
  // The MFA gate in front of Plaid, kept SEPARATE from bankConnectionFailed:
  // 10/10 real-user bank attempts in Aug 2026 were this gate, not a Plaid error,
  // and lumping them together hid the funnel's biggest leak. blocked = the gate
  // fired; gateCompleted / gateAbandoned = what the user did with the inline
  // enrollment sheet (properties: method).
  static const bankConnectionBlockedMfa = 'bank_connection_blocked_mfa';
  static const bankMfaGateCompleted = 'bank_mfa_gate_completed';
  static const bankMfaGateAbandoned = 'bank_mfa_gate_abandoned';
  // Accounts page load lifecycle — instrumentation for the recurring
  // "accounts page won't load" report (see AccountsPage).
  static const accountsLoadStarted = 'accounts_load_started';
  static const accountsLoaded = 'accounts_loaded';
  static const accountsLoadFailed = 'accounts_load_failed';

  // Budget
  static const budgetCreated = 'budget_created';
  static const budgetUpdated = 'budget_updated';
  static const budgetDeleted = 'budget_deleted';

  // Savings Goals
  static const savingsGoalCreated = 'savings_goal_created';
  static const savingsGoalUpdated = 'savings_goal_updated';
  static const savingsGoalCompleted = 'savings_goal_completed';
  static const savingsGoalDeleted = 'savings_goal_deleted';

  // AI Chat
  static const aiChatMessageSent = 'ai_chat_message_sent';
  static const aiToolExecuted = 'ai_tool_executed';

  /// Emitted once per completed chat answer with how long the round trip took
  /// and how big the answer was. Tester reports of "the chat made the app slow"
  /// are otherwise unfalsifiable from our side — this separates "the server took
  /// 20s to answer" from "the answer arrived fast and the client janked
  /// rendering it".
  static const aiChatResponseReceived = 'ai_chat_response_received';

  // Subscription
  static const subscriptionStarted = 'subscription_started';
  static const subscriptionUpgraded = 'subscription_upgraded';
  static const subscriptionCancelled = 'subscription_cancelled';
  static const trialStarted = 'trial_started';

  // Family
  static const familyCreated = 'family_created';
  static const familyMemberInvited = 'family_member_invited';
  static const familyMemberJoined = 'family_member_joined';

  // Screen views (auto-tracked by PosthogObserver — manual only if needed)
  static const pageView = '\$pageview';
}

// ── Analytics singleton ────────────────────────────────
class AnalyticsService {
  AnalyticsService._();
  static final instance = AnalyticsService._();

  bool _initialized = false;

  static const String _posthogKey = String.fromEnvironment(
    'POSTHOG_KEY',
    defaultValue: '',
  );

  static const String _posthogHost = String.fromEnvironment(
    'POSTHOG_HOST',
    defaultValue: 'https://us.i.posthog.com',
  );

  bool get isEnabled => _initialized && _consentGranted && _posthogKey.isNotEmpty;

  // ── Consent (analytics is opt-in; EU/UK require prior consent — issue #606) ──
  static const String _consentKey = 'analytics_consent';
  bool _consentGranted = false;

  /// True when the user has opted into analytics this session.
  bool get consentGranted => _consentGranted;

  /// Read the stored choice: null = undecided (show the banner), true = granted,
  /// false = declined. Also primes [consentGranted] for this session.
  ///
  /// On web the shared cookie wins, because the marketing site and the app sit on one
  /// origin and must not each run their own prompt — a visitor who answered on the
  /// marketing site would otherwise be asked a second time on reaching /app/.
  Future<bool?> loadConsentStatus() async {
    final prefs = await SharedPreferences.getInstance();

    final shared = readSharedConsent();
    if (shared != null) {
      final granted = shared == 'granted';
      _consentGranted = granted;
      // Mirror locally so the rest of the app has a single place to read from.
      await prefs.setBool(_consentKey, granted);
      return granted;
    }

    final v = prefs.getBool(_consentKey);
    _consentGranted = v == true;
    // Someone who already answered inside the app predates the shared cookie. Publish
    // their existing choice rather than asking again on the marketing site.
    if (v != null) writeSharedConsent(v);
    return v;
  }

  /// Opt in and start analytics. PostHog only writes cookies/localStorage and
  /// sends events from this point — never before consent.
  Future<void> grantConsent() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_consentKey, true);
    writeSharedConsent(true);
    _consentGranted = true;
    if (_initialized) {
      try {
        await Posthog().enable();
      } catch (_) {}
    } else {
      await init();
    }
  }

  /// Opt out. Stops the SDK if it was already running.
  Future<void> revokeConsent() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_consentKey, false);
    writeSharedConsent(false);
    _consentGranted = false;
    if (_initialized) {
      try {
        await Posthog().disable();
      } catch (_) {}
    }
  }

  /// Initialize PostHog. No-op until the user has consented (called from main()
  /// only when consent is already granted, and from [grantConsent]).
  Future<void> init() async {
    if (_initialized) return;
    if (!_consentGranted) return; // analytics requires opt-in (#606)
    if (_posthogKey.isEmpty) {
      if (kDebugMode) {
          debugPrint('[Analytics] PostHog key not configured, analytics disabled');
      }
      return;
    }

    final config = PostHogConfig(_posthogKey);
    config.host = _posthogHost;
    config.debug = kDebugMode;
    config.captureApplicationLifecycleEvents = true;
    // Privacy: no session recording for a finance app
    config.sessionReplay = false;

    await Posthog().setup(config);
    _initialized = true;

    // On web, posthog-js is initialized opted-out (opt_out_capturing_by_default in
    // posthog_init.js) so nothing is captured before consent. init() only runs once
    // consent is granted, so opt in now — otherwise a returning, already-consented
    // user (main.dart calls init() but not grantConsent()) would stay opted out and
    // send no events. enable() is idempotent and harmless on native.
    if (_consentGranted) {
      try {
        await Posthog().enable();
      } catch (_) {}
    }

    if (kDebugMode) {
      debugPrint('[Analytics] PostHog initialized');
    }
  }

  /// Identify a user after login. Sets user properties for segmentation.
  Future<void> identify(
    String userId, {
    String? email,
    String? name,
    int? subscriptionTier,
    int? familyId,
    String? familyRole,
    int? connectedBanks,
    String? createdAt,
  }) async {
    if (!isEnabled) return;

    final props = <String, Object>{
      if (email != null) 'email': email,
      if (name != null) 'name': name,
      if (subscriptionTier != null) 'subscription_tier': subscriptionTier,
      if (familyId != null) 'family_id': familyId,
      if (familyRole != null) 'family_role': familyRole,
      if (connectedBanks != null) 'connected_banks': connectedBanks,
      if (createdAt != null) 'created_at': createdAt,
      'client_type': AppConfig.clientType,
      'app_version': AppConfig.appVersion,
    };

    await Posthog().identify(userId: userId, userProperties: props);

    if (kDebugMode) {
      debugPrint('[Analytics] identify (${props.length} properties)');
    }
  }

  /// Track a named event with optional properties.
  Future<void> track(String event, [Map<String, Object>? properties]) async {
    if (!isEnabled) return;

    await Posthog().capture(
      eventName: event,
      properties: <String, Object>{
        'client_type': AppConfig.clientType,
        ...?properties,
      },
    );

    if (kDebugMode) {
      debugPrint('[Analytics] track($event)');
    }
  }

  /// Reset identity on logout.
  Future<void> reset() async {
    if (!isEnabled) return;
    await Posthog().reset();
  }

  /// Check a feature flag value.
  Future<bool> isFeatureEnabled(String flag) async {
    if (!isEnabled) return false;
    return await Posthog().isFeatureEnabled(flag);
  }
}

/// Convenience top-level accessor
final analytics = AnalyticsService.instance;
