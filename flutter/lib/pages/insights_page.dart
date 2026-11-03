import '../config/app_brand.dart';
import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:intl/intl.dart';
import 'package:provider/provider.dart';
import '../l10n/generated/app_localizations.dart';
import '../providers/auth_provider.dart';
import '../services/billing_service.dart';
import '../services/gamification_service.dart';
import '../services/insights_service.dart';
import '../services/reports_service.dart';
import '../services/visualization_service.dart';
import '../widgets/ai_sparkle.dart';
import '../widgets/app_toast.dart';
import '../widgets/glass_card.dart';
import '../widgets/ai/visualization_chart.dart';
import '../widgets/cinematic/achievement_celebration.dart';
import '../widgets/cinematic/share_card.dart';
import '../widgets/ai_consent_sheet.dart';

/// Readable line-length cap — full-bleed cards die on desktop (a 1,500px
/// challenge progress bar for "categorize 5 transactions" is not cinematic).
const double _contentMaxWidth = 760;

// ── VaporClinic palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _sheetBg = Color(0xFF0C0C18);
const _warning = Color(0xFFFBBF24);
const _blue = Color(0xFF60A5FA);
const _teal = Color(0xFF4ECDC4);
const _orange = Color(0xFFFF9F45);

// ── Level ladder ────────────────────────────────────────
// Mirrors backend LevelDefinitions (GamificationDTOs.cs). The names must match
// what the API sends — the previous novice/apprentice set silently fell through
// to the default styling for 5 of the 7 real levels.
class _LevelSpec {
  final String name;
  final Color color;
  final IconData icon;
  const _LevelSpec(this.name, this.color, this.icon);
}

const _levelLadder = [
  _LevelSpec('Beginner', _textMuted, Icons.eco_outlined),
  _LevelSpec('Saver', _teal, Icons.savings_outlined),
  _LevelSpec('Budgeter', _blue, Icons.tune_outlined),
  _LevelSpec('Expert', _accent, Icons.workspace_premium_outlined),
  _LevelSpec('Master', _income, Icons.military_tech_outlined),
  _LevelSpec('Champion', _orange, Icons.emoji_events_rounded),
  _LevelSpec('Legend', _warning, Icons.stars_rounded),
];

int _levelIndex(String level) {
  final i = _levelLadder
      .indexWhere((l) => l.name.toLowerCase() == level.toLowerCase());
  return i < 0 ? 0 : i;
}

// ── Insight-type colors ─────────────────────────────────
// Keys match InsightType.name (insights_service.dart) — the old
// spending/budget/savings set matched nothing the enum actually produces,
// so every type chip rendered muted gray.
Color _insightTypeColor(String type) => switch (type.toLowerCase()) {
      'tip' => _blue,
      'alert' => _expense,
      'opportunity' => _income,
      'trend' => _accent,
      'achievement' => _warning,
      _ => _textMuted,
    };

Color _priorityColor(String p) => switch (p.toLowerCase()) {
      'high' => _expense,
      'medium' => _warning,
      'low' => _income,
      _ => _textMuted,
    };

// ── Challenge type icons ────────────────────────────────
IconData _challengeIcon(String type) => switch (type.toLowerCase()) {
      'no_spend_day' => Icons.do_not_disturb_on_outlined,
      'categorize_transactions' => Icons.category_outlined,
      'review_budget' => Icons.tune_outlined,
      'save_money' => Icons.savings_outlined,
      'under_budget' => Icons.trending_down_rounded,
      _ => Icons.flag_outlined,
    };

// ── Streak heat ─────────────────────────────────────────
// The count gets literally hotter as the streak grows.
Color _streakHeat(int count) {
  if (count >= 30) return _expense;
  if (count >= 14) return _orange;
  if (count >= 7) return _warning;
  return _textGhost;
}

String _relativeTime(DateTime dt) {
  final diff = DateTime.now().difference(dt);
  if (diff.inDays <= 0) return 'today';
  if (diff.inDays == 1) return 'yesterday';
  if (diff.inDays < 7) return '${diff.inDays}d ago';
  if (diff.inDays < 30) return '${(diff.inDays / 7).floor()}w ago';
  return DateFormat.yMMMd().format(dt);
}

// ═══════════════════════════════════════════════════════════
//  InsightsPage
// ═══════════════════════════════════════════════════════════
class InsightsPage extends StatefulWidget {
  const InsightsPage({super.key});

  @override
  State<InsightsPage> createState() => _InsightsPageState();
}

