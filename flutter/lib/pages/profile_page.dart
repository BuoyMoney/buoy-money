import '../config/app_brand.dart';
import '../config/server_capabilities.dart';
import 'dart:convert';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart' show kIsWeb, defaultTargetPlatform, TargetPlatform;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:intl/intl.dart';
import 'package:crop_your_image/crop_your_image.dart';
import 'package:image/image.dart' as img;
import 'package:image_picker/image_picker.dart';
import 'package:provider/provider.dart';
import '../models/family_models.dart';
import '../providers/auth_provider.dart';
import '../services/api_client.dart';
import '../services/family_service.dart';
import '../services/billing_service.dart';
import 'package:share_plus/share_plus.dart';
import '../services/referral_service.dart';
import '../models/billing_models.dart';
import '../services/user_service.dart';
import '../services/device_service.dart';
import '../services/mfa_service.dart';
import '../services/passkey_service.dart';
import '../l10n/generated/app_localizations.dart';
import '../utils/currency.dart';
import '../utils/formatters.dart';
import '../utils/web_navigation.dart';
import '../widgets/app_toast.dart';
import '../widgets/verify_email_dialog.dart';
import '../widgets/step_up_mfa_dialog.dart';

// ── VaporClinic palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _danger = Color(0xFFFF4D6D);
const _sheetBg = Color(0xFF0C0C18);
const _warning = Color(0xFFFBBF24);

// ═══════════════════════════════════════════════════════════
//  ProfilePage
// ═══════════════════════════════════════════════════════════
class ProfilePage extends StatefulWidget {
  const ProfilePage({super.key});

  @override
  State<ProfilePage> createState() => _ProfilePageState();
}

