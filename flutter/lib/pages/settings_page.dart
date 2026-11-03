import '../config/app_brand.dart';
import '../config/server_capabilities.dart';
import 'package:flutter/foundation.dart' show kIsWeb, defaultTargetPlatform, TargetPlatform;
import 'package:flutter/material.dart';
import '../services/review_prompt_service.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:provider/provider.dart';
import '../offline/background_sync.dart'
    if (dart.library.html) '../offline/background_sync_web.dart';
import '../l10n/generated/app_localizations.dart';
import '../providers/auth_provider.dart';
import '../services/analytics_service.dart';
import '../services/auth_service.dart';
import '../services/biometric_service.dart';
import '../services/user_service.dart';
import '../models/billing_models.dart';
import '../services/ai_service.dart';
import '../services/ai_status_service.dart';
import '../services/categorization_rules_service.dart';
import '../services/email_preferences_service.dart';
import '../services/onboarding_quiz_service.dart';
import '../services/push_notification_service.dart';
import '../services/push_preferences_service.dart';
import '../utils/server_roles.dart';
import '../utils/background_preferences.dart';
import '../utils/celebration_fx.dart';
import '../utils/ticker_preferences.dart';
import '../utils/web_navigation.dart';
import '../widgets/app_toast.dart';
import '../widgets/ai_consent_sheet.dart';
import '../widgets/data_export_sheet.dart';

// ── VaporClinic palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _sheetBg = Color(0xFF0C0C18);

// ── All subscription tracking categories ────────────────
const _allSubCategories = [
  'streaming',
  'software',
  'utilities',
  'insurance',
  'fitness',
  'news',
  'gaming',
  'shopping',
  'education',
  'professional',
];

// ═══════════════════════════════════════════════════════════
//  SettingsPage
// ═══════════════════════════════════════════════════════════
class SettingsPage extends StatefulWidget {
  const SettingsPage({super.key});