class _InsightsPageState extends State<InsightsPage>
    with TickerProviderStateMixin {
  // Tab state
  int _tabIndex = 0; // 0 = Progress & Rewards, 1 = AI Insights

  // Gamification data
  GamificationDashboard? _dashboard;
  bool _gamLoading = true;
  String? _gamError;

  // AI Insights data
  List<AIInsight> _aiInsights = [];
  bool _aiLoading = false;
  bool _aiConsentNeeded = false;
  bool _aiRefreshing = false;

  // This-month spending donut (Reports API — deterministic, not AI-generated)
  List<SpendingSlice> _monthSlices = [];

  // Visualizations
  List<UserVisualization> _recentViz = [];
  List<UserVisualization> _starredViz = [];
  int _vizTabIndex = 0; // 0 = Recent, 1 = Starred
  bool _vizLoading = false;
  bool _vizGenerating = false;

  // Premium check
  bool _hasPremium = false;
  bool _premiumChecked = false;

  // Challenge-completion detection: ids already completed when last seen, so a
  // fresh completion (done elsewhere, observed here on reload) gets celebrated
  // exactly once. Null until the baseline load — never celebrate stale wins.
  Set<int>? _seenCompletedChallengeIds;

  // Entrance animation
  late AnimationController _fadeCtrl;
  late Animation<double> _fadeAnim;

  // Ambient shimmer on the level ring — only runs when there is something to
  // savor (max level) or something imminent (>=85% to the next level).
  late AnimationController _shimmerCtrl;
  bool get _shimmerActive {
    final d = _dashboard;
    if (d == null) return false;
    return d.isMaxLevel || d.levelProgress >= 0.85;
  }

  @override
  void initState() {
    super.initState();
    _fadeCtrl = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnim = CurvedAnimation(parent: _fadeCtrl, curve: Curves.easeOut);
    _shimmerCtrl = AnimationController(
      vsync: this,
      duration: const Duration(seconds: 3),
    );
    _loadAll();
  }

  @override
  void dispose() {
    _fadeCtrl.dispose();
    _shimmerCtrl.dispose();
    super.dispose();
  }

  // ── Loading ───────────────────────────────────────────

  Future<void> _loadAll() async {
    _loadGamification();
    _checkPremium();
  }

  Future<void> _loadGamification() async {
    setState(() {
      _gamLoading = true;
      _gamError = null;
    });
    try {
      // check-achievements (not plain dashboard) so badges earned anywhere in
      // the app get awarded AND celebrated here, matching the Achievements
      // page. Falls back to the read-only dashboard if the check endpoint
      // hiccups.
      GamificationDashboard dash;
      try {
        dash = await gamificationService.checkAchievements();
      } catch (_) {
        dash = await gamificationService.getDashboard();
      }
      if (!mounted) return;
      setState(() {
        _dashboard = dash;
        _gamLoading = false;
      });
      _fadeCtrl.forward(from: 0);
      if (_shimmerActive && !_shimmerCtrl.isAnimating) {
        _shimmerCtrl.repeat();
      } else if (!_shimmerActive) {
        _shimmerCtrl.stop();
      }
      await _celebrateChallengeCompletions(dash);
      await _celebrateNewAchievements(dash);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _gamLoading = false;
        _gamError = 'Failed to load progress data';
      });
      _fadeCtrl.forward(from: 0);
    }
  }

  /// A challenge completed since we last looked gets the full celebration —
  /// the moment the user did the thing we asked is the moment to pay them,
  /// not a gray checkmark.
  Future<void> _celebrateChallengeCompletions(
      GamificationDashboard dash) async {
    final completed = dash.activeChallenges
        .where((c) => c.isCompleted)
        .map((c) => c.id)
        .toSet();
    final baseline = _seenCompletedChallengeIds;
    _seenCompletedChallengeIds = completed;
    if (baseline == null || !mounted) return; // first load = baseline only
    final fresh = dash.activeChallenges
        .where((c) => c.isCompleted && !baseline.contains(c.id))
        .toList();
    if (fresh.isEmpty) return;
    await showAchievementCelebration(
      context,
      fresh
          .map((c) => AchievementCatalogItem(
                key: 'challenge_${c.challengeType}',
                title: c.title,
                description: 'Challenge complete!',
                icon: '🏁',
                category: 'challenge',
                points: c.rewardPoints,
                tier: null,
                percentEarned: 0,
                isNegative: false,
                isEarned: true,
                earnedAt: DateTime.now(),
              ))
          .toList(),
    );
  }

  /// Celebrate freshly earned badges, then mark them seen automatically —
  /// nobody should have to press a "Mark seen" button to file their dopamine.
  Future<void> _celebrateNewAchievements(GamificationDashboard dash) async {
    final newIds = dash.newAchievementIds.toSet();
    if (newIds.isEmpty || !mounted) return;
    try {
      final catalog = await gamificationService.getAchievementCatalog();
      if (!mounted) return;
      final fresh =
          catalog.where((c) => c.isEarned && newIds.contains(c.key)).toList();
      if (fresh.isNotEmpty) {
        await showAchievementCelebration(context, fresh);
      }
      await gamificationService.markAchievementsSeen(newIds.toList());
      // Clear the in-memory "new" ids too — otherwise the badge-strip glow
      // survives the celebration until the next full reload.
      if (mounted) {
        setState(() => dash.newAchievementIds.clear());
      }
    } catch (_) {
      // Celebration is a bonus, never a failure mode for the page.
    }
  }

  Future<void> _checkPremium() async {
    try {
      final canAccess = await billingService.canAccessFeature('ai_insights');
      if (!mounted) return;
      setState(() {
        _hasPremium = canAccess;
        _premiumChecked = true;
      });
      if (canAccess) _loadAIContent();
    } catch (_) {
      // API failed — fall back to local subscription tier
      if (!mounted) return;
      final user = context.read<AuthProvider>().user;
      final tier = (user?.subscriptionTier ?? '').toLowerCase();
      final localPremium = tier == 'premium' || tier == 'premiuminherited' || tier == 'admin';
      setState(() {
        _hasPremium = localPremium;
        _premiumChecked = true;
      });
      if (localPremium) _loadAIContent();
    }
  }

  /// Insights and visualizations are independent — load them concurrently
  /// (the old flow chained viz after insights and doubled the wait).
  void _loadAIContent() {
    _loadAIInsights();
    _loadVisualizations();
    _loadMonthSlices();
  }

  Future<void> _loadMonthSlices() async {
    try {
      final now = DateTime.now();
      final slices = await reportsService.getSpendingByCategory(
          start: DateTime(now.year, now.month, 1), end: now);
      if (!mounted) return;
      setState(() => _monthSlices = slices);
    } catch (_) {
      // The donut is a bonus; the charts section stands without it.
    }
  }

  /// Shown in place of AI insights until consent is granted (5.1.2(i)).
  Widget _buildAiConsentCard() {
    return GlassCard(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(AppLocalizations.of(context).insAiFeaturesOff,
                style: GoogleFonts.sora(
                    fontSize: 15,
                    fontWeight: FontWeight.w600,
                    color: const Color(0xFFF0EFF4))),
            const SizedBox(height: 6),
            Text(
              AppLocalizations.of(context).insAiConsentBody,
              style: GoogleFonts.sora(
                  fontSize: 13, color: const Color(0xFF8A8A9A), height: 1.4),
            ),
            const SizedBox(height: 12),
            TextButton(
              onPressed: () async {
                final ok = await showAiConsentSheet(context);
                if (ok && mounted) _loadAIInsights();
              },
              child: Text(AppLocalizations.of(context).insTurnOnAiFeatures,
                  style: GoogleFonts.sora(
                      fontSize: 13, fontWeight: FontWeight.w600, color: _accent)),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _loadAIInsights() async {
    if (!hasAiConsent(context)) {
      if (mounted) {
        setState(() {
          _aiConsentNeeded = true;
          _aiLoading = false;
        });
      }
      return;
    }
    _aiConsentNeeded = false;
    setState(() => _aiLoading = true);
    try {
      final insights =
          await insightsService.getInsights(context: 'insights-page', maxInsights: 10);
      if (!mounted) return;
      setState(() {
        _aiInsights = insights;
        _aiLoading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _aiLoading = false);
    }
  }

  Future<void> _refreshAIInsights() async {
    setState(() => _aiRefreshing = true);
    try {
      final insights =
          await insightsService.refreshInsights(context: 'insights-page');
      if (!mounted) return;
      setState(() {
        _aiInsights = insights;
        _aiRefreshing = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _aiRefreshing = false);
      showAppToast(context, 'Failed to refresh insights', type: AppToastType.error);
    }
  }

  // Dismissal is optimistic with a 4s undo window — the API call only fires
  // once the toast expires un-undone.
  final Map<String, Timer> _pendingInsightDismiss = {};

  void _dismissInsight(AIInsight insight) {
    final idx = _aiInsights.indexOf(insight);
    setState(() => _aiInsights.remove(insight));
    _pendingInsightDismiss.remove(insight.id)?.cancel();
    _pendingInsightDismiss[insight.id] =
        Timer(const Duration(seconds: 4), () {
      _pendingInsightDismiss.remove(insight.id);
      insightsService.dismissInsight(insight.id);
    });
    showAppToast(
      context,
      'Insight dismissed',
      actionLabel: 'Undo',
      onAction: () {
        _pendingInsightDismiss.remove(insight.id)?.cancel();
        if (!mounted) return;
        setState(() =>
            _aiInsights.insert(idx.clamp(0, _aiInsights.length), insight));
      },
    );
  }

  Future<void> _loadVisualizations() async {
    setState(() => _vizLoading = true);
    try {
      final results = await Future.wait([
        visualizationService.getVisualizations(pageSize: 20),
        visualizationService.getStarredVisualizations(pageSize: 20),
      ]);
      if (!mounted) return;
      setState(() {
        _recentViz = results[0].visualizations;
        _starredViz = results[1].visualizations;
        _vizLoading = false;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() => _vizLoading = false);
    }
  }

  Future<void> _generateVisualization() async {
    if (!await ensureAiConsent(context)) return;
    if (!mounted) return;
    setState(() => _vizGenerating = true);
    try {
      final viz = await visualizationService.generateVisualization();
      if (!mounted) return;
      setState(() {
        _recentViz.insert(0, viz);
        _vizGenerating = false;
      });
      showAppToast(context, 'Visualization generated!', type: AppToastType.success);
    } catch (_) {
      if (!mounted) return;
      setState(() => _vizGenerating = false);
      showAppToast(context, 'Failed to generate visualization',
          type: AppToastType.error);
    }
  }

  Future<void> _toggleStar(int id) async {
    final isStarred = await visualizationService.toggleStar(id);
    if (!mounted) return;
    setState(() {
      // Update in recent list
      final idx = _recentViz.indexWhere((v) => v.id == id);
      if (idx >= 0) {
        final old = _recentViz[idx];
        _recentViz[idx] = UserVisualization(
          id: old.id,
          chartType: old.chartType,
          title: old.title,
          message: old.message,
          motivationalMessage: old.motivationalMessage,
          chartData: old.chartData,
          chartConfig: old.chartConfig,
          category: old.category,
          priority: old.priority,
          confidence: old.confidence,
          isStarred: isStarred,
          isDismissed: old.isDismissed,
          viewCount: old.viewCount,
          lastViewedAt: old.lastViewedAt,
          generatedAt: old.generatedAt,
        );
      }
      // Refresh starred list
      if (isStarred) {
        if (idx >= 0 && !_starredViz.any((v) => v.id == id)) {
          _starredViz.insert(0, _recentViz[idx]);
        }
      } else {
        _starredViz.removeWhere((v) => v.id == id);
      }
    });
  }

  Future<void> _dismissVisualization(int id) async {
    setState(() {
      _recentViz.removeWhere((v) => v.id == id);
      _starredViz.removeWhere((v) => v.id == id);
    });
    await visualizationService.dismissVisualization(id);
  }

  // Gamification actions
  Future<void> _generateChallenge() async {
    try {
      final challenge = await gamificationService.generateChallenge();
      if (challenge != null && mounted) {
        setState(() => _dashboard?.activeChallenges.add(challenge));
        showAppToast(context, 'New challenge started!', type: AppToastType.success);
      }
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to generate challenge',
            type: AppToastType.error);
      }
    }
  }

  Future<void> _dismissChallenge(int id) async {
    setState(
        () => _dashboard?.activeChallenges.removeWhere((c) => c.id == id));
    try {
      await gamificationService.dismissChallenge(id);
    } catch (_) {}
  }

  // ── Build ─────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: FadeTransition(
        opacity: _fadeAnim,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Header + tabs
            Padding(
              padding: const EdgeInsets.fromLTRB(24, 24, 24, 0),
              child: Center(
                child: ConstrainedBox(
                  constraints:
                      const BoxConstraints(maxWidth: _contentMaxWidth),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(AppLocalizations.of(context).insTitle,
                          style: GoogleFonts.instrumentSerif(
                              fontSize: 28,
                              fontWeight: FontWeight.w400,
                              color: _textGhost)),
                      const SizedBox(height: 2),
                      Text(AppLocalizations.of(context).insSubtitle,
                          style: GoogleFonts.sora(
                              fontSize: 12, color: _textMuted)),
                      const SizedBox(height: 14),
                      _buildTabBar(),
                    ],
                  ),
                ),
              ),
            ),
            const SizedBox(height: 16),

            // Tab content — IndexedStack keeps each tab's scroll position
            // alive across switches instead of rebuilding from the top.
            Expanded(
              child: IndexedStack(
                index: _tabIndex,
                children: [
                  _buildProgressTab(),
                  _buildAIInsightsTab(),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  // ── Tab bar ───────────────────────────────────────────

  Widget _buildTabBar() {
    return Container(
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: _cardBorder),
      ),
      child: Row(
        children: [
          _TabItem(
            label: 'Progress & Rewards',
            selected: _tabIndex == 0,
            onTap: () => setState(() => _tabIndex = 0),
          ),
          _TabItem(
            label: 'AI Insights',
            selected: _tabIndex == 1,
            onTap: () {
              setState(() => _tabIndex = 1);
              if (_hasPremium && _aiInsights.isEmpty && !_aiLoading) {
                _loadAIContent();
              }
            },
          ),
        ],
      ),
    );
  }

  // ═════════════════════════════════════════════════════
  //  TAB 0: Progress & Rewards
  // ═════════════════════════════════════════════════════

  Widget _buildProgressTab() {
    if (_gamLoading) {
      return const Center(child: CircularProgressIndicator(color: _accent));
    }
    if (_gamError != null) {
      return Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 48,
                color: _expense.withValues(alpha: 0.6)),
            const SizedBox(height: 12),
            Text(_gamError!,
                style: GoogleFonts.sora(fontSize: 14, color: _textMuted)),
            const SizedBox(height: 16),
            _GlassBtn(label: 'Retry', onTap: _loadGamification),
          ],
        ),
      );
    }
    if (_dashboard == null) {
      return Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.emoji_events_outlined, size: 48,
                color: _textMuted.withValues(alpha: 0.4)),
            const SizedBox(height: 12),
            Text(AppLocalizations.of(context).insNoProgressData,
                style: GoogleFonts.sora(fontSize: 16, color: _textMuted)),
          ],
        ),
      );
    }

    return RefreshIndicator(
      color: _accent,
      backgroundColor: _sheetBg,
      onRefresh: _loadGamification,
      child: ListView(
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 120),
        children: [
          Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: _contentMaxWidth),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _buildLevelHero(),
                  const SizedBox(height: 16),
                  _buildStreaksCard(),
                  const SizedBox(height: 16),
                  _buildChallengesCard(),
                  const SizedBox(height: 16),
                  _buildBadgeStrip(),
                  const SizedBox(height: 16),
                  _buildWrappedCta(),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  // ── Level hero ────────────────────────────────────────
  // The game HUD: animated progress ring around the level icon, count-up
  // points, and where the ladder goes next.

  Widget _buildLevelHero() {
    final d = _dashboard!;
    final idx = _levelIndex(d.level);
    final spec = _levelLadder[idx];
    final next = idx + 1 < _levelLadder.length ? _levelLadder[idx + 1] : null;

    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [
            spec.color.withValues(alpha: 0.12),
            const Color(0xFF1A1A2E).withValues(alpha: 0.3),
          ],
        ),
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: spec.color.withValues(alpha: 0.25)),
      ),
      child: Row(
        children: [
          // Animated level ring (+ ambient shimmer at max level / near level-up)
          TweenAnimationBuilder<double>(
            tween: Tween(
                begin: 0,
                end: d.isMaxLevel ? 1.0 : d.levelProgress.clamp(0.0, 1.0)),
            duration: const Duration(milliseconds: 1100),
            curve: Curves.easeOutCubic,
            builder: (_, value, __) => AnimatedBuilder(
              animation: _shimmerCtrl,
              builder: (_, ___) => CustomPaint(
                size: const Size(84, 84),
                painter: _LevelRingPainter(
                  progress: value,
                  color: spec.color,
                  shimmerPhase:
                      _shimmerActive ? _shimmerCtrl.value : -1,
                ),
                child: SizedBox(
                  width: 84,
                  height: 84,
                  child: Center(
                    child: Image.asset(
                      'assets/levels/level_${spec.name.toLowerCase()}.png',
                      width: 58,
                      height: 58,
                      filterQuality: FilterQuality.medium,
                      errorBuilder: (_, __, ___) =>
                          Icon(spec.icon, size: 34, color: spec.color),
                    ),
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(width: 20),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    // FittedBox: rank names must never wrap ("Champio\nn").
                    Flexible(
                      child: FittedBox(
                        fit: BoxFit.scaleDown,
                        alignment: Alignment.centerLeft,
                        child: Text(spec.name,
                            maxLines: 1,
                            style: GoogleFonts.instrumentSerif(
                                fontSize: 24, color: _textGhost)),
                      ),
                    ),
                    const SizedBox(width: 10),
                    _Badge(label: 'Level ${idx + 1}', color: spec.color),
                    const Spacer(),
                    GestureDetector(
                      onTap: _shareProgress,
                      child: Icon(Icons.ios_share,
                          size: 18,
                          color: spec.color.withValues(alpha: 0.8)),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                // The loop lives in the GAP to the next rank, not the lifetime
                // odometer — so the gap is the loud line, in the next level's color.
                if (d.isMaxLevel)
                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(Icons.stars_rounded,
                          size: 18, color: _warning),
                      const SizedBox(width: 8),
                      Text(AppLocalizations.of(context).insTopOfLadder,
                          style: GoogleFonts.firaCode(
                              fontSize: 18,
                              fontWeight: FontWeight.w700,
                              color: _warning)),
                    ],
                  )
                else if (next != null)
                  TweenAnimationBuilder<int>(
                    tween: IntTween(begin: 0, end: d.pointsToNextLevel),
                    duration: const Duration(milliseconds: 900),
                    curve: Curves.easeOutCubic,
                    builder: (_, value, __) => Row(
                      crossAxisAlignment: CrossAxisAlignment.baseline,
                      textBaseline: TextBaseline.alphabetic,
                      children: [
                        Text('$value',
                            style: GoogleFonts.firaCode(
                                fontSize: 26,
                                fontWeight: FontWeight.w700,
                                color: next.color)),
                        const SizedBox(width: 7),
                        Flexible(
                          child: Text(
                              AppLocalizations.of(context)
                                  .insPtsToLevel(next.name),
                              style: GoogleFonts.sora(
                                  fontSize: 14,
                                  fontWeight: FontWeight.w600,
                                  color: next.color)),
                        ),
                        const SizedBox(width: 6),
                        Icon(next.icon, size: 15, color: next.color),
                      ],
                    ),
                  ),
                const SizedBox(height: 4),
                Text(
                    AppLocalizations.of(context)
                        .insLifetimePoints(d.totalPoints),
                    style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              ],
            ),
          ),
        ],
      ),
    );
  }

  void _shareProgress() {
    final d = _dashboard;
    if (d == null) return;
    final idx = _levelIndex(d.level);
    final spec = _levelLadder[idx];
    final bestStreak = d.activeStreaks.isEmpty
        ? 0
        : d.activeStreaks.map((s) => s.currentCount).reduce(math.max);
    showShareCardSheet(
      context,
      ShareCardData(
        kicker: 'MY PROGRESS',
        icon: '🚀',
        title: spec.name,
        subtitle: bestStreak > 0
            ? '$bestStreak-day streak · ${d.achievementCount} badges'
            : '${d.achievementCount} badges earned',
        // The rank's embroidered patch, not an emoji.
        assetPath: 'assets/levels/level_${spec.name.toLowerCase()}.png',
      ),
      caption:
          'Level: ${spec.name} on ${AppBrand.name} 🚀 Building better money habits at buoy-ai.com',
    );
  }

  // ── Streaks ───────────────────────────────────────────

  Widget _buildStreaksCard() {
    final streaks = _dashboard!.activeStreaks;

    return GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Text('🔥', style: TextStyle(fontSize: 20)),
              const SizedBox(width: 10),
              Text(AppLocalizations.of(context).insActiveStreaks,
                  style: GoogleFonts.sora(
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      color: _textGhost)),
            ],
          ),
          const SizedBox(height: 14),
          if (streaks.isEmpty)
            _EmptyMessage(
              icon: Icons.local_fire_department_outlined,
              message: 'Stay under budget today to light your first streak.',
            )
          else
            ...streaks.map((s) => _StreakTile(streak: s)),
        ],
      ),
    );
  }

  // ── Challenges ────────────────────────────────────────

  Widget _buildChallengesCard() {
    final challenges = _dashboard!.activeChallenges;

    return GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.flag_outlined, size: 20, color: _accent),
              const SizedBox(width: 10),
              Text(AppLocalizations.of(context).insDailyChallenges,
                  style: GoogleFonts.sora(
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      color: _textGhost)),
              const Spacer(),
              if (challenges.isNotEmpty)
                _SmallAction(
                    label: 'New Challenge', onTap: _generateChallenge),
            ],
          ),
          const SizedBox(height: 14),
          if (challenges.isEmpty)
            Column(
              children: [
                _EmptyMessage(
                  icon: Icons.flag_outlined,
                  message: 'Your next +25 pts is one tap away.',
                ),
                const SizedBox(height: 8),
                _GlassBtn(
                    label: 'Get a Challenge', onTap: _generateChallenge),
              ],
            )
          else
            ...challenges.map((c) => _buildChallengeTile(c)),
        ],
      ),
    );
  }

  Widget _buildChallengeTile(UserChallenge c) {
    final done = c.isCompleted;
    return Padding(
      padding: const EdgeInsets.only(bottom: 14),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Leading icon disc
          Container(
            width: 36,
            height: 36,
            decoration: BoxDecoration(
              color: (done ? _income : _accent).withValues(alpha: 0.12),
              borderRadius: BorderRadius.circular(10),
            ),
            child: Icon(
              done ? Icons.check_rounded : _challengeIcon(c.challengeType),
              size: 18,
              color: done ? _income : _accent,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(c.title,
                          style: GoogleFonts.sora(
                              fontSize: 13,
                              fontWeight: FontWeight.w600,
                              color: _textGhost)),
                    ),
                    if (done)
                      _Badge(label: 'Done  +${c.rewardPoints} pts', color: _income)
                    else if (c.isExpired)
                      GestureDetector(
                        onTap: () => _dismissChallenge(c.id),
                        child: Icon(Icons.close, size: 16, color: _textMuted),
                      )
                    else ...[
                      Text(
                        c.progressPercentage >= 0.8
                            ? AppLocalizations.of(context).insToGo(
                                (c.targetValue - c.currentValue).ceil())
                            : c.daysRemaining <= 0
                                ? AppLocalizations.of(context).insEndsToday
                                : AppLocalizations.of(context)
                                    .insEndsInDays(c.daysRemaining),
                        style: GoogleFonts.sora(
                            fontSize: 11,
                            fontWeight: c.progressPercentage >= 0.8
                                ? FontWeight.w700
                                : FontWeight.w400,
                            color: c.progressPercentage >= 0.8
                                ? _warning
                                : c.daysRemaining <= 1
                                    ? _expense
                                    : _textMuted),
                      ),
                      const SizedBox(width: 10),
                      // The reward is the headline of a challenge — say it loudly.
                      Text('+${c.rewardPoints}',
                          style: GoogleFonts.firaCode(
                              fontSize: 16,
                              fontWeight: FontWeight.w700,
                              color: _accent)),
                    ],
                  ],
                ),
                const SizedBox(height: 3),
                Text(c.description,
                    style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(
                      child: ClipRRect(
                        borderRadius: BorderRadius.circular(3),
                        child: LinearProgressIndicator(
                          value: c.progressPercentage,
                          backgroundColor: _cardBorder,
                          color: done
                              ? _income
                              : c.progressPercentage >= 0.8
                                  ? _warning
                                  : _accent,
                          minHeight: 5,
                        ),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Text(
                      '${c.currentValue.toInt()}/${c.targetValue.toInt()}',
                      style:
                          GoogleFonts.firaCode(fontSize: 11, color: _textMuted),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  // ── Recent badges strip ───────────────────────────────
  // A teaser, not a gallery — the full coin-art grid, tiers, and sharing live
  // on the Achievements page. Duplicating it here (the old wrap of 20 tiles)
  // just made both pages weaker.

  Widget _buildBadgeStrip() {
    final achievements = _dashboard!.recentAchievements;
    final newIds = _dashboard!.newAchievementIds;

    return GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.emoji_events_outlined, size: 20, color: _warning),
              const SizedBox(width: 10),
              Text(AppLocalizations.of(context).insRecentBadges,
                  style: GoogleFonts.sora(
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      color: _textGhost)),
              const Spacer(),
              _SmallAction(
                label: 'View all ${_dashboard!.achievementCount} →',
                onTap: () => context.push('/achievements'),
              ),
            ],
          ),
          const SizedBox(height: 14),
          if (achievements.isEmpty)
            _EmptyMessage(
              icon: Icons.emoji_events_outlined,
              message: 'Complete tasks to earn badges.',
            )
          else
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: achievements.take(8).map((a) {
                  final isNew = newIds.contains(a.id.toString()) ||
                      newIds.contains(a.achievementKey);
                  return Padding(
                    padding: const EdgeInsets.only(right: 10),
                    child: _AchievementBadge(achievement: a, isNew: isNew),
                  );
                }).toList(),
              ),
            ),
        ],
      ),
    );
  }

  // ── Money Wrapped entry point ─────────────────────────

  Widget _buildWrappedCta() {
    final now = DateTime.now();
    final lastMonth = DateTime(now.year, now.month - 1);
    return GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Text('🎁', style: TextStyle(fontSize: 26)),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(AppLocalizations.of(context).insMoneyWrapped,
                        style: GoogleFonts.sora(
                            fontSize: 15,
                            fontWeight: FontWeight.w700,
                            color: _textGhost)),
                    const SizedBox(height: 2),
                    Text(AppLocalizations.of(context).insWrappedSubtitle,
                        style:
                            GoogleFonts.sora(fontSize: 11, color: _textMuted)),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 14),
          Row(
            children: [
              Expanded(
                child: _GlassBtn(
                  label: DateFormat.MMMM().format(lastMonth),
                  onTap: () => context
                      .push('/wrapped/${lastMonth.year}/${lastMonth.month}'),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: _GlassBtn(
                  label: '${now.year} so far',
                  onTap: () => context.push('/wrapped/yearly/${now.year}'),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  // ═════════════════════════════════════════════════════
  //  TAB 1: AI Insights (Premium)
  // ═════════════════════════════════════════════════════

  Widget _buildAIInsightsTab() {
    if (!_premiumChecked) {
      return const Center(child: CircularProgressIndicator(color: _accent));
    }
    if (!_hasPremium) return _buildPremiumGate();

    return RefreshIndicator(
      color: _accent,
      backgroundColor: _sheetBg,
      onRefresh: () async {
        await Future.wait([
          _refreshAIInsights(),
          _loadVisualizations(),
        ]);
      },
      child: ListView(
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 120),
        children: [
          // Charts get a wider stage (2-up above 1100px); prose-y insight
          // cards keep the readable cap.
          Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 1160),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Center(
                    child: ConstrainedBox(
                      constraints:
                          const BoxConstraints(maxWidth: _contentMaxWidth),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: _buildAIInsightsSection(),
                      ),
                    ),
                  ),
                  const SizedBox(height: 24),
                  ..._buildVisualizationsSection(),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  // ── Premium gate ──────────────────────────────────────

  Widget _buildPremiumGate() {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Container(
              padding: const EdgeInsets.all(28),
              decoration: BoxDecoration(
                gradient: LinearGradient(
                  begin: Alignment.topLeft,
                  end: Alignment.bottomRight,
                  colors: [
                    _accent.withValues(alpha: 0.08),
                    _accent.withValues(alpha: 0.03),
                  ],
                ),
                borderRadius: BorderRadius.circular(16),
                border: Border.all(color: _accent.withValues(alpha: 0.2)),
              ),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(Icons.lock_outline,
                          size: 28, color: _accent.withValues(alpha: 0.7)),
                      const SizedBox(width: 12),
                      Icon(Icons.auto_awesome,
                          size: 28, color: _warning.withValues(alpha: 0.8)),
                    ],
                  ),
                  const SizedBox(height: 16),
                  Text(AppLocalizations.of(context).insUnlockAiTitle,
                      style: GoogleFonts.instrumentSerif(
                          fontSize: 22,
                          fontWeight: FontWeight.w400,
                          color: _textGhost)),
                  const SizedBox(height: 10),
                  Text(
                    AppLocalizations.of(context).insUnlockAiBody,
                    textAlign: TextAlign.center,
                    style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
                  ),
                  const SizedBox(height: 16),
                  // Feature badges
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    alignment: WrapAlignment.center,
                    children: [
                      _FeatureBadge(icon: '💡', label: 'Smart Insights'),
                      _FeatureBadge(icon: '📊', label: 'AI Visualizations'),
                      _FeatureBadge(icon: '🎯', label: 'Personalized Tips'),
                    ],
                  ),
                  const SizedBox(height: 20),
                  _SolidBtn(
                    label: 'Upgrade to Premium',
                    color: _accent,
                    onTap: () => context.push('/billing'),
                  ),
                  const SizedBox(height: 10),
                  Text(
                    AppLocalizations.of(context).insFreeUsersNote,
                    textAlign: TextAlign.center,
                    style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                  ),
                ],
              ),
        ),
      ),
    );
  }

  // ── AI Insights section ───────────────────────────────
  // Flattened: a section header + free-standing cards, instead of cards
  // nested inside a parent card.

  List<Widget> _buildAIInsightsSection() {
    return [
      Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: Row(
          children: [
            Icon(Icons.auto_awesome, size: 20, color: _accent),
            const SizedBox(width: 10),
            Text(AppLocalizations.of(context).insPersonalizedInsights,
                style: GoogleFonts.sora(
                    fontSize: 16,
                    fontWeight: FontWeight.w700,
                    color: _textGhost)),
            if (_aiInsights.isNotEmpty) ...[
              const SizedBox(width: 8),
              _Badge(label: '${_aiInsights.length}', color: _accent),
            ],
            const Spacer(),
            GestureDetector(
              onTap: _aiRefreshing ? null : _refreshAIInsights,
              child: _aiRefreshing
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(
                          strokeWidth: 2, color: _accent),
                    )
                  : Icon(Icons.refresh_rounded,
                      size: 20, color: _accent.withValues(alpha: 0.7)),
            ),
          ],
        ),
      ),
      if (_aiConsentNeeded)
        _buildAiConsentCard()
      else if (_aiLoading)
        ...List.generate(
            3,
            (_) => Padding(
                  padding: const EdgeInsets.only(bottom: 10),
                  child: Container(
                    height: 84,
                    decoration: BoxDecoration(
                      color: _cardBg,
                      borderRadius: BorderRadius.circular(12),
                      border: Border.all(color: _cardBorder),
                    ),
                  ),
                ))
      else if (_aiInsights.isEmpty)
        GlassCard(
          child: _EmptyMessage(
            icon: Icons.lightbulb_outline,
            message:
                'No insights yet. They appear after your next transaction sync.',
          ),
        )
      else
        ..._aiInsights.map((i) => _buildInsightCard(i)),
    ];
  }

  Widget _buildInsightCard(AIInsight insight) {
    final typeColor = _insightTypeColor(insight.type.name);

    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      // A non-uniform Border can't legally combine with borderRadius (Border.paint
      // asserts), so the type-color accent is a painted stripe inside a uniform
      // rounded border instead.
      child: Container(
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: _cardBorder),
        ),
        clipBehavior: Clip.antiAlias,
        child: IntrinsicHeight(
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Container(width: 3, color: typeColor),
              Expanded(
                child: Padding(
                  padding: const EdgeInsets.all(14),
                  child: _insightCardBody(insight, typeColor),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _insightCardBody(AIInsight insight, Color typeColor) {
    final priority = (insight.priority ?? '').toLowerCase();
    // "low" is the default state, not information — only flag what's notable.
    final showPriority = priority == 'high' || priority == 'medium';
    return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                _Badge(label: insight.type.name, color: typeColor),
                if (showPriority) ...[
                  const SizedBox(width: 6),
                  _Badge(label: priority, color: _priorityColor(priority)),
                ],
                const Spacer(),
                Text(_relativeTime(insight.createdAt),
                    style: GoogleFonts.sora(
                        fontSize: 10,
                        color: _textMuted.withValues(alpha: 0.7))),
                const SizedBox(width: 10),
                GestureDetector(
                  onTap: () => _dismissInsight(insight),
                  child: Icon(Icons.close, size: 16, color: _textMuted),
                ),
              ],
            ),
            const SizedBox(height: 8),
            Text(insight.title,
                style: GoogleFonts.sora(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: _textGhost)),
            if (insight.description.trim().isNotEmpty) ...[
              const SizedBox(height: 4),
              AiHighlightedText(
                text: insight.description,
                baseStyle: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                maxLines: 3,
                overflow: TextOverflow.ellipsis,
              ),
            ],
            // Only render the action when it actually goes somewhere, and make
            // it the single filled element on the card — it is the whole point.
            if (insight.actionLabel != null && insight.actionRoute != null) ...[
              const SizedBox(height: 10),
              GestureDetector(
                onTap: () => context.push(insight.actionRoute!),
                child: Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 7),
                  decoration: BoxDecoration(
                    color: _accent.withValues(alpha: 0.85),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(insight.actionLabel!,
                          style: GoogleFonts.sora(
                              fontSize: 12,
                              fontWeight: FontWeight.w700,
                              color: Colors.white)),
                      const SizedBox(width: 5),
                      const Icon(Icons.arrow_forward_rounded,
                          size: 13, color: Colors.white),
                    ],
                  ),
                ),
              ),
            ],
          ],
        );
  }

  // ── Visualizations section ────────────────────────────

  List<Widget> _buildVisualizationsSection() {
    return [
      Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: Row(
          children: [
            Icon(Icons.bar_chart_rounded, size: 20, color: _accent),
            const SizedBox(width: 10),
            Text(AppLocalizations.of(context).insCharts,
                style: GoogleFonts.sora(
                    fontSize: 16,
                    fontWeight: FontWeight.w700,
                    color: _textGhost)),
            const SizedBox(width: 14),
            Container(
              decoration: BoxDecoration(
                color: _cardBg,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  _MiniTab(
                    label: 'Recent',
                    selected: _vizTabIndex == 0,
                    onTap: () => setState(() => _vizTabIndex = 0),
                  ),
                  _MiniTab(
                    label: 'Starred',
                    selected: _vizTabIndex == 1,
                    onTap: () => setState(() => _vizTabIndex = 1),
                  ),
                ],
              ),
            ),
            const Spacer(),
            GestureDetector(
              onTap: _vizGenerating ? null : _generateVisualization,
              child: Container(
                padding:
                    const EdgeInsets.symmetric(horizontal: 12, vertical: 7),
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: _vizGenerating ? 0.4 : 0.85),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (_vizGenerating)
                      const SizedBox(
                        width: 12,
                        height: 12,
                        child: CircularProgressIndicator(
                            strokeWidth: 2, color: Colors.white),
                      )
                    else
                      const Icon(Icons.auto_awesome,
                          size: 13, color: Colors.white),
                    const SizedBox(width: 6),
                    Text(
                        _vizGenerating
                            ? AppLocalizations.of(context).insGenerating
                            : AppLocalizations.of(context).insNewChart,
                        style: GoogleFonts.sora(
                            fontSize: 12,
                            fontWeight: FontWeight.w700,
                            color: Colors.white)),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
      if (_monthSlices.isNotEmpty && _vizTabIndex == 0) _buildMonthDonut(),
      if (_vizLoading)
        const Padding(
          padding: EdgeInsets.symmetric(vertical: 24),
          child: Center(child: CircularProgressIndicator(color: _accent)),
        )
      else if (_vizTabIndex == 0)
        ..._buildVizList(_recentViz,
            isEmpty:
                'Ask for your first chart. Ten seconds, your real numbers.')
      else
        ..._buildVizList(_starredViz,
            isEmpty: 'Star a chart to pin it here.'),
    ];
  }

  /// Pinned "this month" donut from the Reports API — always present and
  /// deterministic, unlike the AI-generated charts below it. Small categories
  /// roll up into "Other" so the ring stays readable.
  Widget _buildMonthDonut() {
    final top = _monthSlices.take(6).toList();
    final rest = _monthSlices.skip(6).fold<double>(0, (s, x) => s + x.amount);
    final data = [
      ...top.map((s) => ChartDataPoint(name: s.category, value: s.amount)),
      if (rest > 0) ChartDataPoint(name: 'Other', value: rest),
    ];
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: _contentMaxWidth),
          child: GlassCard(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Container(
                      width: 30,
                      height: 30,
                      decoration: BoxDecoration(
                        color: _accent.withValues(alpha: 0.12),
                        borderRadius: BorderRadius.circular(8),
                      ),
                      child: const Icon(Icons.pie_chart_rounded,
                          size: 16, color: _accent),
                    ),
                    const SizedBox(width: 10),
                    Text(AppLocalizations.of(context).insThisMonthByCategory,
                        style: GoogleFonts.sora(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                            color: _textGhost)),
                  ],
                ),
                const SizedBox(height: 14),
                VisualizationChart(
                  bare: true,
                  height: 200,
                  viz: UserVisualization(
                    id: -1,
                    chartType: 'donut',
                    title: '',
                    message: '',
                    motivationalMessage: null,
                    chartData: data,
                    chartConfig: ChartConfig(valuePrefix: r'$'),
                    category: 'spending',
                    priority: 'low',
                    confidence: 1,
                    isStarred: false,
                    isDismissed: false,
                    viewCount: 0,
                    lastViewedAt: null,
                    generatedAt: DateTime.now(),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  List<Widget> _buildVizList(List<UserVisualization> vizuals,
      {required String isEmpty}) {
    if (vizuals.isEmpty) {
      return [
        Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: _contentMaxWidth),
            child: GlassCard(
              child: Column(
                children: [
                  _EmptyMessage(
                      icon: Icons.bar_chart_rounded, message: isEmpty),
                  if (_vizTabIndex == 0) ...[
                    const SizedBox(height: 8),
                    _GlassBtn(
                      label: 'Generate Your First Chart',
                      onTap: _generateVisualization,
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ];
    }
    return [
      // Two columns once there's room for two full-width charts side by side.
      LayoutBuilder(
        builder: (context, constraints) {
          if (constraints.maxWidth < 1100) {
            return Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: vizuals.map((v) => _buildVizCard(v)).toList(),
            );
          }
          final colWidth = (constraints.maxWidth - 16) / 2;
          return Wrap(
            spacing: 16,
            children: [
              for (final v in vizuals)
                SizedBox(width: colWidth, child: _buildVizCard(v)),
            ],
          );
        },
      ),
    ];
  }

  Widget _buildVizCard(UserVisualization viz) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: _cardBorder),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  width: 30,
                  height: 30,
                  decoration: BoxDecoration(
                    color: _accent.withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Icon(_chartTypeIcon(viz.chartType),
                      size: 16, color: _accent),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(viz.title,
                      style: GoogleFonts.sora(
                          fontSize: 14,
                          fontWeight: FontWeight.w600,
                          color: _textGhost)),
                ),
                // Star
                GestureDetector(
                  onTap: () => _toggleStar(viz.id),
                  child: Icon(
                    viz.isStarred ? Icons.star : Icons.star_border,
                    size: 18,
                    color: viz.isStarred ? _warning : _textMuted,
                  ),
                ),
                const SizedBox(width: 10),
                // Dismiss
                GestureDetector(
                  onTap: () => _dismissVisualization(viz.id),
                  child: Icon(Icons.close, size: 16, color: _textMuted),
                ),
              ],
            ),

            // The why-it-matters line is the best copy on the card — it goes
            // directly under the title, not in 11px italics at the bottom.
            if (viz.motivationalMessage != null) ...[
              const SizedBox(height: 4),
              Text(viz.motivationalMessage!,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: GoogleFonts.sora(
                      fontSize: 12.5,
                      fontWeight: FontWeight.w600,
                      color: _income)),
            ],

            // #388 Phase 1: real chart for the visualization's actual type.
            if (viz.chartData.isNotEmpty) ...[
              const SizedBox(height: 14),
              VisualizationChart(viz: viz, bare: true, height: 190),
            ],

            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: Text(viz.message,
                      style:
                          GoogleFonts.sora(fontSize: 11, color: _textMuted),
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis),
                ),
                const SizedBox(width: 12),
                Text(_relativeTime(viz.generatedAt),
                    style: GoogleFonts.sora(
                        fontSize: 10,
                        color: _textMuted.withValues(alpha: 0.7))),
              ],
            ),
          ],
        ),
      ),
    );
  }

  IconData _chartTypeIcon(String type) => switch (type.toLowerCase()) {
        'pie' => Icons.pie_chart_outline,
        'bar' => Icons.bar_chart_rounded,
        'line' => Icons.show_chart_rounded,
        'area' => Icons.area_chart_outlined,
        'gauge' => Icons.speed_rounded,
        'progress' => Icons.linear_scale_rounded,
        _ => Icons.insert_chart_outlined,
      };
}

