import '../l10n/generated/app_localizations.dart';
import 'dart:async';
import 'dart:ui';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import '../providers/auth_provider.dart';
import '../services/email_verification_service.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _expense = Color(0xFFF87171);
const _cardBorder = Color(0x14FFFFFF);

/// Shows the email-verification dialog. A code is emailed on open; entering it
/// marks the address verified. Returns true once verified, null if cancelled.
Future<bool?> showVerifyEmailDialog(BuildContext context) {
  return showDialog<bool>(
    context: context,
    barrierDismissible: false,
    builder: (_) => const _VerifyEmailDialog(),
  );
}

class _VerifyEmailDialog extends StatefulWidget {
  const _VerifyEmailDialog();

  @override
  State<_VerifyEmailDialog> createState() => _VerifyEmailDialogState();
}

class _VerifyEmailDialogState extends State<_VerifyEmailDialog> {
  final _codeController = TextEditingController();
  bool _sending = true;
  bool _verifying = false;
  String? _error;
  String? _info;

  // Client-side resend throttle (the server also enforces a cooldown).
  static const _resendCooldown = 30;
  int _resendIn = 0;
  Timer? _cooldownTimer;

  @override
  void initState() {
    super.initState();
    _sendCode(initial: true);
  }

  @override
  void dispose() {
    _cooldownTimer?.cancel();
    _codeController.dispose();
    super.dispose();
  }

  void _startCooldown() {
    _cooldownTimer?.cancel();
    setState(() => _resendIn = _resendCooldown);
    _cooldownTimer = Timer.periodic(const Duration(seconds: 1), (t) {
      if (!mounted) {
        t.cancel();
        return;
      }
      setState(() => _resendIn -= 1);
      if (_resendIn <= 0) t.cancel();
    });
  }

  Future<void> _sendCode({bool initial = false}) async {
    setState(() {
      _sending = true;
      _error = null;
      _info = null;
    });
    try {
      final alreadyVerified = await emailVerificationService.sendCode();
      if (!mounted) return;
      if (alreadyVerified) {
        // No code was sent — the address is already verified (e.g. Google
        // sign-in). Reflect that in the app and close, rather than prompting
        // for a code that never arrived.
        await context.read<AuthProvider>().refreshUser();
        if (mounted) Navigator.pop(context, true);
        return;
      }
      setState(() {
        _sending = false;
        if (!initial) _info = 'A new code has been sent.';
      });
      _startCooldown();
    } catch (_) {
      if (mounted) {
        setState(() {
          _sending = false;
          _error = 'Could not send the code. Please try again.';
        });
      }
    }
  }