class _ProfilePageState extends State<ProfilePage>
    with SingleTickerProviderStateMixin {
  UserProfile? _profile;
  List<TrustedDevice> _devices = [];
  bool _isLoading = true;
  String? _error;

  // Edit profile
  bool _isEditing = false;
  bool _isSaving = false;
  bool _avatarBusy = false;
  final _firstNameCtrl = TextEditingController();
  final _lastNameCtrl = TextEditingController();
  // Primary display currency being edited (ISO code from supportedCurrencies).
  String _editCurrency = 'USD';
  // Week start being edited (0=Sunday..6=Saturday; null = legacy Sunday).
  int? _editWeekStart;

  // MFA / TOTP
  bool _mfaLoading = false;
  bool _mfaLoadError = false;
  MfaStatus? _mfaStatus;
  int _passkeyCount = 0;

  /// Discount currently on the subscription, or null. Loaded in the background: it is a
  /// badge, so it must never delay or block the profile rendering.
  SubscriptionDiscount? _activeDiscount;

  /// Referral code + counts. Loaded in the background; the card renders a placeholder
  /// until it arrives rather than blocking the page.
  ReferralSummary? _referral;
  Map<String, dynamic>? _totpSetup; // { qrCodeUrl, secret, manualEntryCode }
  final _totpCodeCtrl = TextEditingController();
  List<String>? _recoveryCodes;
  bool _secretCopied = false;

  // Family
  Family? _family;
  List<FamilyMember> _familyMembers = [];
  List<FamilyInvitation> _familyInvitations = [];
  List<FamilyInvitation> _myPendingInvitations = [];
  bool _familyLoading = false;

  // Delete account
  bool _isDeletingAccount = false;

  // Entrance animation
  late AnimationController _fadeController;
  late Animation<double> _fadeAnim;

  @override
  void initState() {
    super.initState();
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnim = CurvedAnimation(parent: _fadeController, curve: Curves.easeOut);
    _loadData();
  }

  @override
  void dispose() {
    _fadeController.dispose();
    _firstNameCtrl.dispose();
    _lastNameCtrl.dispose();
    _totpCodeCtrl.dispose();
    super.dispose();
  }

  // ── Data loading ──────────────────────────────────────

  Future<void> _loadData() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        userService.getProfile(),
        deviceService.getTrustedDevices(),
      ]);
      if (!mounted) return;
      setState(() {
        _profile = results[0] as UserProfile;
        _devices = results[1] as List<TrustedDevice>;
        _firstNameCtrl.text = _profile?.firstName ?? '';
        _lastNameCtrl.text = _profile?.lastName ?? '';
        _editCurrency = _profile?.currency?.toUpperCase() ?? 'USD';
        _editWeekStart = _profile?.weekStartDay;
        _isLoading = false;
      });
      _fadeController.forward();
      // Load family + MFA status in background (non-blocking)
      _loadFamilyData();
      _loadMfaStatus();
      // Discounts and referrals belong to the hosted plans; a self-hosted server has neither.
      if (ServerEdition.current.value.hasBilling) {
        _loadActiveDiscount();
        _loadReferralSummary();
      }
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _isLoading = false;
        _error = 'Failed to load profile';
      });
      _fadeController.forward();
    }
  }

  /// Fetches any active discount. Failures are swallowed by the service layer and surface
  /// as null, so the card simply shows no badge.
  Future<void> _loadActiveDiscount() async {
    final discount = await billingService.getActiveDiscount();
    if (!mounted) return;
    setState(() => _activeDiscount = discount);
  }

  Future<void> _loadReferralSummary() async {
    try {
      final summary = await referralService.getSummary();
      if (!mounted) return;
      setState(() => _referral = summary);
    } catch (_) {
      // Non-critical: the card just stays hidden.
    }
  }

  /// Prompts for a promo code and applies it to the existing subscription.
  ///
  /// A code redeemed here changes real billing, so the result is reported explicitly rather
  /// than silently refreshing: "nothing happened" is indistinguishable from "it worked" on a
  /// card that already showed no discount.
  Future<void> _openRedeemCodeSheet() async {
    final controller = TextEditingController();
    var submitting = false;
    // Set inside the sheet, acted on after it closes. Toasting from in there would mean
    // using this State's context guarded by the sheet's lifecycle, which is the wrong check.
    var failed = false;

    final applied = await showModalBottomSheet<SubscriptionDiscount?>(
      context: context,
      isScrollControlled: true,
      backgroundColor: const Color(0xFF12121C),
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (sheetContext) => StatefulBuilder(
        builder: (sheetContext, setSheetState) => Padding(
          padding: EdgeInsets.only(
            left: 20,
            right: 20,
            top: 20,
            // Clear the keyboard, or the field sits under it on a short screen.
            bottom: MediaQuery.of(sheetContext).viewInsets.bottom + 20,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(AppLocalizations.of(sheetContext).profRedeemCodeTitle,
                  style: GoogleFonts.sora(
                      fontSize: 17, color: _textGhost, fontWeight: FontWeight.w600)),
              const SizedBox(height: 6),
              Text(
                AppLocalizations.of(sheetContext).profRedeemCodeHint,
                style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
              ),
              const SizedBox(height: 16),
              TextField(
                controller: controller,
                autofocus: true,
                enabled: !submitting,
                textCapitalization: TextCapitalization.characters,
                textInputAction: TextInputAction.done,
                style: GoogleFonts.firaCode(fontSize: 15, color: _textGhost),
                decoration: InputDecoration(
                  hintText: 'PROMO CODE',
                  hintStyle: GoogleFonts.firaCode(fontSize: 15, color: _textMuted),
                  filled: true,
                  fillColor: Colors.white.withValues(alpha: 0.04),
                  border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(10),
                    borderSide: BorderSide.none,
                  ),
                ),
              ),
              const SizedBox(height: 16),
              SizedBox(
                width: double.infinity,
                child: TextButton(
                  onPressed: submitting
                      ? null
                      : () async {
                          final code = controller.text.trim();
                          if (code.isEmpty) return;
                          setSheetState(() => submitting = true);
                          try {
                            final result = await billingService.redeemPromoCode(code);
                            if (sheetContext.mounted) {
                              Navigator.of(sheetContext).pop(result);
                            }
                          } catch (_) {
                            // Distinct from an invalid code: something on our side broke, and
                            // telling the user their code is bad would be wrong.
                            failed = true;
                            if (sheetContext.mounted) {
                              Navigator.of(sheetContext).pop();
                            }
                          }
                        },
                  style: TextButton.styleFrom(
                    backgroundColor: _accent.withValues(alpha: 0.18),
                    padding: const EdgeInsets.symmetric(vertical: 14),
                    shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(10)),
                  ),
                  child: submitting
                      ? const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(
                              strokeWidth: 2, color: _accent))
                      : Text(AppLocalizations.of(sheetContext).profApply,
                          style: GoogleFonts.sora(
                              fontSize: 14,
                              color: _accent,
                              fontWeight: FontWeight.w600)),
                ),
              ),
            ],
          ),
        ),
      ),
    );

    // Read before dispose — the null branch below needs to know whether anything was typed.
    final submittedText = controller.text.trim();
    controller.dispose();
    if (!mounted) return;

    if (failed) {
      showAppToast(
        context,
        "Couldn't check that code right now. Try again shortly.",
        type: AppToastType.error,
      );
      return;
    }

    if (applied == null) {
      // Null now means either dismissed or rejected. Only the rejection deserves a message,
      // and we cannot tell them apart here — the API returns a flat 404 for every failure so
      // the form cannot be used to discover which codes exist. Saying nothing on dismiss is
      // the safer default, so this reports only when a code was actually submitted.
      if (submittedText.isNotEmpty) {
        showAppToast(
          context,
          "That code isn't valid for your account.",
          type: AppToastType.error,
        );
      }
      return;
    }

    setState(() => _activeDiscount = applied);
    showAppToast(
      context,
      'Applied: ${applied.description}',
      type: AppToastType.success,
    );
  }

  Future<void> _loadFamilyData() async {
    setState(() => _familyLoading = true);
    try {
      final family = await familyService.getFamily();
      if (!mounted) return;
      if (family != null) {
        final results = await Future.wait([
          familyService.getFamilyMembers(),
          familyService.getInvitations(),
        ]);
        if (!mounted) return;
        setState(() {
          _family = family;
          _familyMembers = results[0] as List<FamilyMember>;
          _familyInvitations = results[1] as List<FamilyInvitation>;
          _familyLoading = false;
        });
      } else {
        // No family — check for inbound invitations
        final myInvitations = await familyService.getMyPendingInvitations();
        if (!mounted) return;
        setState(() {
          _family = null;
          _familyMembers = [];
          _familyInvitations = [];
          _myPendingInvitations = myInvitations;
          _familyLoading = false;
        });
      }
    } catch (_) {
      if (!mounted) return;
      setState(() => _familyLoading = false);
    }
  }

  Future<void> _loadMfaStatus() async {
    try {
      final results = await Future.wait([
        mfaService.getStatus(),
        passkeyService.getUserPasskeys(),
      ]);
      if (!mounted) return;
      setState(() {
        _mfaStatus = results[0] as MfaStatus;
        _passkeyCount = (results[1] as List).length;
        _mfaLoadError = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _mfaLoadError = true);
    }
  }

  // ── Edit profile ──────────────────────────────────────

  Future<void> _saveProfile() async {
    if (_isSaving) return;
    setState(() => _isSaving = true);
    try {
      final updated = await userService.updateProfile(UpdateProfileRequest(
        firstName: _firstNameCtrl.text.trim(),
        lastName: _lastNameCtrl.text.trim(),
        currency: _editCurrency,
        weekStartDay: _editWeekStart,
      ));
      if (!mounted) return;
      setState(() {
        _profile = updated;
        _isEditing = false;
        _isSaving = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _isSaving = false);
      showAppToast(context, 'Failed to update profile', type: AppToastType.error);
    }
  }

  // ── MFA method selection ──────────────────────────────

  void _showMfaMethodSelector() {
    final methods = _mfaStatus?.enabledMethods
        .map((m) => m.toLowerCase())
        .toList() ?? [];
    final hasTotp = methods.contains('totp');
    // A verified email means email-code MFA is ready to use (the verification step
    // confirmed delivery works), so show it as configured even if it wasn't separately
    // "enabled". Users can still add stronger methods (authenticator, passkey).
    final isEmailVerified = context.read<AuthProvider>().user?.isEmailVerified ?? false;
    final hasEmail = methods.contains('email') || isEmailVerified;
    final hasPasskey = _passkeyCount > 0;

    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => Container(
        padding: const EdgeInsets.fromLTRB(24, 20, 24, 40),
        decoration: const BoxDecoration(
          color: Color(0xFF0C0C18),
          borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Center(
              child: Container(
                width: 40, height: 4,
                decoration: BoxDecoration(
                  color: Colors.white24,
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 20),
            Text(hasTotp || hasEmail || hasPasskey
                ? AppLocalizations.of(context).profAddAnotherMethod
                : AppLocalizations.of(context).profChooseMfaMethod,
              style: GoogleFonts.sora(fontSize: 18, fontWeight: FontWeight.w700, color: _textGhost)),
            const SizedBox(height: 8),
            Text(AppLocalizations.of(context).profMfaRecommendation,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.5)),
            const SizedBox(height: 20),
            _MfaMethodOption(
              icon: Icons.qr_code_rounded,
              title: hasTotp ? 'Authenticator App (Active)' : 'Authenticator App (TOTP)',
              subtitle: hasTotp ? 'Already configured' : 'Use Google Authenticator, Authy, or similar',
              recommended: !hasTotp,
              onTap: hasTotp ? () => Navigator.pop(context) : () { Navigator.pop(context); _startTotpSetup(); },
            ),
            const SizedBox(height: 10),
            _MfaMethodOption(
              icon: Icons.fingerprint_rounded,
              title: hasPasskey ? 'Passkey ($_passkeyCount active)' : 'Passkey (WebAuthn)',
              subtitle: hasPasskey ? 'Add another passkey' : 'Face ID, Touch ID, or Windows Hello',
              recommended: !hasPasskey,
              onTap: () { Navigator.pop(context); _startPasskeySetup(); },
            ),
            const SizedBox(height: 10),
            _MfaMethodOption(
              icon: Icons.email_outlined,
              title: hasEmail ? 'Email Code (Active)' : 'Email Code',
              subtitle: hasEmail
                  ? (isEmailVerified && !methods.contains('email')
                      ? 'Your email is verified — codes can be sent here'
                      : 'Already configured')
                  : 'Receive a verification code via email',
              onTap: hasEmail ? () => Navigator.pop(context) : () { Navigator.pop(context); _setUpEmailMfa(); },
            ),
          ],
        ),
      ),
    );
  }

  // ── Email MFA ────────────────────────────────────────

  // Email-code MFA is the same thing as email verification: we send a code and the user
  // enters it. Reuse the exact verify-email dialog so this flow and the dashboard banner
  // are identical, and completing either satisfies both (and clears the banner).
  Future<void> _setUpEmailMfa() async {
    final ok = await showVerifyEmailDialog(context);
    if (ok == true && mounted) {
      showAppToast(context, 'Email verified — email code MFA is now active',
          type: AppToastType.success);
      _loadData();
      _loadMfaStatus();
    }
  }

  // ── Passkey setup ────────────────────────────────────

  Future<void> _startPasskeySetup() async {
    final supported = await passkeyService.isSupported();
    if (!supported) {
      if (mounted) {
        showAppToast(context, 'Passkeys are not supported on this device/browser',
            type: AppToastType.error);
      }
      return;
    }
    setState(() => _mfaLoading = true);
    try {
      final deviceName = await passkeyService.registerPasskey(
        displayName: _profile?.firstName ?? '${AppBrand.name} User',
      );
      if (!mounted) return;
      setState(() => _mfaLoading = false);
      showAppToast(context, 'Passkey registered on $deviceName',
          type: AppToastType.success);
      _loadData();
      _loadMfaStatus();
    } catch (e) {
      if (!mounted) return;
      setState(() => _mfaLoading = false);
      final msg = e.toString();
      showAppToast(context, 'Passkey setup failed${msg.length > 50 ? '' : ': $msg'}',
          type: AppToastType.error);
    }
  }

  // ── TOTP setup ────────────────────────────────────────

  Future<void> _startTotpSetup() async {
    setState(() => _mfaLoading = true);
    try {
      final response = await apiClient.dio.post<Map<String, dynamic>>(
        '/twofactor/setup-totp',
      );
      if (!mounted) return;
      setState(() {
        _totpSetup = response.data;
        _recoveryCodes = null;
        _secretCopied = false;
        _mfaLoading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _mfaLoading = false);
      showAppToast(context, 'Failed to start TOTP setup', type: AppToastType.error);
    }
  }

  Future<void> _verifyTotp() async {
    final code = _totpCodeCtrl.text.trim();
    if (code.length < 6) return;
    setState(() => _mfaLoading = true);
    try {
      final response = await apiClient.dio.post<Map<String, dynamic>>(
        '/twofactor/verify-totp',
        data: {'code': code},
      );
      if (!mounted) return;
      final data = response.data ?? {};
      final codes =
          ((data['recoveryCodes'] ?? data['RecoveryCodes']) as List?)
              ?.cast<String>();
      _totpCodeCtrl.clear();
      setState(() {
        _totpSetup = null;
        _recoveryCodes = codes;
        _mfaLoading = false;
      });
      showAppToast(context, 'MFA enabled successfully', type: AppToastType.success);
      _loadData();
      _loadMfaStatus();
    } catch (_) {
      if (!mounted) return;
      setState(() => _mfaLoading = false);
      showAppToast(context, 'Invalid code. Please try again.', type: AppToastType.error);
    }
  }

  Future<void> _disableMfa() async {
    final confirmed = await _showConfirmSheet(
      'Disable MFA',
      'This will remove two-factor authentication from your account. '
      'Are you sure?',
    );
    if (confirmed != true) return;
    await _performDisableMfa();
  }

  /// Calls the disable endpoint, which is gated by step-up MFA. On a first attempt the
  /// backend answers `MfaRequired` (or `EmailVerificationRequired`); we run the matching
  /// dialog to satisfy the gate, then retry once. The operation token from the step-up
  /// dialog is auto-attached by the api client, so the retry passes.
  Future<void> _performDisableMfa({bool afterStepUp = false}) async {
    setState(() => _mfaLoading = true);
    try {
      await apiClient.dio.delete('/twofactor/disable');
      if (!mounted) return;
      setState(() => _mfaLoading = false);
      showAppToast(context, 'MFA disabled', type: AppToastType.success);
      _loadData();
      _loadMfaStatus();
    } on DioException catch (e) {
      if (!mounted) return;
      setState(() => _mfaLoading = false);

      final data = e.response?.data;
      final error = (e.response?.statusCode == 403 && data is Map)
          ? data['error'] as String?
          : null;

      if (error == 'MfaRequired' && !afterStepUp) {
        final token =
            await showStepUpMfaDialog(context, operation: 'ChangeMfaSettings');
        if (token != null && mounted) {
          await _performDisableMfa(afterStepUp: true);
        }
        return;
      }

      if (error == 'EmailVerificationRequired') {
        final verified = await showVerifyEmailDialog(context);
        if (verified == true && mounted) {
          await _performDisableMfa(afterStepUp: afterStepUp);
        }
        return;
      }

      showAppToast(context, 'Failed to disable MFA', type: AppToastType.error);
    } catch (_) {
      if (!mounted) return;
      setState(() => _mfaLoading = false);
      showAppToast(context, 'Failed to disable MFA', type: AppToastType.error);
    }
  }

  void _copyToClipboard(String text, String label) {
    Clipboard.setData(ClipboardData(text: text));
    if (mounted) {
      showAppToast(context, '$label copied to clipboard', type: AppToastType.success);
    }
  }

  // ── Trusted devices ───────────────────────────────────

  Future<void> _revokeDevice(TrustedDevice device) async {
    final confirmed = await _showConfirmSheet(
      'Revoke Device',
      'Remove "${device.displayName}" from trusted devices?',
    );
    if (confirmed != true) return;
    try {
      await deviceService.revokeDevice(device.id);
      if (!mounted) return;
      setState(() => _devices.removeWhere((d) => d.id == device.id));
      showAppToast(context, 'Device revoked', type: AppToastType.success);
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to revoke device', type: AppToastType.error);
      }
    }
  }

  Future<void> _revokeAllDevices() async {
    final confirmed = await _showConfirmSheet(
      'Revoke All Devices',
      'This will sign you out of all other devices. Continue?',
    );
    if (confirmed != true) return;
    try {
      await deviceService.revokeAllDevices();
      if (!mounted) return;
      _loadData();
      showAppToast(context, 'All devices revoked', type: AppToastType.success);
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to revoke devices', type: AppToastType.error);
      }
    }
  }

  // ── Helpers ───────────────────────────────────────────

  Future<bool?> _showConfirmSheet(String title, String message) {
    return showModalBottomSheet<bool>(
      context: context,
      backgroundColor: _sheetBg,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) {
        return Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Center(
                child: Container(
                  width: 40,
                  height: 4,
                  decoration: BoxDecoration(
                    color: _textMuted.withValues(alpha: 0.3),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
              ),
              const SizedBox(height: 20),
              Text(title,
                  style: GoogleFonts.sora(
                    fontSize: 18,
                    fontWeight: FontWeight.w700,
                    color: _textGhost,
                  )),
              const SizedBox(height: 12),
              Text(message,
                  style: GoogleFonts.sora(
                    fontSize: 14,
                    color: _textMuted,
                    height: 1.5,
                  )),
              const SizedBox(height: 24),
              Row(
                children: [
                  Expanded(
                    child: _GlassAction(
                      label: 'Cancel',
                      onTap: () => Navigator.pop(ctx, false),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: _SolidAction(
                      label: 'Confirm',
                      color: _expense,
                      onTap: () => Navigator.pop(ctx, true),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
            ],
          ),
        );
      },
    );
  }

  // ── Build ─────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: FadeTransition(
        opacity: _fadeAnim,
        child: _isLoading
            ? const Center(child: CircularProgressIndicator(color: _accent))
            : _error != null
                ? _buildError()
                : _buildContent(),
      ),
    );
  }

  Widget _buildError() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.error_outline, size: 56,
              color: _expense.withValues(alpha: 0.7)),
          const SizedBox(height: 16),
          Text(_error!, style: GoogleFonts.sora(fontSize: 14, color: _textMuted)),
          const SizedBox(height: 20),
          _GlassAction(label: 'Retry', onTap: _loadData),
        ],
      ),
    );
  }

  Widget _buildContent() {
    final user = context.watch<AuthProvider>().user;

    return RefreshIndicator(
      color: _accent,
      backgroundColor: _sheetBg,
      onRefresh: _loadData,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(24, 24, 24, 120),
        children: [
          _buildProfileHeader(user),
          const SizedBox(height: 24),
          _buildAccountInfo(),
          const SizedBox(height: 16),
          if (ServerEdition.current.value.hasBilling) ...[
            _buildSubscriptionCard(),
            const SizedBox(height: 16),
          ],
          // App Review 3.1.1: a referral month is paid functionality unlocked
          // outside In-App Purchase, so the programme is not offered on iOS.
          if (!_isIos && ServerEdition.current.value.hasBilling) ...[
            _buildReferralCard(),
            const SizedBox(height: 16),
          ],
          _buildSecurityCard(),
          const SizedBox(height: 16),
          if (_totpSetup != null) ...[
            _buildTotpSetupCard(),
            const SizedBox(height: 16),
          ],
          if (_recoveryCodes != null && _recoveryCodes!.isNotEmpty) ...[
            _buildRecoveryCodesCard(),
            const SizedBox(height: 16),
          ],
          _buildTrustedDevicesCard(),
          const SizedBox(height: 16),
          _buildFamilyCard(),
          const SizedBox(height: 16),
          _buildDangerZoneCard(),
        ],
      ),
    );
  }

  // ── Profile header ────────────────────────────────────

  Widget _buildProfileHeader(dynamic user) {
    final p = _profile;
    if (p == null) return const SizedBox.shrink();

    return Container(
          padding: const EdgeInsets.all(24),
          decoration: BoxDecoration(
            gradient: LinearGradient(
              colors: [
                _accent.withValues(alpha: 0.08),
                _cardBg,
              ],
              begin: Alignment.topLeft,
              end: Alignment.bottomRight,
            ),
            borderRadius: BorderRadius.circular(16),
            border: Border.all(color: _accent.withValues(alpha: 0.15)),
          ),
          child: Row(
            children: [
              // Avatar
              _buildAvatar(p),
              const SizedBox(width: 18),
              // Info
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      p.displayName,
                      style: GoogleFonts.instrumentSerif(
                        fontSize: 24,
                        fontWeight: FontWeight.w400,
                        color: _textGhost,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      p.email,
                      style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        _SubscriptionBadge(tier: p.subscriptionTier),
                        if (p.isGoogleUser) ...[
                          const SizedBox(width: 8),
                          _SmallBadge(label: 'Google', color: const Color(0xFF4285F4)),
                        ],
                      ],
                    ),
                    const SizedBox(height: 6),
                    Text(
                      AppLocalizations.of(context)
                          .profMemberSince(formatDate(p.createdAt)),
                      style: GoogleFonts.sora(
                        fontSize: 11,
                        color: _textMuted.withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
    );
  }

  Widget _buildAvatar(UserProfile p) {
    final hasAvatar = p.effectiveAvatar != null;
    return GestureDetector(
      onTap: _avatarBusy ? null : _showAvatarOptions,
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          Container(
            width: 72,
            height: 72,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              gradient: hasAvatar
                  ? null
                  : LinearGradient(
                      colors: [_accent, _accent.withValues(alpha: 0.6)],
                      begin: Alignment.topLeft,
                      end: Alignment.bottomRight,
                    ),
              border: Border.all(color: _accent.withValues(alpha: 0.4), width: 2),
            ),
            child: _avatarBusy
                ? const Center(
                    child: SizedBox(
                      width: 22,
                      height: 22,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                  )
                : hasAvatar
                    ? ClipOval(
                        child: Image.network(
                          p.effectiveAvatar!,
                          fit: BoxFit.cover,
                          errorBuilder: (_, __, ___) => _initialsWidget(p),
                        ),
                      )
                    : _initialsWidget(p),
          ),
          // Edit badge (industry-standard camera button on the avatar).
          Positioned(
            right: -2,
            bottom: -2,
            child: Container(
              padding: const EdgeInsets.all(6),
              decoration: BoxDecoration(
                color: _accent,
                shape: BoxShape.circle,
                border: Border.all(color: _cardBg, width: 2),
              ),
              child: const Icon(Icons.edit, size: 13, color: Colors.white),
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _showAvatarOptions() async {
    final hasAvatar = _profile?.effectiveAvatar != null;
    final action = await showModalBottomSheet<String>(
      context: context,
      backgroundColor: _sheetBg,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const SizedBox(height: 8),
            ListTile(
              leading: Icon(Icons.photo_library_outlined, color: _accent),
              title: Text(AppLocalizations.of(ctx).profChooseAPhoto,
                  style: GoogleFonts.sora(color: _textGhost)),
              onTap: () => Navigator.pop(ctx, 'pick'),
            ),
            if (hasAvatar)
              ListTile(
                leading: const Icon(Icons.delete_outline, color: Colors.redAccent),
                title: Text(AppLocalizations.of(ctx).profRemovePhoto,
                    style: GoogleFonts.sora(color: _textGhost)),
                onTap: () => Navigator.pop(ctx, 'remove'),
              ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
    if (action == 'pick') {
      await _uploadAvatarFromPicker();
    } else if (action == 'remove') {
      await _updateAvatar(() => userService.deleteAvatar(), 'Could not remove photo.');
    }
  }

  Future<void> _uploadAvatarFromPicker() async {
    final XFile? picked;
    try {
      picked = await ImagePicker().pickImage(
        source: ImageSource.gallery,
        maxWidth: 1024,
        maxHeight: 1024,
      );
    } catch (_) {
      if (mounted) _avatarError('Could not open the photo picker.');
      return;
    }
    if (picked == null) return;
    final raw = await picked.readAsBytes();
    if (!mounted) return;

    // Let the user pan/zoom to frame the crop before we upload.
    final cropped = await _showCropDialog(raw);
    if (cropped == null || !mounted) return; // cancelled

    setState(() => _avatarBusy = true);
    try {
      // Normalise the cropped (already-square) region to a small JPEG so the
      // upload stays tiny (~20-30KB) regardless of the source photo.
      final decoded = img.decodeImage(cropped);
      if (decoded == null) {
        if (mounted) {
          setState(() => _avatarBusy = false);
          _avatarError('That image could not be read. Try another.');
        }
        return;
      }
      final square = img.copyResizeCropSquare(decoded, size: 256);
      final jpg = img.encodeJpg(square, quality: 82);
      final updated = await userService.uploadAvatar(jpg, 'avatar.jpg', 'image/jpeg');
      if (!mounted) return;
      setState(() {
        _profile = updated;
        _avatarBusy = false;
      });
    } catch (_) {
      if (mounted) {
        setState(() => _avatarBusy = false);
        _avatarError('Could not update photo. Please try again.');
      }
    }
  }

  Future<void> _updateAvatar(
      Future<UserProfile> Function() action, String errorMsg) async {
    setState(() => _avatarBusy = true);
    try {
      final updated = await action();
      if (!mounted) return;
      setState(() {
        _profile = updated;
        _avatarBusy = false;
      });
    } catch (_) {
      if (mounted) {
        setState(() => _avatarBusy = false);
        _avatarError(errorMsg);
      }
    }
  }

  void _avatarError(String msg) {
    showAppToast(context, msg, type: AppToastType.error);
  }

  /// Pan/zoom crop UI (circular framing for the avatar). Returns the cropped
  /// square bytes, or null if cancelled.
  Future<Uint8List?> _showCropDialog(Uint8List bytes) {
    final controller = CropController();
    return showDialog<Uint8List?>(
      context: context,
      barrierDismissible: false,
      builder: (dialogCtx) {
        var cropping = false;
        return StatefulBuilder(
          builder: (dialogCtx, setSt) => Dialog(
            backgroundColor: _sheetBg,
            insetPadding: const EdgeInsets.all(20),
            shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20)),
            child: SizedBox(
              width: 340,
              height: 470,
              child: Column(
                children: [
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 18, 20, 4),
                    child: Text(
                      AppLocalizations.of(dialogCtx).profPositionYourPhoto,
                      style: GoogleFonts.instrumentSerif(fontSize: 22, color: _textGhost),
                    ),
                  ),
                  Text(
                    AppLocalizations.of(dialogCtx).profCropInstructions,
                    style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                  ),
                  const SizedBox(height: 8),
                  Expanded(
                    child: Crop(
                      controller: controller,
                      image: bytes,
                      aspectRatio: 1,
                      withCircleUi: true,
                      baseColor: _sheetBg,
                      maskColor: Colors.black.withValues(alpha: 0.55),
                      onCropped: (result) {
                        Navigator.of(dialogCtx).pop(
                          result is CropSuccess ? result.croppedImage : null,
                        );
                      },
                    ),
                  ),
                  Padding(
                    padding: const EdgeInsets.all(16),
                    child: Row(
                      children: [
                        Expanded(
                          child: TextButton(
                            onPressed: cropping ? null : () => Navigator.of(dialogCtx).pop(null),
                            child: Text(AppLocalizations.of(dialogCtx).profCancel,
                                style: GoogleFonts.sora(color: _textMuted)),
                          ),
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: FilledButton(
                            style: FilledButton.styleFrom(backgroundColor: _accent),
                            onPressed: cropping
                                ? null
                                : () {
                                    setSt(() => cropping = true);
                                    controller.crop();
                                  },
                            child: cropping
                                ? const SizedBox(
                                    width: 18,
                                    height: 18,
                                    child: CircularProgressIndicator(
                                        strokeWidth: 2, color: Colors.white),
                                  )
                                : Text(AppLocalizations.of(dialogCtx).profUsePhoto,
                                    style: GoogleFonts.sora(
                                        fontWeight: FontWeight.w600, color: Colors.white)),
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        );
      },
    );
  }

  Widget _initialsWidget(UserProfile p) {
    return Center(
      child: Text(
        p.initials,
        style: GoogleFonts.sora(
          fontSize: 24,
          fontWeight: FontWeight.w700,
          color: _textGhost,
        ),
      ),
    );
  }

  // ── Account info ──────────────────────────────────────

  Widget _buildAccountInfo() {
    final p = _profile;
    if (p == null) return const SizedBox.shrink();

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CardHeader(
            icon: Icons.person_outline,
            title: 'Account Information',
            trailing: _isEditing
                ? Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      _SmallAction(
                        label: 'Cancel',
                        onTap: () => setState(() {
                          _isEditing = false;
                          _firstNameCtrl.text = p.firstName ?? '';
                          _lastNameCtrl.text = p.lastName ?? '';
                          _editCurrency = p.currency?.toUpperCase() ?? 'USD';
                          _editWeekStart = p.weekStartDay;
                        }),
                      ),
                      const SizedBox(width: 8),
                      _SmallAction(
                        label: _isSaving ? 'Saving...' : 'Save',
                        color: _income,
                        onTap: _isSaving ? null : _saveProfile,
                      ),
                    ],
                  )
                : _SmallAction(
                    label: 'Edit',
                    onTap: () => setState(() => _isEditing = true),
                  ),
          ),
          const SizedBox(height: 16),
          _InfoRow(label: 'Email', value: p.email),
          if (_isEditing) ...[
            const SizedBox(height: 12),
            _CinematicTextField(
              label: 'First Name',
              controller: _firstNameCtrl,
            ),
            const SizedBox(height: 12),
            _CinematicTextField(
              label: 'Last Name',
              controller: _lastNameCtrl,
            ),
            const SizedBox(height: 12),
            _buildCurrencyPicker(),
            const SizedBox(height: 12),
            _buildWeekStartPicker(),
          ] else ...[
            _InfoRow(label: 'First Name', value: p.firstName ?? '—'),
            _InfoRow(label: 'Last Name', value: p.lastName ?? '—'),
            _InfoRow(
                label: AppLocalizations.of(context).profileCurrencyLabel,
                value: p.currency?.toUpperCase() ?? 'USD'),
            _InfoRow(
                label: AppLocalizations.of(context).profileWeekStartLabel,
                value: _weekdayName(p.weekStartDay ?? 0)),
          ],
        ],
      ),
    );
  }

  /// Primary display currency selector. Every total in the app is formatted in
  /// this currency; picking one here is an explicit choice the first bank link
  /// will never override (backend CurrencyIsUserSet).
  Widget _buildCurrencyPicker() {
    return Row(
      children: [
        Text(AppLocalizations.of(context).profileCurrencyLabel,
            style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
        const SizedBox(width: 16),
        DropdownButtonHideUnderline(
          child: DropdownButton<String>(
            value: supportedCurrencies.contains(_editCurrency)
                ? _editCurrency
                : 'USD',
            isDense: true,
            dropdownColor: const Color(0xFF0C0C18),
            borderRadius: BorderRadius.circular(8),
            icon: Icon(Icons.arrow_drop_down, size: 18, color: _textMuted),
            style: GoogleFonts.sora(fontSize: 14, color: _textGhost),
            items: supportedCurrencies
                .map((code) => DropdownMenuItem(
                      value: code,
                      child: Text('$code (${currencySymbol(code)})'),
                    ))
                .toList(),
            onChanged: (code) {
              if (code != null) setState(() => _editCurrency = code);
            },
          ),
        ),
      ],
    );
  }

  /// Localized weekday name for day index 0=Sunday..6=Saturday.
  /// 2024-09-01 was a Sunday, so day N is simply that date + N days.
  String _weekdayName(int day) =>
      DateFormat.EEEE().format(DateTime(2024, 9, 1 + day));

  /// First-day-of-week picker (#941). Changing it re-bases the user's active
  /// weekly/bi-weekly budget periods server-side, effective immediately.
  Widget _buildWeekStartPicker() {
    return Row(
      children: [
        Text(AppLocalizations.of(context).profileWeekStartLabel,
            style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
        const SizedBox(width: 16),
        DropdownButtonHideUnderline(
          child: DropdownButton<int>(
            value: _editWeekStart ?? 0,
            isDense: true,
            dropdownColor: const Color(0xFF0C0C18),
            borderRadius: BorderRadius.circular(8),
            icon: Icon(Icons.arrow_drop_down, size: 18, color: _textMuted),
            style: GoogleFonts.sora(fontSize: 14, color: _textGhost),
            items: List.generate(
              7,
              (day) => DropdownMenuItem(
                value: day,
                child: Text(_weekdayName(day)),
              ),
            ),
            onChanged: (day) {
              if (day != null) setState(() => _editWeekStart = day);
            },
          ),
        ),
      ],
    );
  }

  // ── Subscription card ─────────────────────────────────

  Widget _buildSubscriptionCard() {
    final p = _profile;
    if (p == null) return const SizedBox.shrink();

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CardHeader(
            icon: Icons.card_membership_rounded,
            title: 'Subscription & Billing',
          ),
          const SizedBox(height: 16),
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(AppLocalizations.of(context).profCurrentPlan,
                        style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                    const SizedBox(height: 4),
                    _SubscriptionBadge(tier: p.subscriptionTier, large: true),
                  ],
                ),
              ),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(AppLocalizations.of(context).profStatusLabel,
                        style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                    const SizedBox(height: 4),
                    _SmallBadge(label: 'Active', color: _income),
                  ],
                ),
              ),
            ],
          ),
          if (p.isTrial && p.trialEndsAt != null) ...[
            const SizedBox(height: 12),
            Builder(builder: (_) {
              final daysLeft = p.trialEndsAt!.difference(DateTime.now()).inDays;
              return _InfoBanner(
                icon: Icons.timer_outlined,
                text: daysLeft > 0
                    ? 'Trial ends in $daysLeft day${daysLeft == 1 ? '' : 's'}'
                    : 'Trial ended',
                color: _warning,
              );
            }),
          ],
          // Active discount. Shown only when one exists — an empty "no promo applied"
          // row would be noise on every account that has never used a code.
          if (_activeDiscount != null) ...[
            const SizedBox(height: 12),
            _InfoBanner(
              icon: Icons.local_offer_outlined,
              text: _discountBannerText(_activeDiscount!),
              // Amber when the discount will expire before it is ever billed against. Showing
              // that in the same green as a working one is the ambiguity worth removing.
              color: _activeDiscount!.expiresBeforeItApplies ? _warning : _income,
            ),
          ],
          const SizedBox(height: 16),
          _GlassAction(
            label: p.isPremium
                ? 'Manage Subscription'
                : 'Upgrade to Premium',
            icon: p.isPremium
                ? Icons.receipt_long_rounded
                : Icons.rocket_launch_rounded,
            onTap: () => context.push('/billing'),
          ),
          // App Review 3.1.1: promo codes discount a subscription outside
          // In-App Purchase, so redemption stays on web and Android only.
          if (!_isIos) ...[
            const SizedBox(height: 8),
            _GlassAction(
              label: _activeDiscount == null ? 'Redeem a code' : 'Redeem a different code',
              icon: Icons.redeem_rounded,
              onTap: _openRedeemCodeSheet,
            ),
          ],
        ],
      ),
    );
  }

  static bool get _isIos => !kIsWeb && defaultTargetPlatform == TargetPlatform.iOS;

  /// Wording for the discount banner. The offer itself is described server-side so web and
  /// mobile agree; this only adds when it runs out, which the server cannot phrase relative
  /// to "today".
  String _discountBannerText(SubscriptionDiscount d) {
    final base = d.description.isEmpty ? 'Promo applied' : d.description;

    // The server already says what the discount is and when it lands, including the
    // expires-before-it-applies case. Only add a countdown for a time-limited discount that
    // is genuinely running, since "N days left" on one that has not started yet reads as if
    // the user is burning through it.
    final endsAt = d.endsAt;
    if (endsAt == null || d.expiresBeforeItApplies) return base;

    final days = endsAt.difference(DateTime.now()).inDays;
    if (days < 0) return base;
    if (days == 0) return '$base · ends today';
    return '$base · $days day${days == 1 ? '' : 's'} left';
  }

  // ── Referral card ─────────────────────────────────────

  /// Invite entry point. Lives here rather than only on Achievements, which is where you go
  /// to look at badges, not where you think about telling someone about the product.
  Widget _buildReferralCard() {
    final r = _referral;
    if (r == null || r.code.isEmpty) return const SizedBox.shrink();

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CardHeader(
            icon: Icons.card_giftcard_rounded,
            title: 'Invite a friend',
          ),
          const SizedBox(height: 8),
          Text(
            AppLocalizations.of(context).profReferralBothFreeMonth,
            style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
          ),

          // Free days from referral rewards, phrased like the trial banner because to the
          // user they are the same question: how long have I got.
          if (r.rewardDaysRemaining > 0) ...[
            const SizedBox(height: 12),
            _InfoBanner(
              icon: Icons.card_giftcard_rounded,
              text:
                  '${r.rewardDaysRemaining} free day${r.rewardDaysRemaining == 1 ? '' : 's'} left from referrals',
              color: _income,
            ),
          ],

          const SizedBox(height: 14),
          // The code itself, tappable to copy. People paste it into a text message far more
          // often than they use a share sheet.
          GestureDetector(
            onTap: () async {
              await Clipboard.setData(ClipboardData(text: r.code));
              if (!mounted) return;
              showAppToast(context, 'Code copied', type: AppToastType.success);
            },
            child: Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
              decoration: BoxDecoration(
                color: Colors.white.withValues(alpha: 0.04),
                borderRadius: BorderRadius.circular(10),
                border: Border.all(color: Colors.white.withValues(alpha: 0.08)),
              ),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      r.code,
                      style: GoogleFonts.firaCode(
                          fontSize: 16, color: _textGhost, letterSpacing: 2),
                    ),
                  ),
                  Icon(Icons.copy_rounded, size: 16, color: _textMuted),
                ],
              ),
            ),
          ),

          // Status, not just a total. "2 joined" cannot explain why no reward has arrived;
          // "1 waiting to connect an account" can.
          if (r.successfulReferrals > 0 || r.pendingReferrals > 0) ...[
            const SizedBox(height: 12),
            Row(
              children: [
                _SmallBadge(
                  label: '${r.successfulReferrals} joined',
                  color: _income,
                ),
                if (r.pendingReferrals > 0) ...[
                  const SizedBox(width: 8),
                  _SmallBadge(
                    label:
                        '${r.pendingReferrals} waiting to connect an account',
                    color: _warning,
                  ),
                ],
              ],
            ),
          ],

          const SizedBox(height: 14),
          _GlassAction(
            label: 'Share your invite',
            icon: Icons.ios_share_rounded,
            onTap: () => _shareReferral(r),
          ),
        ],
      ),
    );
  }

  Future<void> _shareReferral(ReferralSummary r) async {
    await SharePlus.instance.share(
      ShareParams(
        text:
            "I've been using ${AppBrand.name} to keep on top of my money. Use my link and we both get a free month: ${r.link}",
      ),
    );
  }

  // ── Security card ─────────────────────────────────────

  Widget _buildSecurityCard() {
    final p = _profile;
    if (p == null) return const SizedBox.shrink();

    final methods = _mfaStatus?.enabledMethods
        .map((m) => m.toLowerCase())
        .toList() ?? [];
    final hasTotp = methods.contains('totp');
    // A verified email means email-code MFA is available (verification confirmed
    // delivery), so it counts as a configured method here too.
    final isEmailVerified = context.read<AuthProvider>().user?.isEmailVerified ?? false;
    final hasEmail = methods.contains('email') || isEmailVerified;
    final hasPasskey = _passkeyCount > 0;
    final hasAnyMethod = hasTotp || hasEmail || hasPasskey;
    final methodCount = (hasTotp ? 1 : 0) + (hasEmail ? 1 : 0) + (hasPasskey ? 1 : 0);

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CardHeader(
            icon: Icons.shield_outlined,
            title: 'Security Settings',
            trailing: _SmallBadge(
              label: hasAnyMethod
                  ? '$methodCount method${methodCount == 1 ? '' : 's'}'
                  : 'MFA Off',
              color: hasAnyMethod ? _income : _expense,
            ),
          ),
          const SizedBox(height: 16),

          if (_mfaLoadError) ...[
            _InfoBanner(
              icon: Icons.error_outline,
              text: 'Unable to load MFA status. Tap to retry.',
              color: _expense,
            ),
            const SizedBox(height: 8),
            _GlassAction(
              label: 'Retry',
              icon: Icons.refresh_rounded,
              onTap: _loadMfaStatus,
            ),
          ] else if (!hasAnyMethod) ...[
            _InfoBanner(
              icon: Icons.info_outline,
              text: 'Add an extra layer of security with two-factor authentication.',
              color: _accent,
            ),
            const SizedBox(height: 12),
            _GlassAction(
              label: _mfaLoading ? 'Setting up...' : 'Set Up MFA',
              icon: Icons.security_rounded,
              onTap: _mfaLoading ? null : _showMfaMethodSelector,
            ),
          ] else ...[
            // Show each enabled method
            if (hasTotp)
              _MfaMethodRow(
                icon: Icons.qr_code_rounded,
                label: 'Authenticator App',
                subtitle: 'TOTP via Google Authenticator or similar',
                color: _income,
              ),
            if (hasEmail)
              _MfaMethodRow(
                icon: Icons.email_outlined,
                label: 'Email Code',
                subtitle: isEmailVerified && !methods.contains('email')
                    ? 'Email verified — codes sent to ${p.email}'
                    : 'Verification sent to ${p.email}',
                color: _income,
              ),
            if (hasPasskey)
              _MfaMethodRow(
                icon: Icons.fingerprint_rounded,
                label: '$_passkeyCount Passkey${_passkeyCount == 1 ? '' : 's'}',
                subtitle: 'Face ID, Touch ID, or Windows Hello',
                color: _income,
              ),

            const SizedBox(height: 12),

            // Add another method
            _GlassAction(
              label: 'Add Method',
              icon: Icons.add_rounded,
              onTap: _mfaLoading ? null : _showMfaMethodSelector,
            ),
            // "Disable All MFA" only applies to methods the user actively enabled
            // (authenticator / passkey / email MFA) — not the mandatory email
            // verification, which can't be turned off here.
            if (p.mfaEnabled) ...[
              const SizedBox(height: 8),
              _GlassAction(
                label: 'Disable All MFA',
                icon: Icons.lock_open_rounded,
                color: _expense,
                onTap: _disableMfa,
              ),
            ],
          ],
        ],
      ),
    );
  }

  // ── TOTP setup card ───────────────────────────────────

  Widget _buildTotpSetupCard() {
    // Backend may return secret as 'secret', 'manualEntryCode', or 'manualEntryKey'
    final qrUrl = _totpSetup?['qrCodeUrl'] ?? _totpSetup?['QrCodeUrl'];
    final secretKey = _totpSetup?['secret']
        ?? _totpSetup?['Secret']
        ?? _totpSetup?['manualEntryCode']
        ?? _totpSetup?['ManualEntryCode']
        ?? _totpSetup?['manualEntryKey']
        ?? _totpSetup?['ManualEntryKey'];

    return _GlassCard(
      borderColor: _accent.withValues(alpha: 0.3),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CardHeader(
            icon: Icons.qr_code_2_rounded,
            title: 'TOTP Setup',
            iconColor: _accent,
          ),
          const SizedBox(height: 16),

          // Step indicator
          Row(
            children: [
              Container(
                width: 24, height: 24,
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: 0.2),
                  shape: BoxShape.circle,
                ),
                child: Center(
                  child: Text('1',
                    style: GoogleFonts.sora(fontSize: 12, fontWeight: FontWeight.w700, color: _accent)),
                ),
              ),
              const SizedBox(width: 8),
              Text(AppLocalizations.of(context).profTotpStep1,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.5)),
            ],
          ),
          const SizedBox(height: 16),

          // QR code image
          if (qrUrl != null)
            Center(
              child: Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: Colors.white,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: _buildQrImage(qrUrl as String),
              ),
            ),

          // Recommended apps
          const SizedBox(height: 12),
          Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: _cardBg,
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: _cardBorder),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(AppLocalizations.of(context).profRecommendedApps,
                  style: GoogleFonts.sora(fontSize: 11, fontWeight: FontWeight.w600, color: _textMuted)),
                const SizedBox(height: 6),
                _RecommendedApp(name: 'Google Authenticator', icon: Icons.security_rounded),
                _RecommendedApp(name: 'Authy', icon: Icons.verified_rounded),
                _RecommendedApp(name: 'Microsoft Authenticator', icon: Icons.shield_rounded),
              ],
            ),
          ),

          // Manual key with copy button
          if (secretKey != null) ...[
            const SizedBox(height: 16),
            Text(
              AppLocalizations.of(context).profEnterKeyManually,
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
            ),
            const SizedBox(height: 6),
            GestureDetector(
              onTap: () {
                _copyToClipboard(secretKey.toString(), 'Secret key');
                setState(() => _secretCopied = true);
              },
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                decoration: BoxDecoration(
                  color: _cardBg,
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(
                    color: _secretCopied
                        ? _income.withValues(alpha: 0.4)
                        : _cardBorder,
                  ),
                ),
                child: Row(
                  children: [
                    Expanded(
                      child: Text(
                        secretKey.toString(),
                        style: GoogleFonts.firaCode(
                          fontSize: 13,
                          color: _accent,
                          letterSpacing: 1.5,
                        ),
                      ),
                    ),
                    Icon(
                      _secretCopied ? Icons.check_rounded : Icons.copy_rounded,
                      size: 18,
                      color: _secretCopied ? _income : _textMuted,
                    ),
                  ],
                ),
              ),
            ),
          ],

          // Step 2: Verify
          const SizedBox(height: 20),
          Row(
            children: [
              Container(
                width: 24, height: 24,
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: 0.2),
                  shape: BoxShape.circle,
                ),
                child: Center(
                  child: Text('2',
                    style: GoogleFonts.sora(fontSize: 12, fontWeight: FontWeight.w700, color: _accent)),
                ),
              ),
              const SizedBox(width: 8),
              Text(AppLocalizations.of(context).profTotpStep2,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
            ],
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: _CinematicTextField(
                  controller: _totpCodeCtrl,
                  label: 'TOTP Code',
                  keyboardType: TextInputType.number,
                ),
              ),
              const SizedBox(width: 12),
              _SolidAction(
                label: _mfaLoading ? '...' : 'Verify',
                color: _income,
                onTap: _mfaLoading ? null : _verifyTotp,
              ),
            ],
          ),
          const SizedBox(height: 8),
          Align(
            alignment: Alignment.centerRight,
            child: _SmallAction(
              label: 'Cancel',
              onTap: () => setState(() {
                _totpSetup = null;
                _totpCodeCtrl.clear();
              }),
            ),
          ),
        ],
      ),
    );
  }

  // ── Trusted devices card ──────────────────────────────

  Widget _buildTrustedDevicesCard() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CardHeader(
            icon: Icons.devices_rounded,
            title: 'Trusted Devices',
            trailing: _devices.length > 1
                ? _SmallAction(
                    label: 'Revoke All',
                    color: _expense,
                    onTap: _revokeAllDevices,
                  )
                : null,
          ),
          const SizedBox(height: 16),
          if (_devices.isEmpty)
            Text(
              AppLocalizations.of(context).profNoTrustedDevices,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
            )
          else
            ..._devices.map((d) => _DeviceTile(
                  device: d,
                  onRevoke: () => _revokeDevice(d),
                )),
        ],
      ),
    );
  }

  // ── Family card ───────────────────────────────────────

  Widget _buildFamilyCard() {
    final p = _profile;
    if (p == null) return const SizedBox.shrink();

    final isOwner = _family != null &&
        _familyMembers.any((m) => m.isCurrentUser && m.role == FamilyMemberRole.owner);
    final isAdmin = _family != null &&
        _familyMembers.any((m) => m.isCurrentUser &&
            (m.role == FamilyMemberRole.owner || m.role == FamilyMemberRole.budgetAdmin));

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CardHeader(
            icon: Icons.family_restroom_rounded,
            title: 'Family Account',
            trailing: _familyLoading
                ? const SizedBox(
                    width: 18, height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2, color: _accent))
                : null,
          ),
          const SizedBox(height: 16),

          if (_family != null) ...[
            // ── Has family ──
            // Family name + member count
            Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(_family!.name,
                        style: GoogleFonts.instrumentSerif(
                            fontSize: 20, color: _textGhost)),
                      const SizedBox(height: 4),
                      Text(AppLocalizations.of(context)
                              .profMemberCount(_family!.memberCount),
                        style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                    ],
                  ),
                ),
                if (isOwner)
                  _SmallAction(
                    label: 'Edit',
                    onTap: () => _showEditFamilySheet(),
                  ),
              ],
            ),
            const SizedBox(height: 16),

            // Members list
            ..._familyMembers.map((m) => _FamilyMemberTile(
              member: m,
              isOwner: isOwner,
              onChangeRole: isOwner && !m.isCurrentUser
                  ? () => _showChangeRoleSheet(m)
                  : null,
              onRemove: isOwner && !m.isCurrentUser
                  ? () => _removeMember(m)
                  : null,
            )),

            // Pending invitations
            if (isAdmin && _familyInvitations.isNotEmpty) ...[
              const SizedBox(height: 16),
              Row(
                children: [
                  Text(AppLocalizations.of(context).profPendingInvitations,
                    style: GoogleFonts.sora(
                        fontSize: 13, fontWeight: FontWeight.w600, color: _textMuted)),
                  const SizedBox(width: 8),
                  _SmallBadge(
                    label: '${_familyInvitations.length}',
                    color: _warning,
                  ),
                ],
              ),
              const SizedBox(height: 8),
              ..._familyInvitations.map((inv) => _InvitationTile(
                invitation: inv,
                onCancel: isOwner ? () => _cancelInvitation(inv) : null,
              )),
            ],

            // Action buttons
            const SizedBox(height: 16),
            // Inviting and cancelling are Owner-only on the server (FamilyService).
            if (isOwner) ...[
              _GlassAction(
                label: 'Invite Member',
                icon: Icons.person_add_rounded,
                onTap: () => _showInviteMemberSheet(),
              ),
              const SizedBox(height: 8),
            ],
            if (isOwner)
              _GlassAction(
                label: 'Delete Family',
                icon: Icons.delete_forever_rounded,
                color: _expense,
                onTap: _deleteFamily,
              )
            else
              _GlassAction(
                label: 'Leave Family',
                icon: Icons.logout_rounded,
                color: _warning,
                onTap: _leaveFamily,
              ),
          ] else ...[
            // ── No family — show inbound invitations if any ──
            if (_myPendingInvitations.isNotEmpty) ...[
              for (final inv in _myPendingInvitations) ...[
                Container(
                  padding: const EdgeInsets.all(14),
                  decoration: BoxDecoration(
                    color: _accent.withValues(alpha: 0.1),
                    border: Border.all(color: _accent.withValues(alpha: 0.3)),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Container(
                            width: 36,
                            height: 36,
                            decoration: BoxDecoration(
                              color: _accent.withValues(alpha: 0.2),
                              shape: BoxShape.circle,
                            ),
                            child: const Icon(Icons.family_restroom, color: _accent, size: 18),
                          ),
                          const SizedBox(width: 12),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(
                                  inv.familyName,
                                  style: GoogleFonts.sora(fontSize: 14, fontWeight: FontWeight.w600, color: _textGhost),
                                ),
                                Text(
                                  AppLocalizations.of(context).profInvitedByLine(
                                      inv.invitedByName,
                                      inv.invitedRole.displayName),
                                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                                ),
                              ],
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: 12),
                      Row(
                        mainAxisAlignment: MainAxisAlignment.end,
                        children: [
                          _GlassAction(
                            label: 'Decline',
                            icon: Icons.close,
                            onTap: () => _declineInvitation(inv),
                          ),
                          const SizedBox(width: 8),
                          _GlassAction(
                            label: 'Accept',
                            icon: Icons.check,
                            onTap: () => context.go('/accept-invitation/${inv.token}'),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 12),
              ],
            ],
            if (!p.isPremium && !p.isAdmin) ...[
              // ── Not premium ──
              if (_myPendingInvitations.isEmpty) ...[
                _InfoBanner(
                  icon: Icons.info_outline,
                  text: 'Upgrade to Premium to create or join a family account with shared budgets and financial tracking.',
                  color: _accent,
                ),
                const SizedBox(height: 12),
              ],
              _GlassAction(
                label: 'Upgrade to Premium',
                icon: Icons.rocket_launch_rounded,
                onTap: () => context.push('/billing'),
              ),
            ] else ...[
              // ── Premium, no family ──
              if (_myPendingInvitations.isEmpty)
                Text(
                  AppLocalizations.of(context).profFamilyShareSubtitle,
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.5),
                ),
              const SizedBox(height: 16),
              _GlassAction(
                label: 'Create Family',
                icon: Icons.group_add_rounded,
                onTap: () => _showCreateFamilySheet(),
              ),
            ],
          ],
        ],
      ),
    );
  }

  // ── Recovery codes card ───────────────────────────────

  Widget _buildRecoveryCodesCard() {
    return _GlassCard(
      borderColor: _income.withValues(alpha: 0.3),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CardHeader(
            icon: Icons.key_rounded,
            title: 'Recovery Codes',
            iconColor: _income,
            trailing: _SmallAction(
              label: 'Dismiss',
              onTap: () => setState(() => _recoveryCodes = null),
            ),
          ),
          const SizedBox(height: 12),
          _InfoBanner(
            icon: Icons.warning_amber_rounded,
            text: 'Save these codes in a safe place. Each code can only be used once. '
                'You will need one if you lose access to your authenticator app.',
            color: _warning,
          ),
          const SizedBox(height: 12),
          Container(
            width: double.infinity,
            padding: const EdgeInsets.all(16),
            decoration: BoxDecoration(
              color: _cardBg,
              borderRadius: BorderRadius.circular(10),
              border: Border.all(color: _cardBorder),
            ),
            child: Column(
              children: _recoveryCodes!
                  .map((code) => Padding(
                        padding: const EdgeInsets.symmetric(vertical: 3),
                        child: Text(code,
                            style: GoogleFonts.firaCode(
                                fontSize: 14,
                                color: _income,
                                letterSpacing: 2)),
                      ))
                  .toList(),
            ),
          ),
          const SizedBox(height: 12),
          _GlassAction(
            label: 'Copy All Codes',
            icon: Icons.copy_rounded,
            onTap: () => _copyToClipboard(
              _recoveryCodes!.join('\n'),
              'Recovery codes',
            ),
          ),
        ],
      ),
    );
  }

  // ── Family actions ────────────────────────────────────

  Future<void> _showCreateFamilySheet() async {
    final nameCtrl = TextEditingController();
    final result = await showModalBottomSheet<String>(
      context: context,
      backgroundColor: _sheetBg,
      // Required: without it the sheet is capped at roughly half the screen, so the
      // viewInsets padding below pushes every child out of the visible box and the
      // sheet renders as an empty rectangle the moment the keyboard opens.
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => Padding(
        padding: EdgeInsets.fromLTRB(
            24, 24, 24, MediaQuery.of(ctx).viewInsets.bottom + 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Center(child: _DragHandle()),
            const SizedBox(height: 20),
            Text(AppLocalizations.of(ctx).profCreateFamily,
                style: GoogleFonts.instrumentSerif(
                    fontSize: 22, color: _textGhost)),
            const SizedBox(height: 8),
            Text(AppLocalizations.of(ctx).profCreateFamilyOwnerNote,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
            const SizedBox(height: 20),
            _CinematicTextField(
              controller: nameCtrl,
              label: 'Family Name',
            ),
            const SizedBox(height: 20),
            SizedBox(
              width: double.infinity,
              child: _SolidAction(
                label: 'Create Family',
                color: _accent,
                onTap: () {
                  if (nameCtrl.text.trim().isNotEmpty) {
                    Navigator.pop(ctx, nameCtrl.text.trim());
                  }
                },
              ),
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );

    if (result == null || result.isEmpty) return;
    setState(() => _familyLoading = true);
    try {
      await familyService.createFamily(CreateFamilyRequest(name: result));
      if (mounted) {
        showAppToast(context, 'Family created!', type: AppToastType.success);
      }
      _loadData();
    } catch (e) {
      setState(() => _familyLoading = false);
      if (!mounted) return;

      // Family sharing is Premium-gated and the backend says so usefully: a 403 carries
      // { message, upgradeRequired: true }. Dumping the raw exception instead threw a wall
      // of Dio internals at the exact moment a paywall should have appeared — the same
      // handling family_page.dart already does, which this path had simply never gained.
      final data = e is DioException ? e.response?.data : null;
      if (data is Map && data['upgradeRequired'] == true) {
        _showFamilyPremiumRequired(
          (data['message'] as String?) ?? 'Family sharing is a Premium feature.',
        );
        return;
      }

      showAppToast(
        context,
        (data is Map ? data['message'] as String? : null) ??
            'Something went wrong creating your family. Please try again.',
        type: AppToastType.error,
      );
    }
  }

  void _showFamilyPremiumRequired(String message) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: const Icon(Icons.workspace_premium_outlined),
        title: Text(AppLocalizations.of(dialogContext).profPremiumRequired),
        content: Text(
          AppLocalizations.of(dialogContext).profPremiumRequiredBody(message),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: Text(AppLocalizations.of(dialogContext).profNotNow),
          ),
          FilledButton(
            onPressed: () {
              Navigator.of(dialogContext).pop();
              context.go('/billing');
            },
            child: Text(AppLocalizations.of(dialogContext).profSeePlans),
          ),
        ],
      ),
    );
  }

  Future<void> _showEditFamilySheet() async {
    final nameCtrl = TextEditingController(text: _family?.name ?? '');
    final result = await showModalBottomSheet<String>(
      context: context,
      backgroundColor: _sheetBg,
      // Required: without it the sheet is capped at roughly half the screen, so the
      // viewInsets padding below pushes every child out of the visible box and the
      // sheet renders as an empty rectangle the moment the keyboard opens.
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => Padding(
        padding: EdgeInsets.fromLTRB(
            24, 24, 24, MediaQuery.of(ctx).viewInsets.bottom + 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Center(child: _DragHandle()),
            const SizedBox(height: 20),
            Text(AppLocalizations.of(ctx).profEditFamily,
                style: GoogleFonts.instrumentSerif(
                    fontSize: 22, color: _textGhost)),
            const SizedBox(height: 20),
            _CinematicTextField(
              controller: nameCtrl,
              label: 'Family Name',
            ),
            const SizedBox(height: 20),
            SizedBox(
              width: double.infinity,
              child: _SolidAction(
                label: 'Save',
                color: _accent,
                onTap: () {
                  if (nameCtrl.text.trim().isNotEmpty) {
                    Navigator.pop(ctx, nameCtrl.text.trim());
                  }
                },
              ),
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );

    if (result == null || result.isEmpty) return;
    try {
      await familyService.updateFamily(result);
      if (mounted) {
        showAppToast(context, 'Family name updated', type: AppToastType.success);
      }
      _loadFamilyData();
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to update family', type: AppToastType.error);
      }
    }
  }

  Future<void> _showInviteMemberSheet() async {
    final emailCtrl = TextEditingController();
    FamilyMemberRole selectedRole = FamilyMemberRole.member;

    final result = await showModalBottomSheet<Map<String, dynamic>>(
      context: context,
      backgroundColor: _sheetBg,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setSheetState) => Padding(
          padding: EdgeInsets.fromLTRB(
              24, 24, 24, MediaQuery.of(ctx).viewInsets.bottom + 24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Center(child: _DragHandle()),
              const SizedBox(height: 20),
              Text(AppLocalizations.of(ctx).profInviteMember,
                  style: GoogleFonts.instrumentSerif(
                      fontSize: 22, color: _textGhost)),
              const SizedBox(height: 8),
              Text(AppLocalizations.of(ctx).profInviteMemberNote,
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
              const SizedBox(height: 20),
              _CinematicTextField(
                controller: emailCtrl,
                label: 'Email Address',
              ),
              const SizedBox(height: 16),
              Text(AppLocalizations.of(ctx).profRoleLabel,
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              const SizedBox(height: 8),
              ...[
                FamilyMemberRole.budgetAdmin,
                FamilyMemberRole.member,
                FamilyMemberRole.readOnly,
              ].map((role) => Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: GestureDetector(
                  onTap: () => setSheetState(() => selectedRole = role),
                  child: Container(
                    width: double.infinity,
                    padding: const EdgeInsets.symmetric(
                        horizontal: 14, vertical: 10),
                    decoration: BoxDecoration(
                      color: selectedRole == role
                          ? _accent.withValues(alpha: 0.2)
                          : _cardBg,
                      borderRadius: BorderRadius.circular(10),
                      border: Border.all(
                        color: selectedRole == role
                            ? _accent.withValues(alpha: 0.5)
                            : _cardBorder,
                      ),
                    ),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(role.displayName,
                            style: GoogleFonts.sora(
                              fontSize: 13,
                              fontWeight: FontWeight.w600,
                              color: selectedRole == role ? _accent : _textGhost,
                            )),
                        const SizedBox(height: 2),
                        Text(role.description,
                            style: GoogleFonts.sora(
                              fontSize: 10,
                              color: _textMuted,
                            )),
                      ],
                    ),
                  ),
                ),
              )),
              const SizedBox(height: 20),
              SizedBox(
                width: double.infinity,
                child: _SolidAction(
                  label: 'Send Invitation',
                  color: _accent,
                  onTap: () {
                    if (emailCtrl.text.trim().isNotEmpty) {
                      Navigator.pop(ctx, {
                        'email': emailCtrl.text.trim(),
                        'role': selectedRole,
                      });
                    }
                  },
                ),
              ),
              const SizedBox(height: 8),
            ],
          ),
        ),
      ),
    );

    if (result == null) return;
    try {
      await familyService.inviteMember(InviteFamilyMemberRequest(
        email: result['email'] as String,
        role: result['role'] as FamilyMemberRole,
      ));
      if (mounted) {
        showAppToast(context, 'Invitation sent', type: AppToastType.success);
      }
      _loadFamilyData();
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Failed to send invitation: $e', type: AppToastType.error);
      }
    }
  }

  Future<void> _showChangeRoleSheet(FamilyMember member) async {
    FamilyMemberRole selectedRole = member.role;

    final result = await showModalBottomSheet<FamilyMemberRole>(
      context: context,
      backgroundColor: _sheetBg,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setSheetState) => Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Center(child: _DragHandle()),
              const SizedBox(height: 20),
              Text(AppLocalizations.of(ctx).profChangeRole,
                  style: GoogleFonts.instrumentSerif(
                      fontSize: 22, color: _textGhost)),
              const SizedBox(height: 8),
              Text(AppLocalizations.of(ctx).profUpdateRoleFor(member.displayName),
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
              const SizedBox(height: 20),
              ...FamilyMemberRole.values
                  .where((r) => r != FamilyMemberRole.owner)
                  .map((role) => GestureDetector(
                    onTap: () => setSheetState(() => selectedRole = role),
                    child: Container(
                      width: double.infinity,
                      margin: const EdgeInsets.only(bottom: 8),
                      padding: const EdgeInsets.all(14),
                      decoration: BoxDecoration(
                        color: selectedRole == role
                            ? _accent.withValues(alpha: 0.15)
                            : _cardBg,
                        borderRadius: BorderRadius.circular(10),
                        border: Border.all(
                          color: selectedRole == role
                              ? _accent.withValues(alpha: 0.4)
                              : _cardBorder,
                        ),
                      ),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(role.displayName,
                              style: GoogleFonts.sora(
                                fontSize: 14,
                                fontWeight: FontWeight.w600,
                                color: selectedRole == role ? _accent : _textGhost,
                              )),
                          const SizedBox(height: 2),
                          Text(role.description,
                              style: GoogleFonts.sora(
                                fontSize: 11, color: _textMuted)),
                        ],
                      ),
                    ),
                  )),
              const SizedBox(height: 12),
              SizedBox(
                width: double.infinity,
                child: _SolidAction(
                  label: 'Update Role',
                  color: _accent,
                  onTap: () => Navigator.pop(ctx, selectedRole),
                ),
              ),
              const SizedBox(height: 8),
            ],
          ),
        ),
      ),
    );

    if (result == null || result == member.role) return;
    try {
      await familyService.updateMemberRole(member.userId, result);
      if (mounted) {
        showAppToast(context, 'Role updated', type: AppToastType.success);
      }
      _loadFamilyData();
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to update role', type: AppToastType.error);
      }
    }
  }

  Future<void> _removeMember(FamilyMember member) async {
    final confirmed = await _showConfirmSheet(
      'Remove Member',
      'Remove ${member.displayName} from the family? '
      'They will lose access to shared budgets.',
    );
    if (confirmed != true) return;
    try {
      await familyService.removeMember(member.userId);
      if (mounted) {
        showAppToast(context, 'Member removed', type: AppToastType.success);
      }
      _loadFamilyData();
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to remove member', type: AppToastType.error);
      }
    }
  }

  Future<void> _cancelInvitation(FamilyInvitation invitation) async {
    try {
      await familyService.cancelInvitation(invitation.id);
      if (mounted) {
        showAppToast(context, 'Invitation cancelled', type: AppToastType.success);
      }
      _loadFamilyData();
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to cancel invitation', type: AppToastType.error);
      }
    }
  }

  Future<void> _declineInvitation(FamilyInvitation invitation) async {
    try {
      await familyService.declineInvitation(invitation.token);
      if (mounted) {
        showAppToast(context, 'Invitation declined', type: AppToastType.success);
      }
      _loadFamilyData();
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to decline invitation', type: AppToastType.error);
      }
    }
  }

  Future<void> _deleteFamily() async {
    final confirmed = await _showConfirmSheet(
      'Delete Family',
      'This will permanently delete your family and remove all members. '
      'This action cannot be undone.',
    );
    if (confirmed != true) return;
    setState(() => _familyLoading = true);
    try {
      await familyService.deleteFamily();
      if (mounted) {
        showAppToast(context, 'Family deleted', type: AppToastType.success);
      }
      _loadData();
    } catch (_) {
      setState(() => _familyLoading = false);
      if (mounted) {
        showAppToast(context, 'Failed to delete family', type: AppToastType.error);
      }
    }
  }

  Future<void> _leaveFamily() async {
    final confirmed = await _showConfirmSheet(
      'Leave Family',
      'You will lose access to shared budgets and financial data. '
      'You can rejoin if invited again.',
    );
    if (confirmed != true) return;
    setState(() => _familyLoading = true);
    try {
      await familyService.leaveFamily();
      if (mounted) {
        showAppToast(context, 'You left the family', type: AppToastType.success);
      }
      _loadData();
    } catch (_) {
      setState(() => _familyLoading = false);
      if (mounted) {
        showAppToast(context, 'Failed to leave family', type: AppToastType.error);
      }
    }
  }

  // ── Delete account ────────────────────────────────────

  Future<void> _deleteAccount() async {
    final p = _profile;
    if (p == null) return;

    // null = cancelled; non-null = confirmed (empty string for Google users)
    final password = await _showDeleteAccountModal(p);
    if (password == null) return;

    setState(() => _isDeletingAccount = true);
    try {
      await userService.deleteAccount(p.isGoogleUser ? null : password);
      if (!mounted) return;
      await context.read<AuthProvider>().logout();
      if (!mounted) return;
      goToMarketingHome(context);
    } catch (_) {
      if (!mounted) return;
      setState(() => _isDeletingAccount = false);
      showAppToast(context, 'Failed to delete account', type: AppToastType.error);
    }
  }

  Future<String?> _showDeleteAccountModal(UserProfile p) {
    return showModalBottomSheet<String>(
      context: context,
      backgroundColor: _sheetBg,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) => _DeleteAccountSheet(profile: p),
    );
  }

  // ── Danger Zone card ──────────────────────────────────

  Widget _buildDangerZoneCard() {
    return _GlassCard(
      borderColor: _danger.withValues(alpha: 0.25),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const _CardHeader(
            icon: Icons.warning_amber_rounded,
            title: 'Danger Zone',
            iconColor: _danger,
          ),
          const SizedBox(height: 12),
          Text(
            AppLocalizations.of(context).profDeleteAccountBody,
            style: GoogleFonts.sora(
                fontSize: 13, color: _textMuted, height: 1.5),
          ),
          const SizedBox(height: 16),
          _GlassAction(
            label: _isDeletingAccount ? 'Deleting...' : 'Delete My Account',
            icon: Icons.delete_forever_rounded,
            color: _danger,
            onTap: _isDeletingAccount ? null : _deleteAccount,
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  Shared UI components
// ═══════════════════════════════════════════════════════════

/// Glassmorphic card wrapper
class _GlassCard extends StatelessWidget {
  final Widget child;
  final Color? borderColor;

  const _GlassCard({required this.child, this.borderColor});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: borderColor ?? _cardBorder),
        boxShadow: [
          BoxShadow(
            color: const Color(0xFF7B61FF).withValues(alpha: 0.03),
            blurRadius: 24,
            spreadRadius: 0,
          ),
        ],
      ),
      child: child,
    );
  }
}

