import '../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:url_launcher/url_launcher.dart';

import '../providers/auth_provider.dart';
import '../services/user_service.dart';
import 'app_toast.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _sheetBg = Color(0xFF0C0C18);

typedef AiConsentGrant = Future<DateTime?> Function(bool granted);

/// Pure decision: AI features run only on an explicit, current grant, on every
/// platform. Undecided used to mean "on" outside iOS (an implied grant), but consent
/// lives on the user, so a grant implied on Android also counted on iOS; that is how
/// App Review reached AI Chat on an iPad without seeing this sheet (rejected
/// 2026-09-26 under 5.1.1(i)/5.1.2(i)). The server also hides grants given under an
/// older disclosure, so those users see this sheet once more.
bool resolveAiConsent({
  required bool granted,
  required bool withdrawn,
}) =>
    !withdrawn && granted;

/// Reads the cached consent state from AuthProvider. Non-final so tests can
/// stub it (same pattern as the injectable service fakes). With no provider
/// mounted it answers "not granted", so the sheet is shown.
bool Function(BuildContext context) hasAiConsent = (context) {
  bool granted = false, withdrawn = false;
  try {
    final u = context.read<AuthProvider>().user;
    granted = u?.aiConsentGranted ?? false;
    withdrawn = u?.aiConsentWithdrawn ?? false;
  } catch (_) {}
  return resolveAiConsent(granted: granted, withdrawn: withdrawn);
};

/// True if the signed-in user has granted AI data-sharing consent; otherwise
/// shows the consent sheet and returns whether they granted it. Call this before
/// ANY action that sends user data to the model (chat, insights, charts,
/// auto-categorize). The server enforces the same rule (403 AiConsentRequired),
/// so this is UX, not the security boundary.

Future<bool> ensureAiConsent(BuildContext context) async {
  if (hasAiConsent(context)) return true;
  return showAiConsentSheet(context);
}

const _promptLastAskedKey = 'ai_consent_prompt_last_asked';
const _promptRetryInterval = Duration(days: 14);

/// The app-level ask: once on the first authenticated landing, right after the
/// notification pre-prompt, then again at most every 14 days after "Not now" (the
/// same cadence as that prompt). Returns true when the user granted.
///
/// Asked at app level, not on the AI Chat screen, for two reasons. AI runs outside
/// chat too (categorization after every bank sync, dashboard insights, charts), on
/// every plan. And AI Chat is Premium: asking there made a Standard user agree to
/// share data and then hit the upgrade gate on the very next tap.
///
/// Someone who turned AI off in Settings made a choice, so they are never re-asked on
/// a timer; the contextual [ensureAiConsent] still asks if they tap an AI feature.
Future<bool> maybeShowAiConsentPrompt(BuildContext context) async {
  if (hasAiConsent(context)) return false;
  String? userId;
  try {
    final user = context.read<AuthProvider>().user;
    if (user == null || user.aiConsentWithdrawn) return false;
    userId = user.id;
  } catch (_) {
    return false;
  }
  try {
    final prefs = await SharedPreferences.getInstance();
    final key = '$_promptLastAskedKey:$userId';
    final lastAsked = prefs.getInt(key);
    if (lastAsked != null &&
        DateTime.now().difference(DateTime.fromMillisecondsSinceEpoch(lastAsked)) <
            _promptRetryInterval) {
      return false;
    }
    // Recorded before showing, so a dismissed or killed sheet still counts as asked.
    await prefs.setInt(key, DateTime.now().millisecondsSinceEpoch);
  } catch (_) {
    return false;
  }
  if (!context.mounted) return false;
  return showAiConsentSheet(context);
}