  @override
  State<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends State<SettingsPage>
    with SingleTickerProviderStateMixin {
  // Data
  bool _isLoading = true;
  String? _error;

  // AI settings
  String _aiMode = 'Quick'; // 'Quick' or 'Clarifying'
  bool _aiMemoryEnabled = true;
  // AI Control Spectrum (#412): Manual | Coach | Assistant | Manager
  String _aiAutonomyLevel = 'Coach';
  bool _aiLoading = false;

  // Budget settings
  bool _excludePending = false;
  bool _reattributeOnAssoc = false;
  bool _budgetLoading = false;

  // Subscription prompt categories
  List<String> _subCategories = [];
  bool _subLoading = false;

  // Privacy — product analytics consent (#606)
  bool _analyticsConsent = analytics.consentGranted;
  bool _analyticsLoading = false;

  // Privacy — background sync preference (#493; mobile only)
  bool _backgroundSync = true;

  // "Always file X as Y?" after a correction. Local, off by default (friction).
  bool _ruleOffers = CategorizationRulesService.offersEnabledDefault;
  bool _backgroundSyncLoading = false;

  // Security — biometric app-unlock + quick-login (mobile only)
  /// Whether the AI profile quiz has already been answered — flips the button to
  /// "Retake Quiz" so a completed quiz reads as saved rather than untouched.
  bool _quizCompleted = false;

  bool _appLockEnabled = false;
  bool _quickLoginEnabled = false;
  bool _biometricAvailable = false;
  bool _biometricLoading = false;
  String _biometricTypeName = 'Biometric';
  // OAuth-only accounts (Google/Apple, no local password) can't use password-
  // replay quick login. Default true so email/password accounts aren't gated out
  // if the profile hasn't loaded yet.
  bool _hasPassword = true;

  // Entrance animation
  late AnimationController _fadeController;
  late Animation<double> _fadeAnim;

  final _aiService = AIService();

  @override
  void initState() {
    super.initState();
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnim = CurvedAnimation(parent: _fadeController, curve: Curves.easeOut);
    _fadeController.forward();
    _loadSettings();
    _loadBackgroundSyncPref();
    _loadRuleOffersPref();
    _loadBiometricSettings();
    _loadQuizCompletion();
  }

  /// Drives the quiz button's label. Without it the row reads "Take Quiz" even
  /// after you've answered, so there's nothing to confirm the responses saved.
  /// Non-critical: on failure the label stays "Take Quiz", which is the safe
  /// wording either way.
  Future<void> _loadQuizCompletion() async {
    try {
      final completed = await onboardingQuizService.hasCompletedQuiz();
      if (mounted && completed == true) {
        setState(() => _quizCompleted = true);
      }
    } catch (_) {}
  }

  @override
  void dispose() {
    _fadeController.dispose();
    super.dispose();
  }

  // ── Load all settings ─────────────────────────────────

  Future<void> _loadSettings() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        userService.getAiSettings(),
        userService.getBudgetSettings(),
        userService.getSubscriptionPromptSettings(),
      ]);
      if (!mounted) return;
      final ai = results[0] as AiSettings;
      final budget = results[1] as BudgetSettings;
      final sub = results[2] as SubscriptionPromptSettings;
      setState(() {
        _aiMode = ai.aiInteractionMode;
        _aiMemoryEnabled = ai.aiMemoryEnabled;
        _aiAutonomyLevel = ai.aiAutonomyLevel;
        _excludePending = budget.excludePendingFromBudget;
        _reattributeOnAssoc = budget.reattributeOnAccountAssociationChange;
        _subCategories = List<String>.from(sub.promptCategories);
        _isLoading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _isLoading = false;
        _error = 'Failed to load settings';
      });
    }
  }

  // ── AI settings actions ───────────────────────────────

  Future<void> _toggleAiMode() async {
    final newMode = _aiMode == 'Quick' ? 'Clarifying' : 'Quick';
    final prev = _aiMode;
    setState(() {
      _aiMode = newMode;
      _aiLoading = true;
    });
    try {
      await _aiService.updatePreferences(aiInteractionMode: newMode);
      await userService.updateAiSettings(
        AiSettings(
          aiInteractionMode: newMode,
          aiMemoryEnabled: _aiMemoryEnabled,
          aiAutonomyLevel: _aiAutonomyLevel,
        ),
      );
    } catch (_) {
      if (mounted) {
        setState(() => _aiMode = prev);
        showAppToast(context, 'Failed to update AI mode', type: AppToastType.error);
      }
    }
    if (mounted) setState(() => _aiLoading = false);
  }

  Future<void> _toggleAiMemory() async {
    final newVal = !_aiMemoryEnabled;
    final prev = _aiMemoryEnabled;
    setState(() {
      _aiMemoryEnabled = newVal;
      _aiLoading = true;
    });
    try {
      await userService.updateAiSettings(
        AiSettings(
          aiInteractionMode: _aiMode,
          aiMemoryEnabled: newVal,
          aiAutonomyLevel: _aiAutonomyLevel,
        ),
      );
    } catch (_) {
      if (mounted) {
        setState(() => _aiMemoryEnabled = prev);
        showAppToast(context, 'Failed to update AI memory', type: AppToastType.error);
      }
    }
    if (mounted) setState(() => _aiLoading = false);
  }

  /// AI Control Spectrum (#412): persist the chosen autonomy level.
  Future<void> _setAutonomyLevel(String level) async {
    if (level == _aiAutonomyLevel) return;
    final prev = _aiAutonomyLevel;
    setState(() {
      _aiAutonomyLevel = level;
      _aiLoading = true;
    });
    try {
      await userService.updateAiSettings(
        AiSettings(
          aiInteractionMode: _aiMode,
          aiMemoryEnabled: _aiMemoryEnabled,
          aiAutonomyLevel: level,
        ),
      );
    } catch (_) {
      if (mounted) {
        setState(() => _aiAutonomyLevel = prev);
        showAppToast(context, 'Failed to update AI control level',
            type: AppToastType.error);
      }
    }
    if (mounted) setState(() => _aiLoading = false);
  }

  // Three levels are exposed today. "Manager" (fully autonomous: auto-categorize, weekly
  // brief) is reserved in the backend enum and will appear once that proactive layer ships.
  static const List<({String value, String title, String desc})> _autonomyLevels = [
    (value: 'Manual', title: 'Manual', desc: 'AI stays silent. You handle everything yourself.'),
    (value: 'Coach', title: 'Coach', desc: 'AI surfaces optional insights. You take all actions.'),
    (value: 'Assistant', title: 'Assistant', desc: 'AI proactively suggests next steps for you to approve.'),
  ];

  // ── Budget actions ────────────────────────────────────

  Future<void> _togglePending() async {
    final newVal = !_excludePending;
    final prev = _excludePending;
    setState(() {
      _excludePending = newVal;
      _budgetLoading = true;
    });
    try {
      await userService.updateBudgetSettings(BudgetSettings(
        excludePendingFromBudget: newVal,
        reattributeOnAccountAssociationChange: _reattributeOnAssoc,
      ));
    } catch (_) {
      if (mounted) {
        setState(() => _excludePending = prev);
        showAppToast(context, 'Failed to update budget settings',
            type: AppToastType.error);
      }
    }
    if (mounted) setState(() => _budgetLoading = false);
  }

  Future<void> _toggleReattribute() async {
    final newVal = !_reattributeOnAssoc;
    final prev = _reattributeOnAssoc;
    setState(() {
      _reattributeOnAssoc = newVal;
      _budgetLoading = true;
    });
    try {
      await userService.updateBudgetSettings(BudgetSettings(
        excludePendingFromBudget: _excludePending,
        reattributeOnAccountAssociationChange: newVal,
      ));
    } catch (_) {
      if (mounted) {
        setState(() => _reattributeOnAssoc = prev);
        showAppToast(context, 'Failed to update budget settings',
            type: AppToastType.error);
      }
    }
    if (mounted) setState(() => _budgetLoading = false);
  }

  // ── Subscription category actions ─────────────────────

  Future<void> _toggleSubCategory(String cat) async {
    final prev = List<String>.from(_subCategories);
    setState(() {
      if (_subCategories.contains(cat)) {
        _subCategories.remove(cat);
      } else {
        _subCategories.add(cat);
      }
      _subLoading = true;
    });
    try {
      await userService.updateSubscriptionPromptSettings(
        SubscriptionPromptSettings(promptCategories: _subCategories),
      );
    } catch (_) {
      if (mounted) {
        setState(() => _subCategories = prev);
        showAppToast(context, 'Failed to update categories', type: AppToastType.error);
      }
    }
    if (mounted) setState(() => _subLoading = false);
  }

  // ── Email preferences sheet ───────────────────────────

  Future<void> _showEmailPreferencesSheet() async {
    await showModalBottomSheet(
      context: context,
      backgroundColor: _sheetBg,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => const _EmailPreferencesSheet(),
    );
  }

  // ── Logout ────────────────────────────────────────────

  Future<void> _confirmLogout() async {
    final confirmed = await showModalBottomSheet<bool>(
      context: context,
      backgroundColor: _sheetBg,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _sheetHandle(),
            const SizedBox(height: 20),
            Text(AppLocalizations.of(ctx).setSignOut,
                style: GoogleFonts.sora(
                    fontSize: 18, fontWeight: FontWeight.w700, color: _textGhost)),
            const SizedBox(height: 12),
            Text(AppLocalizations.of(ctx).setSignOutConfirm,
                style: GoogleFonts.sora(fontSize: 14, color: _textMuted)),
            const SizedBox(height: 24),
            Row(
              children: [
                Expanded(
                  child: _GlassBtn(
                    label: 'Cancel',
                    onTap: () => Navigator.pop(ctx, false),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _SolidBtn(
                    label: 'Sign Out',
                    color: _expense,
                    onTap: () => Navigator.pop(ctx, true),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );
    if (confirmed == true && mounted) {
      context.read<AuthProvider>().logout();
      goToMarketingHome(context);
    }
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
          _GlassBtn(label: 'Retry', onTap: _loadSettings),
        ],
      ),
    );
  }

  Widget _buildContent() {
    final user = context.watch<AuthProvider>().user;

    return RefreshIndicator(
      color: _accent,
      backgroundColor: _sheetBg,
      onRefresh: _loadSettings,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(24, 24, 24, 120),
        children: [
          // Header
          Text(
            AppLocalizations.of(context).setTitle,
            style: GoogleFonts.instrumentSerif(
              fontSize: 28,
              fontWeight: FontWeight.w400,
              color: _textGhost,
            ),
          ),
          const SizedBox(height: 24),

          // User profile card
          _buildProfileCard(user),
          const SizedBox(height: 16),

          // AI assistant. Hidden when the server says AI is off (nothing to consent to or
          // configure here); the provider row stays for an admin or a self-hosted server so
          // they can see why, and what to set.
          ListenableBuilder(
            listenable: aiAvailability,
            builder: (context, _) {
              final isAdmin = user != null &&
                  SubscriptionTierExtension.fromString(user.subscriptionTier) ==
                      SubscriptionTier.admin;
              final selfHost = aiAvailability.status?.edition == 'selfhost';
              return Column(
                children: [
                  if (!aiAvailability.aiOff) ...[
                    _buildAiSection(),
                    const SizedBox(height: 16),
                    _GlassCard(
                      child: _SettingLink(
                        icon: Icons.data_usage_rounded,
                        title: AppLocalizations.of(context).aiUsageTitle,
                        subtitle: AppLocalizations.of(context).aiUsageLinkSubtitle,
                        onTap: () => context.push('/settings/ai-usage'),
                      ),
                    ),
                    const SizedBox(height: 16),
                  ],
                  if (isAdmin || selfHost) ...[
                    _GlassCard(
                      child: _SettingLink(
                        icon: Icons.memory_rounded,
                        title: AppLocalizations.of(context).aiSetTitle,
                        subtitle: aiAvailability.aiOff
                            ? AppLocalizations.of(context).aiSetLinkSubtitleOff
                            : AppLocalizations.of(context).aiSetLinkSubtitleOn,
                        onTap: () => context.push('/settings/ai'),
                      ),
                    ),
                    const SizedBox(height: 16),
                  ],
                ],
              );
            },
          ),

          // Budget settings
          _buildBudgetSection(),
          const SizedBox(height: 16),

          // Subscription tracking
          _buildSubscriptionTrackingSection(),
          const SizedBox(height: 16),

          // Appearance (background bubble density)
          _buildAppearanceSection(),
          const SizedBox(height: 16),

          // Push notifications (#190) — mobile-only, hidden until Firebase is configured
          if (!kIsWeb && PushNotificationService.isConfigured) ...[
            _buildPushSection(),
            const SizedBox(height: 16),
          ],

          // Email notifications
          _buildEmailSection(),
          const SizedBox(height: 16),

          // Privacy & data (analytics consent)
          _buildPrivacySection(),
          const SizedBox(height: 16),

          // Security — biometric unlock + quick login (mobile only; local_auth
          // has no web implementation, and only surface it where biometrics work).
          if (!kIsWeb && _biometricAvailable) ...[
            _buildSecuritySection(),
            const SizedBox(height: 16),
          ],

          // Account settings
          _buildAccountSection(),
          const SizedBox(height: 24),

          // Sign out
          _buildSignOutButton(),
        ],
      ),
    );
  }

  // ── Profile card ──────────────────────────────────────

  Widget _buildProfileCard(dynamic user) {
    if (user == null) return const SizedBox.shrink();

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              // Avatar
              Container(
                width: 52,
                height: 52,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  gradient: LinearGradient(
                    colors: [_accent, _accent.withValues(alpha: 0.6)],
                  ),
                  border: Border.all(color: _accent.withValues(alpha: 0.3)),
                ),
                child: Center(
                  child: Text(
                    '${(user.firstName as String?)?.substring(0, 1) ?? ''}${(user.lastName as String?)?.substring(0, 1) ?? ''}'
                        .toUpperCase(),
                    style: GoogleFonts.sora(
                      fontSize: 18,
                      fontWeight: FontWeight.w700,
                      color: _textGhost,
                    ),
                  ),
                ),
              ),
              const SizedBox(width: 16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      (user.fullName ?? user.email ?? '').toString(),
                      style: GoogleFonts.sora(
                        fontSize: 16,
                        fontWeight: FontWeight.w700,
                        color: _textGhost,
                      ),
                      overflow: TextOverflow.ellipsis,
                      maxLines: 1,
                    ),
                    const SizedBox(height: 2),
                    Text(
                      (user.email ?? '').toString(),
                      style: GoogleFonts.sora(
                          fontSize: 13, color: _textMuted),
                      overflow: TextOverflow.ellipsis,
                      maxLines: 1,
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          // View Profile action \u2014 bottom-right so the email/name above can
          // use the full card width and stay single-line on narrow phones.
          Align(
            alignment: Alignment.centerRight,
            child: _SmallAction(
              label: 'View Profile',
              onTap: () => context.push('/profile'),
            ),
          ),
        ],
      ),
    );
  }

  // ── AI section ────────────────────────────────────────

  Widget _buildAiSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(icon: Icons.auto_awesome, title: 'AI Assistant'),
          const SizedBox(height: 16),

          // App Store 5.1.2(i): explicit, revocable permission to share data with
          // third-party AI (Google Gemini). Server-enforced; this is the switch.
          Builder(builder: (context) {
            // On only after an explicit, current grant (same rule on every platform).
            context.watch<AuthProvider>();
            final granted = hasAiConsent(context);
            return _SettingToggle(
              title: 'AI features (Google Gemini)',
              subtitle: granted
                  ? 'On. Your transactions, accounts, plans and chat messages are sent to '
                      'Google Gemini for AI chat, insights, charts and categorization. '
                      'Turn off any time.'
                  : 'Off. Nothing is sent to Google Gemini. Turn on to use AI chat, '
                      'insights, charts, and auto-categorize.',
              value: granted,
              onChanged: (v) async {
                if (v) {
                  await showAiConsentSheet(context);
                } else {
                  await userService.setAiConsent(false);
                  if (context.mounted) {
                    await context.read<AuthProvider>().refreshUser();
                  }
                }
              },
            );
          }),
          const SizedBox(height: 16),

          // Opt-in: ask "Always file X as Y?" after a correction. Off by default
          // because a question after every disagreement with the AI is friction
          // on the one action that should be instant; the learned-habit shortcut
          // picks the pattern up regardless. Local preference, no server call.
          _SettingToggle(
            title: AppLocalizations.of(context).setRuleOffersTitle,
            subtitle: AppLocalizations.of(context).setRuleOffersSubtitle,
            value: _ruleOffers,
            onChanged: _toggleRuleOffers,
          ),
          const SizedBox(height: 12),
          _SettingLink(
            icon: Icons.rule_rounded,
            title: AppLocalizations.of(context).rulesTitle,
            subtitle: AppLocalizations.of(context).rulesLinkSubtitle,
            onTap: () => context.push('/settings/rules'),
          ),
          const SizedBox(height: 12),
          _SettingLink(
            icon: Icons.receipt_long_rounded,
            title: AppLocalizations.of(context).rcptTitle,
            subtitle: AppLocalizations.of(context).rcptLinkSubtitle,
            onTap: () => context.push('/settings/receipts'),
          ),
          const SizedBox(height: 16),

          // AI Control Spectrum (#412) — how much the AI does for you.
          Text(AppLocalizations.of(context).setAiControl,
              style: GoogleFonts.sora(
                  fontSize: 14, fontWeight: FontWeight.w600, color: _textGhost)),
          const SizedBox(height: 2),
          Text(AppLocalizations.of(context).setAiControlSubtitle,
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
          const SizedBox(height: 12),
          ..._autonomyLevels.map((lvl) {
            final selected = _aiAutonomyLevel == lvl.value;
            return Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: GestureDetector(
                onTap: _aiLoading ? null : () => _setAutonomyLevel(lvl.value),
                behavior: HitTestBehavior.opaque,
                child: AnimatedContainer(
                  duration: const Duration(milliseconds: 180),
                  padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                  decoration: BoxDecoration(
                    color: selected
                        ? _accent.withValues(alpha: 0.12)
                        : Colors.transparent,
                    borderRadius: BorderRadius.circular(10),
                    border: Border.all(
                      color: selected ? _accent.withValues(alpha: 0.5) : _cardBorder,
                    ),
                  ),
                  child: Row(
                    children: [
                      Icon(
                        selected
                            ? Icons.radio_button_checked
                            : Icons.radio_button_unchecked,
                        size: 18,
                        color: selected ? _accent : _textMuted,
                      ),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(lvl.title,
                                style: GoogleFonts.sora(
                                    fontSize: 13,
                                    fontWeight: FontWeight.w600,
                                    color: selected ? _textGhost : _textGhost)),
                            const SizedBox(height: 1),
                            Text(lvl.desc,
                                style: GoogleFonts.sora(
                                    fontSize: 11, color: _textMuted)),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            );
          }),
          const SizedBox(height: 8),
          const Divider(color: _cardBorder, height: 1),
          const SizedBox(height: 16),

          // Interaction mode
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(AppLocalizations.of(context).setInteractionMode,
                        style: GoogleFonts.sora(
                            fontSize: 14, fontWeight: FontWeight.w600, color: _textGhost)),
                    const SizedBox(height: 2),
                    Text(
                      _aiMode == 'Quick'
                          ? AppLocalizations.of(context).setAiModeQuickDesc
                          : AppLocalizations.of(context).setAiModeClarifyingDesc,
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                    ),
                  ],
                ),
              ),
              _ModeToggle(
                value: _aiMode,
                options: const ['Quick', 'Clarifying'],
                onChanged: (_) => _toggleAiMode(),
                loading: _aiLoading,
              ),
            ],
          ),
          const SizedBox(height: 16),
          const Divider(color: _cardBorder, height: 1),
          const SizedBox(height: 16),

          // AI memory
          _SettingToggle(
            title: 'AI Memory',
            subtitle: 'Allow AI to remember context from past conversations',
            value: _aiMemoryEnabled,
            onChanged: (_) => _toggleAiMemory(),
          ),
          const SizedBox(height: 16),
          const Divider(color: _cardBorder, height: 1),
          const SizedBox(height: 16),

          // AI Profile Quiz
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(AppLocalizations.of(context).setAiProfileQuiz,
                        style: GoogleFonts.sora(
                            fontSize: 14, fontWeight: FontWeight.w600, color: _textGhost)),
                    const SizedBox(height: 2),
                    Text(
                        _quizCompleted
                            ? AppLocalizations.of(context).setQuizCompletedDesc
                            : AppLocalizations.of(context).setQuizTakeDesc,
                        style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                  ],
                ),
              ),
              _SmallAction(
                label: _quizCompleted ? 'Retake Quiz' : 'Take Quiz',
                // Refresh on return: taking it for the first time should flip the
                // label without needing the settings page rebuilt from scratch.
                onTap: () =>
                    context.push('/ai-quiz').then((_) => _loadQuizCompletion()),
              ),
            ],
          ),
        ],
      ),
    );
  }

  // ── Budget section ────────────────────────────────────

  Widget _buildBudgetSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(icon: Icons.tune_outlined, title: 'Budget Settings'),
          const SizedBox(height: 16),
          _SettingToggle(
            title: 'Exclude Pending Transactions',
            subtitle: 'Only count posted transactions toward budgets',
            value: _excludePending,
            onChanged: (_) => _togglePending(),
            loading: _budgetLoading,
          ),
          const SizedBox(height: 8),
          _SettingToggle(
            title: 'Move existing spend when an account changes owner',
            subtitle:
                'When you change which member an account feeds, also reattribute '
                'that account\'s existing transactions to the new budget. Off by '
                'default — normally only future transactions are affected.',
            value: _reattributeOnAssoc,
            onChanged: (_) => _toggleReattribute(),
            loading: _budgetLoading,
          ),
          const SizedBox(height: 8),
          // Local display preference (SharedPreferences) — no server round-trip.
          ValueListenableBuilder<bool>(
            valueListenable: TickerPreferences.liveCountUpTicker,
            builder: (context, enabled, _) => _SettingToggle(
              title: 'Live count-up ticker',
              subtitle:
                  'Watch count-up balances grow in real time, a fraction of a '
                  'cent at a time. Display only: your actual allowance still '
                  'lands at midnight.',
              value: enabled,
              onChanged: (v) => TickerPreferences.setLiveCountUpTicker(v),
            ),
          ),
        ],
      ),
    );
  }

  // ── Privacy & data (product analytics consent, #606) ──

  Future<void> _toggleAnalytics() async {
    final next = !_analyticsConsent;
    setState(() {
      _analyticsConsent = next;
      _analyticsLoading = true;
    });
    if (next) {
      await analytics.grantConsent();
    } else {
      await analytics.revokeConsent();
    }
    if (mounted) setState(() => _analyticsLoading = false);
  }

  Future<void> _loadRuleOffersPref() async {
    final enabled = await categorizationRulesService.areOffersEnabled();
    if (mounted) setState(() => _ruleOffers = enabled);
  }

  Future<void> _toggleRuleOffers(bool enabled) async {
    setState(() => _ruleOffers = enabled);
    await categorizationRulesService.setOffersEnabled(enabled);
  }

  Future<void> _loadBackgroundSyncPref() async {
    final enabled = await isBackgroundSyncEnabled();
    if (mounted) setState(() => _backgroundSync = enabled);
  }

  Future<void> _toggleBackgroundSync() async {
    final next = !_backgroundSync;
    setState(() {
      _backgroundSync = next;
      _backgroundSyncLoading = true;
    });
    await setBackgroundSyncEnabled(next);
    if (mounted) setState(() => _backgroundSyncLoading = false);
  }

  Widget _buildPrivacySection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(
              icon: Icons.privacy_tip_outlined, title: 'Privacy & Data'),
          const SizedBox(height: 16),
          _SettingToggle(
            title: 'Product analytics',
            subtitle:
                'Share anonymous usage analytics to help us improve ${AppBrand.name}. '
                'No session recording, no ads, never tracked across other apps or '
                'sites. Off keeps analytics fully disabled.',
            value: _analyticsConsent,
            onChanged: (_) => _toggleAnalytics(),
            loading: _analyticsLoading,
          ),
          // Background sync only exists on mobile (workmanager); web is
          // network-only, so the switch would be meaningless there.
          if (!kIsWeb) ...[
            const SizedBox(height: 12),
            _SettingToggle(
              title: 'Background sync',
              subtitle:
                  'Refresh your data and sync offline changes every ~30 minutes '
                  'while the app is closed. Off means data updates only when '
                  'you open the app.',
              value: _backgroundSync,
              onChanged: (_) => _toggleBackgroundSync(),
              loading: _backgroundSyncLoading,
            ),
          ],
          const SizedBox(height: 16),
          _SettingLink(
            icon: Icons.download_rounded,
            title: AppLocalizations.of(context).exportDownloadTitle,
            subtitle: AppLocalizations.of(context).exportDownloadSubtitle,
            onTap: _showDataExportSheet,
          ),
          const SizedBox(height: 12),
          _SettingLink(
            icon: Icons.upload_file_rounded,
            title: AppLocalizations.of(context).impTitle,
            subtitle: AppLocalizations.of(context).impSettingsSubtitle,
            onTap: () => context.push('/settings/import'),
          ),
          const SizedBox(height: 12),
          _SettingLink(
            icon: Icons.cloud_outlined,
            title: AppLocalizations.of(context).cloudTitle,
            // A household member on a self-hosted server: linking and add-ons are the owner's.
            subtitle: ServerEdition.current.value.isSelfHost &&
                    !(serverAdminOf(context) ?? true)
                ? AppLocalizations.of(context).ownerManagesThis
                : AppLocalizations.of(context).cloudSettingsSubtitle,
            onTap: () => context.push('/settings/cloud'),
          ),
          // Access tokens for MCP tools (Open WebUI). Only a self-hosted server issues them.
          if (ServerEdition.current.value.isSelfHost) ...[
            const SizedBox(height: 12),
            _SettingLink(
              key: const Key('settingsAiTools'),
              icon: Icons.key_outlined,
              title: AppLocalizations.of(context).ctkSettingsTitle,
              subtitle: AppLocalizations.of(context).ctkSettingsSubtitle,
              onTap: () => context.push('/connect'),
            ),
          ],
        ],
      ),
    );
  }

  Future<void> _showDataExportSheet() async {
    await showModalBottomSheet(
      context: context,
      backgroundColor: _sheetBg,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => const DataExportSheet(),
    );
  }

  // ── Security section (biometric unlock + quick login; mobile only) ────

  Future<void> _loadBiometricSettings() async {
    if (kIsWeb) return;
    try {
      final available = await biometricService.canCheckBiometrics();
      final typeName = await biometricService.getBiometricTypeName();
      final appLock = await biometricService.isAppLockEnabled();
      final quickLogin = await biometricService.isBiometricLoginEnabled();
      // Whether this account has a local password (quick login replays one, so
      // it's only offered to accounts that have one). Cached/offline-safe.
      bool hasPassword = true;
      try {
        hasPassword = (await userService.getProfile()).hasPassword;
      } catch (_) {
        // Keep the safe default (true) — a load failure shouldn't strip quick
        // login from an email/password user.
      }
      if (!mounted) return;
      setState(() {
        _biometricAvailable = available;
        _biometricTypeName = typeName;
        _appLockEnabled = appLock;
        _quickLoginEnabled = quickLogin;
        _hasPassword = hasPassword;
      });
    } catch (_) {
      // Leave the section hidden if biometrics can't be probed.
    }
  }

  /// App-unlock toggle. Enabling first requires a successful biometric check
  /// (handled inside [BiometricService.setAppLockEnabled]); if it's cancelled we
  /// revert the switch so it never shows "on" without protection.
  Future<void> _toggleAppLock() async {
    final next = !_appLockEnabled;
    setState(() {
      _appLockEnabled = next;
      _biometricLoading = true;
    });
    final result = await biometricService.setAppLockEnabled(next);
    if (!mounted) return;
    setState(() {
      _appLockEnabled = result; // authoritative — reverts if enabling failed
      _biometricLoading = false;
    });
    if (next && !result) {
      showAppToast(context, '$_biometricTypeName check cancelled',
          type: AppToastType.error);
    }
  }

  /// Quick-login toggle. Disabling clears the stored credentials and the "declined"
  /// flag so the post-login offer can reappear. Enabling needs the password (not
  /// held anywhere), so it opens a confirm dialog that verifies via the auth
  /// service before enrolling — see [_promptEnableQuickLogin].
  Future<void> _toggleQuickLogin() async {
    if (_quickLoginEnabled) {
      setState(() => _biometricLoading = true);
      await biometricService.disableBiometricLogin();
      await biometricService.setBiometricOfferDeclined(false);
      if (!mounted) return;
      setState(() {
        _quickLoginEnabled = false;
        _biometricLoading = false;
      });
      return;
    }
    await _promptEnableQuickLogin();
  }

  /// Enable quick-login from Settings, where we don't have the password in hand.
  /// A small confirm dialog collects it, the auth service verifies it via
  /// [AuthService.verifyPassword], then the secure enrolment stores it (which also
  /// prompts for a biometric).
  ///
  /// verifyPassword re-confirms identity against the CURRENT session without
  /// minting throwaway tokens or burning the 'auth' rate limit (the old path
  /// called login(), which did both). Because it never starts a fresh login it
  /// also can't trigger an MFA challenge, so this now works for MFA accounts too —
  /// their stored password + the biometric prompt serve as the strong factor.
  Future<void> _promptEnableQuickLogin() async {
    final user = context.read<AuthProvider>().user;
    if (user == null) return;
    // Guard: OAuth-only accounts have no BuoyAI password to verify or store, so we
    // must never prompt them for one. The UI already hides the toggle in this case;
    // this is defense-in-depth.
    if (!_hasPassword) return;

    final password = await showDialog<String>(
      context: context,
      builder: (_) => _PasswordConfirmDialog(typeName: _biometricTypeName),
    );
    if (password == null || password.isEmpty || !mounted) return;

    setState(() => _biometricLoading = true);
    try {
      final valid = await authService.verifyPassword(password);
      if (!valid) {
        if (mounted) {
          showAppToast(context, 'Incorrect password',
              type: AppToastType.error);
        }
        return;
      }
      final ok = await biometricService.enableBiometricLogin(
        email: user.email,
        password: password,
        userId: user.id,
      );
      if (!mounted) return;
      setState(() => _quickLoginEnabled = ok);
      showAppToast(
        context,
        ok
            ? '$_biometricTypeName quick login enabled'
            : "Couldn't enable $_biometricTypeName login",
        type: ok ? AppToastType.success : AppToastType.error,
      );
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Incorrect password', type: AppToastType.error);
      }
    } finally {
      if (mounted) setState(() => _biometricLoading = false);
    }
  }

  Widget _buildSecuritySection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(
              icon: Icons.fingerprint, title: 'Security'),
          const SizedBox(height: 16),
          _SettingToggle(
            title: '$_biometricTypeName unlock',
            subtitle: 'Require $_biometricTypeName every time you open ${AppBrand.name}.',
            value: _appLockEnabled,
            onChanged: (_) => _toggleAppLock(),
            loading: _biometricLoading,
          ),
          const SizedBox(height: 12),
          // Quick login replays a stored password, so it only applies to accounts
          // that have one. For Google/Apple sign-in there's no BuoyAI password —
          // show a short explanation instead of a toggle that can't work (and never
          // ask an OAuth user for "a password").
          if (_hasPassword)
            _SettingToggle(
              title: '$_biometricTypeName quick login',
              subtitle:
                  'Sign in with $_biometricTypeName instead of your password.',
              value: _quickLoginEnabled,
              onChanged: (_) => _toggleQuickLogin(),
              loading: _biometricLoading,
            )
          else
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 4),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(Icons.info_outline, size: 16, color: _textMuted),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context).setOauthQuickLoginNote(_biometricTypeName, _biometricTypeName),
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  // ── Appearance section (background bubble density) ────

  Widget _buildAppearanceSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(icon: Icons.bubble_chart_outlined, title: 'Appearance'),
          const SizedBox(height: 16),
          // Local display preference (SharedPreferences) — no server round-trip.
          ValueListenableBuilder<bool>(
            valueListenable: SoundPreferences.celebrationSound,
            builder: (context, enabled, _) => _SettingToggle(
              title: 'Celebration sound',
              subtitle:
                  'Play a soft chime when you unlock a badge or complete a '
                  'challenge. Milestone taps use a subtle vibration instead.',
              value: enabled,
              onChanged: (v) => SoundPreferences.setCelebrationSound(v),
            ),
          ),
          const SizedBox(height: 16),
          ValueListenableBuilder<int>(
            valueListenable: BackgroundPreferences.particleCount,
            builder: (context, count, _) {
              return Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(AppLocalizations.of(context).setBackgroundBubbles,
                                style: GoogleFonts.sora(
                                    fontSize: 14,
                                    fontWeight: FontWeight.w600,
                                    color: _textGhost)),
                            const SizedBox(height: 2),
                            Text(
                                AppLocalizations.of(context)
                                    .setBackgroundBubblesDesc,
                                style: GoogleFonts.sora(
                                    fontSize: 12, color: _textMuted)),
                          ],
                        ),
                      ),
                      const SizedBox(width: 12),
                      Text('$count',
                          style: GoogleFonts.firaCode(
                              fontSize: 14,
                              fontWeight: FontWeight.w600,
                              color: _accent)),
                    ],
                  ),
                  SliderTheme(
                    data: SliderTheme.of(context).copyWith(
                      activeTrackColor: _accent,
                      inactiveTrackColor: _cardBorder,
                      thumbColor: _accent,
                      overlayColor: _accent.withValues(alpha: 0.15),
                      trackHeight: 3,
                    ),
                    // Live-preview while dragging (write the notifier only); persist
                    // to SharedPreferences once, on release.
                    child: Slider(
                      value: count.toDouble(),
                      min: 0,
                      max: BackgroundPreferences.defaultDensity.toDouble(),
                      divisions: 20,
                      label: '$count',
                      onChanged: (v) =>
                          BackgroundPreferences.particleCount.value = v.round(),
                      onChangeEnd: (v) =>
                          BackgroundPreferences.setDensity(v.round()),
                    ),
                  ),
                ],
              );
            },
          ),
        ],
      ),
    );
  }

  // ── Subscription tracking section ─────────────────────

  Widget _buildSubscriptionTrackingSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(
            icon: Icons.subscriptions_outlined,
            title: 'Subscription Tracking',
          ),
          const SizedBox(height: 8),
          Text(
            AppLocalizations.of(context).setSubTrackingHint,
            style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
          ),
          const SizedBox(height: 14),
          if (_subLoading)
            const Padding(
              padding: EdgeInsets.only(bottom: 8),
              child: LinearProgressIndicator(
                minHeight: 2, color: _accent,
                backgroundColor: Colors.transparent),
            ),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: _allSubCategories.map((cat) {
              final selected = _subCategories.contains(cat);
              return _CategoryChip(
                label: _formatCategory(cat),
                selected: selected,
                onTap: () => _toggleSubCategory(cat),
              );
            }).toList(),
          ),
        ],
      ),
    );
  }

  String _formatCategory(String cat) {
    return cat[0].toUpperCase() + cat.substring(1);
  }

  // ── Push notifications section (#190) ─────────────────

  Widget _buildPushSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(
            icon: Icons.notifications_active_outlined,
            title: 'Push Notifications',
          ),
          const SizedBox(height: 16),
          Text(
            AppLocalizations.of(context).setPushSectionDesc,
            style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
          ),
          const SizedBox(height: 14),
          _GlassBtn(
            label: 'Notification Preferences',
            icon: Icons.tune_rounded,
            onTap: _showPushPreferencesSheet,
          ),
        ],
      ),
    );
  }

  Future<void> _showPushPreferencesSheet() async {
    await showModalBottomSheet(
      context: context,
      backgroundColor: _sheetBg,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (ctx) => const _PushPreferencesSheet(),
    );
  }

  // ── Email section ─────────────────────────────────────

  Widget _buildEmailSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(
            icon: Icons.email_outlined,
            title: 'Email Notifications',
          ),
          const SizedBox(height: 16),
          Text(
            AppLocalizations.of(context).setEmailSectionDesc,
            style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
          ),
          const SizedBox(height: 14),
          _GlassBtn(
            label: 'Email Preferences',
            icon: Icons.tune_rounded,
            onTap: _showEmailPreferencesSheet,
          ),
        ],
      ),
    );
  }

  // ── Account section ───────────────────────────────────

  Widget _buildAccountSection() {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHeader(icon: Icons.manage_accounts_rounded, title: 'Account'),
          const SizedBox(height: 16),
          _SettingLink(
            icon: Icons.person_outline,
            title: 'Profile',
            subtitle: 'Edit your account information',
            onTap: () => context.push('/profile'),
          ),
          const Divider(color: _cardBorder, height: 24),
          _SettingLink(
            icon: Icons.shield_outlined,
            title: 'Security',
            subtitle: 'MFA, passwords, trusted devices',
            onTap: () => context.push('/profile'),
          ),
          const Divider(color: _cardBorder, height: 24),
          if (ServerEdition.current.value.hasBilling) ...[
            _SettingLink(
              icon: Icons.credit_card_rounded,
              title: 'Subscription & Billing',
              subtitle: 'Manage your plan',
              onTap: () => context.push('/billing'),
            ),
            const Divider(color: _cardBorder, height: 24),
          ],
          _SettingLink(
            icon: Icons.help_outline,
            title: 'Help & Support',
            subtitle: 'contact@buoy-ai.com',
            onTap: _contactSupport,
          ),
          // Quota-free store listing: the persistent way to leave a review. The
          // timed prompt (ReviewPromptService) is the other, and it never asks
          // questions first, per Play and App Store rules.
          if (!kIsWeb &&
              (defaultTargetPlatform != TargetPlatform.iOS ||
                  InAppStoreReviewer.appStoreId.isNotEmpty)) ...[
            const Divider(color: _cardBorder, height: 24),
            _SettingLink(
              icon: Icons.star_outline,
              title: 'Rate ${AppBrand.name}',
              subtitle: 'Leave a review on the store',
              onTap: () => reviewPrompts.openStoreListing(),
            ),
          ],
        ],
      ),
    );
  }

  /// Open the user's mail client to contact support. Falls back to showing the
  /// address (mail clients aren't always available, e.g. some web browsers).
  Future<void> _contactSupport() async {
    final uri = Uri(
      scheme: 'mailto',
      path: 'contact@buoy-ai.com',
      query: 'subject=${Uri.encodeComponent('${AppBrand.name} Support Request')}',
    );
    bool launched = false;
    try {
      launched = await launchUrl(uri);
    } catch (_) {
      launched = false;
    }
    if (!launched && mounted) {
      showAppToast(context, 'Email us at contact@buoy-ai.com');
    }
  }

  // ── Sign out button ───────────────────────────────────

  Widget _buildSignOutButton() {
    return _HoverButton(
      onTap: _confirmLogout,
      child: (hovered) => AnimatedContainer(
            duration: const Duration(milliseconds: 200),
            padding: const EdgeInsets.symmetric(vertical: 16),
            decoration: BoxDecoration(
              color: hovered
                  ? _expense.withValues(alpha: 0.12)
                  : _expense.withValues(alpha: 0.06),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(
                color: hovered
                    ? _expense.withValues(alpha: 0.4)
                    : _expense.withValues(alpha: 0.2),
              ),
            ),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(Icons.logout_rounded, size: 20,
                    color: _expense.withValues(alpha: 0.9)),
                const SizedBox(width: 8),
                Text(
                  AppLocalizations.of(context).setSignOut,
                  style: GoogleFonts.sora(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: _expense,
                  ),
                ),
              ],
            ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _EmailPreferencesSheet — full email preferences modal
// ═══════════════════════════════════════════════════════════
class _EmailPreferencesSheet extends StatefulWidget {
  const _EmailPreferencesSheet();

  @override
  State<_EmailPreferencesSheet> createState() => _EmailPreferencesSheetState();
}

class _EmailPreferencesSheetState extends State<_EmailPreferencesSheet> {
  EmailPreferences? _prefs;
  bool _isLoading = true;
  bool _isSaving = false;

  @override
  void initState() {
    super.initState();
    _loadPrefs();
  }

  Future<void> _loadPrefs() async {
    try {
      final prefs = await emailPreferencesService.getPreferences();
      if (!mounted) return;
      setState(() {
        _prefs = prefs;
        _isLoading = false;
      });
    } catch (_) {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  Future<void> _save() async {
    if (_prefs == null || _isSaving) return;
    setState(() => _isSaving = true);
    try {
      final updated = await emailPreferencesService.updatePreferences(_prefs!);
      if (!mounted) return;
      setState(() {
        _prefs = updated;
        _isSaving = false;
      });
      showAppToast(context, 'Preferences saved', type: AppToastType.success);
      Navigator.pop(context);
    } catch (_) {
      if (mounted) {
        setState(() => _isSaving = false);
        showAppToast(context, 'Failed to save', type: AppToastType.error);
      }
    }
  }

  Future<void> _reset() async {
    setState(() => _prefs = EmailPreferences());
  }

  void _toggle(String key) {
    if (_prefs == null) return;
    setState(() {
      _prefs = _prefs!.copyWith(
        plaidConnectionIssues: key == 'plaidConnectionIssues'
            ? !_prefs!.plaidConnectionIssues
            : null,
        plaidReconnectionSuccess: key == 'plaidReconnectionSuccess'
            ? !_prefs!.plaidReconnectionSuccess
            : null,
        subscriptionChanges: key == 'subscriptionChanges'
            ? !_prefs!.subscriptionChanges
            : null,
        trialExpirationWarnings: key == 'trialExpirationWarnings'
            ? !_prefs!.trialExpirationWarnings
            : null,
        budgetAlerts:
            key == 'budgetAlerts' ? !_prefs!.budgetAlerts : null,
        savingsGoalMilestones: key == 'savingsGoalMilestones'
            ? !_prefs!.savingsGoalMilestones
            : null,
        largeTransactionAlerts: key == 'largeTransactionAlerts'
            ? !_prefs!.largeTransactionAlerts
            : null,
        monthlySummary:
            key == 'monthlySummary' ? !_prefs!.monthlySummary : null,
        featureAnnouncements: key == 'featureAnnouncements'
            ? !_prefs!.featureAnnouncements
            : null,
        tipsAndBestPractices: key == 'tipsAndBestPractices'
            ? !_prefs!.tipsAndBestPractices
            : null,
        productUpdates:
            key == 'productUpdates' ? !_prefs!.productUpdates : null,
        promotionalOffers: key == 'promotionalOffers'
            ? !_prefs!.promotionalOffers
            : null,
        badgeEarned: key == 'badgeEarned' ? !_prefs!.badgeEarned : null,
        milestoneReached:
            key == 'milestoneReached' ? !_prefs!.milestoneReached : null,
        streakNotifications: key == 'streakNotifications'
            ? !_prefs!.streakNotifications
            : null,
      );
    });
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.85,
      maxChildSize: 0.95,
      minChildSize: 0.5,
      expand: false,
      builder: (ctx, scrollCtrl) {
        return Column(
          children: [
            // Header
            Padding(
              padding: const EdgeInsets.fromLTRB(24, 12, 24, 0),
              child: Column(
                children: [
                  _sheetHandle(),
                  const SizedBox(height: 16),
                  Row(
                    children: [
                      Text(
                        AppLocalizations.of(context).setEmailPreferences,
                        style: GoogleFonts.sora(
                          fontSize: 18,
                          fontWeight: FontWeight.w700,
                          color: _textGhost,
                        ),
                      ),
                      const Spacer(),
                      _SmallAction(
                        label: 'Reset',
                        color: _textMuted,
                        onTap: _reset,
                      ),
                      const SizedBox(width: 16),
                      _SolidBtn(
                        label: _isSaving ? 'Saving...' : 'Save',
                        color: _income,
                        onTap: _isSaving ? null : _save,
                      ),
                    ],
                  ),
                  const SizedBox(height: 16),
                  const Divider(color: _cardBorder, height: 1),
                ],
              ),
            ),
            // Body
            Expanded(
              child: _isLoading
                  ? const Center(
                      child: CircularProgressIndicator(color: _accent))
                  : _prefs == null
                      ? Center(
                          child: Text(
                              AppLocalizations.of(context)
                                  .setFailedLoadPreferences,
                              style: GoogleFonts.sora(
                                  fontSize: 14, color: _textMuted)))
                      : ListView(
                          controller: scrollCtrl,
                          padding: const EdgeInsets.fromLTRB(24, 16, 24, 24),
                          children: [
                            // Security — always on
                            _PrefsGroup(
                              title: 'Security',
                              subtitle: 'These cannot be disabled',
                              children: [
                                _PrefItem(
                                  title: 'Login Alerts',
                                  enabled: true,
                                  locked: true,
                                ),
                                _PrefItem(
                                  title: 'Password Changes',
                                  enabled: true,
                                  locked: true,
                                ),
                              ],
                            ),
                            const SizedBox(height: 20),

                            // Account Activity
                            _PrefsGroup(
                              title: 'Account Activity',
                              children: [
                                _PrefItem(
                                  title: 'Plaid Connection Issues',
                                  enabled: _prefs!.plaidConnectionIssues,
                                  onChanged: () =>
                                      _toggle('plaidConnectionIssues'),
                                ),
                                _PrefItem(
                                  title: 'Plaid Reconnection Success',
                                  enabled: _prefs!.plaidReconnectionSuccess,
                                  onChanged: () =>
                                      _toggle('plaidReconnectionSuccess'),
                                ),
                                _PrefItem(
                                  title: 'Subscription Changes',
                                  enabled: _prefs!.subscriptionChanges,
                                  onChanged: () =>
                                      _toggle('subscriptionChanges'),
                                ),
                                _PrefItem(
                                  title: 'Trial Expiration Warnings',
                                  enabled: _prefs!.trialExpirationWarnings,
                                  onChanged: () =>
                                      _toggle('trialExpirationWarnings'),
                                ),
                              ],
                            ),
                            const SizedBox(height: 20),

                            // Financial Insights
                            _PrefsGroup(
                              title: 'Financial Insights',
                              children: [
                                _PrefItem(
                                  title: 'Budget Alerts',
                                  enabled: _prefs!.budgetAlerts,
                                  onChanged: () => _toggle('budgetAlerts'),
                                ),
                                _PrefItem(
                                  title: 'Savings Goal Milestones',
                                  enabled: _prefs!.savingsGoalMilestones,
                                  onChanged: () =>
                                      _toggle('savingsGoalMilestones'),
                                ),
                                _PrefItem(
                                  title: 'Large Transaction Alerts',
                                  enabled: _prefs!.largeTransactionAlerts,
                                  onChanged: () =>
                                      _toggle('largeTransactionAlerts'),
                                ),
                                _PrefItem(
                                  title: 'Monthly Summary',
                                  enabled: _prefs!.monthlySummary,
                                  onChanged: () => _toggle('monthlySummary'),
                                ),
                              ],
                            ),
                            const SizedBox(height: 20),

                            // Achievements
                            _PrefsGroup(
                              title: 'Achievements',
                              children: [
                                _PrefItem(
                                  title: 'Badge Earned',
                                  enabled: _prefs!.badgeEarned,
                                  onChanged: () => _toggle('badgeEarned'),
                                ),
                                _PrefItem(
                                  title: 'Milestone Reached',
                                  enabled: _prefs!.milestoneReached,
                                  onChanged: () =>
                                      _toggle('milestoneReached'),
                                ),
                                _PrefItem(
                                  title: 'Streak Notifications',
                                  enabled: _prefs!.streakNotifications,
                                  onChanged: () =>
                                      _toggle('streakNotifications'),
                                ),
                              ],
                            ),
                            const SizedBox(height: 20),

                            // Marketing
                            _PrefsGroup(
                              title: 'Marketing & Updates',
                              children: [
                                _PrefItem(
                                  title: 'Feature Announcements',
                                  enabled: _prefs!.featureAnnouncements,
                                  onChanged: () =>
                                      _toggle('featureAnnouncements'),
                                ),
                                _PrefItem(
                                  title: 'Tips & Best Practices',
                                  enabled: _prefs!.tipsAndBestPractices,
                                  onChanged: () =>
                                      _toggle('tipsAndBestPractices'),
                                ),
                                _PrefItem(
                                  title: 'Product Updates',
                                  enabled: _prefs!.productUpdates,
                                  onChanged: () =>
                                      _toggle('productUpdates'),
                                ),
                                _PrefItem(
                                  title: 'Promotional Offers',
                                  enabled: _prefs!.promotionalOffers,
                                  onChanged: () =>
                                      _toggle('promotionalOffers'),
                                ),
                              ],
                            ),
                          ],
                        ),
            ),
          ],
        );
      },
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  Shared UI components
// ═══════════════════════════════════════════════════════════

Widget _sheetHandle() => Container(
      width: 40,
      height: 4,
      decoration: BoxDecoration(
        color: _textMuted.withValues(alpha: 0.3),
        borderRadius: BorderRadius.circular(2),
      ),
    );

/// Glass card wrapper
// ═══════════════════════════════════════════════════════════
//  _PushPreferencesSheet — push notification preferences modal (#190)
// ═══════════════════════════════════════════════════════════
class _PushPreferencesSheet extends StatefulWidget {
  const _PushPreferencesSheet();

  @override
  State<_PushPreferencesSheet> createState() => _PushPreferencesSheetState();
}

class _PushPreferencesSheetState extends State<_PushPreferencesSheet> {
  PushPreferences? _prefs;
  bool _isLoading = true;
  bool _isSaving = false;

  /// Whether the OS actually permits notifications. Distinct from
  /// [_prefs.pushEnabled], which is only our server-side preference and
  /// defaults to on — so without this the sheet showed every category enabled
  /// while the OS was silently dropping everything.
  bool _osPermissionGranted = true;

  @override
  void initState() {
    super.initState();
    _loadPrefs();
  }

  Future<void> _loadPrefs() async {
    final results = await Future.wait([
      pushPreferencesService.getPreferences(),
      pushNotificationService.hasOsPermission(),
    ]);
    if (!mounted) return;
    setState(() {
      _prefs = results[0] as PushPreferences;
      _osPermissionGranted = results[1] as bool;
      _isLoading = false;
    });
  }

  Future<void> _save() async {
    if (_prefs == null || _isSaving) return;
    setState(() => _isSaving = true);
    try {
      // Turning the master toggle on is the natural moment to make sure the OS-level
      // permission + device registration actually exist (no-op when already granted).
      var granted = _osPermissionGranted;
      if (_prefs!.pushEnabled) {
        granted = await pushNotificationService.requestPermissionAndRegister();
      }
      final updated = await pushPreferencesService.updatePreferences(_prefs!);
      if (!mounted) return;
      setState(() {
        _prefs = updated;
        _osPermissionGranted = granted;
        _isSaving = false;
      });
      // Saying "saved" when the OS just refused the permission would repeat the
      // exact lie this sheet used to tell: preferences stored, nothing deliverable.
      if (_prefs!.pushEnabled && !granted) {
        showAppToast(
          context,
          'Saved, but your phone is still blocking notifications — enable them '
          'for ${AppBrand.name} in Android settings.',
          type: AppToastType.neutral,
        );
      } else {
        showAppToast(context, 'Preferences saved', type: AppToastType.success);
      }
      Navigator.pop(context);
    } catch (_) {
      if (mounted) {
        setState(() => _isSaving = false);
        showAppToast(context, 'Failed to save', type: AppToastType.error);
      }
    }
  }

  TimeOfDay _timeOf(int minutes) =>
      TimeOfDay(hour: minutes ~/ 60, minute: minutes % 60);

  Future<void> _pickQuietTime({required bool isStart}) async {
    if (_prefs == null) return;
    final current = _timeOf(isStart
        ? _prefs!.quietHoursStartMinutes
        : _prefs!.quietHoursEndMinutes);
    final picked = await showTimePicker(context: context, initialTime: current);
    if (picked == null || !mounted) return;
    final minutes = picked.hour * 60 + picked.minute;
    setState(() {
      _prefs = isStart
          ? _prefs!.copyWith(quietHoursStartMinutes: minutes)
          : _prefs!.copyWith(quietHoursEndMinutes: minutes);
    });
  }

  Widget _quietTimeRow(String label, int minutes, VoidCallback onTap) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: Row(
        children: [
          Expanded(
            child: Text(label,
                style: GoogleFonts.sora(fontSize: 14, color: _textGhost)),
          ),
          TextButton(
            onPressed: onTap,
            child: Text(
              _timeOf(minutes).format(context),
              style: GoogleFonts.firaCode(
                  fontSize: 14, fontWeight: FontWeight.w600, color: _accent),
            ),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.85,
      maxChildSize: 0.95,
      minChildSize: 0.5,
      expand: false,
      builder: (ctx, scrollCtrl) {
        return Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(24, 12, 24, 0),
              child: Column(
                children: [
                  _sheetHandle(),
                  const SizedBox(height: 16),
                  Row(
                    children: [
                      Text(
                        AppLocalizations.of(context).setPushNotifications,
                        style: GoogleFonts.sora(
                          fontSize: 18,
                          fontWeight: FontWeight.w700,
                          color: _textGhost,
                        ),
                      ),
                      const Spacer(),
                      _SolidBtn(
                        label: _isSaving ? 'Saving...' : 'Save',
                        color: _income,
                        onTap: _isSaving ? null : _save,
                      ),
                    ],
                  ),
                  const SizedBox(height: 16),
                  const Divider(color: _cardBorder, height: 1),
                ],
              ),
            ),
            Expanded(
              child: _isLoading
                  ? const Center(
                      child: CircularProgressIndicator(color: _accent))
                  : _prefs == null
                      ? Center(
                          child: Text(
                              AppLocalizations.of(context)
                                  .setFailedLoadPreferences,
                              style: GoogleFonts.sora(
                                  fontSize: 14, color: _textMuted)))
                      : ListView(
                          controller: scrollCtrl,
                          padding: const EdgeInsets.fromLTRB(24, 16, 24, 24),
                          children: [
                            // Surface the OS permission FIRST when it's missing.
                            // Every switch below is a server-side preference that
                            // defaults to on, so with the system permission denied
                            // this sheet read as "notifications are working" while
                            // nothing could be delivered — the state that kept the
                            // owner's device silent for weeks with no way to tell.
                            if (!_osPermissionGranted) ...[
                              Container(
                                padding: const EdgeInsets.all(16),
                                decoration: BoxDecoration(
                                  color: const Color(0xFFF59E0B)
                                      .withValues(alpha: 0.12),
                                  borderRadius: BorderRadius.circular(12),
                                  border: Border.all(
                                      color: const Color(0xFFF59E0B)
                                          .withValues(alpha: 0.4)),
                                ),
                                child: Row(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    const Icon(Icons.notifications_off_outlined,
                                        size: 18, color: Color(0xFFF59E0B)),
                                    const SizedBox(width: 12),
                                    Expanded(
                                      child: Text(
                                        AppLocalizations.of(context)
                                            .setOsBlockingNotifications,
                                        style: GoogleFonts.sora(
                                            fontSize: 12.5,
                                            height: 1.45,
                                            color: _textGhost),
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                              const SizedBox(height: 20),
                            ],
                            _PrefsGroup(
                              title: 'Push Alerts',
                              subtitle: _prefs!.registeredDeviceCount > 0
                                  ? '${_prefs!.registeredDeviceCount} device'
                                      '${_prefs!.registeredDeviceCount == 1 ? '' : 's'} registered'
                                  : 'No devices registered yet — saving with '
                                      'alerts on will register this one',
                              children: [
                                _PrefItem(
                                  title: 'Enable Push Notifications',
                                  enabled: _prefs!.pushEnabled,
                                  onChanged: () => setState(() => _prefs =
                                      _prefs!.copyWith(
                                          pushEnabled: !_prefs!.pushEnabled)),
                                ),
                              ],
                            ),
                            const SizedBox(height: 20),
                            _PrefsGroup(
                              title: 'Categories',
                              children: [
                                _PrefItem(
                                  title: 'Bill Reminders',
                                  enabled: _prefs!.billReminders,
                                  onChanged: () => setState(() => _prefs =
                                      _prefs!.copyWith(
                                          billReminders:
                                              !_prefs!.billReminders)),
                                ),
                                _PrefItem(
                                  title: 'Budget Alerts',
                                  enabled: _prefs!.budgetAlerts,
                                  onChanged: () => setState(() => _prefs =
                                      _prefs!.copyWith(
                                          budgetAlerts:
                                              !_prefs!.budgetAlerts)),
                                ),
                                _PrefItem(
                                  title: 'Bank Connection Issues',
                                  enabled: _prefs!.connectionIssues,
                                  onChanged: () => setState(() => _prefs =
                                      _prefs!.copyWith(
                                          connectionIssues:
                                              !_prefs!.connectionIssues)),
                                ),
                                _PrefItem(
                                  title: 'Subscription Detection',
                                  enabled: _prefs!.subscriptionDetection,
                                  onChanged: () => setState(() => _prefs =
                                      _prefs!.copyWith(
                                          subscriptionDetection: !_prefs!
                                              .subscriptionDetection)),
                                ),
                                _PrefItem(
                                  title: 'Transaction Review Requests',
                                  enabled: _prefs!.transactionReview,
                                  onChanged: () => setState(() => _prefs =
                                      _prefs!.copyWith(
                                          transactionReview:
                                              !_prefs!.transactionReview)),
                                ),
                              ],
                            ),
                            const SizedBox(height: 20),
                            _PrefsGroup(
                              title: 'Quiet Hours',
                              subtitle:
                                  'Alerts during this window are held and '
                                  'delivered once it ends',
                              children: [
                                _PrefItem(
                                  title: 'Enable Quiet Hours',
                                  enabled: _prefs!.quietHoursEnabled,
                                  onChanged: () => setState(() => _prefs =
                                      _prefs!.copyWith(
                                          quietHoursEnabled:
                                              !_prefs!.quietHoursEnabled)),
                                ),
                                if (_prefs!.quietHoursEnabled) ...[
                                  _quietTimeRow(
                                      'Start',
                                      _prefs!.quietHoursStartMinutes,
                                      () => _pickQuietTime(isStart: true)),
                                  _quietTimeRow(
                                      'End',
                                      _prefs!.quietHoursEndMinutes,
                                      () => _pickQuietTime(isStart: false)),
                                ],
                              ],
                            ),
                          ],
                        ),
            ),
          ],
        );
      },
    );
  }
}

class _GlassCard extends StatelessWidget {
  final Widget child;
  const _GlassCard({required this.child});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: _cardBorder),
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

/// Section header
class _SectionHeader extends StatelessWidget {
  final IconData icon;
  final String title;

  const _SectionHeader({required this.icon, required this.title});

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, size: 20, color: _accent),
        const SizedBox(width: 10),
        Text(title,
            style: GoogleFonts.sora(
                fontSize: 16, fontWeight: FontWeight.w700, color: _textGhost)),
      ],
    );
  }
}

/// Toggle setting with title + subtitle
class _SettingToggle extends StatelessWidget {
  final String title;
  final String subtitle;
  final bool value;
  final ValueChanged<bool> onChanged;
  final bool loading;

  const _SettingToggle({
    required this.title,
    required this.subtitle,
    required this.value,
    required this.onChanged,
    this.loading = false,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title,
                  style: GoogleFonts.sora(
                      fontSize: 14, fontWeight: FontWeight.w600, color: _textGhost)),
              const SizedBox(height: 2),
              Text(subtitle,
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
            ],
          ),
        ),
        if (loading)
          const SizedBox(
            width: 24,
            height: 24,
            child: CircularProgressIndicator(strokeWidth: 2, color: _accent),
          )
        else
          Switch(
            value: value,
            onChanged: onChanged,
            activeThumbColor: _accent,
            activeTrackColor: _accent.withValues(alpha: 0.3),
            inactiveThumbColor: _textMuted,
            inactiveTrackColor: _textMuted.withValues(alpha: 0.2),
          ),
      ],
    );
  }
}