/// Card section header with icon
class _CardHeader extends StatelessWidget {
  final IconData icon;
  final String title;
  final Color? iconColor;
  final Widget? trailing;

  const _CardHeader({
    required this.icon,
    required this.title,
    this.iconColor,
    this.trailing,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, size: 20, color: iconColor ?? _accent),
        const SizedBox(width: 10),
        Text(
          title,
          style: GoogleFonts.sora(
            fontSize: 16,
            fontWeight: FontWeight.w700,
            color: _textGhost,
          ),
        ),
        const Spacer(),
        if (trailing != null) trailing!,
      ],
    );
  }
}

/// Labeled info row
class _InfoRow extends StatelessWidget {
  final String label;
  final String value;

  const _InfoRow({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        children: [
          SizedBox(
            width: 120,
            child: Text(
              label,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
            ),
          ),
          Expanded(
            child: Text(
              value,
              style: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w500,
                color: _textGhost,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Info banner / callout
class _InfoBanner extends StatelessWidget {
  final IconData icon;
  final String text;
  final Color color;

  const _InfoBanner({required this.icon, required this.text, required this.color});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: color.withValues(alpha: 0.2)),
      ),
      child: Row(
        children: [
          Icon(icon, size: 18, color: color),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              text,
              style: GoogleFonts.sora(
                fontSize: 12,
                color: color,
                height: 1.4,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Subscription tier badge
class _SubscriptionBadge extends StatelessWidget {
  final String tier;
  final bool large;

  const _SubscriptionBadge({required this.tier, this.large = false});

  Color get _color => switch (tier) {
        'Admin' => _accent,
        'Premium' => _income,
        'PremiumInherited' => _income,
        'Trial' => _warning,
        _ => _textMuted,
      };

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: EdgeInsets.symmetric(
        horizontal: large ? 14 : 10,
        vertical: large ? 6 : 3,
      ),
      decoration: BoxDecoration(
        gradient: LinearGradient(
          colors: [_color.withValues(alpha: 0.2), _color.withValues(alpha: 0.1)],
        ),
        borderRadius: BorderRadius.circular(large ? 10 : 8),
        border: Border.all(color: _color.withValues(alpha: 0.3)),
      ),
      child: Text(
        tier,
        style: GoogleFonts.sora(
          fontSize: large ? 14 : 11,
          fontWeight: FontWeight.w600,
          color: _color,
        ),
      ),
    );
  }
}

/// Small colored badge
class _SmallBadge extends StatelessWidget {
  final String label;
  final Color color;

  const _SmallBadge({required this.label, required this.color});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: color.withValues(alpha: 0.3)),
      ),
      child: Text(
        label,
        style: GoogleFonts.sora(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: color,
        ),
      ),
    );
  }
}

/// Glass-style action button
class _GlassAction extends StatefulWidget {
  final String label;
  final IconData? icon;
  final Color? color;
  final VoidCallback? onTap;

  const _GlassAction({
    required this.label,
    this.icon,
    this.color,
    this.onTap,
  });

  @override
  State<_GlassAction> createState() => _GlassActionState();
}

class _GlassActionState extends State<_GlassAction> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = widget.color ?? _accent;
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          decoration: BoxDecoration(
            color: _hovered ? c.withValues(alpha: 0.15) : _cardBg,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: _hovered ? c.withValues(alpha: 0.4) : _cardBorder,
            ),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            mainAxisSize: MainAxisSize.min,
            children: [
              if (widget.icon != null) ...[
                Icon(widget.icon, size: 18, color: c),
                const SizedBox(width: 8),
              ],
              Text(
                widget.label,
                style: GoogleFonts.sora(
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  color: c,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Solid action button (for destructive/confirm)
class _SolidAction extends StatefulWidget {
  final String label;
  final Color color;
  final VoidCallback? onTap;

  const _SolidAction({required this.label, required this.color, this.onTap});

  @override
  State<_SolidAction> createState() => _SolidActionState();
}

class _SolidActionState extends State<_SolidAction> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onTap != null;
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
          decoration: BoxDecoration(
            color: !enabled
                ? widget.color.withValues(alpha: 0.15)
                : _hovered
                    ? widget.color.withValues(alpha: 0.9)
                    : widget.color.withValues(alpha: 0.8),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Center(
            child: Text(
              widget.label,
              style: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w600,
                color: enabled
                    ? Colors.white
                    : widget.color.withValues(alpha: 0.4),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Small text action (like "Edit", "Cancel")
class _SmallAction extends StatefulWidget {
  final String label;
  final Color? color;
  final VoidCallback? onTap;

  const _SmallAction({required this.label, this.color, this.onTap});

  @override
  State<_SmallAction> createState() => _SmallActionState();
}

class _SmallActionState extends State<_SmallAction> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = widget.color ?? _accent;
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Text(
          widget.label,
          style: GoogleFonts.sora(
            fontSize: 12,
            fontWeight: FontWeight.w600,
            color: _hovered ? c : c.withValues(alpha: 0.7),
            decoration: _hovered ? TextDecoration.underline : null,
            decorationColor: c,
          ),
        ),
      ),
    );
  }
}

/// Cinematic styled text field
class _CinematicTextField extends StatelessWidget {
  final TextEditingController controller;
  final String? label;
  final TextInputType? keyboardType;
  final ValueChanged<String>? onChanged;
  final bool obscureText;
  final bool autocorrect;
  final String? hintText;

  const _CinematicTextField({
    required this.controller,
    this.label,
    this.keyboardType,
    this.onChanged,
    this.obscureText = false,
    this.autocorrect = true,
    this.hintText,
  });

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: controller,
      keyboardType: keyboardType,
      onChanged: onChanged,
      obscureText: obscureText,
      autocorrect: autocorrect,
      style: GoogleFonts.sora(fontSize: 14, color: _textGhost),
      decoration: InputDecoration(
        labelText: label,
        labelStyle: GoogleFonts.sora(fontSize: 13, color: _textMuted),
        hintText: hintText,
        hintStyle: GoogleFonts.sora(
            fontSize: 13, color: _textMuted.withValues(alpha: 0.5)),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: const BorderSide(color: _cardBorder),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: BorderSide(color: _accent.withValues(alpha: 0.6)),
        ),
        filled: true,
        fillColor: _cardBg,
        contentPadding:
            const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      ),
    );
  }
}

/// Device list tile
class _DeviceTile extends StatefulWidget {
  final TrustedDevice device;
  final VoidCallback onRevoke;

