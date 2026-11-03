import 'package:flutter/material.dart';

import '../config/app_config.dart';
import '../config/server_capabilities.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/server_selection_service.dart';

/// "Server: BuoyAI Cloud  Change" under the sign-in and sign-up forms. Lets someone who runs
/// their own Buoy Money server point the phone app at it. Hidden on every web build: a web page
/// talks to the server it came from.
class ServerPickerControl extends StatelessWidget {
  const ServerPickerControl({super.key, this.onChanged, this.service});

  /// Called after the server was switched, so the page can drop state tied to the old one.
  final VoidCallback? onChanged;

  /// Injected by tests; the app uses the global service.
  final ServerSelectionService? service;

  @override
  Widget build(BuildContext context) {
    if (!AppConfig.canChooseServer) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    // Rebuild when a switch publishes the new server's answer.
    return ListenableBuilder(
      listenable: ServerEdition.changes,
      builder: (context, _) {
        final custom = AppConfig.customServerOrigin;
        final name = custom == null ? l10n.srvCloudName : displayServerHost(custom);
        final muted = theme.colorScheme.onSurface.withValues(alpha: 0.6);
        return Row(
          key: const Key('serverPickerControl'),
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            if (custom != null && AppConfig.isInsecureOrigin(custom)) ...[
              Icon(Icons.lock_open, size: 14, color: muted),
              const SizedBox(width: 4),
            ],
            Flexible(
              child: Text(
                l10n.srvLabel(name),
                key: const Key('serverPickerLabel'),
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.bodySmall?.copyWith(color: muted, fontSize: 13),
              ),
            ),
            TextButton(
              key: const Key('serverPickerChange'),
              onPressed: () async {
                final changed = await showServerPickerSheet(context, service: service);
                if (changed) onChanged?.call();
              },
              style: TextButton.styleFrom(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                minimumSize: const Size(48, 36),
              ),
              child: Text(
                l10n.srvChange,
                style: TextStyle(
                  color: theme.colorScheme.secondary,
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ),
          ],
        );
      },
    );
  }
}

/// `https://buoy.example.ts.net:8443/x` as `buoy.example.ts.net:8443/x`.
String displayServerHost(String origin) {
  final uri = Uri.tryParse(origin);
  if (uri == null || uri.host.isEmpty) return origin;
  final port = uri.hasPort ? ':${uri.port}' : '';
  final path = uri.path == '/' ? '' : uri.path;
  return '${uri.host}$port$path';
}

/// What to tell the person when [probe] turned the address down.
String serverProbeMessage(AppLocalizations l10n, ServerProbeResult probe) => switch (probe.error!) {
      ServerProbeError.invalidAddress => l10n.srvErrInvalid,
      ServerProbeError.unreachable => l10n.srvErrUnreachable,
      ServerProbeError.notBuoyServer => l10n.srvErrNotBuoy,
      ServerProbeError.serverTooOld => l10n.srvErrServerOld(
          probe.version?.serverVersion ?? '', probe.version?.requiredServerVersion ?? ''),
      ServerProbeError.appTooOld => l10n.srvErrAppOld(probe.version?.requiredAppVersion ?? ''),
    };

/// Opens the server chooser. Resolves true when the server was switched.
Future<bool> showServerPickerSheet(BuildContext context, {ServerSelectionService? service}) async {
  final result = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (sheetContext) => ServerPickerSheet(service: service ?? serverSelectionService),
  );
  return result ?? false;
}

/// The chooser itself: BuoyAI Cloud, or an address of your own that is checked before saving.
class ServerPickerSheet extends StatefulWidget {
  const ServerPickerSheet({super.key, required this.service});

  final ServerSelectionService service;

  @override
  State<ServerPickerSheet> createState() => _ServerPickerSheetState();
}

