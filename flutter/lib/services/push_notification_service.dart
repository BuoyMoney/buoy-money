import '../l10n/generated/app_localizations.dart';
import 'dart:async';

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart'
    show debugPrint, defaultTargetPlatform, kIsWeb;
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'dart:convert';
import 'package:crypto/crypto.dart';
import '../config/app_config.dart';
import '../config/server_capabilities.dart';
import '../firebase_options.dart';
import '../utils/deep_link.dart';
import '../widgets/app_toast.dart';
import '../utils/device_timezone.dart';
import 'api_client.dart';
import 'relay_push_service.dart';

/// Push notifications (#190) — FCM on Android and iOS (APNs delivery via FCM).
///
/// Responsibilities:
///  * Initialize Firebase (Dart-only, from [DefaultFirebaseOptions]) — but ONLY when the
///    committed placeholder in firebase_options.dart has been replaced by `flutterfire
///    configure`. Until then, and always on web, every method here is a silent no-op, so
///    the app runs fine without a Firebase project.
///  * Register the device token with the backend (`POST /notifications/devices`) after
///    login and again on every FCM token rotation; unregister on logout so pushes stop
///    following a signed-out account.
///  * Route notification taps: the backend puts the in-app route in `data['route']`;
///    cold-start taps arrive via getInitialMessage, background taps via
///    onMessageOpenedApp. Both funnel through [normalizeInAppRoute] like every other
///    server-provided actionUrl.
///  * Foreground messages: iOS presents them natively (presentation options); Android
///    shows an in-app SnackBar with a View action — while the user is *inside* the app,
///    a subtle nudge beats a system tray banner.
///
/// The permission ask follows the platform soft-ask guidance: never at first launch —
/// [maybeShowEnablePrompt] shows a one-time in-app explainer after login, and the system
/// prompt only fires if the user opts in there (or later flips the Settings toggle).
class PushNotificationService {
  static const _promptShownKey = 'push_permission_prompt_shown';

  /// Epoch ms of the last time the enable prompt was shown. Replaces the plain
  /// "shown once, ever" boolean above, which is kept only to migrate installs.
  static const _promptLastAskedKey = 'push_permission_prompt_last_asked';

  /// How long to wait before offering notifications again after a decline. Long
  /// enough not to nag, short enough that missing the prompt once isn't
  /// permanent.
  static const _promptRetryInterval = Duration(days: 14);

  /// Epoch ms of the last successful token registration, persisted so the refresh
  /// cadence survives app restarts.
  static const _lastRegisteredKey = 'push_last_registered_at';

  /// Per server: a hash of the token this device last registered there successfully. The owner's setup checklist
  /// reads it to tell "registered with this server" from "paired with the relay only".
  static String get registeredHerePrefKey =>
      'push_registered|${AppConfig.customServerOrigin ?? AppConfig.defaultBaseUrl}';

  static String _fingerprint(String token) => sha256.convert(utf8.encode(token)).toString();

  /// How often a still-valid token is re-reported to the server.
  ///
  /// FCM expires a registration after 270 days of inactivity and the backend
  /// prunes on the same horizon (PushNotificationService.PruneStaleTokensAsync),
  /// but "inactivity" is measured by when the client last reported the token —
  /// so a device that never re-reports is eventually reaped even though the user
  /// opens the app daily. Google's guidance is to refresh roughly monthly
  /// ("strikes a good balance between battery impact and detecting inactive
  /// registration tokens"); a week is well inside that and costs one small POST.
  static const _refreshInterval = Duration(days: 7);

  GoRouter? _router;
  bool _firebaseReady = false;
  bool _handlersAttached = false;
  StreamSubscription<String>? _tokenRefreshSub;
  StreamSubscription<RemoteMessage>? _openedAppSub;
  StreamSubscription<RemoteMessage>? _foregroundSub;
  String? _registeredToken;

  /// True when firebase_options.dart still holds the committed placeholder — i.e. the
  /// owner hasn't run `flutterfire configure` yet.
  static bool get isConfigured =>
      DefaultFirebaseOptions.currentPlatform.apiKey != 'REPLACE-ME';

  bool get _active => !kIsWeb && isConfigured;

