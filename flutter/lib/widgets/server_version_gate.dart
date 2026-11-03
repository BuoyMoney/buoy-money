import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../config/app_config.dart';
import '../l10n/generated/app_localizations.dart';
import '../providers/auth_provider.dart';
import '../services/server_selection_service.dart';
import '../services/server_version_service.dart';
import 'server_picker.dart';

/// Wraps the app and replaces it with a plain "update needed" screen when the server and this app
/// cannot work together (see [ServerVersionService]). It checks once at startup and does not hold
/// the app back while the check runs: the app is shown immediately, and the screen takes over only
/// if the answer is a clear no. Any doubt (offline, old server without the endpoint, dev build)
/// leaves the app alone.
class ServerVersionGate extends StatefulWidget {
  const ServerVersionGate({
    super.key,
    required this.child,
    this.service,
    this.selectionService,
    this.isCustomServer,
  });

  final Widget child;

  /// Injected by tests; the app uses the default.
  final ServerVersionService? service;

  /// Injected by tests; the app uses the global server picker service.
  final ServerSelectionService? selectionService;

  /// Injected by tests; the app asks [AppConfig.isCustomServer].
  final bool Function()? isCustomServer;

  @override
  State<ServerVersionGate> createState() => _ServerVersionGateState();
}

class _ServerVersionGateState extends State<ServerVersionGate> {
  late final ServerVersionService _service = widget.service ?? ServerVersionService();
  VersionCheckResult _result = VersionCheckResult.ok;
  bool _checking = false;

  @override
  void initState() {
    super.initState();
    _check();
  }

  bool get _customServer => (widget.isCustomServer ?? () => AppConfig.isCustomServer)();

  Future<void> _check() async {
    if (_checking) return;
    // A local build has no release number to compare, and its server is the one it was built
    // with, so there is nothing to ask. (Also keeps tests that pump the whole app off the network.)
    // A page served by a self-hosted server (same-origin build), or an app pointed at a server
    // the user picked, always asks: what that server offers (Google sign-in, billing, Plaid)
    // decides which screens exist, version or not.
    if (widget.service == null &&
        !AppConfig.isNonHostedServer &&
        ServerVersionService.parseVersion(AppConfig.version) == null) {
      // After a switch back to the build's own server, a stale "update needed" must go away.
      if (_result.compatibility != ServerCompatibility.ok) {
        setState(() => _result = VersionCheckResult.ok);
      }
      return;
    }
    setState(() => _checking = true);
    final result = await _service.check();
    if (!mounted) return;
    setState(() {
      _result = result;
      _checking = false;
    });
  }

  /// "Use a different server": the picker clears tokens and the offline cache of the old server;
  /// here the signed-in state in memory goes too, then the new server is checked.
  Future<void> _changeServer(BuildContext sheetContext) async {
    final changed = await showServerPickerSheet(sheetContext, service: widget.selectionService);
    if (!changed || !mounted) return;
    context.read<AuthProvider?>()?.signOutForServerSwitch();
    await _check();
  }

  @override
  Widget build(BuildContext context) {
    if (_result.compatibility == ServerCompatibility.ok) return widget.child;
    // The gate sits above the app's navigator, so it brings its own for the server picker sheet.
    return Navigator(
      pages: [
        MaterialPage<void>(
          key: const ValueKey('versionGate'),
          child: Builder(
            builder: (navContext) => UpdateRequiredScreen(
              result: _result,
              checking: _checking,
              onCheckAgain: _check,
              onChangeServer: _customServer ? () => _changeServer(navContext) : null,
            ),
          ),
        ),
      ],
      onDidRemovePage: (_) {},
    );
  }
}

/// The screen itself. Public so it can be tested and previewed on its own.
class UpdateRequiredScreen extends StatelessWidget {
  const UpdateRequiredScreen({
    super.key,
    required this.result,
    required this.onCheckAgain,
    this.checking = false,
    this.onChangeServer,
  });

  final VersionCheckResult result;
  final VoidCallback onCheckAgain;
  final bool checking;

  /// Shown as "Use a different server" when the app is pointed at a server the user picked.
  final VoidCallback? onChangeServer;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final serverSide = result.compatibility == ServerCompatibility.serverNeedsUpdate;
    final title = serverSide ? l10n.verServerTitle : l10n.verAppTitle;
    final body = serverSide
        ? l10n.verServerBody(result.requiredServerVersion, result.serverVersion)
        : l10n.verAppBody(result.requiredAppVersion, result.appVersion);
    final theme = Theme.of(context);

    return Material(
      color: theme.scaffoldBackgroundColor,
      child: SafeArea(
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 480),
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(Icons.system_update_alt, size: 40, color: theme.colorScheme.primary),
                  const SizedBox(height: 16),
                  Text(title, key: const Key('versionGateTitle'), style: theme.textTheme.headlineSmall),
                  const SizedBox(height: 12),
                  Text(body, key: const Key('versionGateBody'), style: theme.textTheme.bodyLarge),
                  if (serverSide) ...[
                    const SizedBox(height: 12),
                    Text(l10n.verServerHow, style: theme.textTheme.bodyMedium),
                  ],
                  const SizedBox(height: 24),
                  FilledButton(
                    key: const Key('versionGateRetry'),
                    onPressed: checking ? null : onCheckAgain,
                    child: Text(l10n.verCheckAgain),
                  ),
                  if (onChangeServer != null) ...[
                    const SizedBox(height: 8),
                    TextButton(
                      key: const Key('versionGateChangeServer'),
                      onPressed: checking ? null : onChangeServer,
                      child: Text(l10n.verChangeServer),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
