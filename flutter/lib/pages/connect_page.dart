import '../l10n/generated/app_localizations.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show Clipboard, ClipboardData;
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';

import '../config/app_config.dart';
import '../config/server_capabilities.dart';
import '../providers/auth_provider.dart';
import '../services/connect_service.dart';
import '../utils/formatters.dart';
import '../widgets/app_toast.dart';
import '../utils/web_navigation.dart';
import '../widgets/glass_card.dart';
import '../widgets/cinematic/glass_settings.dart';
import '../widgets/cinematic/noise_overlay.dart';
import '../widgets/cinematic/backgrounds/interactive_background_layer.dart';

/// Lifecycle of the OAuth consent page.
enum _ConnectState {
  /// Fetching the consent request.
  loading,

  /// Request loaded — showing the approve/deny card.
  consent,

  /// A decision POST is in flight.
  deciding,

  /// Approved — sending the browser back to the assistant.
  redirecting,

  /// Denied — showing the confirmation + return link.
  denied,

  /// The request is gone (expired / consumed / already decided → 404).
  expired,

  /// Any other failure loading or deciding.
  error,
}

/// "BuoyAI Connect" OAuth consent page (RFC #782).
///
/// Reached at `/app/connect?request=<id>` after the backend `/oauth/authorize`
/// leg redirects here. The router auth-guard funnels an unauthenticated user to
/// login and back (preserving `?request=`), so by the time this builds the user
/// is signed in. On a decision the browser is navigated (full-page) to the
/// assistant's `redirect_uri`; a manual link is always offered as a fallback for
/// callbacks that reopen the native assistant app.
class ConnectPage extends StatefulWidget {
  /// The `request` query param — the id of the pending authorization request.
  final String? requestId;

  const ConnectPage({super.key, required this.requestId, this.animateBackground = true});

  /// False in widget tests of the access tokens view: the animated background never settles.
  final bool animateBackground;

  @override
  State<ConnectPage> createState() => _ConnectPageState();
}

class _ConnectPageState extends State<ConnectPage> {
  final ValueNotifier<Offset> _mousePos = ValueNotifier(Offset.zero);