  const _DeviceTile({required this.device, required this.onRevoke});

  @override
  State<_DeviceTile> createState() => _DeviceTileState();
}

class _DeviceTileState extends State<_DeviceTile> {
  bool _hovered = false;

  IconData get _deviceIcon => switch (widget.device.deviceType?.toLowerCase()) {
        'mobile' || 'phone' => Icons.phone_android_rounded,
        'tablet' => Icons.tablet_rounded,
        'desktop' || 'computer' => Icons.desktop_windows_rounded,
        'laptop' => Icons.laptop_rounded,
        _ => Icons.devices_rounded,
      };

  @override
  Widget build(BuildContext context) {
    final d = widget.device;
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 150),
        margin: const EdgeInsets.only(bottom: 8),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: _hovered ? _cardBg : Colors.transparent,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: _hovered ? _cardBorder : Colors.transparent,
          ),
        ),
        child: Row(
          children: [
            Icon(_deviceIcon, size: 22, color: _accent.withValues(alpha: 0.7)),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Text(
                        d.displayName,
                        style: GoogleFonts.sora(
                          fontSize: 13,
                          fontWeight: FontWeight.w600,
                          color: _textGhost,
                        ),
                      ),
                      if (d.isCurrentDevice) ...[
                        const SizedBox(width: 8),
                        _SmallBadge(label: 'Current', color: _income),
                      ],
                    ],
                  ),
                  const SizedBox(height: 2),
                  Text(
                    AppLocalizations.of(context).profLastUsed(formatRelativeDate(d.lastUsedAt)),
                    style: GoogleFonts.sora(
                      fontSize: 11,
                      color: _textMuted.withValues(alpha: 0.6),
                    ),
                  ),
                ],
              ),
            ),
            if (!d.isCurrentDevice)
              GestureDetector(
                onTap: widget.onRevoke,
                child: Icon(
                  Icons.close_rounded,
                  size: 18,
                  color: _hovered ? _expense : _textMuted.withValues(alpha: 0.4),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// Family member list tile
class _FamilyMemberTile extends StatefulWidget {
  final FamilyMember member;
  final bool isOwner;
  final VoidCallback? onChangeRole;
  final VoidCallback? onRemove;

  const _FamilyMemberTile({
    required this.member,
    required this.isOwner,
    this.onChangeRole,
    this.onRemove,
  });

  @override
  State<_FamilyMemberTile> createState() => _FamilyMemberTileState();
}

class _FamilyMemberTileState extends State<_FamilyMemberTile> {
  bool _hovered = false;

  Color get _roleColor => switch (widget.member.role) {
        FamilyMemberRole.owner => _accent,
        FamilyMemberRole.budgetAdmin => _income,
        FamilyMemberRole.member => _textGhost,
        FamilyMemberRole.readOnly => _textMuted,
      };

  @override
  Widget build(BuildContext context) {
    final m = widget.member;
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 150),
        margin: const EdgeInsets.only(bottom: 8),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: _hovered ? _cardBg : Colors.transparent,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: _hovered ? _cardBorder : Colors.transparent,
          ),
        ),
        child: Row(
          children: [
            // Avatar
            Container(
              width: 36,
              height: 36,
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                gradient: LinearGradient(
                  colors: [_roleColor.withValues(alpha: 0.3), _roleColor.withValues(alpha: 0.1)],
                ),
              ),
              child: Center(
                child: Text(
                  m.displayName.isNotEmpty
                      ? m.displayName[0].toUpperCase()
                      : '?',
                  style: GoogleFonts.sora(
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                    color: _roleColor,
                  ),
                ),
              ),
            ),
            const SizedBox(width: 12),
            // Info
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Flexible(
                        child: Text(
                          m.displayName,
                          style: GoogleFonts.sora(
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                            color: _textGhost,
                          ),
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                      if (m.isCurrentUser) ...[
                        const SizedBox(width: 6),
                        _SmallBadge(label: 'You', color: _accent),
                      ],
                    ],
                  ),
                  Text(
                    m.email,
                    style: GoogleFonts.sora(
                      fontSize: 11,
                      color: _textMuted.withValues(alpha: 0.6),
                    ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ),
            ),
            // Role badge
            _SmallBadge(label: m.role.displayName, color: _roleColor),
            // Actions for owner
            if (_hovered && (widget.onChangeRole != null || widget.onRemove != null)) ...[
              const SizedBox(width: 8),
              if (widget.onChangeRole != null)
                GestureDetector(
                  onTap: widget.onChangeRole,
                  child: Icon(Icons.edit_rounded,
                      size: 16, color: _accent.withValues(alpha: 0.7)),
                ),
              if (widget.onRemove != null) ...[
                const SizedBox(width: 6),
                GestureDetector(
                  onTap: widget.onRemove,
                  child: Icon(Icons.close_rounded,
                      size: 16, color: _expense.withValues(alpha: 0.7)),
                ),
              ],
            ],
          ],
        ),
      ),
    );
  }
}