class _ServerPickerSheetState extends State<ServerPickerSheet> {
  late bool _own = AppConfig.isCustomServer;
  late final TextEditingController _address =
      TextEditingController(text: AppConfig.customServerOrigin ?? '');
  bool _busy = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _address.addListener(_onAddressChanged);
  }

  @override
  void dispose() {
    _address.removeListener(_onAddressChanged);
    _address.dispose();
    super.dispose();
  }

  void _onAddressChanged() {
    // Rebuild for the http warning; drop a stale error once the user edits.
    setState(() => _error = null);
  }

  bool get _typedHttp {
    final origin = AppConfig.normalizeServerOrigin(_address.text);
    return origin != null && AppConfig.isInsecureOrigin(origin);
  }

  Future<void> _connect() async {
    final l10n = AppLocalizations.of(context);
    final navigator = Navigator.of(context);

    if (!_own) {
      if (!AppConfig.isCustomServer) {
        navigator.pop(false);
        return;
      }
      setState(() => _busy = true);
      await widget.service.useServer(null);
      if (mounted) navigator.pop(true);
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });
    final probe = await widget.service.probe(_address.text);
    if (!mounted) return;
    if (!probe.ok) {
      setState(() {
        _busy = false;
        _error = serverProbeMessage(l10n, probe);
      });
      return;
    }
    if (probe.origin == AppConfig.customServerOrigin) {
      // Same server as before: nothing to clear, just refresh what it can do.
      ServerEdition.adopt(probe.capabilities!);
      navigator.pop(false);
      return;
    }
    await widget.service.useServer(probe.origin, capabilities: probe.capabilities!);
    if (mounted) navigator.pop(true);
  }

  Widget _option({
    required Key key,
    required bool selected,
    required String label,
    required VoidCallback onTap,
  }) {
    final theme = Theme.of(context);
    return ListTile(
      key: key,
      contentPadding: EdgeInsets.zero,
      leading: Icon(
        selected ? Icons.radio_button_checked : Icons.radio_button_unchecked,
        color: selected
            ? theme.colorScheme.secondary
            : theme.colorScheme.onSurface.withValues(alpha: 0.7),
      ),
      title: Text(label),
      selected: selected,
      onTap: _busy ? null : onTap,
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final muted = theme.colorScheme.onSurface.withValues(alpha: 0.7);

    return Padding(
      padding: EdgeInsets.fromLTRB(24, 20, 24, 24 + MediaQuery.viewInsetsOf(context).bottom),
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l10n.srvSheetTitle, style: theme.textTheme.titleLarge),
            const SizedBox(height: 8),
            Text(l10n.srvSheetBody, style: theme.textTheme.bodyMedium?.copyWith(color: muted)),
            const SizedBox(height: 12),
            _option(
              key: const Key('serverOptionCloud'),
              selected: !_own,
              label: l10n.srvOptionCloud(AppConfig.defaultServerHost),
              onTap: () => setState(() {
                _own = false;
                _error = null;
              }),
            ),
            _option(
              key: const Key('serverOptionOwn'),
              selected: _own,
              label: l10n.srvOptionOwn,
              onTap: () => setState(() => _own = true),
            ),
            if (_own) ...[
              const SizedBox(height: 8),
              TextField(
                key: const Key('serverAddressField'),
                controller: _address,
                enabled: !_busy,
                autofocus: !AppConfig.isCustomServer,
                keyboardType: TextInputType.url,
                autocorrect: false,
                enableSuggestions: false,
                textInputAction: TextInputAction.go,
                onSubmitted: (_) {
                  if (!_busy) _connect();
                },
                decoration: InputDecoration(
                  labelText: l10n.srvAddressLabel,
                  hintText: 'https://buoy.example.ts.net',
                  border: const OutlineInputBorder(),
                ),
              ),
              if (_typedHttp) ...[
                const SizedBox(height: 8),
                Row(
                  key: const Key('serverHttpWarning'),
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Icon(Icons.lock_open, size: 16, color: theme.colorScheme.tertiary),
                    const SizedBox(width: 6),
                    Expanded(
                      child: Text(l10n.srvHttpWarning,
                          style: theme.textTheme.bodySmall?.copyWith(color: muted)),
                    ),
                  ],
                ),
              ],
            ],
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(
                _error!,
                key: const Key('serverPickerError'),
                style: theme.textTheme.bodyMedium?.copyWith(color: theme.colorScheme.error),
              ),
            ],
            const SizedBox(height: 20),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                TextButton(
                  onPressed: _busy ? null : () => Navigator.of(context).pop(false),
                  child: Text(l10n.srvCancel),
                ),
                const SizedBox(width: 8),
                FilledButton(
                  key: const Key('serverPickerConnect'),
                  onPressed: _busy ? null : _connect,
                  child: _busy
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : Text(l10n.srvConnect),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