/// App Store Review Guideline 5.1.2(i): "clearly disclose where personal data will
/// be shared with third parties, including with third-party AI, and obtain explicit
/// permission before doing so." This sheet is that disclosure and permission.
/// Names the recipient (Google Gemini), lists every category of data sent (including
/// the background categorization after a bank sync), says what is never sent, offers a
/// real decline, and points to where consent can be withdrawn (Settings > AI Assistant).
Future<bool> showAiConsentSheet(BuildContext context, {AiConsentGrant? grant}) async {
  final result = await showModalBottomSheet<bool>(
    context: context,
    backgroundColor: _sheetBg,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
    ),
    builder: (_) => _AiConsentSheet(grant: grant ?? userService.setAiConsent),
  );
  if (result == true && context.mounted) {
    // Refresh the cached profile so every ensureAiConsent() after this is instant.
    try {
      await context.read<AuthProvider>().refreshUser();
    } catch (_) {}
  }
  return result == true;
}

class _AiConsentSheet extends StatefulWidget {
  final AiConsentGrant grant;
  const _AiConsentSheet({required this.grant});

  @override
  State<_AiConsentSheet> createState() => _AiConsentSheetState();
}

class _AiConsentSheetState extends State<_AiConsentSheet> {
  bool _busy = false;

  Future<void> _accept() async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await widget.grant(true);
      if (!mounted) return;
      Navigator.pop(context, true);
    } catch (_) {
      if (!mounted) return;
      setState(() => _busy = false);
      showAppToast(context, 'Could not save your choice. Please try again.',
          type: AppToastType.error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return SafeArea(
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
                  child: const Icon(Icons.auto_awesome, color: _accent, size: 22),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(AppLocalizations.of(context).consentTurnOnTitle,
                      style: GoogleFonts.sora(
                          fontSize: 18, fontWeight: FontWeight.w700, color: _textGhost)),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Text(
              AppLocalizations.of(context).consentGeminiBody,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.5),
            ),
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
                  Text(l10n.consentWhatIsSent,
                      style: GoogleFonts.sora(
                          fontSize: 12, fontWeight: FontWeight.w600, color: _textGhost)),
                  const SizedBox(height: 8),
                  _item(l10n.consentDataTransactions),
                  _item(l10n.consentDataAccounts),
                  _item(l10n.consentDataPlans),
                  _item(l10n.consentDataPersonal),
                  _item(l10n.consentDataBackground),
                  const SizedBox(height: 4),
                  Text(l10n.consentNeverSent,
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted, height: 1.4)),
                ],
              ),
            ),
            const SizedBox(height: 12),
            _bullet(l10n.consentGoogleUse),
            _bullet(l10n.consentWithdraw),
            const SizedBox(height: 16),
            SizedBox(
              height: 48,
              child: ElevatedButton(
                onPressed: _busy ? null : _accept,
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
                    : Text(AppLocalizations.of(context).consentTurnOnButton,
                        style: GoogleFonts.sora(fontSize: 14, fontWeight: FontWeight.w600)),
              ),
            ),
            const SizedBox(height: 6),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                TextButton(
                  onPressed: _busy ? null : () => Navigator.pop(context, false),
                  child: Text(AppLocalizations.of(context).consentNotNow,
                      style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
                ),
                TextButton(
                  onPressed: () => launchUrl(
                      Uri.parse('https://buoy-ai.com/privacy-policy'),
                      mode: LaunchMode.externalApplication),
                  child: Text(AppLocalizations.of(context).consentPrivacyPolicy,
                      style: GoogleFonts.sora(fontSize: 13, color: _accent)),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _item(String text) => Padding(
        padding: const EdgeInsets.only(bottom: 6),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Padding(
              padding: EdgeInsets.only(top: 6),
              child: Icon(Icons.circle, size: 5, color: _textMuted),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Text(text,
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted, height: 1.4)),
            ),
          ],
        ),
      );

  Widget _bullet(String text) => Padding(
        padding: const EdgeInsets.only(bottom: 6),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Padding(
              padding: EdgeInsets.only(top: 5),
              child: Icon(Icons.check_rounded, size: 14, color: _accent),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Text(text,
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted, height: 1.4)),
            ),
          ],
        ),
      );
}
