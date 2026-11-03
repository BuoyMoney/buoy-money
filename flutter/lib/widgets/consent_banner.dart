import '../config/app_brand.dart';
import '../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../services/analytics_service.dart';

/// Wraps the whole app and shows a one-time analytics consent banner until the
/// user makes a choice (issue #606). Analytics (PostHog) is not started until
/// the user opts in — see [AnalyticsService]. Choice is remembered across
/// launches; it can be changed later from Settings.
class ConsentGate extends StatefulWidget {
  const ConsentGate({super.key, required this.child});

  final Widget child;

  @override
  State<ConsentGate> createState() => _ConsentGateState();
}

class _ConsentGateState extends State<ConsentGate> {
  /// Null while we load the stored choice; true once the user has decided
  /// (either way) so the banner stays hidden.
  bool _decided = true;

  @override
  void initState() {
    super.initState();
    analytics.loadConsentStatus().then((status) {
      if (mounted) setState(() => _decided = status != null);
    });
  }

  Future<void> _accept() async {
    await analytics.grantConsent();
    if (mounted) setState(() => _decided = true);
  }

  Future<void> _decline() async {
    await analytics.revokeConsent();
    if (mounted) setState(() => _decided = true);
  }

  Future<void> _openPrivacyPolicy() async {
    final uri = Uri.parse('https://buoy-ai.com/privacy-policy');
    await launchUrl(uri, mode: LaunchMode.externalApplication);
  }

  @override
  Widget build(BuildContext context) {
    return Stack(
      children: [
        widget.child,
        if (!_decided)
          Positioned(
            left: 0,
            right: 0,
            bottom: 0,
            child: SafeArea(
              top: false,
              child: _ConsentBanner(
                onAccept: _accept,
                onDecline: _decline,
                onPrivacy: _openPrivacyPolicy,
              ),
            ),
          ),
      ],
    );
  }
}

class _ConsentBanner extends StatelessWidget {
  const _ConsentBanner({
    required this.onAccept,
    required this.onDecline,
    required this.onPrivacy,
  });

  final VoidCallback onAccept;
  final VoidCallback onDecline;
  final VoidCallback onPrivacy;

  static const Color _bg = Color(0xFF0C0C18);
  static const Color _accent = Color(0xFF6366F1);
  static const Color _text = Color(0xFFF0EFF4);
  static const Color _muted = Color(0xFFB8B8C8);

  @override
  Widget build(BuildContext context) {
    final isNarrow = MediaQuery.of(context).size.width < 560;

    final message = Text.rich(
      TextSpan(
        style: const TextStyle(color: _muted, fontSize: 13, height: 1.4),
        children: [
          const TextSpan(
            text:
                'We use privacy-friendly product analytics to understand how ${AppBrand.name} '
                'is used and improve it. No session recording, no ads, and we never '
                'track you across other apps or websites or share this data with '
                'anyone. You can change this anytime in Settings. ',
          ),
          WidgetSpan(
            alignment: PlaceholderAlignment.middle,
            child: GestureDetector(
              onTap: onPrivacy,
              child: const Text(
                'Privacy Policy',
                style: TextStyle(
                  color: _accent,
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  decoration: TextDecoration.underline,
                ),
              ),
            ),
          ),
        ],
      ),
    );

    final declineButton = TextButton(
      onPressed: onDecline,
      style: TextButton.styleFrom(
        foregroundColor: _muted,
        padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
      ),
      child: Text(AppLocalizations.of(context).uiDecline),
    );

    final acceptButton = FilledButton(
      onPressed: onAccept,
      style: FilledButton.styleFrom(
        backgroundColor: _accent,
        foregroundColor: Colors.white,
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 12),
        shape:
            RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      ),
      child: Text(AppLocalizations.of(context).uiAccept),
    );

    return Container(
      margin: const EdgeInsets.all(12),
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 16),
      constraints: const BoxConstraints(maxWidth: 720),
      decoration: BoxDecoration(
        color: _bg,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: Colors.white.withValues(alpha: 0.08)),
        boxShadow: [
          BoxShadow(
            color: Colors.black.withValues(alpha: 0.4),
            blurRadius: 24,
            offset: const Offset(0, 8),
          ),
        ],
      ),
      child: DefaultTextStyle(
        style: const TextStyle(color: _text),
        child: isNarrow
            ? Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  message,
                  const SizedBox(height: 14),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.end,
                    children: [declineButton, const SizedBox(width: 8), acceptButton],
                  ),
                ],
              )
            : Row(
                children: [
                  Expanded(child: message),
                  const SizedBox(width: 16),
                  declineButton,
                  const SizedBox(width: 8),
                  acceptButton,
                ],
              ),
      ),
    );
  }
}
