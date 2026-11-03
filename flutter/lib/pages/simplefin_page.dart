import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:url_launcher/url_launcher.dart';

import '../l10n/generated/app_localizations.dart';
import '../services/simplefin_service.dart';
import '../utils/api_error.dart';
import '../widgets/app_toast.dart';
import '../widgets/cinematic/glass_settings.dart';

/// Settings > Bank feed (SimpleFIN). The user makes a setup token at SimpleFIN, pastes it here, and
/// sees each connection with its state. A connection that needs attention says what to do; the
/// fix is always "make a new token and paste it".
class SimpleFinPage extends StatefulWidget {
  const SimpleFinPage({super.key, this.service});

  /// Injected in tests; the app uses the global service.
  final SimpleFinService? service;

  @override
  State<SimpleFinPage> createState() => _SimpleFinPageState();
}

class _SimpleFinPageState extends State<SimpleFinPage> {
  final _token = TextEditingController();

  List<BankConnectionSummary> _connections = const [];
  bool _loading = true;
  bool _loadFailed = false;
  bool _connecting = false;
  String? _error;

  SimpleFinService get _service => widget.service ?? simpleFinService;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _token.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final connections = await _service.connections();
      if (!mounted) return;
      setState(() {
        _connections = connections;
        _loading = false;
        _loadFailed = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _loadFailed = true;
      });
    }
  }

  Future<void> _openBridge() async {
    try {
      final url = await _service.bridgeUrl();
      final uri = Uri.tryParse(url);
      if (uri != null && uri.scheme == 'https') {
        await launchUrl(uri, mode: LaunchMode.externalApplication);
      }
    } catch (_) {
      // The page explains where to go; a failed link is not worth an error.
    }
  }

  Future<void> _connect() async {
    final l10n = AppLocalizations.of(context);
    final token = _token.text.trim();
    if (token.isEmpty || _connecting) return;

    setState(() {
      _connecting = true;
      _error = null;
    });
    try {
      final result = await _service.connect(token);
      if (!mounted) return;
      if (result.success) {
        _token.clear();
        showAppToast(context, result.message ?? l10n.sfConnected, type: AppToastType.success);
        await _load();
      } else {
        setState(() => _error = result.message ?? l10n.sfConnectFailed);
      }
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = apiErrorMessage(e, fallback: l10n.sfConnectFailed));
    } finally {
      if (mounted) setState(() => _connecting = false);
    }
  }

  Future<void> _disconnect(BankConnectionSummary connection) async {
    final l10n = AppLocalizations.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.sfDisconnectTitle),
        content: Text(l10n.sfDisconnectBody),
        actions: [
          TextButton(onPressed: () => Navigator.of(ctx).pop(false), child: Text(l10n.sfCancel)),
          FilledButton(
            key: const ValueKey('simplefin-confirm-disconnect'),
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(l10n.sfDisconnect),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;

    try {
      await _service.remove(connection.id);
      if (!mounted) return;
      showAppToast(context, l10n.sfDisconnected, type: AppToastType.success);
      await _load();
    } catch (e) {
      if (!mounted) return;
      showAppToast(context, apiErrorMessage(e, fallback: l10n.sfDisconnectFailed), type: AppToastType.error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    return GlassSubPage(
      title: l10n.sfTitle,
      intro: l10n.sfIntro,
      children: [
        GlassSection(
          icon: Icons.vpn_key_outlined,
          title: l10n.sfStep1,
          children: [
            Align(
              alignment: Alignment.centerLeft,
              child: GlassSecondaryButton(
                key: const ValueKey('simplefin-open-bridge'),
                onPressed: _openBridge,
                icon: Icons.open_in_new_rounded,
                label: l10n.sfOpenBridge,
              ),
            ),
          ],
        ),
        GlassSection(
          icon: Icons.content_paste_rounded,
          title: l10n.sfStep2,
          highlight: true,
          children: [
            GlassTextField(
              fieldKey: const ValueKey('simplefin-token'),
              controller: _token,
              enabled: !_connecting,
              autocorrect: false,
              label: l10n.sfTokenLabel,
              hint: l10n.sfTokenHint,
              onSubmitted: (_) => _connect(),
            ),
            if (_error != null) ...[
              const SizedBox(height: 10),
              GlassNote(key: const ValueKey('simplefin-error'), text: _error!, tone: GlassNoteTone.error),
            ],
            const SizedBox(height: 14),
            GlassPrimaryButton(
              key: const ValueKey('simplefin-connect'),
              expand: true,
              onPressed: _connecting ? null : _connect,
              label: _connecting ? l10n.sfConnecting : l10n.sfConnectButton,
            ),
            const SizedBox(height: 8),
            Text(l10n.sfTokenOnce, style: cineMutedStyle()),
          ],
        ),
        GlassSection(
          icon: Icons.account_balance_outlined,
          title: l10n.sfYourConnections,
          children: [
            if (_loading)
              const GlassSpinner()
            else if (_loadFailed)
              GlassNote(text: l10n.sfLoadFailed, tone: GlassNoteTone.error)
            else if (_connections.isEmpty)
              Text(l10n.sfNoConnections, style: cineMutedStyle(size: 13))
            else
              for (var i = 0; i < _connections.length; i++) ...[
                if (i > 0) const GlassDivider(height: 20),
                _ConnectionCard(connection: _connections[i], onDisconnect: () => _disconnect(_connections[i])),
              ],
          ],
        ),
      ],
    );
  }
}

class _ConnectionCard extends StatelessWidget {
  const _ConnectionCard({required this.connection, required this.onDisconnect});

  final BankConnectionSummary connection;
  final VoidCallback onDisconnect;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    final (label, color) = switch (connection) {
      BankConnectionSummary(isActive: false) => (l10n.sfStatusDisconnected, cineMuted),
      BankConnectionSummary(isHealthy: true) => (l10n.sfStatusOk, cineIncome),
      _ => (l10n.sfStatusNeedsAttention, cineWarning),
    };

    final lastSync = connection.lastSyncAt == null
        ? l10n.sfNeverSynced
        : l10n.sfLastSync(MaterialLocalizations.of(context).formatShortDate(connection.lastSyncAt!.toLocal()));

    return Column(
      key: ValueKey('simplefin-connection-${connection.id}'),
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(child: Text(connection.institutionName, style: cineRowTitleStyle())),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
              decoration: BoxDecoration(
                color: color.withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(6),
              ),
              child: Text(label, style: GoogleFonts.sora(fontSize: 11, fontWeight: FontWeight.w600, color: color)),
            ),
          ],
        ),
        const SizedBox(height: 4),
        Text(l10n.sfConnectionSummary(l10n.sfAccountCount(connection.accountCount), lastSync), style: cineMutedStyle()),
        if (connection.requiresUserAction) ...[
          const SizedBox(height: 8),
          if (connection.message != null && connection.message!.isNotEmpty)
            Text(connection.message!, style: cineBodyStyle()),
          const SizedBox(height: 6),
          // Active but flagged: the bridge wants a fresh sign-in, and a new token would only
          // add a second connection. Only a broken (login required / error) one is repaired by a token.
          GlassNote(
            text: connection.status == 'Active' ? l10n.sfNeedsBridgeSignInHint : l10n.sfNeedsTokenHint,
            tone: GlassNoteTone.warning,
          ),
        ],
        if (connection.isActive)
          Align(
            alignment: Alignment.centerRight,
            child: GlassTextAction(
              key: ValueKey('simplefin-disconnect-${connection.id}'),
              onPressed: onDisconnect,
              label: l10n.sfDisconnect,
              color: cineMuted,
            ),
          ),
      ],
    );
  }
}
