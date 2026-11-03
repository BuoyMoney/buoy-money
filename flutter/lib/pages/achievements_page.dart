import '../config/app_brand.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/gamification_service.dart';
import '../services/referral_service.dart';
import '../widgets/glass_card.dart';
import '../widgets/cinematic/achievement_badges.dart';
import '../widgets/cinematic/achievement_celebration.dart';
import '../widgets/cinematic/share_card.dart';

// VaporClinic palette (shared with the other cinematic pages).
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);

/// The Achievements page. "Good" badges sit up top and stay silhouetted until
/// unlocked (industry standard). Cautionary ("oops") badges sit below and are
/// always legible — with a tooltip explaining what they are — so users don't have
/// to actually earn a bad badge to find out what it means.
class AchievementsPage extends StatefulWidget {
  const AchievementsPage({super.key});

  @override
  State<AchievementsPage> createState() =>
      _AchievementsPageState();
}

class _AchievementsPageState extends State<AchievementsPage>
    with SingleTickerProviderStateMixin {
  List<AchievementCatalogItem> _catalog = [];
  GamificationDashboard? _dashboard;
  bool _loading = true;
  String? _error;

  late final AnimationController _fade =
      AnimationController(vsync: this, duration: const Duration(milliseconds: 500));

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _fade.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      // Evaluate first so anything the user just qualified for is awarded before we
      // read the catalog; fall back to a plain read if the check hiccups.
      GamificationDashboard dashboard;
      try {
        dashboard = await gamificationService.checkAchievements();
      } catch (_) {
        dashboard = await gamificationService.getDashboard();
      }
      final catalog = await gamificationService.getAchievementCatalog();
      if (!mounted) return;
      setState(() {
        _dashboard = dashboard;
        _catalog = catalog;
        _loading = false;
      });
      _fade.forward(from: 0);
      await _celebrateNewlyEarned(dashboard, catalog);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = 'Could not load achievements. Pull to retry.';
        _loading = false;
      });
    }
  }

  /// Derive a personal "archetype" from the user's profile — the ego-driven second share
  /// surface (per the research: broadens sharing beyond power users). Computed from data
  /// already loaded; no dollar amounts involved.
  ({String icon, String name, String tagline}) _computeArchetype() {
    int countCat(String c) =>
        _catalog.where((a) => a.isEarned && !a.isNegative && a.category == c).length;
    final bestStreak = (_dashboard?.activeStreaks ?? [])
        .fold<int>(0, (m, s) => s.bestCount > m ? s.bestCount : m);
    final savings = countCat('savings');
    final budget = countCat('budget') + countCat('streak');
    final countUp = countCat('count_up');
    final engagement = countCat('engagement');
    final totalEarned =
        _catalog.where((a) => a.isEarned && !a.isNegative).length;

    // Weight streak length so a strong streak can define identity.
    final scores = <(String, String, String, num)>[
      ('🔥', 'The Streak Keeper', 'Shows up, day after day', bestStreak / 3.0),
      ('🎯', 'The Goal Crusher', 'Turns targets into wins', savings * 1.0),
      ('🦅', 'The Budget Hawk', 'Nothing gets past the budget', budget * 1.0),
      ('📈', 'The Optimizer', 'Dials in every dollar', countUp * 1.0),
      ('🧭', 'The Explorer', 'Always trying something new', engagement * 1.0),
    ];
    scores.sort((a, b) => b.$4.compareTo(a.$4));
    if (totalEarned == 0 || scores.first.$4 <= 0) {
      return (icon: '🌟', name: 'The Rising Star', tagline: 'Just getting started');
    }
    final top = scores.first;
    return (icon: top.$1, name: top.$2, tagline: top.$3);
  }

  Widget _archetypeCard() {
    final a = _computeArchetype();
    return GlassCard(
      child: Row(
        children: [
          Text(a.icon, style: const TextStyle(fontSize: 34)),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(AppLocalizations.of(context).uiYourArchetype,
                    style: GoogleFonts.sora(
                        fontSize: 9,
                        fontWeight: FontWeight.w700,
                        letterSpacing: 1,
                        color: _textMuted)),
                const SizedBox(height: 2),
                Text(a.name,
                    style: GoogleFonts.sora(
                        fontSize: 17,
                        fontWeight: FontWeight.w800,
                        color: _textGhost)),
                Text(a.tagline,
                    style:
                        GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              ],
            ),
          ),
          IconButton(
            onPressed: () => _shareArchetype(a),
            icon: const Icon(Icons.ios_share, size: 18, color: _accent),
            tooltip: 'Share archetype',
          ),
        ],
      ),
    );
  }

  Widget _referralCard() {
    return GlassCard(
      child: Row(
        children: [
          const Text('🤝', style: TextStyle(fontSize: 30)),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(AppLocalizations.of(context).uiInviteAFriend,
                    style: GoogleFonts.sora(
                        fontSize: 16,
                        fontWeight: FontWeight.w700,
                        color: _textGhost)),
                Text(AppLocalizations.of(context).uiInviteSubtitle,
                    style:
                        GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              ],
            ),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: _accent,
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
            ),
            onPressed: _shareReferral,
            child: Text(AppLocalizations.of(context).uiInvite,
                style: GoogleFonts.sora(
                    fontWeight: FontWeight.w700, color: Colors.white)),
          ),
        ],
      ),
    );
  }

  Future<void> _shareReferral() async {
    ReferralSummary? summary;
    try {
      summary = await referralService.getSummary();
    } catch (_) {
      summary = null;
    }
    if (!mounted) return;
    final link = summary?.link ?? 'https://buoy-ai.com/app/';
    showShareCardSheet(
      context,
      const ShareCardData(
        kicker: 'JOIN ME ON BUOYAI',
        icon: '🤝',
        title: 'Take control of your money',
        subtitle: 'Budgeting that actually sticks',
        tier: null,
      ),
      caption:
          'I\'m using ${AppBrand.name} to budget smarter. Join me and we both level up! $link',
    );
  }

  void _shareArchetype(({String icon, String name, String tagline}) a) {
    showShareCardSheet(
      context,
      ShareCardData(
        kicker: 'MY MONEY ARCHETYPE',
        icon: a.icon,
        title: a.name,
        subtitle: a.tagline,
        tier: null,
      ),
      caption:
          'My ${AppBrand.name} money archetype: ${a.name} ${a.icon}: ${a.tagline}. What\'s yours? buoy-ai.com',
    );
  }

  void _shareBadge(AchievementCatalogItem a) {
    final rarity = a.percentEarned > 0
        ? '${a.percentEarned % 1 == 0 ? a.percentEarned.toInt() : a.percentEarned}% have this'
        : null;
    showShareCardSheet(
      context,
      ShareCardData(
        kicker: a.isNegative ? 'BADGE OF HONOR (?)' : 'ACHIEVEMENT UNLOCKED',
        icon: a.icon ?? '🏆',
        title: a.title ?? 'Achievement',
        subtitle: [
          if (a.tier != null) '${a.tier} tier',
          if (rarity != null) rarity,
        ].join(' · '),
        tier: a.tier,
        assetPath: 'assets/achievements/${a.key}.png',
      ),
      caption:
          'I just unlocked "${a.title}" on ${AppBrand.name} 🎯 Building better money habits at buoy-ai.com',
    );
  }

  void _shareProgress() {
    final level = _dashboard?.level ?? 'Beginner';
    final unlocked = _catalog.where((c) => !c.isNegative && c.isEarned).length;
    showShareCardSheet(
      context,
      ShareCardData(
        kicker: 'MY PROGRESS',
        icon: '🚀',
        title: level,
        subtitle: '$unlocked achievements unlocked',
        tier: null,
        // The rank's embroidered patch, not an emoji.
        assetPath: 'assets/levels/level_${level.toLowerCase()}.png',
      ),
      caption:
          'Level: $level with $unlocked achievements on ${AppBrand.name} 🚀 buoy-ai.com',
    );
  }

  /// Celebrate anything freshly earned since the user last saw it, then mark it seen.
  Future<void> _celebrateNewlyEarned(
      GamificationDashboard dashboard, List<AchievementCatalogItem> catalog) async {
    final newIds = dashboard.newAchievementIds.toSet();
    if (newIds.isEmpty || !mounted) return;
    final fresh = catalog.where((c) => c.isEarned && newIds.contains(c.key)).toList();
    if (fresh.isEmpty) return;
    await showAchievementCelebration(context, fresh);
    await gamificationService.markAchievementsSeen(newIds.toList());
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: SafeArea(
        child: _loading
            ? const Center(
                child: CircularProgressIndicator(color: _accent, strokeWidth: 2))
            : _error != null
                // Make the "Pull to retry" copy real: wrap the error view in a
                // pull-to-refresh over a full-height scroll view so the gesture works
                // (the Retry button still works too).
                ? RefreshIndicator(
                    color: _accent,
                    backgroundColor: const Color(0xFF14141F),
                    onRefresh: _load,
                    child: LayoutBuilder(
                      builder: (context, constraints) => SingleChildScrollView(
                        physics: const AlwaysScrollableScrollPhysics(),
                        child: ConstrainedBox(
                          constraints:
                              BoxConstraints(minHeight: constraints.maxHeight),
                          child: _ErrorView(message: _error!, onRetry: _load),
                        ),
                      ),
                    ),
                  )
                : FadeTransition(
                    opacity: _fade,
                    child: RefreshIndicator(
                      color: _accent,
                      backgroundColor: const Color(0xFF14141F),
                      onRefresh: _load,
                      child: _buildContent(context),
                    ),
                  ),
      ),
    );
  }

  Widget _buildContent(BuildContext context) {
    final good = _catalog.where((a) => !a.isNegative).toList();
    final oops = _catalog.where((a) => a.isNegative).toList();
    final earnedGood = good.where((a) => a.isEarned).length;
    final width = MediaQuery.of(context).size.width;
    final pad = width > 800 ? 40.0 : 16.0;

    return ListView(
      padding: EdgeInsets.fromLTRB(pad, 24, pad, 48),
      children: [
        Text(
          AppLocalizations.of(context).uiAchievementsTitle,
          style: GoogleFonts.sora(
              fontSize: 28, fontWeight: FontWeight.bold, color: _textGhost),
        ),
        const SizedBox(height: 4),
        Text(
          AppLocalizations.of(context).uiAchievementsSubtitle,
          style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
        ),
        const SizedBox(height: 20),
        _summaryCard(earnedGood, good.length),
        const SizedBox(height: 12),
        _archetypeCard(),
        const SizedBox(height: 12),
        _referralCard(),
        const SizedBox(height: 28),
        _sectionHeader('Milestones', '$earnedGood / ${good.length} unlocked'),
        const SizedBox(height: 12),
        if (good.isEmpty)
          _emptyNote('No milestones yet — check back soon.')
        else
          Wrap(
            spacing: 12,
            runSpacing: 12,
            children: [
              for (final a in good)
                a.isEarned
                    ? GestureDetector(
                        onTap: () => _shareBadge(a),
                        child: GoodAchievementBadge(item: a),
                      )
                    : GoodAchievementBadge(item: a),
            ],
          ),
        if (oops.isNotEmpty) ...[
          const SizedBox(height: 32),
          _sectionHeader('Watch-outs',
              '${oops.where((a) => a.isEarned).length} earned'),
          const SizedBox(height: 6),
          Text(
            AppLocalizations.of(context).uiWatchOutsNote,
            style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
          ),
          const SizedBox(height: 12),
          for (final a in oops) ...[
            OopsAchievementBadge(item: a),
            const SizedBox(height: 10),
          ],
        ],
      ],
    );
  }

  Widget _summaryCard(int earnedGood, int totalGood) {
    final level = _dashboard?.level ?? '—';
    final points = _dashboard?.totalPoints ?? 0;
    final toNext = _dashboard?.pointsToNextLevel ?? 0;
    final progress = totalGood > 0 ? earnedGood / totalGood : 0.0;

    return GlassCard(
      glow: true,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              _stat('Level', level),
              const SizedBox(width: 24),
              _stat('Points', '$points'),
              const SizedBox(width: 24),
              _stat('Unlocked', '$earnedGood / $totalGood'),
              const Spacer(),
              IconButton(
                onPressed: _shareProgress,
                icon: const Icon(Icons.ios_share, size: 18, color: _accent),
                tooltip: 'Share progress',
              ),
            ],
          ),
          const SizedBox(height: 16),
          ClipRRect(
            borderRadius: BorderRadius.circular(4),
            child: LinearProgressIndicator(
              value: progress.clamp(0.0, 1.0),
              minHeight: 6,
              backgroundColor: const Color(0x14FFFFFF),
              valueColor: const AlwaysStoppedAnimation(_accent),
            ),
          ),
          const SizedBox(height: 8),
          Text(
            toNext > 0
                ? AppLocalizations.of(context).uiPointsToNextLevel(toNext)
                : AppLocalizations.of(context).uiTopLevelReached,
            style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
          ),
        ],
      ),
    );
  }

  Widget _stat(String label, String value) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label.toUpperCase(),
            style: GoogleFonts.sora(
                fontSize: 10,
                fontWeight: FontWeight.w600,
                letterSpacing: 1,
                color: _textMuted)),
        const SizedBox(height: 2),
        Text(value,
            style: GoogleFonts.sora(
                fontSize: 18, fontWeight: FontWeight.bold, color: _textGhost)),
      ],
    );
  }

  Widget _sectionHeader(String title, String trailing) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(title,
            style: GoogleFonts.sora(
                fontSize: 17, fontWeight: FontWeight.w700, color: _textGhost)),
        Text(trailing,
            style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
      ],
    );
  }

  Widget _emptyNote(String text) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 12),
        child: Text(text, style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
      );
}

class _ErrorView extends StatelessWidget {
  final String message;
  final VoidCallback onRetry;
  const _ErrorView({required this.message, required this.onRetry});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.emoji_events_outlined, size: 40, color: _textMuted),
          const SizedBox(height: 12),
          Text(message,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          const SizedBox(height: 16),
          TextButton(
            onPressed: onRetry,
            child: Text(AppLocalizations.of(context).uiRetry,
                style: GoogleFonts.sora(
                    color: _accent, fontWeight: FontWeight.w600)),
          ),
        ],
      ),
    );
  }
}