/// AI mode toggle (Quick ↔ Clarifying)
class _ModeToggle extends StatelessWidget {
  final String value;
  final List<String> options;
  final ValueChanged<String> onChanged;
  final bool loading;

  const _ModeToggle({
    required this.value,
    required this.options,
    required this.onChanged,
    this.loading = false,
  });

  @override
  Widget build(BuildContext context) {
    if (loading) {
      return const SizedBox(
        width: 24,
        height: 24,
        child: CircularProgressIndicator(strokeWidth: 2, color: _accent),
      );
    }
    return Container(
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: _cardBorder),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: options.map((opt) {
          final selected = opt == value;
          return GestureDetector(
            onTap: () => onChanged(opt),
            child: AnimatedContainer(
              duration: const Duration(milliseconds: 200),
              padding:
                  const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
              decoration: BoxDecoration(
                color: selected
                    ? _accent.withValues(alpha: 0.15)
                    : Colors.transparent,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(
                opt,
                style: GoogleFonts.sora(
                  fontSize: 12,
                  fontWeight: selected ? FontWeight.w700 : FontWeight.w500,
                  color: selected ? _accent : _textMuted,
                ),
              ),
            ),
          );
        }).toList(),
      ),
    );
  }
}

/// Navigation setting link
class _SettingLink extends StatefulWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;

  const _SettingLink({
    super.key,
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
  });

  @override
  State<_SettingLink> createState() => _SettingLinkState();
}