  /// Wire the service to the app's router — used for tap navigation and to reach a
  /// live BuildContext for foreground toasts. Safe to call on web — no-op.
  Future<void> init(GoRouter router) async {
    _router = router;
    if (!_active) return;

    try {
      await Firebase.initializeApp(
        options: DefaultFirebaseOptions.currentPlatform,
      );
      _firebaseReady = true;

      // iOS: present foreground notifications natively (banner + sound).
      await FirebaseMessaging.instance
          .setForegroundNotificationPresentationOptions(
        alert: true,
        badge: true,
        sound: true,
      );

      _attachHandlers();
    } catch (e) {
      debugPrint('Push init failed (continuing without push): $e');
    }
  }

  void _attachHandlers() {
    if (_handlersAttached) return;
    _handlersAttached = true;

    // Cold start from a notification tap: route once the first frame is up.
    FirebaseMessaging.instance.getInitialMessage().then((message) {
      if (message != null) _routeFromMessage(message);
    });

    // Backgrounded app brought forward by a tap.
    _openedAppSub =
        FirebaseMessaging.onMessageOpenedApp.listen(_routeFromMessage);

    // Token rotation: keep the backend's registration current.
    _tokenRefreshSub = FirebaseMessaging.instance.onTokenRefresh.listen((token) {
      _registerToken(token);
    });

    // Foreground messages on Android get an in-app SnackBar (iOS shows the native
    // banner via the presentation options set in init).
    _foregroundSub = FirebaseMessaging.onMessage.listen(_showForegroundNotice);
  }

  /// Called after login, on app start when already logged in, and every time the
  /// app returns to the foreground: if the user has already granted notification
  /// permission, (re)register the current token. Never prompts — prompting is
  /// [maybeShowEnablePrompt]'s job.
  ///
  /// Re-reporting on resume is what makes a lost registration self-heal. It used
  /// to run only on the unauthenticated -> authenticated transition, so anything
  /// that dropped the server-side row — an explicit logout, a token the backend
  /// pruned as stale or invalid — stayed dropped until the next full sign-in.
  /// Silent forever, because permission was still granted, so the one-time enable
  /// prompt never fired either.
  Future<void> syncRegistration() async {
    if (!_active || !_firebaseReady) return;
    try {
      final settings =
          await FirebaseMessaging.instance.getNotificationSettings();
      if (settings.authorizationStatus != AuthorizationStatus.authorized &&
          settings.authorizationStatus != AuthorizationStatus.provisional) {
        return;
      }
      if (!await _dueForRefresh()) return;
      final token = await FirebaseMessaging.instance.getToken();
      if (token != null) await _registerToken(token);
    } catch (e) {
      debugPrint('Push registration sync failed: $e');
    }
  }

  /// Whether the OS currently permits notifications for this app.
  ///
  /// The preferences sheet needs this because the server-side toggles default to
  /// all-on: with the OS permission denied it rendered every switch enabled and a
  /// "Push Alerts" heading, which reads as "notifications are working" while
  /// nothing can possibly be delivered. That is how the owner's device sat silent
  /// for weeks.
  Future<bool> hasOsPermission() async {
    if (!_active || !_firebaseReady) return false;
    try {
      final settings =
          await FirebaseMessaging.instance.getNotificationSettings();
      return settings.authorizationStatus == AuthorizationStatus.authorized ||
          settings.authorizationStatus == AuthorizationStatus.provisional;
    } catch (_) {
      return false;
    }
  }

  /// Whether this device's last registration with this server succeeded, for the token it holds now. With a relay
  /// that token is the relay pairing: a pairing saved while `POST /notifications/devices` failed does not count.
  Future<bool> isRegisteredWithServer() async {
    if (!_active) return false;
    try {
      final prefs = await SharedPreferences.getInstance();
      final saved = prefs.getString(registeredHerePrefKey);
      if (saved == null) return false;
      if (ServerEdition.current.value.pushRelayUrl == null) return true;
      final pairing = await relayPushService.pairingToken();
      return pairing != null && saved == _fingerprint(pairing);
    } catch (_) {
      return false;
    }
  }