/// Pending invitation tile
class _InvitationTile extends StatelessWidget {
  final FamilyInvitation invitation;
  /// Null for non-owners: only the Owner can cancel an invitation.
  final VoidCallback? onCancel;

  const _InvitationTile({required this.invitation, this.onCancel});

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: _cardBorder),
      ),
      child: Row(
        children: [
          Icon(Icons.mail_outline_rounded, size: 18,
              color: _warning.withValues(alpha: 0.7)),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(invitation.email,
                    style: GoogleFonts.sora(
                        fontSize: 13, color: _textGhost)),
                Text(
                  '${invitation.invitedRole.displayName} · ${invitation.isExpired ? AppLocalizations.of(context).profInvitationExpired : AppLocalizations.of(context).profInvitationPending}',
                  style: GoogleFonts.sora(
                    fontSize: 11,
                    color: invitation.isExpired ? _expense : _textMuted,
                  ),
                ),
              ],
            ),
          ),
          if (onCancel != null)
            GestureDetector(
              onTap: onCancel,
              child: Icon(Icons.close_rounded, size: 18,
                  color: _textMuted.withValues(alpha: 0.5)),
            ),
        ],
      ),
    );
  }
}

Widget _buildQrImage(String url) {
  const size = 180.0;
  const fallback = SizedBox(
    width: size, height: size,
    child: Center(child: Text('QR failed to load', style: TextStyle(color: Colors.black54))),
  );
  if (url.startsWith('data:image/')) {
    try {
      final base64Str = url.split(',').last;
      final bytes = base64Decode(base64Str);
      return Image.memory(bytes, width: size, height: size, errorBuilder: (_, __, ___) => fallback);
    } catch (_) {
      return fallback;
    }
  }
  return Image.network(url, width: size, height: size, errorBuilder: (_, __, ___) => fallback);
}