class _SettingLinkState extends State<_SettingLink> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Row(
          children: [
            Icon(widget.icon, size: 20,
                color: _hovered ? _accent : _textMuted),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(widget.title,
                      style: GoogleFonts.sora(
                          fontSize: 14,
                          fontWeight: FontWeight.w600,
                          color: _hovered ? _textGhost : _textGhost.withValues(alpha: 0.8))),
                  Text(widget.subtitle,
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                ],
              ),
            ),
            Icon(Icons.chevron_right_rounded, size: 20,
                color: _hovered ? _accent : _textMuted.withValues(alpha: 0.5)),
          ],
        ),
      ),
    );
  }
}

/// Category chip for subscription tracking
class _CategoryChip extends StatefulWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;

  const _CategoryChip({
    required this.label,
    required this.selected,
    required this.onTap,
  });

  @override
  State<_CategoryChip> createState() => _CategoryChipState();
}

class _CategoryChipState extends State<_CategoryChip> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding:
              const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
          decoration: BoxDecoration(
            color: widget.selected
                ? _accent.withValues(alpha: 0.15)
                : _hovered
                    ? _cardBg
                    : Colors.transparent,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: widget.selected
                  ? _accent.withValues(alpha: 0.4)
                  : _cardBorder,
            ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (widget.selected)
                const Padding(
                  padding: EdgeInsets.only(right: 6),
                  child: Icon(Icons.check, size: 14, color: _accent),
                ),
              Text(
                widget.label,
                style: GoogleFonts.sora(
                  fontSize: 12,
                  fontWeight:
                      widget.selected ? FontWeight.w600 : FontWeight.w500,
                  color: widget.selected ? _accent : _textMuted,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Email preferences group
class _PrefsGroup extends StatelessWidget {
  final String title;
  final String? subtitle;
  final List<Widget> children;

  const _PrefsGroup({
    required this.title,
    this.subtitle,
    required this.children,
  });

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(title,
            style: GoogleFonts.sora(
                fontSize: 15, fontWeight: FontWeight.w700, color: _textGhost)),
        if (subtitle != null) ...[
          const SizedBox(height: 2),
          Text(subtitle!,
              style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
        ],
        const SizedBox(height: 12),
        ...children,
      ],
    );
  }
}

/// Single email preference toggle item
class _PrefItem extends StatelessWidget {
  final String title;
  final bool enabled;
  final bool locked;
  final VoidCallback? onChanged;

  const _PrefItem({
    required this.title,
    required this.enabled,
    this.locked = false,
    this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: Row(
        children: [
          Expanded(
            child: Text(title,
                style: GoogleFonts.sora(fontSize: 13, color: _textGhost)),
          ),
          if (locked)
            Container(
              padding:
                  const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
              decoration: BoxDecoration(
                color: _income.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(6),
              ),
              child: Text(AppLocalizations.of(context).setAlwaysOn,
                  style: GoogleFonts.sora(
                      fontSize: 10,
                      fontWeight: FontWeight.w600,
                      color: _income)),
            )
          else
            Switch(
              value: enabled,
              onChanged: onChanged != null ? (_) => onChanged!() : null,
              activeThumbColor: _accent,
              activeTrackColor: _accent.withValues(alpha: 0.3),
              inactiveThumbColor: _textMuted,
              inactiveTrackColor: _textMuted.withValues(alpha: 0.2),
            ),
        ],
      ),
    );
  }
}

/// Glass button
class _GlassBtn extends StatefulWidget {
  final String label;
  final IconData? icon;
  final VoidCallback? onTap;

  const _GlassBtn({required this.label, this.icon, this.onTap});

  @override
  State<_GlassBtn> createState() => _GlassBtnState();
}

class _GlassBtnState extends State<_GlassBtn> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          decoration: BoxDecoration(
            color: _hovered ? _accent.withValues(alpha: 0.15) : _cardBg,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color:
                  _hovered ? _accent.withValues(alpha: 0.4) : _cardBorder,
            ),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            mainAxisSize: MainAxisSize.min,
            children: [
              if (widget.icon != null) ...[
                Icon(widget.icon, size: 18, color: _accent),
                const SizedBox(width: 8),
              ],
              Text(widget.label,
                  style: GoogleFonts.sora(
                      fontSize: 13, fontWeight: FontWeight.w600, color: _accent)),
            ],
          ),
        ),
      ),
    );
  }
}

