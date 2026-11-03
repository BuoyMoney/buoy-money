import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';
import '../l10n/generated/app_localizations.dart';
import '../providers/auth_provider.dart';
import '../models/auth_models.dart';
import '../widgets/cinematic/rollover_display.dart';
import '../services/dashboard_service.dart';
import '../services/ai_service.dart';
import '../services/ai_status_service.dart';
import '../services/notification_service.dart';
import '../services/savings_service.dart';
import '../models/dashboard_models.dart';
import '../models/savings_models.dart';
import '../theme/app_theme.dart';
import '../utils/formatters.dart';
import '../utils/pull_to_refresh.dart';
import '../models/cash_flow.dart';
import '../models/subscription_models.dart';
import '../services/push_notification_service.dart';
import '../services/subscriptions_service.dart';
import '../services/transactions_service.dart';
import '../widgets/app_toast.dart';
import '../widgets/dashboard/section_header.dart';
import '../widgets/dashboard/stat_card.dart';
import '../widgets/dashboard/summary_cards.dart';
import '../widgets/verify_email_dialog.dart';
import '../widgets/ai_consent_sheet.dart';
import '../widgets/owner_setup_checklist.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF); // ~5% white — more transparent for bubble visibility
const _cardBorder = Color(0x14FFFFFF); // ~8% white
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);

// Layout breakpoints & dimensions
const _wideBreakpoint = 900.0;
const _tripleRowCardHeight = 400.0;

class DashboardPage extends StatefulWidget {
  const DashboardPage({super.key});

  @override
  State<DashboardPage> createState() => _DashboardPageState();
}

