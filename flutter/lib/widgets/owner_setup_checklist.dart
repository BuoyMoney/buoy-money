import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';

import '../config/server_capabilities.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/owner_setup_service.dart';
import '../services/push_notification_service.dart';
import '../services/server_version_service.dart';
import '../utils/server_roles.dart';
import 'cinematic/glass_settings.dart';

/// The self-hosted owner's setup steps on the dashboard: banks, AI, household, phone notifications. Each step opens
/// the real screen for it. Shown only to the administrator of a self-hosted server; never on the hosted service and
/// never to a household member. Hidden once every step is done, or when the owner hides it (remembered on this
/// device).
class OwnerSetupChecklist extends StatefulWidget {
  const OwnerSetupChecklist({
    super.key,
    this.service,
    this.isAdmin,
    this.navigate,
    this.enablePush,
    this.refreshServer,
  });

  /// Injected by tests; the app reads the real server.
  final OwnerSetupService? service;

  /// Injected by tests; the app reads the signed-in user.
  final bool? isAdmin;

  /// Opens a screen and resolves when the person is back. Injected by tests.
  final Future<void> Function(BuildContext context, String path)? navigate;

  /// Asks for notification permission and pairs this phone with the relay. Injected by tests.
  final Future<bool> Function()? enablePush;

  /// Reads the server's settings again (`/system/version`), after the owner changed its configuration. Injected by tests.
  final Future<void> Function()? refreshServer;

  @override
  State<OwnerSetupChecklist> createState() => _OwnerSetupChecklistState();
}

class _OwnerSetupChecklistState extends State<OwnerSetupChecklist> {
  late final OwnerSetupService _service = widget.service ?? OwnerSetupService();
  OwnerSetupStatus? _status;
  bool _started = false;
  bool _hidden = false;

  static Future<void> _defaultNavigate(BuildContext context, String path) async {
    // The Accounts page is a tab: switch to it. Settings screens open on top and come back here.
    if (path.startsWith('/accounts')) {
      context.go(path);
      return;
    }
    await context.push(path);
  }

  Future<void> _load() async {
    if (await _service.isDismissed()) {
      if (mounted) setState(() => _hidden = true);
      return;
    }
    final status = await _service.load();
    if (mounted) setState(() => _status = status);
  }

  Future<void> _open(String path) async {
    await (widget.navigate ?? _defaultNavigate)(context, path);
    if (mounted) unawaited(_load());
  }

  Future<void> _notifications() async {
    final setup = _status?.notificationSetup ?? NotificationSetup.phoneNotSetUp;
    if (setup == NotificationSetup.noRelay || ServerEdition.current.value.pushRelayUrl == null) {
      // No relay address on this server: linking with the Cloud does not set one, so show what to change.
      await _showRelaySetup();
      return;
    }
    if (setup == NotificationSetup.serverNotLinked) {
      // The relay is set but refuses this server until it holds a Cloud credential: linking gives it one.
      await _open('/settings/cloud');
      return;
    }
    if (!kIsWeb) {
      final enabled =
          await (widget.enablePush ?? pushNotificationService.requestPermissionAndRegister)();
      if (!mounted) return;
      if (enabled) {
        unawaited(_load());
        return;
      }
    }
    await _open('/settings');
  }

  /// The setting to add, exactly as it goes in the server's .env file (a name, not text to translate).
  static const relaySettingLine = 'BUOY_PUSH_RELAY_URL=';