  /// Whether to re-report the token now.
  ///
  /// Always true when this run of the app hasn't registered yet — that covers a
  /// cold start and a logout/login within one run (unregisterDevice clears
  /// [_registeredToken]), and is what heals a device whose row went missing.
  /// Otherwise throttled to [_refreshInterval] so returning to the foreground
  /// twenty times a day doesn't mean twenty POSTs.
  Future<bool> _dueForRefresh() async {
    if (_registeredToken == null) return true;
    try {
      final prefs = await SharedPreferences.getInstance();
      final last = prefs.getInt(_lastRegisteredKey);
      if (last == null) return true;
      final elapsed = DateTime.now()
          .difference(DateTime.fromMillisecondsSinceEpoch(last));
      return elapsed >= _refreshInterval;
    } catch (_) {
      // If the throttle can't be read, refreshing is the safe failure — a
      // redundant registration is harmless, a skipped one goes silent.
      return true;
    }
  }

  /// Fires the system permission prompt (Android 13+ dialog / iOS alert) and, when
  /// granted, registers the device. Returns true when notifications ended up enabled:
  /// permission granted and this server accepted the device.
  /// Used by the one-time post-login prompt and the Settings toggle.
  Future<bool> requestPermissionAndRegister() async {
    if (!_active || !_firebaseReady) return false;
    try {
      final settings = await FirebaseMessaging.instance.requestPermission();
      final granted =
          settings.authorizationStatus == AuthorizationStatus.authorized ||
              settings.authorizationStatus == AuthorizationStatus.provisional;
      if (!granted) return false;
      final token = await FirebaseMessaging.instance.getToken();
      return token != null && await _registerToken(token);
    } catch (e) {
      debugPrint('Push permission request failed: $e');
      return false;
    }
  }