/// Solid action button
class _SolidBtn extends StatefulWidget {
  final String label;
  final Color color;
  final VoidCallback? onTap;

  const _SolidBtn({required this.label, required this.color, this.onTap});

  @override
  State<_SolidBtn> createState() => _SolidBtnState();
}

class _SolidBtnState extends State<_SolidBtn> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 10),
          decoration: BoxDecoration(
            color: _hovered
                ? widget.color.withValues(alpha: 0.9)
                : widget.color.withValues(alpha: 0.8),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Center(
            child: Text(widget.label,
                style: GoogleFonts.sora(
                    fontSize: 13, fontWeight: FontWeight.w600, color: Colors.white)),
          ),
        ),
      ),
    );
  }
}

/// Small text action
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
        child: Text(widget.label,
            style: GoogleFonts.sora(
                fontSize: 12,
                fontWeight: FontWeight.w600,
                color: _hovered ? c : c.withValues(alpha: 0.7),
                decoration: _hovered ? TextDecoration.underline : null,
                decorationColor: c)),
      ),
    );
  }
}

/// Generic hover wrapper
class _HoverButton extends StatefulWidget {
  final Widget Function(bool hovered) child;
  final VoidCallback onTap;

  const _HoverButton({required this.child, required this.onTap});

  @override
  State<_HoverButton> createState() => _HoverButtonState();
}