// ═══════════════════════════════════════════════════════════
//  Streak tile
// ═══════════════════════════════════════════════════════════

/// One active streak. The count burns hotter as it grows (flicker at 7+),
/// the sub-line always names the next milestone instead of leaving the user
/// to do the math, an at-risk streak carries a midnight deadline plus the
/// action that saves it, and tapping the tile opens the streak share card.
class _StreakTile extends StatefulWidget {
  final UserStreak streak;
  const _StreakTile({required this.streak});

  @override
  State<_StreakTile> createState() => _StreakTileState();
}

class _StreakTileState extends State<_StreakTile> {
  // Medallions stay STILL — the owner found the scale flicker too busy next
  // to five tiles; the heat glow on the count carries the energy instead.
  bool get _hot => widget.streak.currentCount >= 7;

  /// The next milestone as a story, not a database field.
  String? _milestoneLine() {
    final s = widget.streak;
    if (s.currentCount <= 0) return null;
    if (s.currentCount == s.bestCount) {
      return 'New personal best! Every day extends it 🎉';
    }
    if (s.bestCount > s.currentCount) {
      final gap = s.bestCount - s.currentCount;
      return '$gap day${gap == 1 ? '' : 's'} from your best 🏆';
    }
    return null;
  }

  /// The action that saves an at-risk streak, when one exists for the type.
  (String, String)? _rescueAction() => switch (widget.streak.streakType) {
        'categorization' => ('Categorize one →', '/transactions'),
        'under_budget' || 'family_under_budget' => ('Check budget →', '/budgets'),
        'savings_goal' => ('Add to savings →', '/savings'),
        _ => null,
      };