  /// One-time post-login soft ask: an in-app explainer before the system prompt, per
  /// platform guidance (asking cold at first launch tanks grant rates). Only ever shows
  /// once per install, and only when permission is still undetermined.
  Future<void> maybeShowEnablePrompt(BuildContext context) async {
    if (!_active || !_firebaseReady) return;
    try {
      final prefs = await SharedPreferences.getInstance();

      // Already granted: nothing to ask, just make sure the token is current.
      if (await hasOsPermission()) {
        await syncRegistration();
        return;
      }

      // Not granted. Ask again on a cadence rather than once per install.
      //
      // This used to set a permanent flag the moment the dialog was shown, so a
      // single "Not now" — or simply missing it — meant the app never asked
      // again for the life of the install. Meanwhile the settings sheet kept
      // reporting every category as on, so there was nothing to reveal that
      // notifications could not be delivered. That is the state the owner's
      // device was in: silent for weeks, with the UI insisting it was fine.
      //
      // A permanent one-shot is the constraint Google documents for apps
      // targeting SDK 32 or lower, where the platform enforces it. We target 36
      // and may re-prompt, so the only reason to hold back is not nagging.
      final lastAsked = prefs.getInt(_promptLastAskedKey);
      if (lastAsked != null) {
        final since = DateTime.now()
            .difference(DateTime.fromMillisecondsSinceEpoch(lastAsked));
        if (since < _promptRetryInterval) return;
      } else if (prefs.getBool(_promptShownKey) ?? false) {
        // Migrating an install that only has the old boolean: treat the legacy
        // "already shown" as an ask that happened now, so it waits one interval
        // instead of firing immediately on upgrade.
        await prefs.setInt(
            _promptLastAskedKey, DateTime.now().millisecondsSinceEpoch);
        return;
      }

      if (!context.mounted) return;
      final enable = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text(AppLocalizations.of(context).uiPushPromptTitle),
          content: Text(AppLocalizations.of(context).uiPushPromptBody),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(context).pop(false),
              child: Text(AppLocalizations.of(context).consentNotNow),
            ),
            FilledButton(
              onPressed: () => Navigator.of(context).pop(true),
              child: Text(AppLocalizations.of(context).uiEnableNotifications),
            ),
          ],
        ),
      );
      await prefs.setBool(_promptShownKey, true);
      await prefs.setInt(
          _promptLastAskedKey, DateTime.now().millisecondsSinceEpoch);
      if (enable == true) {
        await requestPermissionAndRegister();
      }
    } catch (e) {
      debugPrint('Push enable prompt failed: $e');
    }
  }

  /// Remove this device's registration server-side (logout). The FCM token itself stays
  /// valid on-device; only the account linkage is dropped.
  Future<void> unregisterDevice() async {
    if (!_active || !_firebaseReady) return;
    try {
      final isRelay = ServerEdition.current.value.pushRelayUrl != null;
      final token = _registeredToken ??
          (isRelay
              ? await relayPushService.pairingToken()
              : await FirebaseMessaging.instance.getToken());
      if (token == null) return;
      await apiClient.delete<void>(
        '/notifications/devices',
        data: {'token': token},
      );
      _registeredToken = null;
      // Clear the refresh stamp too: the server-side row is gone, so the next
      // sign-in must re-register immediately rather than being throttled out by
      // a timestamp describing a registration that no longer exists.
      try {
        final prefs = await SharedPreferences.getInstance();
        await prefs.remove(_lastRegisteredKey);
        await prefs.remove(registeredHerePrefKey);
      } catch (_) {}
    } catch (e) {
      // Logout must never block on push cleanup; a dangling token is pruned
      // server-side on the first failed send anyway.
      debugPrint('Push unregister failed: $e');
    }
  }

  /// True when the server accepted the device.
  Future<bool> _registerToken(String token) async {
    try {
      // A self-hosted server has no Firebase credentials of its own. When it names a push relay,
      // this phone pairs with the relay and registers the pairing credential and its public key
      // instead of the raw Firebase token (docs/self_host/push_relay.md).
      final relayUrl = ServerEdition.current.value.pushRelayUrl;
      final body = <String, dynamic>{
        'timeZoneOffsetMinutes': DateTime.now().timeZoneOffset.inMinutes,
        'timeZoneId': await deviceTimeZoneId(),
      };
      String registered;
      if (relayUrl != null) {
        registered = await relayPushService.pair(
            relayUrl: relayUrl, pushToken: token, platform: _platformName);
        body.addAll(await relayPushService.registrationBody(registered));
      } else {
        registered = token;
        body.addAll({'token': token, 'platform': _platformName});
      }
      await apiClient.post<void>('/notifications/devices', data: body);
      _registeredToken = registered;
      // Only stamped on success, so a failed POST leaves the device due for a
      // retry on the next foreground rather than silently starting the clock.
      try {
        final prefs = await SharedPreferences.getInstance();
        await prefs.setInt(
            _lastRegisteredKey, DateTime.now().millisecondsSinceEpoch);
        await prefs.setString(registeredHerePrefKey, _fingerprint(registered));
      } catch (_) {}
      return true;
    } catch (e) {
      debugPrint('Push token registration failed: $e');
      return false;
    }
  }

  // kIsWeb is excluded by _active; anything not iOS/macOS registers as android.
  String get _platformName => switch (defaultTargetPlatform) {
        TargetPlatform.iOS || TargetPlatform.macOS => 'ios',
        _ => 'android',
      };

  void _routeFromMessage(RemoteMessage message) {
    // A relayed push carries the real route only inside the encrypted envelope.
    final envelope = message.data['e'];
    if (envelope is String) {
      relayPushService.open(envelope).then((opened) {
        // An envelope that fails to open (altered, or sealed for another phone) is dropped.
        if (opened != null) _goToRoute(opened.route);
      });
      return;
    }
    _goToRoute(message.data['route'] as String?);
  }

  void _goToRoute(String? rawRoute) {
    final route = normalizeInAppRoute(rawRoute);
    if (route == null) {
      _router?.go('/notifications');
      return;
    }
    _router?.go(route);
  }

  void _showForegroundNotice(RemoteMessage message) {
    // iOS already shows the native banner (presentation options); an in-app toast on
    // top would double-notify.
    if (defaultTargetPlatform == TargetPlatform.iOS) return;

    final envelope = message.data['e'];
    if (envelope is String) {
      // Relayed push: the visible text is generic, the real text is in the envelope.
      relayPushService.open(envelope).then((opened) {
        if (opened == null) return;
        _showToast(opened.title.isNotEmpty ? opened.title : opened.body,
            () => _goToRoute(opened.route));
      });
      return;
    }

    final notification = message.notification;
    if (notification == null) return;
    _showToast(notification.title ?? notification.body ?? 'New notification',
        () => _routeFromMessage(message));
  }

  void _showToast(String text, VoidCallback onView) {
    final context = _router?.routerDelegate.navigatorKey.currentContext;
    if (context == null || !context.mounted) return;

    showAppToast(
      context,
      text,
      actionLabel: 'View',
      onAction: onView,
      duration: const Duration(seconds: 5),
    );
  }

  void dispose() {
    _tokenRefreshSub?.cancel();
    _openedAppSub?.cancel();
    _foregroundSub?.cancel();
    _handlersAttached = false;
  }
}

/// Singleton instance
final pushNotificationService = PushNotificationService();
