import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';

import '../../config/app_config.dart';
import '../../config/first_run.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../services/registration_service.dart';
import '../../services/server_selection_service.dart';
import '../../widgets/cinematic/glass_settings.dart';
import '../../widgets/glass_card.dart';
import '../../widgets/server_picker.dart';

/// First launch of the phone app: "How would you like to use Buoy Money?"
///
/// Hosted goes to sign-up on buoy-ai.com. "On my own server" asks for the address, checks it is a Buoy Money server
/// this app can use ([ServerSelectionService.probe]), switches to it, then asks it whether it has any account yet:
/// a brand-new server goes to sign-up (the first account becomes its admin), an existing one to sign-in. Either way the question
/// is never asked again on this device. The router decides who sees it ([FirstRun.shouldShow]).
class FirstRunPage extends StatefulWidget {
  const FirstRunPage({
    super.key,
    this.service,
    this.onHosted,
    this.onOwnServer,
    this.registrationLoader,
    this.animateBackground = true,
  });

  /// Injected by tests; the app uses the global service.
  final ServerSelectionService? service;

  /// After choosing hosted. Injected by tests; the app goes to sign-up.
  final VoidCallback? onHosted;

  /// After switching to the person's own server, with whether it has no account yet. Injected by tests; the app
  /// goes to sign-up for a brand-new server and to sign-in otherwise.
  final void Function(bool firstAccount)? onOwnServer;

  /// Reads the new server's sign-up status. Injected by tests; the app uses [registrationService].
  final Future<RegistrationStatus?> Function()? registrationLoader;

  /// False in widget tests: the animated background never settles.
  final bool animateBackground;

  @override
  State<FirstRunPage> createState() => _FirstRunPageState();
}

class _FirstRunPageState extends State<FirstRunPage> {
  final _address = TextEditingController();
  bool _own = false;
  bool _busy = false;
  String? _error;