/// Recommended authenticator app row
class _RecommendedApp extends StatelessWidget {
  final String name;
  final IconData icon;

  const _RecommendedApp({required this.name, required this.icon});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          Icon(icon, size: 16, color: _accent.withValues(alpha: 0.6)),
          const SizedBox(width: 8),
          Text(name,
              style: GoogleFonts.sora(fontSize: 12, color: _textGhost)),
        ],
      ),
    );
  }
}

/// MFA method option in the selector bottom sheet
class _MfaMethodRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String subtitle;
  final Color color;

  const _MfaMethodRow({
    required this.icon,
    required this.label,
    required this.subtitle,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.06),
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: color.withValues(alpha: 0.15)),
        ),
        child: Row(
          children: [
            Icon(icon, size: 20, color: color),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(label,
                    style: GoogleFonts.sora(
                      fontSize: 13, fontWeight: FontWeight.w600, color: _textGhost)),
                  const SizedBox(height: 2),
                  Text(subtitle,
                    style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis),
                ],
              ),
            ),
            Icon(Icons.check_circle_outline, size: 18, color: color.withValues(alpha: 0.7)),
          ],
        ),
      ),
    );
  }
}

class _MfaMethodOption extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final bool recommended;
  final VoidCallback onTap;

  const _MfaMethodOption({
    required this.icon,
    required this.title,
    required this.subtitle,
    this.recommended = false,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: _cardBg,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: recommended ? _accent.withValues(alpha: 0.3) : _cardBorder),
          ),
          child: Row(
            children: [
              Icon(icon, size: 24, color: recommended ? _accent : _textMuted),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        // Flexible + ellipsis: the title must yield to the badge
                        // or the Row overflows on narrow phones ("Authenticator
                        // App (TOTP)" + "Recommended" exceeded 411dp by 10px).
                        Flexible(
                          child: Text(title,
                              overflow: TextOverflow.ellipsis,
                              style: GoogleFonts.sora(fontSize: 14, fontWeight: FontWeight.w600, color: _textGhost)),
                        ),
                        if (recommended) ...[
                          const SizedBox(width: 8),
                          Container(
                            padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                            decoration: BoxDecoration(
                              color: _accent.withValues(alpha: 0.15),
                              borderRadius: BorderRadius.circular(4),
                            ),
                            child: Text(AppLocalizations.of(context).profRecommended,
                              style: GoogleFonts.sora(fontSize: 9, fontWeight: FontWeight.w600, color: _accent)),
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(subtitle, style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
                  ],
                ),
              ),
              Icon(Icons.chevron_right_rounded, size: 20, color: _textMuted),
            ],
          ),
        ),
      ),
    );
  }
}

