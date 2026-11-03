import '../config/app_brand.dart';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:google_fonts/google_fonts.dart';

import '../l10n/generated/app_localizations.dart';
import '../services/analytics_service.dart';
import '../services/api_client.dart';
import '../services/passkey_service.dart';
import 'app_toast.dart';
import 'verify_email_dialog.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _sheetBg = Color(0xFF0C0C18);

/// Inline enrollment for the phishing-resistant MFA that the server requires
/// before any Plaid operation (TOTP or passkey; see TwoFactorService
/// .CanAccessPlaidFeaturesAsync).
///
/// Shown at the moment of intent: the user tapped Connect Bank, the server said
/// requiresMfa, and instead of a dead-end "go to Settings" toast they get the
/// enrollment right here, then continue straight into Plaid Link. The security
/// policy is unchanged; only the path to satisfying it moved to where the user
/// already is. Every real user who tried to connect a bank in Aug 2026 (10 of 10
/// attempts) hit the old toast and none came back, which is why this exists.
///
/// Returns true when a qualifying method was enrolled, false/null when the user
/// backed out. Emits bank_mfa_gate_completed / bank_mfa_gate_abandoned.
///
/// [client] is the enrollment backend; defaults to the live services. Tests
/// inject a fake (the passkeys plugin and the ApiClient interceptor chain both
/// misbehave under flutter_test's fake async).
Future<bool> showBankMfaGateSheet(
  BuildContext context, {
  String? displayName,
  BankMfaEnrollmentClient? client,
}) async {
  final result = await showModalBottomSheet<bool>(
    context: context,
    backgroundColor: _sheetBg,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
    ),
    builder: (_) => _BankMfaGateSheet(
      displayName: displayName,
      client: client ?? const LiveBankMfaEnrollmentClient(),
    ),
  );
  if (result != true) {
    analytics.track(AnalyticsEvents.bankMfaGateAbandoned);
  }
  return result == true;
}

/// The three backend interactions the gate sheet needs. Kept tiny so it is
/// trivially fakeable; the live implementation delegates to passkeyService and
/// the /twofactor endpoints.
abstract class BankMfaEnrollmentClient {
  Future<bool> passkeySupported();
  /// Returns the device name the passkey was registered on.
  Future<String> registerPasskey({String? displayName});
  /// Returns the setup payload: qrCodeUrl + secret (case-tolerant keys).
  Future<Map<String, dynamic>> setupTotp();
  /// Throws when the code is rejected.
  Future<void> verifyTotp(String code);
  /// Proves inbox control (the verify-email dialog), then enables email-code
  /// MFA. Returns false if the user backed out of the dialog.
  Future<bool> enrollEmail(BuildContext context);
}

class LiveBankMfaEnrollmentClient implements BankMfaEnrollmentClient {
  const LiveBankMfaEnrollmentClient();

  @override
  Future<bool> passkeySupported() => passkeyService.isSupported();

  @override
  Future<String> registerPasskey({String? displayName}) =>
      passkeyService.registerPasskey(displayName: displayName);

  @override
  Future<Map<String, dynamic>> setupTotp() async {
    final r = await apiClient.dio.post<Map<String, dynamic>>('/twofactor/setup-totp');
    return r.data ?? const {};
  }

  @override
  Future<void> verifyTotp(String code) =>
      apiClient.dio.post<Map<String, dynamic>>('/twofactor/verify-totp', data: {'code': code});

  @override
  Future<bool> enrollEmail(BuildContext context) async {
    // Email-code MFA is the same proof as email verification (send + confirm a
    // code), so reuse that dialog verbatim; then flip the account's MFA type.
    final verified = await showVerifyEmailDialog(context);
    if (verified != true) return false;
    await apiClient.dio.post<Map<String, dynamic>>('/twofactor/enable-email');
    return true;
  }
}

class _BankMfaGateSheet extends StatefulWidget {
  final String? displayName;
  final BankMfaEnrollmentClient client;
  const _BankMfaGateSheet({this.displayName, required this.client});

  @override
  State<_BankMfaGateSheet> createState() => _BankMfaGateSheetState();
}

class _BankMfaGateSheetState extends State<_BankMfaGateSheet> {
  bool _passkeySupported = false;
  bool _checkedPasskey = false;
  bool _busy = false;