  /// How to give the server a relay address, with a button that reads the server's settings again afterwards.
  Future<void> _showRelaySetup() async {
    final again = await showModalBottomSheet<bool>(
      context: context,
      backgroundColor: Colors.transparent,
      isScrollControlled: true,
      builder: (sheetContext) {
        final l10n = AppLocalizations.of(sheetContext);
        return SafeArea(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: GlassSection(
              key: const ValueKey('owner-setup-relay-help'),
              icon: Icons.notifications_active_outlined,
              title: l10n.ownSetupRelayTitle,
              children: [
                Text(l10n.ownSetupRelayBody, style: cineBodyStyle()),
                const SizedBox(height: 10),
                SelectableText(relaySettingLine,
                    key: const ValueKey('owner-setup-relay-setting'),
                    style: GoogleFonts.firaCode(fontSize: 14, color: cineText)),
                const SizedBox(height: 10),
                Text(l10n.ownSetupRelayAfter, style: cineMutedStyle()),
                const SizedBox(height: 16),
                GlassPrimaryButton(
                  key: const ValueKey('owner-setup-relay-check'),
                  expand: true,
                  onPressed: () => Navigator.of(sheetContext).pop(true),
                  label: l10n.ownSetupRelayCheck,
                ),
              ],
            ),
          ),
        );
      },
    );
    if (again != true || !mounted) return;
    try {
      await (widget.refreshServer ?? () => ServerVersionService().check())();
    } catch (_) {
      // Could not ask: the list below still reloads with what is known.
    }
    if (mounted) unawaited(_load());
  }

  Future<void> _skipHousehold() async {
    await _service.skipHousehold();
    if (mounted) setState(() => _status = _status?.copyWith(householdSkipped: true));
  }

  Future<void> _hide() async {
    setState(() => _hidden = true);
    await _service.dismiss();
  }

  @override
  Widget build(BuildContext context) {
    final admin = widget.isAdmin ?? serverAdminOf(context, listen: true) ?? false;
    return ValueListenableBuilder<ServerCapabilities>(
      valueListenable: ServerEdition.current,
      builder: (context, caps, _) {
        if (!caps.isSelfHost || !admin || _hidden) return const SizedBox.shrink();
        if (!_started) {
          _started = true;
          scheduleMicrotask(_load);
        }
        final status = _status;
        if (status == null || status.dismissed || status.allDone) return const SizedBox.shrink();
        return _card(context, status);
      },
    );
  }

  Widget _card(BuildContext context, OwnerSetupStatus status) {
    final l10n = AppLocalizations.of(context);
    return GlassSection(
      key: const ValueKey('owner-setup'),
      icon: Icons.checklist_rounded,
      title: l10n.ownSetupTitle,
      highlight: true,
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(l10n.ownSetupProgress(status.doneCount, OwnerSetupStatus.total),
              key: const ValueKey('owner-setup-progress'), style: cineMutedStyle()),
          IconButton(
            key: const ValueKey('owner-setup-hide'),
            tooltip: l10n.ownSetupHide,
            onPressed: _hide,
            icon: const Icon(Icons.close_rounded, size: 18, color: cineMuted),
          ),
        ],
      ),
      children: [
        _StepRow(
          key: const ValueKey('owner-setup-banks'),
          number: 1,
          title: l10n.ownSetupBanksTitle,
          body: l10n.ownSetupBanksBody,
          done: status.banks,
          onTap: () => _open('/accounts?add=1'),
        ),
        const GlassDivider(height: 12),
        _StepRow(
          key: const ValueKey('owner-setup-ai'),
          number: 2,
          title: l10n.ownSetupAiTitle,
          body: l10n.ownSetupAiBody,
          done: status.ai,
          onTap: () => _open('/settings/ai'),
        ),
        const GlassDivider(height: 12),
        _StepRow(
          key: const ValueKey('owner-setup-household'),
          number: 3,
          title: l10n.ownSetupHouseholdTitle,
          body: l10n.ownSetupHouseholdBody,
          done: status.householdDone,
          onTap: () => _open('/family'),
          extra: GlassTextAction(
            key: const ValueKey('owner-setup-household-skip'),
            label: l10n.ownSetupSkip,
            color: cineMuted,
            onPressed: _skipHousehold,
          ),
        ),
        const GlassDivider(height: 12),
        _StepRow(
          key: const ValueKey('owner-setup-notifications'),
          number: 4,
          title: l10n.ownSetupNotifyTitle,
          body: l10n.ownSetupNotifyBody,
          done: status.notifications,
          onTap: _notifications,
        ),
      ],
    );
  }
}

/// One step: a number (or a check once done), what it is, and a tap that opens the screen for it.
class _StepRow extends StatelessWidget {
  const _StepRow({
    super.key,
    required this.number,
    required this.title,
    required this.body,
    required this.done,
    required this.onTap,
    this.extra,
  });

  final int number;
  final String title;
  final String body;
  final bool done;
  final VoidCallback onTap;

  /// A quiet second action shown while the step is open ("Skip").
  final Widget? extra;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Semantics(
      button: !done,
      child: InkWell(
        onTap: done ? null : onTap,
        borderRadius: BorderRadius.circular(10),
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 6),
          child: Row(
            children: [
              SizedBox(
                width: 28,
                height: 28,
                child: done
                    ? const Icon(Icons.check_circle_rounded,
                        key: ValueKey('owner-setup-step-done'), size: 24, color: cineIncome)
                    : Container(
                        alignment: Alignment.center,
                        decoration: BoxDecoration(
                          shape: BoxShape.circle,
                          border: Border.all(color: cineAccent.withValues(alpha: 0.6)),
                        ),
                        child: Text(number.toString(),
                            style: GoogleFonts.firaCode(
                                fontSize: 12, fontWeight: FontWeight.w600, color: cineAccent)),
                      ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(title,
                        style: done
                            ? cineRowTitleStyle().copyWith(color: cineMuted)
                            : cineRowTitleStyle()),
                    const SizedBox(height: 2),
                    Text(body, style: cineMutedStyle()),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              if (done)
                GlassStatusPill(label: l10n.ownSetupDone, color: cineIncome)
              else ...[
                if (extra != null) extra!,
                const Icon(Icons.chevron_right_rounded, color: cineMuted),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