  _ConnectState _state = _ConnectState.loading;
  ConsentRequest? _consent;
  String? _redirectUrl;
  String? _errorMessage;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _mousePos.dispose();
    super.dispose();
  }

  /// Display name for the connecting client, with a neutral fallback so the copy
  /// still reads before the request loads or if the backend omits the name.
  String get _clientName {
    final name = _consent?.clientName.trim() ?? '';
    return name.isEmpty ? 'the assistant' : name;
  }

  Future<void> _load() async {
    final id = widget.requestId;
    if (id == null || id.trim().isEmpty) {
      // No request id at all — treat as an expired/invalid link.
      setState(() => _state = _ConnectState.expired);
      return;
    }
    setState(() => _state = _ConnectState.loading);
    try {
      final consent = await connectService.getConsentRequest(id);
      if (!mounted) return;
      setState(() {
        _consent = consent;
        _state = _ConnectState.consent;
      });
    } on DioException catch (e) {
      if (!mounted) return;
      // 404 = expired / consumed / already decided.
      setState(() => _state = e.response?.statusCode == 404
          ? _ConnectState.expired
          : _ConnectState.error);
    } catch (_) {
      if (!mounted) return;
      setState(() => _state = _ConnectState.error);
    }
  }

  Future<void> _decide(bool approve) async {
    final id = widget.requestId;
    if (id == null || id.trim().isEmpty) {
      setState(() => _state = _ConnectState.expired);
      return;
    }
    setState(() {
      _state = _ConnectState.deciding;
      _errorMessage = null;
    });
    try {
      final redirectUrl = await connectService.decide(id, approve);
      if (!mounted) return;
      setState(() {
        _redirectUrl = redirectUrl;
        _state = approve ? _ConnectState.redirecting : _ConnectState.denied;
      });
      // Send the browser back to the assistant. Fire-and-forget: the manual
      // "Return to {clientName}" link is always rendered as a fallback (and this
      // is a no-op under tests, where the url_launcher plugin is absent).
      if (redirectUrl.isNotEmpty) {
        await navigateToExternalUrl(redirectUrl);
      }
    } on DioException catch (e) {
      if (!mounted) return;
      setState(() {
        if (e.response?.statusCode == 404) {
          _state = _ConnectState.expired;
        } else {
          _errorMessage = 'Something went wrong. Please try again.';
          _state = _ConnectState.consent;
        }
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _errorMessage = 'Something went wrong. Please try again.';
        _state = _ConnectState.consent;
      });
    }
  }

  Future<void> _returnToClient() async {
    final url = _redirectUrl;
    if (url != null && url.isNotEmpty) {
      await navigateToExternalUrl(url);
    }
  }

  /// Opened without a consent request on a self-hosted server (Settings > Tools for AI
  /// assistants): the page manages access tokens instead.
  bool get _tokensMode =>
      (widget.requestId == null || widget.requestId!.trim().isEmpty) &&
      ServerEdition.current.value.isSelfHost;

  @override
  Widget build(BuildContext context) {
    if (_tokensMode) return _TokensScaffold(animateBackground: widget.animateBackground);
    final theme = Theme.of(context);
    return Scaffold(
      body: Stack(
        children: [
          Positioned.fill(
            child: InteractiveBackgroundLayer(mouseNotifier: _mousePos),
          ),
          const Positioned.fill(child: NoiseOverlay(opacity: 0.1)),
          Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.fromLTRB(24, 64, 24, 48),
              // Centre INSIDE the scroll view: wrapping it in Center makes it
              // shrink-wrap to the card, stranding the web scrollbar mid-page.
              child: Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 460),
                  child: _buildBody(theme),
                ),
              ),
            ),
          ),
          Positioned.fill(
            child: MouseRegion(
              hitTestBehavior: HitTestBehavior.translucent,
              onHover: (e) => _mousePos.value = e.localPosition,
              child: const SizedBox.expand(),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildBody(ThemeData theme) {
    switch (_state) {
      case _ConnectState.loading:
        return _buildLoading(theme);
      case _ConnectState.consent:
      case _ConnectState.deciding:
        return _buildConsent(theme);
      case _ConnectState.redirecting:
        return _buildOutcome(
          theme,
          icon: Icons.check_circle_outline,
          title: 'Connected',
          body: 'Sending you back to $_clientName…',
        );
      case _ConnectState.denied:
        return _buildOutcome(
          theme,
          icon: Icons.block_outlined,
          title: 'Access denied',
          body: 'You denied access. Nothing was shared with $_clientName.',
        );
      case _ConnectState.expired:
        return _buildError(
          theme,
          'This connection request has expired — start again from the assistant.',
        );
      case _ConnectState.error:
        return _buildError(
          theme,
          "We couldn't load this connection request. Please start again from "
          'the assistant.',
        );
    }
  }

  Widget _buildLoading(ThemeData theme) {
    return GlassCard(
      padding: const EdgeInsets.all(48),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          SizedBox(
            width: 28,
            height: 28,
            child: CircularProgressIndicator(
              strokeWidth: 2.5,
              color: theme.colorScheme.secondary,
            ),
          ),
          const SizedBox(height: 24),
          Text(
            AppLocalizations.of(context).connLoadingRequest,
            style: theme.textTheme.bodyMedium,
            textAlign: TextAlign.center,
          ),
        ],
      ),
    );
  }

  Widget _buildConsent(ThemeData theme) {
    final auth = context.watch<AuthProvider>();
    final email = auth.user?.email ?? '';
    final deciding = _state == _ConnectState.deciding;

    final descriptions = _consent?.scopeDescriptions ?? const <String>[];
    final bullets = descriptions.isNotEmpty
        ? descriptions
        : const [
            'Read your budgets, transactions, accounts, savings goals and '
                'insights — read-only',
          ];

    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          AppLocalizations.of(context).connConnectClientTitle(_clientName),
          style: theme.textTheme.displayLarge?.copyWith(fontSize: 34),
          textAlign: TextAlign.center,
        ),
        if (_consent?.clientUri != null &&
            _consent!.clientUri!.trim().isNotEmpty) ...[
          const SizedBox(height: 10),
          Text(
            _originText(_consent!.clientUri!),
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurface.withValues(alpha: 0.5),
            ),
            textAlign: TextAlign.center,
          ),
        ],
        const SizedBox(height: 32),
        GlassCard(
          padding: const EdgeInsets.all(28),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (_errorMessage != null) ...[
                _ErrorBanner(message: _errorMessage!),
                const SizedBox(height: 20),
              ],
              Text(
                AppLocalizations.of(context).connClientWantsTo(_clientName),
                style: GoogleFonts.sora(
                  fontSize: 13,
                  fontWeight: FontWeight.w500,
                  letterSpacing: 0.5,
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                ),
              ),
              const SizedBox(height: 16),
              ...bullets.map((b) => _ScopeBullet(text: b)),
              const SizedBox(height: 24),
              if (email.isNotEmpty) _SignedInAs(email: email),
              const SizedBox(height: 24),
              _AccentButton(
                label: 'Approve',
                isLoading: deciding,
                onPressed: deciding ? null : () => _decide(true),
              ),
              const SizedBox(height: 12),
              _QuietButton(
                label: 'Deny',
                onPressed: deciding ? null : () => _decide(false),
              ),
              const SizedBox(height: 20),
              Text(
                AppLocalizations.of(context).connRevokeNote(_clientName),
                style: GoogleFonts.sora(
                  fontSize: 12,
                  height: 1.5,
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.4),
                ),
                textAlign: TextAlign.center,
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildOutcome(
    ThemeData theme, {
    required IconData icon,
    required String title,
    required String body,
  }) {
    final accent = theme.colorScheme.secondary;
    final hasRedirect = _redirectUrl != null && _redirectUrl!.isNotEmpty;
    return GlassCard(
      padding: const EdgeInsets.all(36),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 44, color: accent),
          const SizedBox(height: 20),
          Text(
            title,
            style: theme.textTheme.displayLarge?.copyWith(fontSize: 30),
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 12),
          Text(
            body,
            style: theme.textTheme.bodyMedium,
            textAlign: TextAlign.center,
          ),
          if (hasRedirect) ...[
            const SizedBox(height: 28),
            _AccentButton(
              label: 'Return to $_clientName',
              onPressed: _returnToClient,
            ),
          ],
          const SizedBox(height: 16),
          Text(
            AppLocalizations.of(context).connCloseWindow,
            style: GoogleFonts.sora(
              fontSize: 12,
              color: theme.colorScheme.onSurface.withValues(alpha: 0.4),
            ),
            textAlign: TextAlign.center,
          ),
        ],
      ),
    );
  }

  Widget _buildError(ThemeData theme, String message) {
    return GlassCard(
      padding: const EdgeInsets.all(36),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            Icons.link_off_outlined,
            size: 44,
            color: theme.colorScheme.error.withValues(alpha: 0.8),
          ),
          const SizedBox(height: 20),
          Text(
            AppLocalizations.of(context).connUnavailable,
            style: theme.textTheme.displayLarge?.copyWith(fontSize: 28),
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 12),
          Text(
            message,
            style: theme.textTheme.bodyMedium,
            textAlign: TextAlign.center,
          ),
        ],
      ),
    );
  }

  /// Reduce a client URI to a bare origin ("https://chat.openai.com" → the host)
  /// for a compact trust line, falling back to the raw value if unparseable.
  String _originText(String uri) {
    final parsed = Uri.tryParse(uri);
    if (parsed != null && parsed.host.isNotEmpty) return parsed.host;
    return uri;
  }
}