  // TOTP enrollment state (null until the user picks the authenticator path).
  Map<String, dynamic>? _totpSetup;
  final _codeCtrl = TextEditingController();
  bool _secretCopied = false;
  String? _codeError;

  @override
  void initState() {
    super.initState();
    _probePasskey();
  }

  @override
  void dispose() {
    _codeCtrl.dispose();
    super.dispose();
  }

  Future<void> _probePasskey() async {
    bool ok = false;
    try {
      ok = await widget.client.passkeySupported();
    } catch (_) {}
    if (!mounted) return;
    setState(() {
      _passkeySupported = ok;
      _checkedPasskey = true;
    });
  }

  // ── Passkey (one tap) ────────────────────────────────

  Future<void> _enrollPasskey() async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      final device = await widget.client.registerPasskey(
        displayName: widget.displayName ?? '${AppBrand.name} User',
      );
      if (!mounted) return;
      analytics.track(AnalyticsEvents.bankMfaGateCompleted, {'method': 'passkey'});
      showAppToast(context, 'Passkey registered on $device', type: AppToastType.success);
      Navigator.pop(context, true);
    } catch (e) {
      if (!mounted) return;
      setState(() => _busy = false);
      final msg = e.toString();
      // User-cancelled OS sheets surface as generic errors; keep the toast short.
      showAppToast(context, 'Passkey setup did not finish${msg.length > 60 ? '' : ': $msg'}',
          type: AppToastType.error);
    }
  }

  // ── Email code (least secure; 30-day window) ─────────

  Future<void> _enrollEmail() async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      final ok = await widget.client.enrollEmail(context);
      if (!mounted) return;
      if (!ok) {
        setState(() => _busy = false);
        return;
      }
      analytics.track(AnalyticsEvents.bankMfaGateCompleted, {'method': 'email'});
      showAppToast(context, 'Email code enabled. Add a passkey later to keep bank access.',
          type: AppToastType.success);
      Navigator.pop(context, true);
    } catch (_) {
      if (!mounted) return;
      setState(() => _busy = false);
      showAppToast(context, 'Could not enable email code', type: AppToastType.error);
    }
  }

  // ── TOTP (QR + 6-digit code) ─────────────────────────

  Future<void> _startTotp() async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      final setup = await widget.client.setupTotp();
      if (!mounted) return;
      setState(() {
        _totpSetup = setup;
        _busy = false;
        _codeError = null;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _busy = false);
      showAppToast(context, 'Could not start authenticator setup', type: AppToastType.error);
    }
  }

  Future<void> _verifyTotp() async {
    final code = _codeCtrl.text.trim();
    if (code.length < 6 || _busy) return;
    setState(() {
      _busy = true;
      _codeError = null;
    });
    try {
      await widget.client.verifyTotp(code);
      if (!mounted) return;
      analytics.track(AnalyticsEvents.bankMfaGateCompleted, {'method': 'totp'});
      showAppToast(context, 'Authenticator enabled', type: AppToastType.success);
      Navigator.pop(context, true);
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _codeError = 'That code did not match. Codes rotate every 30 seconds.';
      });
    }
  }

  // ── UI ───────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    final bottomInset = MediaQuery.of(context).viewInsets.bottom;
    return AnimatedPadding(
      duration: const Duration(milliseconds: 150),
      padding: EdgeInsets.only(bottom: bottomInset),
      child: SafeArea(
        top: false,
        child: SingleChildScrollView(
          padding: const EdgeInsets.fromLTRB(24, 12, 24, 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Center(
                child: Container(
                  width: 36,
                  height: 4,
                  decoration: BoxDecoration(
                    color: Colors.white24,
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
              ),
              const SizedBox(height: 20),
              Row(
                children: [
                  Container(
                    width: 40,
                    height: 40,
                    decoration: BoxDecoration(
                      color: _accent.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(12),
                    ),
                    child: const Icon(Icons.shield_outlined, color: _accent, size: 22),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      _totpSetup == null
                          ? AppLocalizations.of(context).uiBankMfaTitle
                          : AppLocalizations.of(context).uiBankMfaTotpTitle,
                      style: GoogleFonts.sora(
                          fontSize: 18, fontWeight: FontWeight.w700, color: _textGhost),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 10),
              Text(
                _totpSetup == null
                    ? AppLocalizations.of(context).uiBankMfaIntro
                    : AppLocalizations.of(context).uiBankMfaTotpIntro,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.5),
              ),
              const SizedBox(height: 20),
              if (_totpSetup == null) _buildMethodPicker() else _buildTotpEnroll(),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildMethodPicker() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (!_checkedPasskey)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 12),
            child: Center(child: CircularProgressIndicator(color: _accent, strokeWidth: 2)),
          )
        else if (_passkeySupported)
          _MethodTile(
            icon: Icons.fingerprint_rounded,
            title: 'Use a passkey',
            subtitle: 'Face ID, fingerprint, or your device PIN. Recommended, one tap.',
            recommended: true,
            busy: _busy,
            onTap: _enrollPasskey,
          ),
        if (_checkedPasskey) const SizedBox(height: 10),
        if (_checkedPasskey)
          _MethodTile(
            icon: Icons.qr_code_rounded,
            title: 'Use an authenticator app',
            subtitle: 'Google Authenticator, Authy, or similar. Scan a code, type 6 digits.',
            recommended: !_passkeySupported,
            busy: _busy,
            onTap: _startTotp,
          ),
        if (_checkedPasskey) const SizedBox(height: 10),
        if (_checkedPasskey)
          _MethodTile(
            icon: Icons.email_outlined,
            title: 'Use an email code',
            subtitle: 'Least secure. Good to get started; upgrade within 30 days to keep linking banks.',
            badge: 'LEAST SECURE',
            busy: _busy,
            onTap: _enrollEmail,
          ),
        const SizedBox(height: 14),
        Center(
          child: TextButton(
            onPressed: _busy ? null : () => Navigator.pop(context, false),
            child: Text(AppLocalizations.of(context).uiNotNow,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          ),
        ),
      ],
    );
  }

  Widget _buildTotpEnroll() {
    final setup = _totpSetup!;
    final qrUrl = (setup['qrCodeUrl'] ?? setup['QrCodeUrl'])?.toString();
    final secret = (setup['secret'] ??
            setup['Secret'] ??
            setup['manualEntryCode'] ??
            setup['ManualEntryCode'] ??
            setup['manualEntryKey'] ??
            setup['ManualEntryKey'])
        ?.toString();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (qrUrl != null)
          Center(
            child: Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: Colors.white,
                borderRadius: BorderRadius.circular(12),
              ),
              child: _qrImage(qrUrl),
            ),
          ),
        if (secret != null) ...[
          const SizedBox(height: 14),
          Text(AppLocalizations.of(context).uiEnterKeyManually,
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
          const SizedBox(height: 6),
          GestureDetector(
            onTap: () async {
              await Clipboard.setData(ClipboardData(text: secret));
              if (mounted) setState(() => _secretCopied = true);
            },
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
              decoration: BoxDecoration(
                color: _cardBg,
                borderRadius: BorderRadius.circular(10),
                border: Border.all(
                    color: _secretCopied ? _income.withValues(alpha: 0.4) : _cardBorder),
              ),
              child: Row(
                children: [
                  Expanded(
                    child: Text(secret,
                        style: GoogleFonts.firaCode(
                            fontSize: 13, color: _accent, letterSpacing: 1.5)),
                  ),
                  Icon(_secretCopied ? Icons.check_rounded : Icons.copy_rounded,
                      size: 18, color: _secretCopied ? _income : _textMuted),
                ],
              ),
            ),
          ),
        ],
        const SizedBox(height: 18),
        TextField(
          controller: _codeCtrl,
          keyboardType: TextInputType.number,
          maxLength: 6,
          autofocus: true,
          onSubmitted: (_) => _verifyTotp(),
          onChanged: (v) {
            if (v.trim().length == 6) _verifyTotp();
          },
          style: GoogleFonts.firaCode(fontSize: 22, color: _textGhost, letterSpacing: 8),
          textAlign: TextAlign.center,
          decoration: InputDecoration(
            counterText: '',
            hintText: '000000',
            hintStyle: GoogleFonts.firaCode(
                fontSize: 22, color: _textMuted.withValues(alpha: 0.4), letterSpacing: 8),
            filled: true,
            fillColor: _cardBg,
            errorText: _codeError,
            errorStyle: GoogleFonts.sora(fontSize: 12, color: _expense),
            enabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(12),
              borderSide: const BorderSide(color: _cardBorder),
            ),
            focusedBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(12),
              borderSide: const BorderSide(color: _accent),
            ),
            errorBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(12),
              borderSide: const BorderSide(color: _expense),
            ),
            focusedErrorBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(12),
              borderSide: const BorderSide(color: _expense),
            ),
          ),
        ),
        const SizedBox(height: 14),
        SizedBox(
          height: 48,
          child: ElevatedButton(
            onPressed: _busy ? null : _verifyTotp,
            style: ElevatedButton.styleFrom(
              backgroundColor: _accent,
              foregroundColor: Colors.white,
              shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
            ),
            child: _busy
                ? const SizedBox(
                    width: 20,
                    height: 20,
                    child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                : Text(AppLocalizations.of(context).uiVerifyAndConnectBank,
                    style: GoogleFonts.sora(fontSize: 14, fontWeight: FontWeight.w600)),
          ),
        ),
        const SizedBox(height: 6),
        Center(
          child: TextButton(
            onPressed: _busy
                ? null
                : () => setState(() {
                      _totpSetup = null;
                      _codeCtrl.clear();
                      _codeError = null;
                    }),
            child: Text(AppLocalizations.of(context).back,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          ),
        ),
      ],
    );
  }

  Widget _qrImage(String url) {
    const size = 180.0;
    final fallback = SizedBox(
      width: size,
      height: size,
      child: Center(
          child: Text(AppLocalizations.of(context).uiQrFailedToLoad,
              style: const TextStyle(color: Colors.black54))),
    );
    if (url.startsWith('data:image/')) {
      try {
        final bytes = base64Decode(url.split(',').last);
        return Image.memory(bytes,
            width: size, height: size, errorBuilder: (_, __, ___) => fallback);
      } catch (_) {
        return fallback;
      }
    }
    return Image.network(url,
        width: size, height: size, errorBuilder: (_, __, ___) => fallback);
  }
}

