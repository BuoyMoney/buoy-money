import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../config/app_config.dart';
import '../config/server_capabilities.dart';
import 'accounts_service.dart';
import 'ai_status_service.dart';
import 'api_client.dart';
import 'family_service.dart';
import 'push_notification_service.dart';

/// Where phone notifications stand on this server and this device. Only [NotificationSetup.ready] counts as done.
enum NotificationSetup {
  /// Alerts can reach this device: the server has a relay it may use, and (in the phone app) this phone allows
  /// notifications and is registered with this server.
  ready,

  /// The server has no push relay address (`BUOY_PUSH_RELAY_URL` is empty). Linking alone does not fix that.
  noRelay,

  /// The relay is set, but the server holds no credential the relay accepts: linking with BuoyAI Cloud gives it one.
  serverNotLinked,

  /// The server is ready, but this phone does not allow notifications or is not registered with this server yet.
  phoneNotSetUp,
}

extension NotificationSetupX on NotificationSetup {
  static NotificationSetup fromString(String? value) => NotificationSetup.values.firstWhere(
        (s) => s.name.toLowerCase() == value?.toLowerCase(),
        orElse: () => NotificationSetup.phoneNotSetUp,
      );

  static NotificationSetup fromInt(int value) => value >= 0 && value < NotificationSetup.values.length
      ? NotificationSetup.values[value]
      : NotificationSetup.phoneNotSetUp;

  String get displayName => switch (this) {
        NotificationSetup.ready => 'Ready',
        NotificationSetup.noRelay => 'No relay address',
        NotificationSetup.serverNotLinked => 'Server not linked',
        NotificationSetup.phoneNotSetUp => 'Phone not set up',
      };
}

/// Which of the owner's four setup steps are done on this self-hosted server.
@immutable
class OwnerSetupStatus {
  const OwnerSetupStatus({
    required this.banks,
    required this.ai,
    required this.household,
    required this.notifications,
    NotificationSetup? notificationSetup,
    this.householdSkipped = false,
    this.dismissed = false,
  }) : notificationSetup =
            notificationSetup ?? (notifications ? NotificationSetup.ready : NotificationSetup.phoneNotSetUp);

  /// At least one account exists.
  final bool banks;

  /// AI is turned on.
  final bool ai;

  /// A household (family) exists, so invites can go out.
  final bool household;

  /// Phone notifications reach this device through the push relay.
  final bool notifications;

  /// Why [notifications] is not done yet, so the step can open the right fix.
  final NotificationSetup notificationSetup;

  /// The owner chose to skip the household step.
  final bool householdSkipped;

  /// The owner hid the checklist on this device.
  final bool dismissed;

  bool get householdDone => household || householdSkipped;

  int get doneCount => [banks, ai, householdDone, notifications].where((d) => d).length;

  static const total = 4;

  bool get allDone => doneCount == total;

  OwnerSetupStatus copyWith({bool? householdSkipped, bool? dismissed}) => OwnerSetupStatus(
        banks: banks,
        ai: ai,
        household: household,
        notifications: notifications,
        notificationSetup: notificationSetup,
        householdSkipped: householdSkipped ?? this.householdSkipped,
        dismissed: dismissed ?? this.dismissed,
      );
}

/// Reads the owner's setup steps from the server and remembers, on this device, whether the checklist was hidden
/// and whether the household step was skipped. Each check stands alone: one that fails counts as not done and never
/// hides the others.
class OwnerSetupService {
  OwnerSetupService({
    Future<bool> Function()? hasAccounts,
    Future<bool> Function()? aiEnabled,
    Future<bool> Function()? hasHousehold,
    Future<NotificationSetup> Function()? notificationSetup,
  })  : _hasAccounts = hasAccounts ?? _defaultHasAccounts,
        _aiEnabled = aiEnabled ?? _defaultAiEnabled,
        _hasHousehold = hasHousehold ?? _defaultHasHousehold,
        _notificationSetup = notificationSetup ?? _defaultNotificationSetup;

  final Future<bool> Function() _hasAccounts;
  final Future<bool> Function() _aiEnabled;
  final Future<bool> Function() _hasHousehold;
  final Future<NotificationSetup> Function() _notificationSetup;