class _ScopeBullet extends StatelessWidget {
  final String text;
  const _ScopeBullet({required this.text});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 2),
            child: Icon(
              Icons.check_circle,
              size: 18,
              color: theme.colorScheme.secondary.withValues(alpha: 0.9),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              text,
              style: GoogleFonts.sora(
                fontSize: 14,
                height: 1.4,
                color: theme.colorScheme.onSurface.withValues(alpha: 0.9),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _SignedInAs extends StatelessWidget {
  final String email;
  const _SignedInAs({required this.email});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      decoration: BoxDecoration(
        color: theme.scaffoldBackgroundColor.withValues(alpha: 0.4),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(
          color: theme.colorScheme.onSurface.withValues(alpha: 0.08),
        ),
      ),
      child: Row(
        children: [
          Icon(
            Icons.person_outline,
            size: 18,
            color: theme.colorScheme.onSurface.withValues(alpha: 0.4),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  AppLocalizations.of(context).connSignedInAs,
                  style: GoogleFonts.sora(
                    fontSize: 11,
                    color: theme.colorScheme.onSurface.withValues(alpha: 0.4),
                  ),
                ),
                Text(
                  email,
                  style: GoogleFonts.sora(
                    fontSize: 14,
                    fontWeight: FontWeight.w500,
                    color: theme.colorScheme.onSurface.withValues(alpha: 0.9),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _AccentButton extends StatelessWidget {
  final VoidCallback? onPressed;
  final bool isLoading;
  final String label;
  const _AccentButton({
    this.onPressed,
    this.isLoading = false,
    required this.label,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final accent = theme.colorScheme.secondary;
    return MouseRegion(
      cursor: onPressed != null
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      child: GestureDetector(
        onTap: onPressed,
        child: Container(
          padding: const EdgeInsets.symmetric(vertical: 16),
          decoration: BoxDecoration(
            color: onPressed != null
                ? accent.withValues(alpha: 0.9)
                : accent.withValues(alpha: 0.3),
            borderRadius: BorderRadius.circular(16),
          ),
          alignment: Alignment.center,
          child: isLoading
              ? SizedBox(
                  width: 22,
                  height: 22,
                  child: CircularProgressIndicator(
                    strokeWidth: 2.5,
                    color: theme.scaffoldBackgroundColor,
                  ),
                )
              : Text(
                  label,
                  style: GoogleFonts.sora(
                    fontSize: 16,
                    fontWeight: FontWeight.w600,
                    color: theme.scaffoldBackgroundColor,
                    letterSpacing: 0.3,
                  ),
                ),
        ),
      ),
    );
  }
}

class _QuietButton extends StatelessWidget {
  final VoidCallback? onPressed;
  final String label;
  const _QuietButton({this.onPressed, required this.label});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return TextButton(
      onPressed: onPressed,
      style: TextButton.styleFrom(
        padding: const EdgeInsets.symmetric(vertical: 14),
      ),
      child: Text(
        label,
        style: GoogleFonts.sora(
          fontSize: 15,
          fontWeight: FontWeight.w500,
          color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
        ),
      ),
    );
  }
}

class _ErrorBanner extends StatelessWidget {
  final String message;
  const _ErrorBanner({required this.message});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: theme.colorScheme.error.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(12),
        border:
            Border.all(color: theme.colorScheme.error.withValues(alpha: 0.2)),
      ),
      child: Row(
        children: [
          Icon(Icons.error_outline,
              size: 18, color: theme.colorScheme.error.withValues(alpha: 0.8)),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              message,
              style: GoogleFonts.sora(
                fontSize: 13,
                color: theme.colorScheme.error.withValues(alpha: 0.9),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

// ─── Access tokens (self-hosted servers) ──────────────────────────────────

/// Settings > Tools for AI assistants on a self-hosted server: the page at `/connect` without a
/// consent request. A top-level route (outside the nav shell), so it paints the app's background itself.
class _TokensScaffold extends StatelessWidget {
  const _TokensScaffold({this.animateBackground = true});

  final bool animateBackground;

  @override
  Widget build(BuildContext context) {
    return GlassSubPage(
      title: AppLocalizations.of(context).ctkSettingsTitle,
      backdrop: true,
      animateBackdrop: animateBackground,
      children: const [ConnectTokensSection()],
    );
  }
}

enum _TokensState { loading, ready, off, error }

/// Personal access tokens for MCP tools such as Open WebUI. List, create (the secret is shown
/// once), revoke. A 404 from the server means tools for AI assistants are turned off there.
class ConnectTokensSection extends StatefulWidget {
  const ConnectTokensSection({super.key, this.service});

  /// Injected by tests; the app uses the global [connectService].
  final ConnectService? service;

  @override
  State<ConnectTokensSection> createState() => _ConnectTokensSectionState();
}

class _ConnectTokensSectionState extends State<ConnectTokensSection> {
  ConnectService get _svc => widget.service ?? connectService;

  _TokensState _state = _TokensState.loading;
  List<ConnectToken> _tokens = const [];
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => _state = _TokensState.loading);
    try {
      final tokens = await _svc.listTokens();
      if (!mounted) return;
      setState(() {
        _tokens = tokens;
        _state = _TokensState.ready;
      });
    } on DioException catch (e) {
      if (!mounted) return;
      setState(() => _state =
          e.response?.statusCode == 404 ? _TokensState.off : _TokensState.error);
    } catch (_) {
      if (mounted) setState(() => _state = _TokensState.error);
    }
  }

  /// The server's own words for a 400 or 409, else [fallback].
  static String _serverMessage(Object error, String fallback) {
    if (error is DioException) {
      final data = error.response?.data;
      if (data is Map && data['message'] is String && (data['message'] as String).isNotEmpty) {
        return data['message'] as String;
      }
    }
    return fallback;
  }

  Future<void> _create() async {
    final l10n = AppLocalizations.of(context);
    final name = await showDialog<String>(
      context: context,
      builder: (_) => const _NewTokenDialog(),
    );
    if (name == null || !mounted) return;
    setState(() => _busy = true);
    try {
      final created = await _svc.createToken(name);
      if (!mounted) return;
      setState(() => _busy = false);
      await showDialog<void>(
        context: context,
        barrierDismissible: false,
        builder: (_) => _TokenShownDialog(created: created),
      );
      if (mounted) await _load();
    } catch (e) {
      if (!mounted) return;
      setState(() => _busy = false);
      showAppToast(context, _serverMessage(e, l10n.ctkCreateFailed), type: AppToastType.error);
    }
  }

  Future<void> _revoke(ConnectToken token) async {
    final l10n = AppLocalizations.of(context);
    final ok = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(l10n.ctkRevokeTitle(token.name)),
        content: Text(l10n.ctkRevokeBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(l10n.ctkCancel),
          ),
          FilledButton(
            key: const Key('connectTokenRevokeConfirm'),
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: Text(l10n.ctkRevoke),
          ),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    setState(() => _busy = true);
    try {
      await _svc.revokeToken(token.id);
      if (!mounted) return;
      showAppToast(context, l10n.ctkRevoked);
    } on DioException catch (e) {
      // 404: already gone, which is what the user wanted.
      if (mounted && e.response?.statusCode != 404) {
        showAppToast(context, l10n.ctkRevokeFailed, type: AppToastType.error);
      }
    } catch (_) {
      if (mounted) showAppToast(context, l10n.ctkRevokeFailed, type: AppToastType.error);
    }
    if (!mounted) return;
    setState(() => _busy = false);
    await _load();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);

    final Widget body;
    switch (_state) {
      case _TokensState.loading:
        body = const GlassSpinner();
      case _TokensState.off:
        // Turned off on this server: an expected state with a next step, not an error.
        body = GlassNote(text: l10n.ctkOff, key: const Key('connectTokensOff'));
      case _TokensState.error:
        body = Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            GlassNote(text: l10n.ctkLoadFailed, tone: GlassNoteTone.error),
            const SizedBox(height: 10),
            GlassSecondaryButton(onPressed: _load, label: l10n.ctkRetry),
          ],
        );
      case _TokensState.ready:
        body = Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (_tokens.isEmpty) Text(l10n.ctkEmpty, style: cineMutedStyle(size: 13)),
            for (var i = 0; i < _tokens.length; i++) ...[
              if (i > 0) const GlassDivider(height: 16),
              _TokenRow(token: _tokens[i], onRevoke: _busy ? null : () => _revoke(_tokens[i])),
            ],
            const SizedBox(height: 16),
            Align(
              alignment: Alignment.centerLeft,
              child: GlassPrimaryButton(
                key: const Key('connectTokenNew'),
                onPressed: _busy ? null : _create,
                icon: Icons.add_rounded,
                label: l10n.ctkNew,
              ),
            ),
          ],
        );
    }

    return GlassSection(
      icon: Icons.key_rounded,
      title: l10n.ctkTitle,
      children: [
        Text(l10n.ctkIntro, style: cineBodyStyle()),
        const SizedBox(height: 16),
        body,
      ],
    );
  }
}

class _TokenRow extends StatelessWidget {
  const _TokenRow({required this.token, required this.onRevoke});

  final ConnectToken token;
  final VoidCallback? onRevoke;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final created = token.createdAt;
    final used = token.lastUsedAt;
    return Row(
      key: ValueKey('connectToken-${token.id}'),
      children: [
        Container(
          padding: const EdgeInsets.all(8),
          decoration: BoxDecoration(
            color: cineAccent.withValues(alpha: 0.12),
            borderRadius: BorderRadius.circular(10),
          ),
          child: const Icon(Icons.vpn_key_outlined, size: 18, color: cineAccent),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(token.name, style: cineRowTitleStyle()),
              if (created != null) Text(l10n.ctkCreated(formatMediumDate(created)), style: cineMutedStyle()),
              Text(
                used == null ? l10n.ctkNeverUsed : l10n.ctkLastUsed(formatMediumDate(used)),
                style: cineMutedStyle(),
              ),
            ],
          ),
        ),
        GlassTextAction(
          key: ValueKey('connectTokenRevoke-${token.id}'),
          onPressed: onRevoke,
          label: l10n.ctkRevoke,
          color: cineExpense,
        ),
      ],
    );
  }
}