  void _share() {
    final s = widget.streak;
    showShareCardSheet(
      context,
      ShareCardData(
        kicker: 'STREAK',
        icon: s.icon,
        title: '${s.currentCount}-day ${s.displayName} streak',
        subtitle: s.currentCount == s.bestCount
            ? 'Personal best 🎉'
            : 'Best: ${s.bestCount} days',
        assetPath: 'assets/streaks/streak_${s.streakType}.png',
      ),
      caption:
          '${s.currentCount} days and counting on ${AppBrand.name} 🔥 buoy-ai.com',
    );
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.streak;
    final heat = _streakHeat(s.currentCount);
    final milestone = _milestoneLine();
    final rescue = s.isAtRisk ? _rescueAction() : null;

    // Fixed row anatomy so every tile lines up: title, then a chips row that
    // is ALWAYS in the same slot (even when empty it costs no height), then
    // the milestone line, then the rescue button. Tags never share a line
    // with text that can wrap.
    return Padding(
      padding: const EdgeInsets.only(bottom: 18),
      child: GestureDetector(
        onTap: _share,
        behavior: HitTestBehavior.opaque,
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Streak medallion art (still); emoji fallback until the asset
            // set ships for a type.
            Image.asset(
              'assets/streaks/streak_${s.streakType}.png',
              width: 40,
              height: 40,
              filterQuality: FilterQuality.medium,
              errorBuilder: (_, __, ___) =>
                  Text(s.icon, style: const TextStyle(fontSize: 24)),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(s.displayName,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: GoogleFonts.sora(
                          fontSize: 13,
                          fontWeight: FontWeight.w600,
                          color: _textGhost)),
                  if (s.isAtRisk || s.streakFreezes > 0) ...[
                    const SizedBox(height: 5),
                    Wrap(
                      spacing: 6,
                      runSpacing: 4,
                      children: [
                        if (s.isAtRisk)
                          Container(
                            padding: const EdgeInsets.symmetric(
                                horizontal: 7, vertical: 2),
                            decoration: BoxDecoration(
                              color: _expense.withValues(alpha: 0.12),
                              borderRadius: BorderRadius.circular(5),
                            ),
                            child: Text(
                                AppLocalizations.of(context).insEndsAtMidnight,
                                style: GoogleFonts.sora(
                                    fontSize: 10,
                                    fontWeight: FontWeight.w700,
                                    color: _expense)),
                          ),
                        if (s.streakFreezes > 0)
                          GestureDetector(
                            onTap: () => showAppToast(
                              context,
                              'Streak freezes: each ❄️ automatically covers one '
                              'missed day so a single slip never resets your streak.',
                            ),
                            child: Container(
                              padding: const EdgeInsets.symmetric(
                                  horizontal: 7, vertical: 2),
                              decoration: BoxDecoration(
                                color: _blue.withValues(alpha: 0.12),
                                borderRadius: BorderRadius.circular(5),
                              ),
                              child: Text('❄️ ${s.streakFreezes}',
                                  style: GoogleFonts.sora(
                                      fontSize: 10, color: _blue)),
                            ),
                          ),
                      ],
                    ),
                  ],
                  const SizedBox(height: 4),
                  Text(
                      milestone ??
                          AppLocalizations.of(context)
                              .insBestDays(s.bestCount),
                      style: GoogleFonts.sora(
                          fontSize: 11,
                          color: milestone != null &&
                                  s.currentCount == s.bestCount
                              ? _income
                              : _textMuted)),
                  if (rescue != null) ...[
                    const SizedBox(height: 8),
                    GestureDetector(
                      onTap: () => context.push(rescue.$2),
                      child: Container(
                        padding: const EdgeInsets.symmetric(
                            horizontal: 12, vertical: 6),
                        decoration: BoxDecoration(
                          color: _expense.withValues(alpha: 0.85),
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: Text(
                            AppLocalizations.of(context).insSaveIt(rescue.$1),
                            style: GoogleFonts.sora(
                                fontSize: 11,
                                fontWeight: FontWeight.w700,
                                color: Colors.white)),
                      ),
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(width: 12),
            // Current count — glows once the streak is a week old.
            Column(
              children: [
                Text('${s.currentCount}',
                    style: GoogleFonts.firaCode(
                      fontSize: 24,
                      fontWeight: FontWeight.w700,
                      color: heat,
                      shadows: _hot
                          ? [
                              Shadow(
                                  color: heat.withValues(alpha: 0.55),
                                  blurRadius: 14),
                            ]
                          : null,
                    )),
                Text(AppLocalizations.of(context).insDayStreak,
                    style: GoogleFonts.sora(fontSize: 9, color: _textMuted)),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  Level ring painter
// ═══════════════════════════════════════════════════════════

class _LevelRingPainter extends CustomPainter {
  final double progress;
  final Color color;

  /// 0..1 rotation phase for the ambient highlight; -1 disables it.
  final double shimmerPhase;

  const _LevelRingPainter(
      {required this.progress, required this.color, this.shimmerPhase = -1});

  @override
  void paint(Canvas canvas, Size size) {
    final center = Offset(size.width / 2, size.height / 2);
    final radius = (size.width - 7) / 2;
    final rect = Rect.fromCircle(center: center, radius: radius);

    // Track
    final track = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 6
      ..color = _cardBorder;
    canvas.drawCircle(center, radius, track);

    if (progress <= 0) return;

    // Progress arc — sweep gradient so the head of the arc burns brightest.
    final sweep = progress.clamp(0.0, 1.0) * 2 * math.pi;
    final arc = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 6
      ..strokeCap = StrokeCap.round
      ..shader = SweepGradient(
        startAngle: 0,
        endAngle: 2 * math.pi,
        colors: [color.withValues(alpha: 0.35), color],
        stops: const [0.0, 1.0],
        transform: const GradientRotation(-math.pi / 2),
      ).createShader(rect);
    canvas.drawArc(rect, -math.pi / 2, sweep, false, arc);

    // Ambient shimmer: a short bright highlight orbiting the filled arc, so a
    // maxed (or nearly maxed) ring reads "alive" on camera without shouting.
    if (shimmerPhase >= 0) {
      final phaseAngle = -math.pi / 2 + shimmerPhase * 2 * math.pi;
      const highlightSweep = 0.45;
      // Keep the highlight inside the filled portion of the ring.
      final maxAngle = -math.pi / 2 + sweep - highlightSweep;
      if (maxAngle > -math.pi / 2) {
        final clamped =
            math.min(phaseAngle, maxAngle).clamp(-math.pi / 2, maxAngle);
        final highlight = Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 6
          ..strokeCap = StrokeCap.round
          ..color = Colors.white.withValues(alpha: 0.35)
          ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 3);
        canvas.drawArc(rect, clamped.toDouble(), highlightSweep, false, highlight);
      }
    }
  }

  @override
  bool shouldRepaint(_LevelRingPainter old) =>
      old.progress != progress ||
      old.color != color ||
      old.shimmerPhase != shimmerPhase;
}

// ═══════════════════════════════════════════════════════════
//  Shared cinematic widgets
// ═══════════════════════════════════════════════════════════

class _Badge extends StatelessWidget {
  final String label;
  final Color color;
  const _Badge({required this.label, required this.color});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Text(label,
          style: GoogleFonts.sora(
              fontSize: 11, fontWeight: FontWeight.w600, color: color)),
    );
  }
}

class _TabItem extends StatefulWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;

  const _TabItem(
      {required this.label, required this.selected, required this.onTap});

  @override
  State<_TabItem> createState() => _TabItemState();
}

class _TabItemState extends State<_TabItem> {
  bool _h = false;

  @override
  Widget build(BuildContext context) {
    return Expanded(
      child: MouseRegion(
        onEnter: (_) => setState(() => _h = true),
        onExit: (_) => setState(() => _h = false),
        child: GestureDetector(
          onTap: widget.onTap,
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 200),
            padding: const EdgeInsets.symmetric(vertical: 10),
            decoration: BoxDecoration(
              color: widget.selected
                  ? _accent.withValues(alpha: 0.12)
                  : _h
                      ? _cardBg
                      : Colors.transparent,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Center(
              child: Text(widget.label,
                  style: GoogleFonts.sora(
                      fontSize: 13,
                      fontWeight:
                          widget.selected ? FontWeight.w700 : FontWeight.w500,
                      color: widget.selected ? _accent : _textMuted)),
            ),
          ),
        ),
      ),
    );
  }
}

class _MiniTab extends StatefulWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;

  const _MiniTab(
      {required this.label, required this.selected, required this.onTap});

  @override
  State<_MiniTab> createState() => _MiniTabState();
}

class _MiniTabState extends State<_MiniTab> {
  bool _h = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _h = true),
      onExit: (_) => setState(() => _h = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding:
              const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
          decoration: BoxDecoration(
            color: widget.selected
                ? _accent.withValues(alpha: 0.12)
                : _h
                    ? _cardBg
                    : Colors.transparent,
            borderRadius: BorderRadius.circular(6),
          ),
          child: Text(widget.label,
              style: GoogleFonts.sora(
                  fontSize: 12,
                  fontWeight:
                      widget.selected ? FontWeight.w700 : FontWeight.w500,
                  color: widget.selected ? _accent : _textMuted)),
        ),
      ),
    );
  }
}