  static Future<bool> _defaultHasAccounts() async => (await accountsService.getAccounts()).isNotEmpty;

  static Future<bool> _defaultAiEnabled() async => (await aiStatusService.fetch()).enabled;

  static Future<bool> _defaultHasHousehold() async => await familyService.getFamily() != null;

  /// Done only when alerts can really arrive: the server names a push relay and holds a credential the relay accepts
  /// (`GET /notifications/relay-status`), and, in the phone app, the OS allows notifications and this phone's
  /// registration with this server succeeded. A stored relay pairing alone is not enough: it is saved before the
  /// server accepts the device, and pairing with the relay does not let the server send. A web page cannot get push,
  /// so there the server side is all there is to set up.
  static Future<NotificationSetup> _defaultNotificationSetup() => notificationSetupFrom(
        relayUrl: ServerEdition.current.value.pushRelayUrl,
        serverReady: _serverRelayReady,
        web: kIsWeb,
        osPermission: pushNotificationService.hasOsPermission,
        registered: pushNotificationService.isRegisteredWithServer,
      );

  static Future<bool> _serverRelayReady() async {
    final relay = await apiClient.get<Map<String, dynamic>>('/notifications/relay-status');
    return (relay['ready'] ?? relay['Ready']) == true;
  }

  /// The notifications step from its parts, in the order they must hold. Separate so it can be tested.
  @visibleForTesting
  static Future<NotificationSetup> notificationSetupFrom({
    required String? relayUrl,
    required Future<bool> Function() serverReady,
    required bool web,
    required Future<bool> Function() osPermission,
    required Future<bool> Function() registered,
  }) async {
    if (relayUrl == null) return NotificationSetup.noRelay;
    if (!await serverReady()) return NotificationSetup.serverNotLinked;
    if (web) return NotificationSetup.ready;
    if (!await osPermission()) return NotificationSetup.phoneNotSetUp;
    if (!await registered()) return NotificationSetup.phoneNotSetUp;
    return NotificationSetup.ready;
  }

  /// One device remembers per server, so hiding the list for one server does not hide it for another.
  static String get _serverKey => AppConfig.customServerOrigin ?? AppConfig.defaultBaseUrl;

  static String get dismissedPrefKey => 'owner_setup_dismissed|$_serverKey';
  static String get householdSkippedPrefKey => 'owner_setup_household_skipped|$_serverKey';

  static Future<bool> _check(Future<bool> Function() check) async {
    try {
      return await check();
    } catch (_) {
      return false;
    }
  }

  /// A notifications check that fails counts as not done. Without a relay address there is nothing to ask.
  Future<NotificationSetup> _checkNotifications() async {
    try {
      return await _notificationSetup();
    } catch (_) {
      return ServerEdition.current.value.pushRelayUrl == null
          ? NotificationSetup.noRelay
          : NotificationSetup.phoneNotSetUp;
    }
  }

  Future<OwnerSetupStatus> load() async {
    final prefs = await _prefs();
    final notifications = _checkNotifications();
    final checks = await Future.wait([
      _check(_hasAccounts),
      _check(_aiEnabled),
      _check(_hasHousehold),
    ]);
    final setup = await notifications;
    return OwnerSetupStatus(
      banks: checks[0],
      ai: checks[1],
      household: checks[2],
      notifications: setup == NotificationSetup.ready,
      notificationSetup: setup,
      householdSkipped: prefs?.getBool(householdSkippedPrefKey) ?? false,
      dismissed: prefs?.getBool(dismissedPrefKey) ?? false,
    );
  }

  /// True when the checklist was hidden on this device. Read alone so a hidden list makes no server calls.
  Future<bool> isDismissed() async => (await _prefs())?.getBool(dismissedPrefKey) ?? false;

  Future<void> dismiss() async => (await _prefs())?.setBool(dismissedPrefKey, true);

  Future<void> skipHousehold() async => (await _prefs())?.setBool(householdSkippedPrefKey, true);

  static Future<SharedPreferences?> _prefs() async {
    try {
      return await SharedPreferences.getInstance();
    } catch (_) {
      return null;
    }
  }
}
