import 'dart:ui';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:passkeys/authenticator.dart';
import 'package:passkeys_platform_interface/types/types.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/mfa_service.dart';
import '../services/api_client.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _expense = Color(0xFFF87171);
const _cardBorder = Color(0x14FFFFFF);

const _operationLabels = {
  'ConnectBankAccount': 'connecting a bank account',
  'DisconnectBankAccount': 'disconnecting a bank account',
  'RefreshBankData': 'refreshing bank data',
  'ViewSensitiveFinancialData': 'viewing sensitive financial data',
  'ChangePassword': 'changing your password',
  'UpdateEmail': 'updating your email',
  'ChangeMfaSettings': 'changing MFA settings',
  'ManagePasskeys': 'managing passkeys',
  'DeleteAccount': 'deleting your account',
  'ManageCloudServers': 'managing your linked servers',
};

/// Shows a step-up MFA dialog for protected operations.
///
/// Returns the operation token on success, or null if cancelled.
Future<String?> showStepUpMfaDialog(
  BuildContext context, {
  required String operation,
}) {
  return showDialog<String>(
    context: context,
    barrierDismissible: false,
    builder: (_) => _StepUpMfaDialog(operation: operation),
  );
}

class _StepUpMfaDialog extends StatefulWidget {
  final String operation;
  const _StepUpMfaDialog({required this.operation});

  @override
  State<_StepUpMfaDialog> createState() => _StepUpMfaDialogState();
}