  Future<void> _verify() async {
    final code = _codeController.text.trim();
    if (code.isEmpty) return;

    setState(() {
      _verifying = true;
      _error = null;
      _info = null;
    });
    try {
      await emailVerificationService.confirmCode(code);
      // Refresh the profile so isEmailVerified updates app-wide.
      if (mounted) await context.read<AuthProvider>().refreshUser();
      if (mounted) Navigator.pop(context, true);
    } on DioException catch (e) {
      if (mounted) {
        setState(() {
          _verifying = false;
          _error = (e.response?.data is Map)
              ? (e.response!.data as Map)['message'] as String? ??
                  'Invalid or expired code'
              : 'Invalid or expired code';
        });
      }
    } catch (_) {
      if (mounted) {
        setState(() {
          _verifying = false;
          _error = 'Verification failed';
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final email = context.read<AuthProvider>().user?.email;

    return Dialog(
      backgroundColor: Colors.transparent,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(24),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 16, sigmaY: 16),
          child: Container(
            constraints: const BoxConstraints(maxWidth: 400),
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
                  width: 56,
                  height: 56,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    color: _accent.withValues(alpha: 0.1),
                    border: Border.all(color: _accent.withValues(alpha: 0.3)),
                  ),
                  child: const Icon(Icons.mark_email_read_outlined,
                      size: 28, color: _accent),
                ),
                const SizedBox(height: 16),
                Text(AppLocalizations.of(context).uiVerifyYourEmail,
                    style: GoogleFonts.sora(
                        fontSize: 18,
                        fontWeight: FontWeight.w700,
                        color: _textGhost)),
                const SizedBox(height: 8),
                Text(
                  email != null && email.isNotEmpty
                      ? 'Enter the 6-digit code we sent to $email.'
                      : 'Enter the 6-digit code we sent to your email.',
                  textAlign: TextAlign.center,
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
                ),
                const SizedBox(height: 20),
                if (_sending) ...[
                  const Padding(
                    padding: EdgeInsets.symmetric(vertical: 12),
                    child: CircularProgressIndicator(color: _accent),
                  ),
                ] else ...[
                  TextField(
                    controller: _codeController,
                    textAlign: TextAlign.center,
                    keyboardType: TextInputType.number,
                    maxLength: 6,
                    autofocus: true,
                    style: GoogleFonts.firaCode(
                        fontSize: 24, letterSpacing: 8, color: _textGhost),
                    decoration: InputDecoration(
                      counterText: '',
                      hintText: '000000',
                      hintStyle: GoogleFonts.firaCode(
                          fontSize: 24,
                          letterSpacing: 8,
                          color: _textMuted.withValues(alpha: 0.3)),
                      enabledBorder: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(12),
                        borderSide: BorderSide(color: _cardBorder),
                      ),
                      focusedBorder: OutlineInputBorder(
                        borderRadius: BorderRadius.circular(12),
                        borderSide:
                            BorderSide(color: _accent.withValues(alpha: 0.5)),
                      ),
                      filled: true,
                      fillColor: Colors.white.withValues(alpha: 0.03),
                    ),
                    onSubmitted: (_) => _verify(),
                  ),
                  const SizedBox(height: 16),
                  SizedBox(
                    width: double.infinity,
                    child: ElevatedButton(
                      onPressed: _verifying ? null : _verify,
                      style: ElevatedButton.styleFrom(
                        backgroundColor: _accent,
                        foregroundColor: _textGhost,
                        disabledBackgroundColor: _accent.withValues(alpha: 0.3),
                        padding: const EdgeInsets.symmetric(vertical: 14),
                        shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(12)),
                      ),
                      child: _verifying
                          ? const SizedBox(
                              width: 20,
                              height: 20,
                              child: CircularProgressIndicator(
                                  strokeWidth: 2, color: _textGhost))
                          : Text(AppLocalizations.of(context).uiVerify,
                              style: GoogleFonts.sora(
                                  fontSize: 14, fontWeight: FontWeight.w600)),
                    ),
                  ),
                  const SizedBox(height: 8),
                  TextButton(
                    onPressed: (_verifying || _resendIn > 0) ? null : () => _sendCode(),
                    child: Text(
                        _resendIn > 0 ? 'Resend code in ${_resendIn}s' : 'Resend code',
                        style: GoogleFonts.sora(
                            fontSize: 12,
                            color: _accent.withValues(
                                alpha: _resendIn > 0 ? 0.4 : 0.7))),
                  ),
                ],
                if (_info != null) ...[
                  const SizedBox(height: 4),
                  Text(_info!,
                      style:
                          GoogleFonts.sora(fontSize: 12, color: _accent)),
                ],
                if (_error != null) ...[
                  const SizedBox(height: 8),
                  Text(_error!,
                      style: GoogleFonts.sora(fontSize: 12, color: _expense)),
                ],
                const SizedBox(height: 8),
                TextButton(
                  onPressed: () => Navigator.pop(context, null),
                  child: Text(AppLocalizations.of(context).uiCancel2,
                      style: GoogleFonts.sora(
                          fontSize: 13, color: _textMuted.withValues(alpha: 0.7))),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