class _DashboardPageState extends State<DashboardPage>
    with SingleTickerProviderStateMixin {
  final ScrollController _scrollController = ScrollController();

  DashboardData? _dashboardData;
  bool _isLoading = true;
  String? _error;

  List<AIInsight> _insights = [];
  bool _isLoadingInsights = false;
  // True when the user has not granted AI consent: show the enable card, call no model.
  bool _aiConsentNeeded = false;

  // Id of the insight whose agent_handoff action is currently running (for the button spinner).
  String? _runningActionInsightId;

  SavingsOverview? _savingsOverview;
  bool _isLoadingSavings = false;

  // Summary row: actual cash flow + upcoming bills.
  CashFlow? _cashFlow;
  bool _isLoadingCashFlow = false;
  List<Subscription> _subscriptions = [];
  double _subscriptionMonthlyTotal = 0;
  bool _isLoadingSubscriptions = false;

  // Notification count for bell badge
  int _unreadNotifications = 0;

  late AnimationController _fadeController;
  late Animation<double> _fadeAnimation;

  @override
  void initState() {
    super.initState();
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 800),
    );
    _fadeAnimation = CurvedAnimation(
      parent: _fadeController,
      curve: Curves.easeOut,
    );
    _fadeController.forward();
    aiAvailability.addListener(_onAiAvailabilityChanged);
    _loadData();
    _loadInsights();
    _loadSavingsOverview();
    _loadCashFlow();
    _loadSubscriptions();
    _loadBellCounts();
    // Soft asks after the first authenticated landing, not at app launch, one after the
    // other so they never stack: push notifications (#190; no-op on web), then AI data
    // sharing with Google Gemini (App Store 5.1.2(i)). Each re-asks at most every 14 days.
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      if (!mounted) return;
      await pushNotificationService.maybeShowEnablePrompt(context);
      if (!mounted) return;
      // No AI on this server: nothing to ask consent for.
      await aiAvailability.ensureLoaded();
      if (!mounted || !_insightsOn) return;
      if (await maybeShowAiConsentPrompt(context) && mounted) _loadInsights();
    });
  }

  /// False only when the server has said AI (or its insights) is off. Unknown counts as on.
  bool get _insightsOn => aiAvailability.featureOn('insights');

  void _onAiAvailabilityChanged() {
    if (mounted) setState(() {});
  }

  Future<void> _loadCashFlow() async {
    setState(() => _isLoadingCashFlow = true);
    try {
      final cf = await transactionsService.getCashFlow();
      if (mounted) setState(() { _cashFlow = cf; _isLoadingCashFlow = false; });
    } catch (_) {
      if (mounted) setState(() => _isLoadingCashFlow = false);
    }
  }

  Future<void> _loadSubscriptions() async {
    setState(() => _isLoadingSubscriptions = true);
    try {
      // Two cheap reads: the list (for the next few renewals) and the summary (monthly total).
      final results = await Future.wait([
        subscriptionsService.getSubscriptions(),
        subscriptionsService.getSubscriptionSummary(),
      ]);
      if (!mounted) return;
      setState(() {
        _subscriptions = results[0] as List<Subscription>;
        _subscriptionMonthlyTotal = (results[1] as SubscriptionSummary).monthlyTotal;
        _isLoadingSubscriptions = false;
      });
    } catch (_) {
      if (mounted) setState(() => _isLoadingSubscriptions = false);
    }
  }

  @override
  void dispose() {
    aiAvailability.removeListener(_onAiAvailabilityChanged);
    _scrollController.dispose();
    _fadeController.dispose();
    super.dispose();
  }

  /// Reload every section — the pull-to-refresh handler and the desktop refresh button both use
  /// this so a manual refresh means the same thing on every platform. Each loader owns its own
  /// error handling, so Future.wait never throws here; the returned future completes when the
  /// slowest section is done, which is what keeps the pull spinner up for the right duration.
  Future<void> _refreshAll() => Future.wait([
        _loadData(),
        _loadInsights(),
        _loadSavingsOverview(),
        _loadCashFlow(),
        _loadSubscriptions(),
        _loadBellCounts(),
      ]);

  Future<void> _loadData() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    try {
      final data = await dashboardService.getDashboardData();
      if (!mounted) return;
      setState(() {
        _dashboardData = data;
        _isLoading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _isLoading = false;
      });
    }
  }

  Future<void> _loadInsights() async {
    // AI off on this server: no consent card, no request.
    await aiAvailability.ensureLoaded();
    if (!mounted) return;
    if (!_insightsOn) {
      setState(() {
        _aiConsentNeeded = false;
        _isLoadingInsights = false;
      });
      return;
    }
    // 5.1.2(i): never call the model before explicit consent; show the enable card instead.
    if (!hasAiConsent(context)) {
      setState(() {
        _aiConsentNeeded = true;
        _isLoadingInsights = false;
      });
      return;
    }
    _aiConsentNeeded = false;
    setState(() => _isLoadingInsights = true);
    try {
      final insights = await aiService.getInsights(
        context: 'dashboard',
        maxInsights: 3,
      );
      if (!mounted) return;
      setState(() {
        _insights = insights;
        _isLoadingInsights = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _isLoadingInsights = false);
    }
  }

  Future<void> _refreshInsights() async {
    if (!await ensureAiConsent(context)) return;
    if (!mounted) return;
    _aiConsentNeeded = false;
    setState(() => _isLoadingInsights = true);
    try {
      final insights = await aiService.refreshInsights('dashboard');
      if (!mounted) return;
      setState(() {
        _insights = insights;
        _isLoadingInsights = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _isLoadingInsights = false);
    }
  }

  Future<void> _dismissInsight(String insightId) async {
    final success = await aiService.dismissInsight(insightId);
    if (!mounted) return;
    if (success) {
      setState(() => _insights.removeWhere((i) => i.id == insightId));
    }
  }

  Future<void> _loadSavingsOverview() async {
    setState(() => _isLoadingSavings = true);
    try {
      final overview = await savingsService.getOverview();
      if (!mounted) return;
      setState(() {
        _savingsOverview = overview;
        _isLoadingSavings = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _isLoadingSavings = false);
    }
  }

  Future<void> _loadBellCounts() async {
    try {
      final count = await notificationService.getUnreadCount();
      if (!mounted) return;
      setState(() => _unreadNotifications = count);
    } catch (_) {
      // Bell count is non-critical — swallow errors silently
    }
  }

  String _getGreeting() {
    final hour = DateTime.now().hour;
    if (hour < 12) return 'Good morning';
    if (hour < 17) return 'Good afternoon';
    return 'Good evening';
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: FadeTransition(
        opacity: _fadeAnimation,
        child: _isLoading
            ? _buildLoadingState()
            : (_error != null && _dashboardData == null)
                ? _buildErrorState()
                : _buildContent(),
      ),
    );
  }

  Widget _buildLoadingState() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const SizedBox(
            width: 32,
            height: 32,
            child: CircularProgressIndicator(
              strokeWidth: 2,
              color: _accent,
            ),
          ),
          const SizedBox(height: 16),
          Text(
            AppLocalizations.of(context).dashLoading,
            style: const TextStyle(color: _textMuted, fontSize: 14),
          ),
        ],
      ),
    );
  }

  Widget _buildErrorState() {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.error_outline, size: 48, color: _expense),
            const SizedBox(height: 16),
            Text(
              AppLocalizations.of(context).dashLoadFailed,
              style: GoogleFonts.sora(
                fontSize: 18,
                fontWeight: FontWeight.w600,
                color: _textGhost,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              _error ?? AppLocalizations.of(context).dashUnknownError,
              style: const TextStyle(color: _textMuted, fontSize: 13),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 24),
            _GlassButton(
              onTap: _loadData,
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Icon(Icons.refresh, size: 16, color: _accent),
                  const SizedBox(width: 8),
                  Text(AppLocalizations.of(context).dashRetry,
                      style: const TextStyle(color: _accent)),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildContent() {
    final authProvider = context.watch<AuthProvider>();
    final user = authProvider.user;
    final pendingInvitations = authProvider.pendingFamilyInvitations;
    final data = _dashboardData ?? DashboardData.empty();
    final screenWidth = MediaQuery.of(context).size.width;
    final isWide = screenWidth > _wideBreakpoint;
    final horizontalPadding = isWide ? screenWidth * 0.06 : 24.0;

    return RefreshIndicator(
      onRefresh: _refreshAll,
      color: _accent,
      backgroundColor: const Color(0xFF16161F),
      // Always scrollable so the pull gesture works even when content is short.
      child: CustomScrollView(
      controller: _scrollController,
      physics: const AlwaysScrollableScrollPhysics(),
      slivers: [
        // Top spacing
        const SliverToBoxAdapter(child: SizedBox(height: 48)),

        // Welcome header
        SliverToBoxAdapter(
          child: Padding(
            padding: EdgeInsets.symmetric(horizontal: horizontalPadding),
            child: _buildWelcomeHeader(user),
          ),
        ),

        // Email verification banner — prompt unverified users to confirm their email.
        if (user != null && !user.isEmailVerified)
          SliverToBoxAdapter(
            child: Padding(
              padding: EdgeInsets.fromLTRB(horizontalPadding, 16, horizontalPadding, 0),
              child: _EmailVerificationBanner(
                onVerify: () => showVerifyEmailDialog(context),
              ),
            ),
          ),

        // Family invitation banner
        if (pendingInvitations.isNotEmpty)
          SliverToBoxAdapter(
            child: Padding(
              padding: EdgeInsets.fromLTRB(horizontalPadding, 16, horizontalPadding, 0),
              child: _FamilyInvitationBanner(
                invitation: pendingInvitations.first,
                onAccept: () => context.go('/accept-invitation/${pendingInvitations.first.invitationToken}'),
                onDismiss: () {
                  // Dismiss the banner for this session only — do NOT decline. Tapping X
                  // should never irrevocably reject an invitation; the user declines
                  // explicitly from the family screen.
                  context.read<AuthProvider>().clearPendingInvitations();
                },
              ),
            ),
          ),
        const SliverToBoxAdapter(child: SizedBox(height: 28)),

        // Self-hosted owner only: the four setup steps (banks, AI, household, notifications). Hides itself for
        // everyone else, once done, or when hidden.
        SliverToBoxAdapter(
          child: Padding(
            padding: EdgeInsets.symmetric(horizontal: horizontalPadding),
            child: const OwnerSetupChecklist(),
          ),
        ),

        // Position: Net Worth (hero) over its Total Cash / Total Debt components. Income &
        // spending are NOT here — those are flows, owned by the Cash Flow card in the row below.
        SliverToBoxAdapter(
          child: Padding(
            padding: EdgeInsets.symmetric(horizontal: horizontalPadding),
            child: _buildPositionSection(data),
          ),
        ),
        const SliverToBoxAdapter(child: SizedBox(height: 28)),

        // Summary row: actual cash flow, upcoming bills, savings — three forward-looking cards.
        SliverToBoxAdapter(
          child: Padding(
            padding: EdgeInsets.symmetric(horizontal: horizontalPadding),
            child: _buildSummaryRow(isWide),
          ),
        ),
        const SliverToBoxAdapter(child: SizedBox(height: 28)),

        // Budget Overview (given the room) + AI Insights.
        SliverToBoxAdapter(
          child: Padding(
            padding: EdgeInsets.symmetric(horizontal: horizontalPadding),
            child: isWide
                ? _buildBudgetInsightsRow(data)
                : _buildBudgetInsightsStacked(data),
          ),
        ),
        const SliverToBoxAdapter(child: SizedBox(height: 28)),

        // Recent Transactions
        SliverToBoxAdapter(
          child: Padding(
            padding: EdgeInsets.symmetric(horizontal: horizontalPadding),
            child: _buildRecentTransactionsSection(data),
          ),
        ),
        const SliverToBoxAdapter(child: SizedBox(height: 48)),
      ],
      ),
    );
  }

  // ── Summary row (cash flow · bills · savings) ───────────

  Widget _buildSummaryRow(bool isWide) {
    final cashFlow = CashFlowCard(cashFlow: _cashFlow, loading: _isLoadingCashFlow);
    final bills = UpcomingBillsCard(
      subscriptions: _subscriptions,
      monthlyTotal: _subscriptionMonthlyTotal,
      loading: _isLoadingSubscriptions,
      onViewAll: () => context.go('/subscriptions'),
    );
    final savings = SavingsSummaryCard(
      overview: _savingsOverview,
      loading: _isLoadingSavings,
      onViewAll: () => context.go('/savings'),
      onCreateGoal: () => context.go('/savings'),
    );

    if (isWide) {
      return IntrinsicHeight(
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Expanded(child: cashFlow),
            const SizedBox(width: 16),
            Expanded(child: bills),
            const SizedBox(width: 16),
            Expanded(child: savings),
          ],
        ),
      );
    }
    return Column(
      children: [
        cashFlow,
        const SizedBox(height: 16),
        bills,
        const SizedBox(height: 16),
        savings,
      ],
    );
  }

  // ── Budget + Insights row ───────────────────────────────
  // Budget Overview gets the room it earns (flex 2); AI Insights sits beside it (flex 1).

  Widget _buildBudgetInsightsRow(DashboardData data) {
    if (!_insightsOn) return _buildBudgetOverview(data);
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(flex: 2, child: _buildBudgetOverview(data)),
        const SizedBox(width: 16),
        Expanded(flex: 1, child: _buildAIInsightsSection()),
      ],
    );
  }

  Widget _buildBudgetInsightsStacked(DashboardData data) {
    if (!_insightsOn) return _buildBudgetOverview(data);
    return Column(
      children: [
        _buildBudgetOverview(data),
        const SizedBox(height: 28),
        _buildAIInsightsSection(),
      ],
    );
  }

  // ── Welcome Header ──────────────────────────────────────

  Widget _buildWelcomeHeader(User? user) {
    // Apple (and "Hide My Email") sign-ins often provide no name, and the field
    // can arrive as an empty string rather than null — so fall through on blank,
    // not just null, otherwise the greeting renders a bare ".".
    final firstName = user?.firstName.trim() ?? '';
    final email = user?.email ?? '';
    final emailPrefix = email.contains('@') ? email.split('@').first : email;
    final displayName = firstName.isNotEmpty
        ? firstName
        : (emailPrefix.isNotEmpty ? emailPrefix : 'there');

    return Row(
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                AppLocalizations.of(context).dashGreetingComma(_getGreeting()),
                style: GoogleFonts.sora(
                  fontSize: 14,
                  color: _textMuted,
                ),
              ),
              Text(
                AppLocalizations.of(context).dashDisplayNamePeriod(displayName),
                style: GoogleFonts.instrumentSerif(
                  fontSize: 36,
                  color: _textGhost,
                ),
              ),
            ],
          ),
        ),
        _buildBellButton(),
        // Manual refresh is desktop-only — on mobile/touch the pull-to-refresh gesture on the
        // page replaces it (a mouse can't pull, so desktop keeps the button).
        if (showsManualRefresh(context)) ...[
          const SizedBox(width: 8),
          _GlassButton(
            onTap: _refreshAll,
            child: const Icon(Icons.refresh, size: 18, color: _textMuted),
          ),
        ],
      ],
    );
  }

  Widget _buildBellButton() {
    final totalBadge = _unreadNotifications;
    return _GlassButton(
      onTap: () => context.go('/notifications'),
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          // Owner feedback (2026-08-22): the 18px bell read as an afterthought and
          // the 8pt count was illegible. ~55% larger glyph, a badge big enough to
          // hold two digits at a readable size, and a dark ring so the red pill
          // separates from the glass instead of bleeding into it.
          const Icon(Icons.notifications_outlined, size: 28, color: _textGhost),
          if (totalBadge > 0)
            Positioned(
              top: -6,
              right: -8,
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 2),
                constraints: const BoxConstraints(minWidth: 20, minHeight: 20),
                decoration: BoxDecoration(
                  color: const Color(0xFFEF4444),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(color: const Color(0xFF0C0C18), width: 2),
                ),
                alignment: Alignment.center,
                child: Text(
                  totalBadge > 99 ? '99+' : '$totalBadge',
                  style: const TextStyle(
                    color: Colors.white,
                    fontSize: 11,
                    fontWeight: FontWeight.w700,
                    height: 1,
                  ),
                  textAlign: TextAlign.center,
                ),
              ),
            ),
        ],
      ),
    );
  }

  // ── Stats Grid (4 cards) ─────────────────────────────────

  /// Position / net-worth section: a full-width Net Worth hero over its two components
  /// (Total Cash, Total Debt). Flows (income / spending) deliberately live in the Cash Flow card,
  /// not here — see _buildSummaryRow. Same shape on every width: the hero reads as the headline,
  /// the pair as the breakdown, and it sits cleanly above the 3-card summary row below.
  Widget _buildPositionSection(DashboardData data) {
    final netWorth = data.totalBalance - data.totalDebt;
    return Column(
      children: [
        DashboardNetWorthCard(
          netWorth: netWorth,
          excludedCurrencyCodes: data.foreignCurrencyCodes,
          onTap: () => context.go('/accounts'),
        ),
        const SizedBox(height: 12),
        IntrinsicHeight(
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Expanded(
                child: DashboardStatCard(
                  label: 'Total Cash',
                  value: formatCurrency(data.totalBalance),
                  icon: Icons.account_balance_wallet,
                  color: _income,
                  compact: true,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: DashboardStatCard(
                  label: 'Total Debt',
                  value: formatCurrency(data.totalDebt),
                  icon: Icons.credit_card,
                  color: data.totalDebt == 0 ? _income : _expense,
                  compact: true,
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  // ── AI Insights ─────────────────────────────────────────

  /// Shown in place of insights until the user grants AI consent (5.1.2(i)).
  Widget _buildAiConsentCard() {
    return _GlassCard(
      child: Row(
        children: [
          const Icon(Icons.auto_awesome, color: _accent, size: 20),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              AppLocalizations.of(context).dashAiInsightsOff,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.4),
            ),
          ),
          TextButton(
            onPressed: () async {
              final ok = await showAiConsentSheet(context);
              if (ok && mounted) _loadInsights();
            },
            child: Text(AppLocalizations.of(context).dashTurnOn,
                style: GoogleFonts.sora(
                    fontSize: 13, fontWeight: FontWeight.w600, color: _accent)),
          ),
        ],
      ),
    );
  }

  Widget _buildAIInsightsSection() {
    final screenWidth = MediaQuery.of(context).size.width;
    final isWide = screenWidth > _wideBreakpoint;

    Widget content;
    if (_aiConsentNeeded) {
      content = _buildAiConsentCard();
    } else if (_isLoadingInsights && _insights.isEmpty) {
      content = _GlassCard(
        child: Row(
          children: [
            const SizedBox(
                width: 16,
                height: 16,
                child: CircularProgressIndicator(
                    strokeWidth: 2, color: _accent)),
            const SizedBox(width: 12),
            Expanded(
              child: Text(AppLocalizations.of(context).dashGeneratingInsights,
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
            ),
          ],
        ),
      );
    } else if (_insights.isEmpty) {
      content = _GlassCard(
        child: Row(
          children: [
            const Icon(Icons.lightbulb_outline, color: _textMuted, size: 20),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                AppLocalizations.of(context).dashNoInsightsYet,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
              ),
            ),
          ],
        ),
      );
    } else {
      content = Column(
        mainAxisSize: MainAxisSize.min,
        children: _insights.map((insight) => Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: _buildInsightCard(insight),
            )).toList(),
      );
    }

    // On wide screens (triple row), constrain height and scroll
    final insightsBody = _insights.isNotEmpty
        ? Flexible(
            child: ClipRRect(
              borderRadius: BorderRadius.circular(16),
              child: SingleChildScrollView(
                child: content,
              ),
            ),
          )
        : content;

    // Build header once, reused in both layouts
    // The trailing button's padding is stripped by DashboardSectionHeader so its glyph sits
    // flush with the section's right edge — the same treatment every section header gets, so
    // this one no longer needs to special-case it (it used to, and the others still drifted).
    final header = DashboardSectionHeader(
        title: 'AI Insights',
        icon: Icons.auto_awesome,
        trailing: IconButton(
          icon: _isLoadingInsights
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(
                      strokeWidth: 2, color: _accent),
                )
              : const Icon(Icons.refresh, size: 18, color: _textMuted),
          onPressed: _isLoadingInsights ? null : _refreshInsights,
          tooltip: 'Refresh insights',
        ));

    // On wide screens, cap the section height to prevent unbounded growth
    final wrapper = isWide
        ? SizedBox(
            height: _tripleRowCardHeight,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                header,
                const SizedBox(height: 12),
                insightsBody,
              ],
            ),
          )
        : ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: _tripleRowCardHeight),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              mainAxisSize: MainAxisSize.min,
              children: [
                header,
                const SizedBox(height: 12),
                insightsBody,
              ],
            ),
          );

    return wrapper;
  }

  // Route keywords → paths for insight text linking
  static const _routeKeywords = {
    'dashboard': '/dashboard',
    'transactions': '/transactions',
    'budgets': '/budgets',
    'savings': '/savings',
    'accounts': '/accounts',
    'subscriptions': '/subscriptions',
    // 'insights' keyword link removed — Insights page hidden for launch.
    'ai chat': '/ai-chat',
    'notifications': '/notifications',
    'profile': '/profile',
    'settings': '/settings',
    'billing': '/billing',
    'family': '/family',
  };

  /// Build a RichText widget that turns route-keyword mentions into tappable links.
  Widget _buildLinkedText(String text, {required TextStyle baseStyle}) {
    // Build a regex that matches any keyword (case-insensitive, word-boundary)
    // Sort keywords longest-first so multi-word matches take priority
    final sorted = _routeKeywords.keys.toList()
      ..sort((a, b) => b.length.compareTo(a.length));
    final pattern = RegExp(
      sorted.map((k) => '\\b${RegExp.escape(k)}\\b').join('|'),
      caseSensitive: false,
    );
    final spans = <InlineSpan>[];
    int lastEnd = 0;
    for (final m in pattern.allMatches(text)) {
      if (m.start > lastEnd) {
        spans.add(TextSpan(text: text.substring(lastEnd, m.start), style: baseStyle));
      }
      final matched = m.group(0)!;
      final route = _routeKeywords[matched.toLowerCase()]!;
      spans.add(
        WidgetSpan(
          alignment: PlaceholderAlignment.baseline,
          baseline: TextBaseline.alphabetic,
          child: GestureDetector(
            onTap: () => context.go(route),
            child: Text(
              matched,
              style: baseStyle.copyWith(
                color: _accent,
                decoration: TextDecoration.underline,
                decorationColor: _accent.withValues(alpha: 0.4),
                decorationStyle: TextDecorationStyle.dotted,
              ),
            ),
          ),
        ),
      );
      lastEnd = m.end;
    }
    if (lastEnd < text.length) {
      spans.add(TextSpan(text: text.substring(lastEnd), style: baseStyle));
    }
    if (spans.isEmpty) {
      return Text(text, style: baseStyle);
    }
    return Text.rich(TextSpan(children: spans));
  }

  Widget _buildInsightCard(AIInsight insight) {
    final priorityColor = switch (insight.priority) {
      'high' => _expense,
      'medium' => const Color(0xFFFBBF24),
      _ => _accent,
    };

    return Dismissible(
      key: Key(insight.id),
      direction: DismissDirection.endToStart,
      onDismissed: (_) => _dismissInsight(insight.id),
      background: Container(
        alignment: Alignment.centerRight,
        padding: const EdgeInsets.only(right: 20),
        decoration: BoxDecoration(
          color: _expense.withValues(alpha: 0.2),
          borderRadius: BorderRadius.circular(16),
        ),
        child: const Icon(Icons.delete_outline, color: _expense),
      ),
      child: _GlassCard(
        borderColor: priorityColor.withValues(alpha: 0.3),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: priorityColor.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(insight.icon, style: const TextStyle(fontSize: 18)),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          insight.title,
                          // Cap to 2 clean lines so the badge squeezing the title in
                          // the narrow 3-up column can't break it mid-word.
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                          style: GoogleFonts.sora(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: _textGhost,
                          ),
                        ),
                      ),
                      Container(
                        padding: const EdgeInsets.symmetric(
                            horizontal: 6, vertical: 2),
                        decoration: BoxDecoration(
                          color: priorityColor.withValues(alpha: 0.1),
                          borderRadius: BorderRadius.circular(4),
                        ),
                        child: Text(
                          insight.type.toUpperCase(),
                          style: GoogleFonts.firaCode(
                            fontSize: 9,
                            fontWeight: FontWeight.w600,
                            color: priorityColor,
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 4),
                  _buildLinkedText(
                    insight.message,
                    baseStyle: GoogleFonts.sora(fontSize: 13, color: _textMuted),
                  ),
                  if (insight.actionSuggestion != null) ...[
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        const Icon(Icons.lightbulb_outline,
                            size: 13, color: _accent),
                        const SizedBox(width: 4),
                        Expanded(
                          child: _buildLinkedText(
                            insight.actionSuggestion!,
                            baseStyle: GoogleFonts.sora(
                              fontSize: 12,
                              fontWeight: FontWeight.w500,
                              color: _accent,
                            ),
                          ),
                        ),
                      ],
                    ),
                  ],
                  if (insight.suggestedAction != null) ...[
                    const SizedBox(height: 10),
                    Align(
                      alignment: Alignment.centerLeft,
                      child: _buildSuggestedActionButton(insight),
                    ),
                  ],
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  /// Renders the insight's one-tap follow-up: navigate (go to a page) or agent_handoff
  /// (run a reversible change on the user's behalf, then offer Undo).
  Widget _buildSuggestedActionButton(AIInsight insight) {
    final action = insight.suggestedAction!;
    final busy = _runningActionInsightId == insight.id;
    final icon = action.isAgentHandoff ? Icons.auto_fix_high : Icons.arrow_forward;

    return OutlinedButton.icon(
      onPressed: busy
          ? null
          : () {
              final route = action.route;
              if (action.isNavigate &&
                  route != null &&
                  route.startsWith('/') &&
                  !route.startsWith('//')) {
                // In-app absolute paths only (defense-in-depth; the backend validates too).
                context.go(route);
              } else if (action.isAgentHandoff && action.instruction != null) {
                _runInsightHandoff(insight);
              }
            },
      icon: busy
          ? const SizedBox(
              width: 13,
              height: 13,
              child: CircularProgressIndicator(strokeWidth: 2, color: _accent),
            )
          : Icon(icon, size: 15, color: _accent),
      label: Text(
        action.label,
        style: GoogleFonts.sora(
          fontSize: 12,
          fontWeight: FontWeight.w600,
          color: _accent,
        ),
      ),
      style: OutlinedButton.styleFrom(
        side: const BorderSide(color: _accent, width: 1),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
      ),
    );
  }

  /// Runs an agent_handoff action. The tap is the confirmation; on success we show a
  /// snackbar summarizing what changed with an Undo affordance.
  Future<void> _runInsightHandoff(AIInsight insight) async {
    // The follow-up runs a Gemini agent over the user's data: consent first (5.1.2(i)).
    if (!await ensureAiConsent(context)) return;
    if (!mounted) return;
    final action = insight.suggestedAction!;
    setState(() => _runningActionInsightId = insight.id);
    try {
      final result = await aiService.actOnInsight(
        action.instruction!,
        insightId: insight.id,
      );
      if (!mounted) return;

      final canUndo = result.success && result.canUndo;
      showAppToast(
        context,
        result.summary.isNotEmpty
            ? result.summary
            : (result.success ? 'Done.' : "I couldn't complete that."),
        type: result.success ? AppToastType.success : AppToastType.error,
        actionLabel: canUndo ? 'Undo' : null,
        onAction: canUndo
            ? () async {
                final ok = await aiService.undoInsightAction(result.undoToken!);
                if (!mounted) return;
                showAppToast(
                  context,
                  ok ? 'Reverted.' : "Couldn't undo that.",
                  type: ok ? AppToastType.success : AppToastType.error,
                );
              }
            : null,
      );
    } catch (_) {
      if (!mounted) return;
      showAppToast(context, 'Something went wrong running that action.',
          type: AppToastType.error);
    } finally {
      if (mounted) setState(() => _runningActionInsightId = null);
    }
  }

  // ── Budget Overview ─────────────────────────────────────

  Widget _buildBudgetOverview(DashboardData data) {
    final percentage = data.budgetOverallPercentage ?? 0;
    final isOverBudget = percentage > 100;
    final hasBudget = data.budgetName != null;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        DashboardSectionHeader(
            title: 'Budget Overview',
            icon: Icons.tune_outlined,
            trailing: hasBudget
                ? TextButton(
                    onPressed: () => context.go('/budgets'),
                    child: Text(AppLocalizations.of(context).dashViewAll,
                        style: GoogleFonts.sora(
                            fontSize: 12,
                            color: _accent,
                            fontWeight: FontWeight.w500)),
                  )
                : null),
        const SizedBox(height: 12),
        if (!hasBudget)
          _buildEmptyState(
              icon: Icons.tune_outlined,
              title: 'No budgets yet',
              subtitle: 'Create a budget to start tracking spending',
              actionLabel: 'Create Budget',
              onAction: () => context.go('/budgets'),
            )
        else
          _GlassCard(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Header
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Expanded(
                    child: Text(
                      data.budgetName ??
                          AppLocalizations.of(context).dashBudgetFallback,
                      style: GoogleFonts.sora(
                        fontSize: 15,
                        fontWeight: FontWeight.w600,
                        color: _textGhost,
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  Container(
                    padding:
                        const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                    decoration: BoxDecoration(
                      color: (isOverBudget ? _expense : _income)
                          .withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(6),
                    ),
                    child: Text(
                      AppLocalizations.of(context)
                          .dashPercentUsed(percentage.toStringAsFixed(0)),
                      style: GoogleFonts.firaCode(
                        fontSize: 11,
                        fontWeight: FontWeight.w600,
                        color: isOverBudget ? _expense : _income,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 14),
              // Progress bar
              ClipRRect(
                borderRadius: BorderRadius.circular(4),
                child: LinearProgressIndicator(
                  value: (percentage / 100).clamp(0, 1).toDouble(),
                  backgroundColor: Colors.white.withValues(alpha: 0.06),
                  valueColor: AlwaysStoppedAnimation<Color>(
                      isOverBudget ? _expense : _accent),
                  minHeight: 6,
                ),
              ),
              const SizedBox(height: 14),
              // Spent / Remaining
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(AppLocalizations.of(context).dashSpent,
                          style: GoogleFonts.sora(
                              fontSize: 11, color: _textMuted)),
                      Text(
                        formatCurrency(data.budgetTotalSpent ?? 0),
                        style: GoogleFonts.firaCode(
                          fontSize: 15,
                          fontWeight: FontWeight.w600,
                          color: _textGhost,
                        ),
                      ),
                    ],
                  ),
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      Text(AppLocalizations.of(context).dashRemaining,
                          style: GoogleFonts.sora(
                              fontSize: 11, color: _textMuted)),
                      Text(
                        formatCurrency(data.budgetTotalRemaining ?? 0),
                        style: GoogleFonts.firaCode(
                          fontSize: 15,
                          fontWeight: FontWeight.w600,
                          color: (data.budgetTotalRemaining ?? 0) < 0
                              ? _expense
                              : _income,
                        ),
                      ),
                    ],
                  ),
                ],
              ),
              // Category breakdown
              if (data.budgetCategories.isNotEmpty) ...[
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 14),
                  child: Container(
                    height: 1,
                    color: Colors.white.withValues(alpha: 0.06),
                  ),
                ),
                Text(
                  AppLocalizations.of(context).dashTopCategories,
                  style: GoogleFonts.sora(
                    fontSize: 10,
                    letterSpacing: 1.5,
                    fontWeight: FontWeight.w600,
                    color: _textMuted,
                  ),
                ),
                const SizedBox(height: 12),
                ...data.budgetCategories
                    .map((cat) => _buildBudgetCategoryRow(cat)),
              ],
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildBudgetCategoryRow(DashboardBudgetCategory category) {
    final categoryColor = _parseColor(category.color);

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Expanded(
                child: Row(
                  children: [
                    Container(
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: categoryColor,
                        borderRadius: BorderRadius.circular(3),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Flexible(
                      child: Text(category.name,
                          overflow: TextOverflow.ellipsis,
                          style:
                              GoogleFonts.sora(fontSize: 13, color: _textGhost)),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              // Shrink-to-fit so the amounts stay on one line in the narrow 3-up
              // column (iPad/wide) instead of overflowing ~11–13px to the right.
              Flexible(
                child: FittedBox(
                  fit: BoxFit.scaleDown,
                  alignment: Alignment.centerRight,
                  child: Text(
                    '${formatCurrency(category.spent)} / ${formatCurrency(category.allocated)}',
                    maxLines: 1,
                    style: GoogleFonts.firaCode(fontSize: 11, color: _textMuted),
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 6),
          if (category.hasRollover) ...[
            Align(
              alignment: Alignment.centerRight,
              child: RolloverChip(
                  rolloverBalance: category.rolloverBalance,
                  allocated: category.allocated),
            ),
            const SizedBox(height: 6),
            RolloverSpendBar(
              allocated: category.allocated,
              spent: category.spent,
              rolloverBalance: category.rolloverBalance,
              height: 3,
            ),
          ] else
            ClipRRect(
              borderRadius: BorderRadius.circular(2),
              child: LinearProgressIndicator(
                value: (category.percentUsed / 100).clamp(0, 1),
                backgroundColor: Colors.white.withValues(alpha: 0.06),
                valueColor: AlwaysStoppedAnimation<Color>(
                  category.isOverBudget ? _expense : categoryColor,
                ),
                minHeight: 3,
              ),
            ),
        ],
      ),
    );
  }

  Color _parseColor(String hexColor) {
    try {
      final hex = hexColor.replaceAll('#', '');
      return Color(int.parse('FF$hex', radix: 16));
    } catch (e) {
      return _accent;
    }
  }

  // ── Recent Transactions ─────────────────────────────────

  Widget _buildRecentTransactionsSection(DashboardData data) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        DashboardSectionHeader(
          title: 'Recent Transactions',
          icon: Icons.receipt_long_outlined,
          count: data.recentTransactions.length,
          trailing: TextButton(
            onPressed: () => context.go('/transactions'),
            child: Text(AppLocalizations.of(context).dashViewAll,
                style: GoogleFonts.sora(
                    fontSize: 12, color: _accent, fontWeight: FontWeight.w500)),
          ),
        ),
        const SizedBox(height: 12),
        if (data.recentTransactions.isEmpty)
          _buildEmptyState(
            icon: Icons.receipt_long_outlined,
            title: 'No recent transactions',
            subtitle: 'Transactions will appear here after syncing',
          )
        else
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 420),
            child: ClipRRect(
              borderRadius: BorderRadius.circular(16),
              child: SingleChildScrollView(
                child: _GlassCard(
                  padding: EdgeInsets.zero,
                  child: Column(
                    children: data.recentTransactions.asMap().entries.map((entry) {
                      final index = entry.key;
                      final txn = entry.value;
                      final isLast = index == data.recentTransactions.length - 1;
                      return Column(
                        children: [
                          _buildTransactionRow(txn),
                          if (!isLast)
                            Padding(
                              padding: const EdgeInsets.only(left: 56),
                              child: Container(
                                height: 1,
                                color: Colors.white.withValues(alpha: 0.04),
                              ),
                            ),
                        ],
                      );
                    }).toList(),
                  ),
                ),
              ),
            ),
          ),
      ],
    );
  }

  Widget _buildTransactionRow(DashboardTransaction txn) {
    final isIncome = txn.isIncome;
    final color = isIncome ? _income : _expense;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Row(
        children: [
          Container(
            width: 36,
            height: 36,
            decoration: BoxDecoration(
              color: color.withValues(alpha: 0.12),
              borderRadius: BorderRadius.circular(10),
            ),
            child: Icon(
              isIncome ? Icons.arrow_downward : Icons.arrow_upward,
              color: color,
              size: 18,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  txn.description,
                  style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                    color: _textGhost,
                  ),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
                Text(txn.category,
                    style:
                        GoogleFonts.sora(fontSize: 11, color: _textMuted)),
              ],
            ),
          ),
          Text(
            '${isIncome ? '+' : '-'}${formatCurrency(txn.amount.abs())}',
            style: GoogleFonts.firaCode(
              fontSize: 13,
              fontWeight: FontWeight.w600,
              color: color,
            ),
          ),
        ],
      ),
    );
  }

  // ── Shared Helpers ──────────────────────────────────────

  Widget _buildEmptyState({
    required IconData icon,
    required String title,
    required String subtitle,
    String? actionLabel,
    VoidCallback? onAction,
  }) {
    return _GlassCard(
      child: Column(
        children: [
          Icon(icon, size: 40, color: _textMuted.withValues(alpha: 0.4)),
          const SizedBox(height: 12),
          Text(title,
              style: GoogleFonts.sora(
                  fontSize: 15,
                  fontWeight: FontWeight.w500,
                  color: _textGhost)),
          const SizedBox(height: 4),
          Text(subtitle,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
              textAlign: TextAlign.center),
          if (actionLabel != null && onAction != null) ...[
            const SizedBox(height: 16),
            _GlassButton(
              onTap: onAction,
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Icon(Icons.add, size: 14, color: _accent),
                  const SizedBox(width: 6),
                  Text(actionLabel,
                      style: GoogleFonts.sora(
                          fontSize: 13,
                          color: _accent,
                          fontWeight: FontWeight.w500)),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}

// ── Reusable glass widgets ──────────────────────────────────

class _GlassCard extends StatelessWidget {
  final Widget child;
  final EdgeInsetsGeometry padding;
  final Color? borderColor;

  const _GlassCard({
    required this.child,
    this.padding = const EdgeInsets.all(20),
    this.borderColor,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: padding,
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(
          color: borderColor ?? _cardBorder,
          width: 1,
        ),
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

class _GlassButton extends StatelessWidget {
  final VoidCallback onTap;
  final Widget child;

  const _GlassButton({required this.onTap, required this.child});

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Container(
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: Colors.white.withValues(alpha: 0.06),
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: Colors.white.withValues(alpha: 0.08),
            ),
          ),
          child: child,
        ),
      ),
    );
  }
}

/// Notifications Modal - Shows notifications in a bottom sheet
class _NotificationsModal extends StatefulWidget {
  final ScrollController scrollController;

  const _NotificationsModal({required this.scrollController});

  @override
  State<_NotificationsModal> createState() => _NotificationsModalState();
}

class _NotificationsModalState extends State<_NotificationsModal> {
  List<AppNotification> _notifications = [];
  bool _isLoading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadNotifications();
  }

  Future<void> _loadNotifications() async {
    try {
      final notifications = await notificationService.getNotifications();
      if (!mounted) return;
      setState(() {
        _notifications = notifications.where((n) => !n.isArchived).toList();
        _isLoading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _isLoading = false;
      });
    }
  }

  Future<void> _markAsRead(AppNotification notification) async {
    if (notification.isRead) return;
    try {
      await notificationService.markAsRead(notification.id);
      if (!mounted) return;
      setState(() {
        final index = _notifications.indexWhere((n) => n.id == notification.id);
        if (index != -1) {
          _notifications[index] = AppNotification(
            id: notification.id,
            type: notification.type,
            title: notification.title,
            message: notification.message,
            isRead: true,
            isArchived: notification.isArchived,
            createdAt: notification.createdAt,
            metadata: notification.metadata,
          );
        }
      });
    } catch (e) {
      // Silently fail
    }
  }

  Future<void> _markAllAsRead() async {
    try {
      await notificationService.markAllAsRead();
      if (!mounted) return;
      setState(() {
        _notifications = _notifications.map((n) => AppNotification(
          id: n.id,
          type: n.type,
          title: n.title,
          message: n.message,
          isRead: true,
          isArchived: n.isArchived,
          createdAt: n.createdAt,
          metadata: n.metadata,
        )).toList();
      });
    } catch (e) {
      if (!mounted) return;
      showAppToast(context, 'Failed to mark all as read', type: AppToastType.error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final unreadCount = _notifications.where((n) => !n.isRead).length;

    return Column(
      children: [
        // Handle bar
        Container(
          margin: const EdgeInsets.only(top: 8),
          width: 40,
          height: 4,
          decoration: BoxDecoration(
            color: colorScheme.onSurfaceVariant.withValues(alpha: 0.4),
            borderRadius: BorderRadius.circular(2),
          ),
        ),
        
        // Header
        Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Row(
                children: [
                  Text(
                    AppLocalizations.of(context).dashNotificationsTitle,
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                  if (unreadCount > 0) ...[
                    const SizedBox(width: 8),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                      decoration: BoxDecoration(
                        color: colorScheme.primary,
                        borderRadius: BorderRadius.circular(12),
                      ),
                      child: Text(
                        unreadCount.toString(),
                        style: TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.bold,
                          color: colorScheme.onPrimary,
                        ),
                      ),
                    ),
                  ],
                ],
              ),
              if (unreadCount > 0)
                TextButton(
                  onPressed: _markAllAsRead,
                  child: Text(AppLocalizations.of(context).dashMarkAllRead),
                ),
            ],
          ),
        ),

        const Divider(height: 1),

        // Content
        Expanded(
          child: _isLoading
              ? const Center(child: CircularProgressIndicator())
              : _error != null
                  ? Center(
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          Icon(Icons.error_outline, size: 48, color: colorScheme.error),
                          const SizedBox(height: 16),
                          Text(AppLocalizations.of(context)
                              .dashLoadNotificationsFailed),
                          const SizedBox(height: 8),
                          TextButton(
                            onPressed: _loadNotifications,
                            child:
                                Text(AppLocalizations.of(context).dashRetry),
                          ),
                        ],
                      ),
                    )
                  : _notifications.isEmpty
                      ? Center(
                          child: Column(
                            mainAxisAlignment: MainAxisAlignment.center,
                            children: [
                              Icon(
                                Icons.notifications_off_outlined,
                                size: 64,
                                color: colorScheme.onSurfaceVariant.withValues(alpha: 0.5),
                              ),
                              const SizedBox(height: 16),
                              Text(
                                AppLocalizations.of(context)
                                    .dashNoNotifications,
                                style: TextStyle(
                                  color: colorScheme.onSurfaceVariant,
                                ),
                              ),
                            ],
                          ),
                        )
                      : ListView.separated(
                          controller: widget.scrollController,
                          padding: const EdgeInsets.all(16),
                          itemCount: _notifications.length,
                          separatorBuilder: (_, __) => const SizedBox(height: 8),
                          itemBuilder: (context, index) {
                            final notification = _notifications[index];
                            return _buildNotificationCard(notification, colorScheme);
                          },
                        ),
        ),
      ],
    );
  }

  Widget _buildNotificationCard(AppNotification notification, ColorScheme colorScheme) {
    final iconData = _getNotificationIcon(notification.type);
    final iconColor = _getNotificationColor(notification.type, colorScheme);

    return Card(
      color: notification.isRead 
          ? null 
          : colorScheme.primaryContainer.withValues(alpha: 0.3),
      child: InkWell(
        onTap: () => _markAsRead(notification),
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: iconColor.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(iconData, color: iconColor, size: 20),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            notification.title,
                            style: TextStyle(
                              fontWeight: notification.isRead 
                                  ? FontWeight.normal 
                                  : FontWeight.bold,
                            ),
                          ),
                        ),
                        if (!notification.isRead)
                          Container(
                            width: 8,
                            height: 8,
                            decoration: BoxDecoration(
                              color: colorScheme.primary,
                              shape: BoxShape.circle,
                            ),
                          ),
                      ],
                    ),
                    const SizedBox(height: 4),
                    Text(
                      notification.message,
                      style: TextStyle(
                        fontSize: 13,
                        color: colorScheme.onSurfaceVariant,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      _formatTimeAgo(notification.createdAt),
                      style: TextStyle(
                        fontSize: 11,
                        color: colorScheme.onSurfaceVariant.withValues(alpha: 0.7),
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
  }

  IconData _getNotificationIcon(String type) {
    return switch (type) {
      'budget_alert' => Icons.tune,
      'savings_goal' => Icons.savings,
      'subscription' => Icons.subscriptions,
      'transaction' => Icons.receipt_long,
      'security' => Icons.security,
      'insight' => Icons.lightbulb,
      _ => Icons.notifications,
    };
  }

  Color _getNotificationColor(String type, ColorScheme colorScheme) {
    return switch (type) {
      'budget_alert' => AppTheme.expenseColor,
      'savings_goal' => AppTheme.incomeColor,
      'subscription' => colorScheme.tertiary,
      'security' => colorScheme.error,
      'insight' => AppTheme.primaryIndigo,
      _ => colorScheme.primary,
    };
  }

  String _formatTimeAgo(DateTime date) {
    final now = DateTime.now();
    final diff = now.difference(date);

    if (diff.inMinutes < 1) return 'Just now';
    if (diff.inMinutes < 60) return '${diff.inMinutes}m ago';
    if (diff.inHours < 24) return '${diff.inHours}h ago';
    if (diff.inDays < 7) return '${diff.inDays}d ago';
    return '${date.month}/${date.day}/${date.year}';
  }
}

class _FamilyInvitationBanner extends StatelessWidget {
  final PendingFamilyInvitation invitation;
  final VoidCallback onAccept;
  final VoidCallback onDismiss;

  const _FamilyInvitationBanner({
    required this.invitation,
    required this.onAccept,
    required this.onDismiss,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: _accent.withValues(alpha: 0.12),
        border: Border.all(color: _accent.withValues(alpha: 0.3)),
        borderRadius: BorderRadius.circular(14),
      ),
      padding: const EdgeInsets.all(16),
      child: Row(
        children: [
          Container(
            width: 40,
            height: 40,
            decoration: BoxDecoration(
              color: _accent.withValues(alpha: 0.2),
              shape: BoxShape.circle,
            ),
            child: const Icon(Icons.family_restroom, color: _accent, size: 22),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  AppLocalizations.of(context)
                      .dashInvitedToJoin(invitation.familyName),
                  style: GoogleFonts.sora(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: _textGhost,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  AppLocalizations.of(context).dashInvitationFrom(
                      invitation.invitedByName, invitation.invitedRole),
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                ),
              ],
            ),
          ),
          const SizedBox(width: 8),
          _GlassButton(
            onTap: onAccept,
            child: Text(
              AppLocalizations.of(context).dashView,
              style: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w600,
                color: _accent,
              ),
            ),
          ),
          const SizedBox(width: 4),
          IconButton(
            onPressed: onDismiss,
            icon: const Icon(Icons.close, size: 18, color: _textMuted),
            padding: EdgeInsets.zero,
            constraints: const BoxConstraints(minWidth: 32, minHeight: 32),
          ),
        ],
      ),
    );
  }
}