class _EmptyMessage extends StatelessWidget {
  final IconData icon;
  final String message;

  const _EmptyMessage({required this.icon, required this.message});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 12),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(icon, size: 20, color: _textMuted.withValues(alpha: 0.4)),
          const SizedBox(width: 10),
          Flexible(
            child: Text(message,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          ),
        ],
      ),
    );
  }
}

class _AchievementBadge extends StatefulWidget {
  final UserAchievement achievement;
  final bool isNew;

  const _AchievementBadge({required this.achievement, required this.isNew});

  @override
  State<_AchievementBadge> createState() => _AchievementBadgeState();
}

class _AchievementBadgeState extends State<_AchievementBadge> {
  bool _h = false;

  // Same point→tier derivation as the backend (GamificationDTOs.TierFor) and
  // the share card's rim colors.
  Color get _tierColor {
    final p = widget.achievement.points;
    if (p >= 500) return const Color(0xFF9B8CFF); // Platinum
    if (p >= 250) return const Color(0xFFFFD700); // Gold
    if (p >= 100) return const Color(0xFFC0C0C0); // Silver
    return const Color(0xFFCD7F32); // Bronze
  }

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _h = true),
      onExit: (_) => setState(() => _h = false),
      child: Tooltip(
        message: '${widget.achievement.title}\n${widget.achievement.description}\n+${widget.achievement.points} pts',
        child: GestureDetector(
          // The strip is a teaser for the trophy room — tapping goes there.
          onTap: () => context.push('/achievements'),
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 150),
            width: 72,
            height: 72,
            padding: const EdgeInsets.all(6),
            decoration: BoxDecoration(
              color: _h ? _accent.withValues(alpha: 0.1) : _cardBg,
              borderRadius: BorderRadius.circular(14),
              border: Border.all(
                color: widget.isNew
                    ? _warning.withValues(alpha: 0.7)
                    : _tierColor.withValues(alpha: 0.45),
              ),
              boxShadow: widget.isNew
                  ? [
                      BoxShadow(
                          color: _warning.withValues(alpha: 0.15),
                          blurRadius: 12),
                    ]
                  : null,
            ),
            child: Stack(
              children: [
                // The real coin art, not a Slack reaction; emoji fallback if
                // an asset is ever missing.
                Center(
                  child: Image.asset(
                    'assets/achievements/${widget.achievement.achievementKey}.png',
                    width: 52,
                    height: 52,
                    filterQuality: FilterQuality.medium,
                    errorBuilder: (_, __, ___) => Text(
                        widget.achievement.icon,
                        style: const TextStyle(fontSize: 30)),
                  ),
                ),
                if (widget.isNew)
                  Positioned(
                    top: 0,
                    right: 0,
                    child: Container(
                      width: 10,
                      height: 10,
                      decoration: const BoxDecoration(
                        color: _warning,
                        shape: BoxShape.circle,
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _FeatureBadge extends StatelessWidget {
  final String icon;
  final String label;

  const _FeatureBadge({required this.icon, required this.label});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      decoration: BoxDecoration(
        color: _accent.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: _accent.withValues(alpha: 0.15)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(icon, style: const TextStyle(fontSize: 14)),
          const SizedBox(width: 6),
          Text(label,
              style: GoogleFonts.sora(
                  fontSize: 12, fontWeight: FontWeight.w600, color: _textGhost)),
        ],
      ),
    );
  }
}

class _GlassBtn extends StatefulWidget {
  final String label;
  final VoidCallback? onTap;
  const _GlassBtn({required this.label, this.onTap});

  @override
  State<_GlassBtn> createState() => _GlassBtnState();
}

class _GlassBtnState extends State<_GlassBtn> {
  bool _h = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _h = true),
      onExit: (_) => setState(() => _h = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
          decoration: BoxDecoration(
            color: _h ? _accent.withValues(alpha: 0.12) : _cardBg,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
                color: _h ? _accent.withValues(alpha: 0.4) : _cardBorder),
          ),
          child: Center(
            child: Text(widget.label,
                style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: _accent)),
          ),
        ),
      ),
    );
  }
}