class _MethodTile extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final bool recommended;
  /// Small chip text. Defaults to FASTEST when [recommended]; pass e.g.
  /// 'LEAST SECURE' for a cautionary tile.
  final String? badge;
  final bool busy;
  final VoidCallback onTap;

  const _MethodTile({
    required this.icon,
    required this.title,
    required this.subtitle,
    this.recommended = false,
    this.badge,
    required this.busy,
    required this.onTap,
  });

  String? get _chip => badge ?? (recommended ? 'FASTEST' : null);
  bool get _cautionary => badge != null && !recommended;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: busy ? null : onTap,
      borderRadius: BorderRadius.circular(14),
      child: Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(14),
          border: Border.all(
              color: recommended ? _accent.withValues(alpha: 0.5) : _cardBorder),
        ),
        child: Row(
          children: [
            Container(
              width: 40,
              height: 40,
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Icon(icon, color: _accent, size: 22),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Flexible(
                        child: Text(title,
                            style: GoogleFonts.sora(
                                fontSize: 14,
                                fontWeight: FontWeight.w600,
                                color: _textGhost)),
                      ),
                      if (_chip != null) ...[
                        const SizedBox(width: 8),
                        Container(
                          padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
                          decoration: BoxDecoration(
                            color: (_cautionary ? _expense : _accent).withValues(alpha: 0.2),
                            borderRadius: BorderRadius.circular(6),
                          ),
                          child: Text(_chip!,
                              style: GoogleFonts.sora(
                                  fontSize: 9,
                                  fontWeight: FontWeight.w700,
                                  color: _cautionary ? _expense : _accent,
                                  letterSpacing: 0.5)),
                        ),
                      ],
                    ],
                  ),
                  const SizedBox(height: 3),
                  Text(subtitle,
                      style: GoogleFonts.sora(
                          fontSize: 12, color: _textMuted, height: 1.4)),
                ],
              ),
            ),
            const SizedBox(width: 8),
            const Icon(Icons.chevron_right, color: _textMuted, size: 20),
          ],
        ),
      ),
    );
  }
}