class _HoverButtonState extends State<_HoverButton> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: widget.child(_hovered),
      ),
    );
  }
}

/// Password-confirm dialog used to enable biometric quick-login from Settings
/// (where the password isn't already in hand). Pops the entered password, or
/// null on cancel.
class _PasswordConfirmDialog extends StatefulWidget {
  final String typeName;
  const _PasswordConfirmDialog({required this.typeName});

  @override
  State<_PasswordConfirmDialog> createState() => _PasswordConfirmDialogState();
}

class _PasswordConfirmDialogState extends State<_PasswordConfirmDialog> {
  final _controller = TextEditingController();
  bool _obscure = true;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Dialog(
      backgroundColor: _sheetBg,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(20)),
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(AppLocalizations.of(context).setConfirmYourPassword,
                style: GoogleFonts.sora(
                    fontSize: 18,
                    fontWeight: FontWeight.w700,
                    color: _textGhost)),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context)
                  .setEnterPasswordToEnable(widget.typeName),
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
            ),
            const SizedBox(height: 20),
            TextField(
              controller: _controller,
              obscureText: _obscure,
              autofocus: true,
              style: GoogleFonts.sora(fontSize: 15, color: _textGhost),
              onSubmitted: (v) => Navigator.of(context).pop(v),
              decoration: InputDecoration(
                hintText: 'Password',
                hintStyle: GoogleFonts.sora(
                    fontSize: 15, color: _textMuted.withValues(alpha: 0.6)),
                filled: true,
                fillColor: _cardBg,
                suffixIcon: IconButton(
                  icon: Icon(
                    _obscure
                        ? Icons.visibility_outlined
                        : Icons.visibility_off_outlined,
                    size: 20,
                    color: _textMuted,
                  ),
                  onPressed: () => setState(() => _obscure = !_obscure),
                ),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(12),
                  borderSide: const BorderSide(color: _cardBorder),
                ),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(12),
                  borderSide: const BorderSide(color: _cardBorder),
                ),
                focusedBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(12),
                  borderSide:
                      BorderSide(color: _accent.withValues(alpha: 0.5)),
                ),
              ),
            ),
            const SizedBox(height: 20),
            Row(
              children: [
                Expanded(
                  child: _GlassBtn(
                    label: 'Cancel',
                    onTap: () => Navigator.of(context).pop(),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _SolidBtn(
                    label: 'Enable',
                    color: _accent,
                    onTap: () => Navigator.of(context).pop(_controller.text),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