/// Asks for the token's name. Pops the trimmed name, or null on cancel.
class _NewTokenDialog extends StatefulWidget {
  const _NewTokenDialog();

  @override
  State<_NewTokenDialog> createState() => _NewTokenDialogState();
}

class _NewTokenDialogState extends State<_NewTokenDialog> {
  TextEditingController? _name;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _name ??= TextEditingController(text: AppLocalizations.of(context).ctkNameDefault);
  }

  @override
  void dispose() {
    _name?.dispose();
    super.dispose();
  }

  void _submit() {
    final name = _name!.text.trim();
    if (name.isEmpty) return;
    Navigator.of(context).pop(name);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return AlertDialog(
      title: Text(l10n.ctkNewTitle),
      content: TextField(
        key: const Key('connectTokenName'),
        controller: _name,
        autofocus: true,
        maxLength: 100,
        textInputAction: TextInputAction.done,
        onSubmitted: (_) => _submit(),
        decoration: InputDecoration(labelText: l10n.ctkNameLabel),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.ctkCancel),
        ),
        FilledButton(
          key: const Key('connectTokenCreate'),
          onPressed: _submit,
          child: Text(l10n.ctkCreate),
        ),
      ],
    );
  }
}

/// Shows the new secret and the MCP address once, each with a Copy button.
class _TokenShownDialog extends StatelessWidget {
  const _TokenShownDialog({required this.created});