  ServerSelectionService get _service => widget.service ?? serverSelectionService;

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
    // Rebuild for the http warning; drop a stale error once the person edits.
    setState(() => _error = null);
  }

  bool get _typedHttp {
    final origin = AppConfig.normalizeServerOrigin(_address.text);
    return origin != null && AppConfig.isInsecureOrigin(origin);
  }

  Future<void> _chooseHosted() async {
    if (_busy) return;
    await FirstRun.markDone();
    if (!mounted) return;
    if (widget.onHosted != null) {
      widget.onHosted!();
    } else {
      context.go('/register');
    }
  }

  Future<void> _connect() async {
    if (_busy) return;
    final l10n = AppLocalizations.of(context);
    setState(() {
      _busy = true;
      _error = null;
    });
    final probe = await _service.probe(_address.text);
    if (!mounted) return;
    if (!probe.ok) {
      setState(() {
        _busy = false;
        _error = serverProbeMessage(l10n, probe);
      });
      return;
    }
    await _service.useServer(probe.origin, capabilities: probe.capabilities!);
    await FirstRun.markDone();
    final firstAccount = await _isFreshServer();
    if (!mounted) return;
    setState(() => _busy = false);
    if (widget.onOwnServer != null) {
      widget.onOwnServer!(firstAccount);
    } else {
      context.go(firstAccount ? '/register' : '/login');
    }
  }

  /// True when the server we just switched to has no account yet. Anything unclear (no answer, an older server
  /// without the endpoint) counts as an existing server: sign-in is the safe landing, and it links to sign-up.
  Future<bool> _isFreshServer() async {
    try {
      final load = widget.registrationLoader ?? registrationService.getStatus;
      final status = await load().timeout(const Duration(seconds: 6));
      return status?.firstAccount == true;
    } catch (_) {
      return false;
    }
  }

  void _back() {
    if (_busy) return;
    setState(() {
      _own = false;
      _error = null;
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Scaffold(
      backgroundColor: cineBackground,
      body: GlassBackdrop(
        animate: widget.animateBackground,
        child: SafeArea(
          child: LayoutBuilder(
            builder: (context, constraints) => SingleChildScrollView(
              padding: const EdgeInsets.fromLTRB(24, 32, 24, 32),
              child: ConstrainedBox(
                constraints: BoxConstraints(minHeight: constraints.maxHeight - 64),
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 440),
                    child: AnimatedSwitcher(
                      duration: const Duration(milliseconds: 220),
                      switchInCurve: Curves.easeOutCubic,
                      switchOutCurve: Curves.easeInCubic,
                      child: _own ? _ownServer(l10n) : _choice(l10n),
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _headline(String text) => Text(
        text,
        textAlign: TextAlign.center,
        style: GoogleFonts.instrumentSerif(
            fontSize: 40, height: 1.1, fontWeight: FontWeight.w400, color: cineText),
      );

  Widget _kicker(AppLocalizations l10n) => Text(
        l10n.frunKicker.toUpperCase(),
        textAlign: TextAlign.center,
        style: GoogleFonts.sora(
            fontSize: 11, letterSpacing: 2.4, fontWeight: FontWeight.w600, color: cineAccent),
      );

  Widget _choice(AppLocalizations l10n) => Column(
        key: const ValueKey('first-run-choice'),
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _kicker(l10n),
          const SizedBox(height: 14),
          _headline(l10n.frunTitle),
          const SizedBox(height: 12),
          Text(l10n.frunSubtitle,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(fontSize: 13, height: 1.5, color: cineMuted)),
          const SizedBox(height: 36),
          _ChoiceCard(
            key: const ValueKey('first-run-hosted'),
            icon: Icons.cloud_outlined,
            title: l10n.frunHostedTitle,
            body: l10n.frunHostedBody,
            onTap: _chooseHosted,
          ),
          const SizedBox(height: 14),
          _ChoiceCard(
            key: const ValueKey('first-run-own'),
            icon: Icons.dns_outlined,
            title: l10n.frunOwnTitle,
            body: l10n.frunOwnBody,
            onTap: () => setState(() => _own = true),
          ),
        ],
      );

  Widget _ownServer(AppLocalizations l10n) => Column(
        key: const ValueKey('first-run-own-step'),
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _kicker(l10n),
          const SizedBox(height: 14),
          _headline(l10n.frunOwnHeading),
          const SizedBox(height: 12),
          Text(l10n.frunOwnHelp,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(fontSize: 13, height: 1.5, color: cineMuted)),
          const SizedBox(height: 32),
          GlassCard(
            borderRadius: 16,
            padding: const EdgeInsets.all(20),
            child: Material(
              type: MaterialType.transparency,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  GlassTextField(
                    fieldKey: const ValueKey('first-run-address'),
                    controller: _address,
                    label: l10n.srvAddressLabel,
                    hint: 'https://buoy.example.ts.net',
                    keyboardType: TextInputType.url,
                    autocorrect: false,
                    autofocus: true,
                    enabled: !_busy,
                    onSubmitted: (_) => _connect(),
                  ),
                  if (_typedHttp) ...[
                    const SizedBox(height: 10),
                    GlassNote(
                      key: const ValueKey('first-run-http-warning'),
                      text: l10n.srvHttpWarning,
                      tone: GlassNoteTone.warning,
                      icon: Icons.lock_open_rounded,
                    ),
                  ],
                  if (_error != null) ...[
                    const SizedBox(height: 10),
                    GlassNote(
                      key: const ValueKey('first-run-error'),
                      text: _error!,
                      tone: GlassNoteTone.error,
                    ),
                  ],
                  const SizedBox(height: 18),
                  GlassPrimaryButton(
                    key: const ValueKey('first-run-connect'),
                    label: _busy ? l10n.frunChecking : l10n.srvConnect,
                    busy: _busy,
                    expand: true,
                    onPressed: _busy ? null : _connect,
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 12),
          Center(
            child: GlassTextAction(
              key: const ValueKey('first-run-back'),
              label: l10n.back,
              color: cineMuted,
              onPressed: _busy ? null : _back,
            ),
          ),
        ],
      );
}

/// One of the two answers: an icon, what it is, one line about it.
class _ChoiceCard extends StatelessWidget {
  const _ChoiceCard({
    super.key,
    required this.icon,
    required this.title,
    required this.body,
    required this.onTap,
  });

  final IconData icon;
  final String title;
  final String body;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Semantics(
        button: true,
        child: GlassCard(
          padding: EdgeInsets.zero,
          borderRadius: 16,
          glowOpacity: 0.06,
          child: Material(
            type: MaterialType.transparency,
            child: InkWell(
              onTap: onTap,
              borderRadius: BorderRadius.circular(16),
              child: Padding(
                padding: const EdgeInsets.all(18),
                child: Row(
                  children: [
                    Container(
                      width: 44,
                      height: 44,
                      alignment: Alignment.center,
                      decoration: BoxDecoration(
                        color: cineAccent.withValues(alpha: 0.12),
                        borderRadius: BorderRadius.circular(12),
                        border: Border.all(color: cineAccent.withValues(alpha: 0.3)),
                      ),
                      child: Icon(icon, color: cineAccent, size: 22),
                    ),
                    const SizedBox(width: 14),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(title,
                              style: GoogleFonts.sora(
                                  fontSize: 15, fontWeight: FontWeight.w700, color: cineText)),
                          const SizedBox(height: 3),
                          Text(body, style: cineMutedStyle(size: 12.5)),
                        ],
                      ),
                    ),
                    const SizedBox(width: 8),
                    const Icon(Icons.arrow_forward_rounded, color: cineMuted, size: 20),
                  ],
                ),
              ),
            ),
          ),
        ),
      );
}