class _SolidBtn extends StatefulWidget {
  final String label;
  final Color color;
  final VoidCallback? onTap;
  const _SolidBtn({required this.label, required this.color, this.onTap});

  @override
  State<_SolidBtn> createState() => _SolidBtnState();
}

class _SolidBtnState extends State<_SolidBtn> {
  bool _h = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _h = true),
      onExit: (_) => setState(() => _h = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
          decoration: BoxDecoration(
            color: _h
                ? widget.color.withValues(alpha: 0.9)
                : widget.color.withValues(alpha: 0.75),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Center(
            child: Text(widget.label,
                style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: Colors.white)),
          ),
        ),
      ),
    );
  }
}

class _SmallAction extends StatefulWidget {
  final String label;
  final Color? color;
  final VoidCallback? onTap;
  // ignore: unused_element_parameter
  const _SmallAction({required this.label, this.color, this.onTap});

  @override
  State<_SmallAction> createState() => _SmallActionState();
}

class _SmallActionState extends State<_SmallAction> {
  bool _h = false;

  @override
  Widget build(BuildContext context) {
    final c = widget.color ?? _accent;
    return MouseRegion(
      onEnter: (_) => setState(() => _h = true),
      onExit: (_) => setState(() => _h = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Text(widget.label,
            style: GoogleFonts.sora(
                fontSize: 12,
                fontWeight: FontWeight.w600,
                color: _h ? c : c.withValues(alpha: 0.7),
                decoration: _h ? TextDecoration.underline : null,
                decorationColor: c)),
      ),
    );
  }
}