/// Non-blocking prompt for users who haven't verified their email yet.
class _EmailVerificationBanner extends StatelessWidget {
  final VoidCallback onVerify;

  const _EmailVerificationBanner({required this.onVerify});

  @override
  Widget build(BuildContext context) {
    const warning = Color(0xFFFBBF24);
    return Container(
      decoration: BoxDecoration(
        color: warning.withValues(alpha: 0.10),
        border: Border.all(color: warning.withValues(alpha: 0.30)),
        borderRadius: BorderRadius.circular(14),
      ),
      padding: const EdgeInsets.all(16),
      child: Row(
        children: [
          Container(
            width: 40,
            height: 40,
            decoration: BoxDecoration(
              color: warning.withValues(alpha: 0.18),
              shape: BoxShape.circle,
            ),
            child: const Icon(Icons.mark_email_unread_outlined,
                color: warning, size: 22),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  AppLocalizations.of(context).dashVerifyEmailTitle,
                  style: GoogleFonts.sora(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: _textGhost,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  AppLocalizations.of(context).dashVerifyEmailSubtitle,
                  style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                ),
              ],
            ),
          ),
          const SizedBox(width: 8),
          _GlassButton(
            onTap: onVerify,
            child: Text(
              AppLocalizations.of(context).dashVerify,
              style: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w600,
                color: warning,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
