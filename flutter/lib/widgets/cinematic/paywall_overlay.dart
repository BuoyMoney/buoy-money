import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../providers/auth_provider.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBorder = Color(0x1FFFFFFF);
const _expense = Color(0xFFF87171);

/// Full-screen, non-dismissible paywall shown over every authenticated page when
/// the account must choose and pay for a plan before using the app (a returning
/// user whose email already consumed its free trial).
///
/// It deliberately does NOT collect payment itself — it routes the user to the
/// billing page, where hosted Stripe Checkout completes the purchase. Once a plan
/// is active the backend clears `requiresPaidPlan` and this overlay disappears.
class PaywallOverlay extends StatefulWidget {
  const PaywallOverlay({super.key});

  @override
  State<PaywallOverlay> createState() => _PaywallOverlayState();
}

class _PaywallOverlayState extends State<PaywallOverlay> {
  bool _refreshing = false;

  Future<void> _refresh() async {
    setState(() => _refreshing = true);
    await context.read<AuthProvider>().refreshUser();
    if (mounted) setState(() => _refreshing = false);
  }

  @override
  Widget build(BuildContext context) {
    return Positioned.fill(
      child: ClipRect(
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 18, sigmaY: 18),
          child: Container(
            color: const Color(0xFF04040A).withValues(alpha: 0.82),
            alignment: Alignment.center,
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(24),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 440),
                child: Container(
                  padding: const EdgeInsets.all(28),
                  decoration: BoxDecoration(
                    color: const Color(0xFF0C0C18).withValues(alpha: 0.92),
                    borderRadius: BorderRadius.circular(24),
                    border: Border.all(color: _cardBorder),
                  ),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(
                        width: 60,
                        height: 60,
                        decoration: BoxDecoration(
                          shape: BoxShape.circle,
                          color: _accent.withValues(alpha: 0.12),
                          border:
                              Border.all(color: _accent.withValues(alpha: 0.3)),
                        ),
                        child: const Icon(Icons.lock_outline,
                            size: 28, color: _accent),
                      ),
                      const SizedBox(height: 18),
                      Text(
                        AppLocalizations.of(context).uiPaywallTitle,
                        textAlign: TextAlign.center,
                        style: GoogleFonts.sora(
                          fontSize: 20,
                          fontWeight: FontWeight.w700,
                          color: _textGhost,
                        ),
                      ),
                      const SizedBox(height: 10),
                      Text(
                        AppLocalizations.of(context).uiPaywallBody,
                        textAlign: TextAlign.center,
                        style: GoogleFonts.sora(
                          fontSize: 13,
                          height: 1.5,
                          color: _textMuted,
                        ),
                      ),
                      const SizedBox(height: 24),
                      SizedBox(
                        width: double.infinity,
                        child: ElevatedButton.icon(
                          onPressed: () => context.go('/billing'),
                          icon: const Icon(Icons.workspace_premium_outlined,
                              size: 18),
                          label: Text(
                            AppLocalizations.of(context).uiChooseYourPlan,
                            style: GoogleFonts.sora(
                                fontSize: 14, fontWeight: FontWeight.w600),
                          ),
                          style: ElevatedButton.styleFrom(
                            backgroundColor: _accent,
                            foregroundColor: _textGhost,
                            padding: const EdgeInsets.symmetric(vertical: 14),
                            shape: RoundedRectangleBorder(
                                borderRadius: BorderRadius.circular(12)),
                          ),
                        ),
                      ),
                      const SizedBox(height: 10),
                      SizedBox(
                        width: double.infinity,
                        child: OutlinedButton(
                          onPressed: _refreshing ? null : _refresh,
                          style: OutlinedButton.styleFrom(
                            foregroundColor: _textMuted,
                            side: BorderSide(color: _cardBorder),
                            padding: const EdgeInsets.symmetric(vertical: 13),
                            shape: RoundedRectangleBorder(
                                borderRadius: BorderRadius.circular(12)),
                          ),
                          child: _refreshing
                              ? const SizedBox(
                                  width: 18,
                                  height: 18,
                                  child: CircularProgressIndicator(
                                      strokeWidth: 2, color: _textMuted),
                                )
                              : Text(
                                  AppLocalizations.of(context).uiCompletedPaymentRefresh,
                                  style: GoogleFonts.sora(fontSize: 13),
                                ),
                        ),
                      ),
                      const SizedBox(height: 4),
                      TextButton(
                        onPressed: () => context.read<AuthProvider>().logout(),
                        child: Text(
                          AppLocalizations.of(context).uiSignOut,
                          style: GoogleFonts.sora(
                              fontSize: 13,
                              color: _expense.withValues(alpha: 0.85)),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