  final CreatedConnectToken created;

  /// The server's own answer, else this app's server address plus /mcp.
  String get _mcpUrl => created.mcpUrl.isNotEmpty
      ? created.mcpUrl
      : '${AppConfig.baseUrl.replaceFirst(RegExp(r'/api$'), '')}/mcp';

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);

    Widget field(String label, String value, Key copyKey) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label, style: theme.textTheme.labelMedium),
          const SizedBox(height: 4),
          Row(
            children: [
              Expanded(
                child: SelectableText(
                  value,
                  style: GoogleFonts.firaCode(fontSize: 13),
                ),
              ),
              TextButton(
                key: copyKey,
                onPressed: () async {
                  await Clipboard.setData(ClipboardData(text: value));
                  if (context.mounted) showAppToast(context, l10n.ctkCopied);
                },
                child: Text(l10n.ctkCopy),
              ),
            ],
          ),
        ],
      );
    }

    return AlertDialog(
      title: Text(l10n.ctkShownTitle),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l10n.ctkShownNote,
                key: const Key('connectTokenOnceNote'),
                style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600)),
            const SizedBox(height: 16),
            field(l10n.ctkTokenLabel, created.token, const Key('connectTokenCopy')),
            const SizedBox(height: 12),
            field(l10n.ctkMcpLabel, _mcpUrl, const Key('connectTokenCopyMcp')),
          ],
        ),
      ),
      actions: [
        FilledButton(
          key: const Key('connectTokenDone'),
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.ctkDone),
        ),
      ],
    );
  }
}