class _StepUpMfaDialogState extends State<_StepUpMfaDialog> {
  final _codeController = TextEditingController();
  String _selectedMethod = 'totp';
  List<String> _availableMethods = [];
  bool _isLoading = true;
  bool _isVerifying = false;
  bool _emailSent = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadAvailableMethods();
  }

  @override
  void dispose() {
    _codeController.dispose();
    super.dispose();
  }

  Future<void> _loadAvailableMethods() async {
    try {
      final methods = await mfaService.getAvailableMethods();
      if (!mounted) return;
      final normalized = methods.map((m) => m.toLowerCase()).toList();
      if (!normalized.contains('email')) {
        normalized.add('email');
      }
      setState(() {
        _availableMethods = normalized;
        if (normalized.contains('passkey')) {
          _selectedMethod = 'passkey';
        } else if (normalized.contains('totp')) {
          _selectedMethod = 'totp';
        } else {
          _selectedMethod = 'email';
        }
        _isLoading = false;
      });
    } catch (_) {
      if (mounted) setState(() { _isLoading = false; _error = 'Failed to load MFA methods'; });
    }
  }

  Future<void> _sendEmailCode() async {
    setState(() { _isLoading = true; _error = null; });
    try {
      await mfaService.requestStepUp(widget.operation);
      if (mounted) setState(() { _emailSent = true; _isLoading = false; });
    } catch (_) {
      if (mounted) setState(() { _isLoading = false; _error = 'Failed to send code'; });
    }
  }

  Future<void> _verifyCode() async {
    final code = _codeController.text.trim();
    if (code.isEmpty) return;

    setState(() { _isVerifying = true; _error = null; });
    try {
      final result = await mfaService.verifyStepUp(
        mfaCode: code,
        operation: widget.operation,
      );
      await apiClient.setOperationToken(result.operationToken);
      if (mounted) Navigator.pop(context, result.operationToken);
    } on DioException catch (e) {
      if (mounted) {
        setState(() {
          _isVerifying = false;
          _error = (e.response?.data is Map)
              ? (e.response!.data as Map)['message'] as String? ?? 'Verification failed'
              : 'Verification failed';
        });
      }
    } catch (_) {
      if (mounted) setState(() { _isVerifying = false; _error = 'Verification failed'; });
    }
  }

  Future<void> _verifyPasskey() async {
    setState(() { _isVerifying = true; _error = null; });
    try {
      final options = await mfaService.beginStepUpPasskey(widget.operation);
      final assertionOptionsJson = options['assertionOptions'] as String;
      final authRequest = AuthenticateRequestType.fromJsonString(assertionOptionsJson);

      final authenticator = PasskeyAuthenticator();
      final authResponse = await authenticator.authenticate(authRequest);

      final result = await mfaService.verifyStepUpPasskey(
        assertionResponse: authResponse.toJsonString(),
        operation: widget.operation,
      );
      await apiClient.setOperationToken(result.operationToken);
      if (mounted) Navigator.pop(context, result.operationToken);
    } on DioException catch (e) {
      if (mounted) {
        setState(() {
          _isVerifying = false;
          _error = (e.response?.data is Map)
              ? (e.response!.data as Map)['message'] as String? ?? 'Passkey verification failed'
              : 'Passkey verification failed';
        });
      }
    } catch (e) {
      if (mounted) {
        setState(() {
          _isVerifying = false;
          _error = e.toString().contains('NotAllowed')
              ? 'Passkey verification was cancelled'
              : 'Passkey verification failed';
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final opLabel = _operationLabels[widget.operation] ?? widget.operation;

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
            child: _isLoading
                ? const Padding(
                    padding: EdgeInsets.all(32),
                    child: Center(
                        child: CircularProgressIndicator(color: _accent)),
                  )
                : Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(
                        width: 56,
                        height: 56,
                        decoration: BoxDecoration(
                          shape: BoxShape.circle,
                          color: _accent.withValues(alpha: 0.1),
                          border:
                              Border.all(color: _accent.withValues(alpha: 0.3)),
                        ),
                        child: const Icon(Icons.shield_outlined,
                            size: 28, color: _accent),
                      ),
                      const SizedBox(height: 16),
                      Text(AppLocalizations.of(context).uiAdditionalVerification,
                          style: GoogleFonts.sora(
                              fontSize: 18,
                              fontWeight: FontWeight.w700,
                              color: _textGhost)),
                      const SizedBox(height: 8),
                      Text(
                        _selectedMethod == 'passkey'
                            ? AppLocalizations.of(context).uiUsePasskeyFor(opLabel)
                            : AppLocalizations.of(context).uiEnterCodeFor(opLabel),
                        textAlign: TextAlign.center,
                        style:
                            GoogleFonts.sora(fontSize: 13, color: _textMuted),
                      ),
                      const SizedBox(height: 20),

                      // Method selector
                      if (_availableMethods.length > 1) ...[
                        Wrap(
                          spacing: 8,
                          runSpacing: 8,
                          children: [
                            if (_availableMethods.contains('passkey'))
                              _MethodChip(
                                label: 'Passkey',
                                icon: Icons.fingerprint,
                                selected: _selectedMethod == 'passkey',
                                onTap: () => setState(() {
                                  _selectedMethod = 'passkey';
                                  _codeController.clear();
                                  _error = null;
                                }),
                              ),
                            if (_availableMethods.contains('totp'))
                              _MethodChip(
                                label: 'Authenticator',
                                icon: Icons.phone_android,
                                selected: _selectedMethod == 'totp',
                                onTap: () => setState(() {
                                  _selectedMethod = 'totp';
                                  _emailSent = false;
                                  _codeController.clear();
                                  _error = null;
                                }),
                              ),
                            if (_availableMethods.contains('email'))
                              _MethodChip(
                                label: 'Email',
                                icon: Icons.mail_outline,
                                selected: _selectedMethod == 'email',
                                onTap: () => setState(() {
                                  _selectedMethod = 'email';
                                  _codeController.clear();
                                  _error = null;
                                }),
                              ),
                          ],
                        ),
                        const SizedBox(height: 16),
                      ],

                      // Passkey: single verify button
                      if (_selectedMethod == 'passkey') ...[
                        SizedBox(
                          width: double.infinity,
                          child: ElevatedButton.icon(
                            onPressed: _isVerifying ? null : _verifyPasskey,
                            icon: _isVerifying
                                ? const SizedBox(
                                    width: 20,
                                    height: 20,
                                    child: CircularProgressIndicator(
                                        strokeWidth: 2, color: _textGhost))
                                : const Icon(Icons.fingerprint, size: 20),
                            label: Text(
                                _isVerifying
                                    ? AppLocalizations.of(context).uiVerifyingEllipsis
                                    : AppLocalizations.of(context).uiVerifyWithPasskey,
                                style: GoogleFonts.sora(
                                    fontSize: 14,
                                    fontWeight: FontWeight.w600)),
                            style: ElevatedButton.styleFrom(
                              backgroundColor: _accent,
                              foregroundColor: _textGhost,
                              disabledBackgroundColor:
                                  _accent.withValues(alpha: 0.3),
                              padding:
                                  const EdgeInsets.symmetric(vertical: 14),
                              shape: RoundedRectangleBorder(
                                  borderRadius: BorderRadius.circular(12)),
                            ),
                          ),
                        ),
                      ],

                      // Email: send code button first
                      if (_selectedMethod == 'email' && !_emailSent) ...[
                        SizedBox(
                          width: double.infinity,
                          child: ElevatedButton(
                            onPressed: _sendEmailCode,
                            style: ElevatedButton.styleFrom(
                              backgroundColor: _accent,
                              foregroundColor: _textGhost,
                              padding:
                                  const EdgeInsets.symmetric(vertical: 14),
                              shape: RoundedRectangleBorder(
                                  borderRadius: BorderRadius.circular(12)),
                            ),
                            child: Text(AppLocalizations.of(context).uiSendVerificationCode,
                                style: GoogleFonts.sora(
                                    fontSize: 14,
                                    fontWeight: FontWeight.w600)),
                          ),
                        ),
                      ],

                      // Code input (TOTP always, email after sent)
                      if (_selectedMethod == 'totp' ||
                          (_selectedMethod == 'email' && _emailSent)) ...[
                        TextField(
                          controller: _codeController,
                          textAlign: TextAlign.center,
                          keyboardType: TextInputType.number,
                          maxLength: 6,
                          style: GoogleFonts.firaCode(
                              fontSize: 24,
                              letterSpacing: 8,
                              color: _textGhost),
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
                              borderSide: BorderSide(
                                  color: _accent.withValues(alpha: 0.5)),
                            ),
                            filled: true,
                            fillColor: Colors.white.withValues(alpha: 0.03),
                          ),
                          onSubmitted: (_) => _verifyCode(),
                        ),
                        const SizedBox(height: 16),

                        SizedBox(
                          width: double.infinity,
                          child: ElevatedButton(
                            onPressed: _isVerifying ? null : _verifyCode,
                            style: ElevatedButton.styleFrom(
                              backgroundColor: _accent,
                              foregroundColor: _textGhost,
                              disabledBackgroundColor:
                                  _accent.withValues(alpha: 0.3),
                              padding:
                                  const EdgeInsets.symmetric(vertical: 14),
                              shape: RoundedRectangleBorder(
                                  borderRadius: BorderRadius.circular(12)),
                            ),
                            child: _isVerifying
                                ? const SizedBox(
                                    width: 20,
                                    height: 20,
                                    child: CircularProgressIndicator(
                                        strokeWidth: 2, color: _textGhost))
                                : Text(AppLocalizations.of(context).verify,
                                    style: GoogleFonts.sora(
                                        fontSize: 14,
                                        fontWeight: FontWeight.w600)),
                          ),
                        ),

                        if (_selectedMethod == 'email') ...[
                          const SizedBox(height: 8),
                          TextButton(
                            onPressed: _sendEmailCode,
                            child: Text(AppLocalizations.of(context).resendCode,
                                style: GoogleFonts.sora(
                                    fontSize: 12,
                                    color:
                                        _accent.withValues(alpha: 0.7))),
                          ),
                        ],
                      ],

                      // Error
                      if (_error != null) ...[
                        const SizedBox(height: 12),
                        Text(_error!,
                            style: GoogleFonts.sora(
                                fontSize: 12, color: _expense)),
                      ],

                      const SizedBox(height: 16),

                      // Cancel
                      TextButton(
                        onPressed: () => Navigator.pop(context, null),
                        child: Text(AppLocalizations.of(context).uiCancel,
                            style: GoogleFonts.sora(
                                fontSize: 13,
                                color: _textMuted.withValues(alpha: 0.7))),
                      ),
                    ],
                  ),
          ),
        ),
      ),
    );
  }
}

class _MethodChip extends StatelessWidget {
  final String label;
  final IconData icon;
  final bool selected;
  final VoidCallback onTap;

  const _MethodChip({
    required this.label,
    required this.icon,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 200),
          padding: const EdgeInsets.symmetric(vertical: 10, horizontal: 16),
          decoration: BoxDecoration(
            color: selected
                ? _accent.withValues(alpha: 0.15)
                : Colors.white.withValues(alpha: 0.03),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: selected
                  ? _accent.withValues(alpha: 0.4)
                  : _cardBorder,
            ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon,
                  size: 16,
                  color: selected
                      ? _accent
                      : _textMuted.withValues(alpha: 0.6)),
              const SizedBox(width: 6),
              Text(label,
                  style: GoogleFonts.sora(
                      fontSize: 12,
                      fontWeight: FontWeight.w600,
                      color: selected ? _accent : _textMuted)),
            ],
          ),
        ),
      ),
    );
  }
}