/// Delete account confirmation sheet — owns its controllers and disposes them
class _DeleteAccountSheet extends StatefulWidget {
  final UserProfile profile;

  const _DeleteAccountSheet({required this.profile});

  @override
  State<_DeleteAccountSheet> createState() => _DeleteAccountSheetState();
}

class _DeleteAccountSheetState extends State<_DeleteAccountSheet> {
  final _emailCtrl = TextEditingController();
  final _passwordCtrl = TextEditingController();

  @override
  void dispose() {
    _emailCtrl.dispose();
    _passwordCtrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final p = widget.profile;
    final emailMatches =
        _emailCtrl.text.trim().toLowerCase() == p.email.toLowerCase();
    final canDelete =
        emailMatches && (p.isGoogleUser || _passwordCtrl.text.isNotEmpty);

    return Padding(
      padding: EdgeInsets.fromLTRB(
          24, 24, 24, MediaQuery.of(context).viewInsets.bottom + 24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Center(child: _DragHandle()),
          const SizedBox(height: 20),
          Row(
            children: [
              const Icon(Icons.warning_rounded, size: 22, color: _danger),
              const SizedBox(width: 10),
              Text(
                AppLocalizations.of(context).profDeleteAccountTitle,
                style: GoogleFonts.instrumentSerif(
                    fontSize: 22, color: _danger),
              ),
            ],
          ),
          const SizedBox(height: 12),
          const _InfoBanner(
            icon: Icons.warning_amber_rounded,
            text: 'This will permanently delete your account and all '
                'associated data, cancel any active subscriptions, and '
                'disconnect any linked services. '
                'This action cannot be undone.',
            color: _danger,
          ),
          const SizedBox(height: 20),
          Text(
            AppLocalizations.of(context).profDeleteConfirmEmailPrompt,
            style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
          ),
          const SizedBox(height: 8),
          _CinematicTextField(
            controller: _emailCtrl,
            keyboardType: TextInputType.emailAddress,
            autocorrect: false,
            hintText: p.email,
            onChanged: (_) => setState(() {}),
          ),
          if (!p.isGoogleUser) ...[
            const SizedBox(height: 12),
            Text(
              AppLocalizations.of(context).profDeleteEnterPassword,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
            ),
            const SizedBox(height: 8),
            _CinematicTextField(
              controller: _passwordCtrl,
              label: 'Password',
              obscureText: true,
              onChanged: (_) => setState(() {}),
            ),
          ],
          const SizedBox(height: 24),
          Row(
            children: [
              Expanded(
                child: _GlassAction(
                  label: 'Cancel',
                  onTap: () => Navigator.pop(context),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: _SolidAction(
                  label: 'Delete Account',
                  color: _danger,
                  onTap: canDelete
                      ? () => Navigator.pop(context, _passwordCtrl.text)
                      : null,
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
        ],
      ),
    );
  }
}

/// Drag handle for bottom sheets
class _DragHandle extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return Container(
      width: 40,
      height: 4,
      decoration: BoxDecoration(
        color: _textMuted.withValues(alpha: 0.3),
        borderRadius: BorderRadius.circular(2),
      ),
    );
  }
}
