import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';

import '../../l10n/generated/app_localizations.dart';
import '../../providers/auth_provider.dart';
import '../../services/oidc_sign_in_service.dart';
import '../../widgets/glass_card.dart';

/// Trades the one-time code for a session. Returns true when signed in.
typedef OidcExchange = Future<bool> Function(String code, String? codeVerifier);

/// Where single sign-on lands: `/auth/oidc?code=...` (web, from the server's redirect) or the
/// phone's `com.buoyai.buoymoney://oidc?code=...` deep link, routed here by main.dart. Trades the
/// code for tokens, then continues like any other sign-in: the dashboard, or the login page's MFA
/// step when the account has app MFA.
class OidcCallbackPage extends StatefulWidget {
  const OidcCallbackPage({
    super.key,
    this.code,
    this.error,
    this.next,
    this.exchange,
    this.takeVerifier,
  });

  final String? code;

  /// A short code from the server when the sign-in was refused (no_account, expired, ...).
  final String? error;

  /// An in-app path to open after sign-in.
  final String? next;

  /// Test seam; defaults to [AuthProvider.oidcExchange].
  final OidcExchange? exchange;

  /// Test seam; defaults to [OidcSignInService.takeVerifier].
  final Future<String?> Function()? takeVerifier;

  @override
  State<OidcCallbackPage> createState() => _OidcCallbackPageState();
}

class _OidcCallbackPageState extends State<OidcCallbackPage> {
  String? _errorCode;
  String? _errorText;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _finish());
  }

  Future<void> _finish() async {
    final code = widget.code;
    if (widget.error != null || code == null || code.isEmpty) {
      setState(() => _errorCode = widget.error ?? 'expired');
      return;
    }

    final auth = context.read<AuthProvider>();
    final verifier =
        await (widget.takeVerifier ?? oidcSignInService.takeVerifier)();
    final exchange =
        widget.exchange ?? (c, v) => auth.oidcExchange(c, codeVerifier: v);
    final ok = await exchange(code, verifier);
    if (!mounted) return;

    if (ok) {
      context.go(_safeNext(widget.next) ?? '/dashboard');
      return;
    }
    if (auth.status == AuthStatus.mfaRequired && auth.mfaChallenge != null) {
      // The login page shows the MFA step for the pending challenge.
      context.go('/login');
      return;
    }
    setState(() {
      _errorCode = 'generic';
      _errorText = auth.errorMessage;
    });
  }

  static String? _safeNext(String? next) {
    if (next == null || !next.startsWith('/') || next.startsWith('//')) {
      return null;
    }
    return next;
  }

  String _message(AppLocalizations l10n) {
    switch (_errorCode) {
      case 'no_account':
        return l10n.oidcErrorNoAccount;
      case 'registration_closed':
        return l10n.oidcErrorClosed;
      case 'email_unverified':
        return l10n.oidcErrorUnverified;
      case 'denied':
        return l10n.oidcErrorDenied;
      case 'expired':
        return l10n.oidcErrorExpired;
      case 'provider_unavailable':
        return l10n.oidcErrorUnavailable;
      default:
        return _errorText ?? l10n.oidcErrorGeneric;
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final failed = _errorCode != null;
    return Scaffold(
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 420),
            child: GlassCard(
              padding: const EdgeInsets.all(28),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (!failed) ...[
                    const CircularProgressIndicator(),
                    const SizedBox(height: 20),
                    Text(l10n.oidcSigningIn,
                        style: theme.textTheme.titleMedium),
                  ] else ...[
                    Icon(Icons.error_outline,
                        size: 36, color: theme.colorScheme.error),
                    const SizedBox(height: 16),
                    Text(
                      _message(l10n),
                      key: const Key('oidc-error'),
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyLarge,
                    ),
                    const SizedBox(height: 24),
                    FilledButton(
                      onPressed: () {
                        context.read<AuthProvider>().clearError();
                        context.go('/login');
                      },
                      child: Text(l10n.oidcBackToSignIn),
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
