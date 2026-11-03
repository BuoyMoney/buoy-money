import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/dashboard_models.dart' show Account;
import '../models/savings_models.dart';
import '../utils/currency.dart';
import '../services/accounts_service.dart';
import '../services/analytics_service.dart';
import '../services/savings_service.dart';
import '../utils/formatters.dart';
import '../widgets/app_toast.dart';

// ═══════════════════════════════════════════════════════════
//  CONSTANTS & CONFIGURATION
// ═══════════════════════════════════════════════════════════

// ── VaporClinic palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _sheetBg = Color(0xFF0C0C18);
const _destructive = Color(0xFFF87171);

// ── Goal icon mapping (backend iconName → Material icon) ─
const _goalIconMap = <String, IconData>{
  'target': Icons.gps_fixed,
  'home': Icons.home_outlined,
  'truck': Icons.local_shipping_outlined,
  'map-pin': Icons.place_outlined,
  'gift': Icons.card_giftcard_outlined,
  'book': Icons.menu_book_outlined,
  'dollar-sign': Icons.attach_money,
  'trending-up': Icons.trending_up,
  'heart': Icons.favorite_outline,
  'star': Icons.star_outline,
};

// ── Selectable icon entries (label → iconName for backend) ─
const _selectableIcons = <String>[
  'target',
  'home',
  'truck',
  'map-pin',
  'gift',
  'book',
  'dollar-sign',
  'trending-up',
  'heart',
  'star',
];

// ── Goal color mapping (backend colorTheme → Color) ─────
const _goalColorMap = <String, Color>{
  'blue': Color(0xFF60A5FA),
  'green': Color(0xFF34D399),
  'purple': Color(0xFFA78BFA),
  'pink': Color(0xFFF472B6),
  'orange': Color(0xFFFB923C),
  'red': Color(0xFFF87171),
  'teal': Color(0xFF2DD4BF),
  'cyan': Color(0xFF22D3EE),
  'yellow': Color(0xFFFBBF24),
  'gray': Color(0xFF9CA3AF),
  'primary': Color(0xFF7B61FF),
};

const _selectableColors = <String>[
  'blue',
  'green',
  'purple',
  'pink',
  'orange',
  'red',
  'teal',
  'cyan',
  'yellow',
  'gray',
];

/// Resolve goal icon from backend [iconName], with fallback.
IconData _resolveIcon(String iconName) =>
    _goalIconMap[iconName] ?? Icons.savings_outlined;

/// Resolve goal color from backend [colorTheme], with fallback.
Color _resolveColor(String colorTheme) =>
    _goalColorMap[colorTheme] ?? _accent;

// ═══════════════════════════════════════════════════════════
//  MAIN PAGE
// ═══════════════════════════════════════════════════════════

/// Cinematic Savings Goals page.
///
/// Full-featured savings page:
///  • Overview stats (total saved, allocated, active, completed)
///  • Leftover budget info (personal + family)
///  • Goals separated by family / personal
///  • Goal cards with progress, icons, color themes
///  • Create / edit goal via shared form sheet (DRY)
///  • Add funds (allocate) sheet
///  • Delete confirmation
///  • Achievements display
///  • Empty state with CTA
class SavingsPage extends StatefulWidget {
  const SavingsPage({super.key});

  @override
  State<SavingsPage> createState() => _SavingsPageState();
}

class _SavingsPageState extends State<SavingsPage>
    with SingleTickerProviderStateMixin {
  // ── Data ─────────────────────────────────────────────
  SavingsOverview? _overview;
  bool _isLoading = true;
  String? _error;

  // ── Animation ────────────────────────────────────────
  late AnimationController _fadeCtrl;
  late Animation<double> _fade;

  @override
  void initState() {
    super.initState();
    _fadeCtrl = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fade = CurvedAnimation(parent: _fadeCtrl, curve: Curves.easeOut);
    _fadeCtrl.forward();
    _loadData();
  }

  @override
  void dispose() {
    _fadeCtrl.dispose();
    super.dispose();
  }

  // ── Data loading ─────────────────────────────────────

  Future<void> _loadData() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    try {
      final overview = await savingsService.getOverview();
      if (!mounted) return;
      setState(() {
        _overview = overview;
        _isLoading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = 'Failed to load savings data';
        _isLoading = false;
      });
    }
  }

  Future<void> _refresh() async {
    try {
      final overview = await savingsService.getOverview();
      if (!mounted) return;
      setState(() {
        _overview = overview;
        _error = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Failed to refresh');
    }
  }

  // ── Actions ──────────────────────────────────────────

  void _showCreateGoal() {
    _showGoalFormSheet(
      onSaved: (goal) {
        _refresh();
        if (mounted) {
          showAppToast(
            context,
            'Created "${goal.name}"',
            type: AppToastType.success,
          );
        }
      },
    );
  }

  void _showGoalDetails(SavingsGoal goal) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _GoalDetailsSheet(
        goal: goal,
        onEdited: () => _showEditGoal(goal),
        onDeleted: () => _showDeleteConfirm(goal),
        onReset: () => _showResetConfirm(goal),
        onAddFunds: () => _showAllocateSheet(goal),
        onRefresh: _refresh,
      ),
    );
  }

  void _showEditGoal(SavingsGoal goal) {
    _showGoalFormSheet(
      editingGoal: goal,
      existingGoals: _overview?.goals ?? [],
      onSaved: (updated) {
        _refresh();
        if (mounted) {
          showAppToast(
            context,
            'Updated "${updated.name}"',
            type: AppToastType.success,
          );
        }
      },
    );
  }

  void _showDeleteConfirm(SavingsGoal goal) {
    showModalBottomSheet(
      context: context,
      backgroundColor: Colors.transparent,
      builder: (_) => _ConfirmSheet(
        title: 'Delete Goal',
        message: 'Are you sure you want to delete "${goal.name}"? '
            'This action cannot be undone.',
        confirmLabel: 'Delete',
        isDestructive: true,
        onConfirm: () async {
          await savingsService.deleteGoal(goal.id);
          analytics.track(AnalyticsEvents.savingsGoalDeleted);
          _refresh();
          if (mounted) {
            showAppToast(context, 'Goal deleted', type: AppToastType.success);
          }
        },
      ),
    );
  }

  void _showResetConfirm(SavingsGoal goal) {
    showModalBottomSheet(
      context: context,
      backgroundColor: Colors.transparent,
      builder: (_) => _ConfirmSheet(
        title: 'Reset Goal Balance',
        message: 'Reset "${goal.name}" back to ${primaryCurrencySymbol}0? '
            'Your goal, target and contribution history stay; only the '
            'current balance is cleared. If your other goals now use part of '
            'its auto-save share, it keeps only what is still free.',
        confirmLabel: 'Reset to ${primaryCurrencySymbol}0',
        isDestructive: true,
        onConfirm: () async {
          await savingsService.resetGoal(goal.id);
          _refresh();
          if (mounted) {
            showAppToast(
              context,
              'Reset "${goal.name}" to ${primaryCurrencySymbol}0',
              type: AppToastType.success,
            );
          }
        },
      ),
    );
  }

  void _showAllocateSheet(SavingsGoal goal) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _AllocateFundsSheet(
        goal: goal,
        onAllocated: (updated) {
          _refresh();
          if (mounted) {
            showAppToast(
              context,
              'Added funds to "${updated.name}"',
              type: AppToastType.success,
            );
          }
        },
      ),
    );
  }

  void _showGoalFormSheet({
    SavingsGoal? editingGoal,
    List<SavingsGoal>? existingGoals,
    required ValueChanged<SavingsGoal> onSaved,
  }) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      useSafeArea: true,
      builder: (_) => DraggableScrollableSheet(
        initialChildSize: 0.9,
        minChildSize: 0.5,
        maxChildSize: 0.95,
        expand: false,
        builder: (ctx, scroll) => _GoalFormSheet(
          scrollController: scroll,
          editingGoal: editingGoal,
          existingGoals: existingGoals ?? _overview?.goals ?? [],
          hasFamilyBudget: _overview?.hasFamilyBudget ?? false,
          personalLeftover: _overview?.personalLeftover ?? 0,
          familyLeftover: _overview?.familyLeftover ?? 0,
          onSaved: (goal) {
            Navigator.pop(ctx);
            onSaved(goal);
          },
        ),
      ),
    );
  }

  // ═══════════════════════════════════════════════════════
  //  BUILD
  // ═══════════════════════════════════════════════════════

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: FadeTransition(
        opacity: _fade,
        child: _isLoading
            ? const Center(
                child: CircularProgressIndicator(color: _accent),
              )
            : _error != null
                ? _buildError()
                : _overview != null && _overview!.goals.isEmpty
                    ? _buildEmptyState()
                    : _buildContent(),
      ),
    );
  }

  // ── Error state ──────────────────────────────────────

  Widget _buildError() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.error_outline,
              size: 48, color: _destructive.withValues(alpha: 0.6)),
          const SizedBox(height: 16),
          Text(_error!, style: GoogleFonts.sora(color: _textMuted)),
          const SizedBox(height: 16),
          _GlassButton(
            label: 'Retry',
            icon: Icons.refresh,
            onTap: _loadData,
          ),
        ],
      ),
    );
  }

  // ── Empty state ──────────────────────────────────────

  Widget _buildEmptyState() {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 80,
              height: 80,
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(20),
                gradient: LinearGradient(
                  colors: [_accent.withValues(alpha: 0.3), _accent.withValues(alpha: 0.1)],
                  begin: Alignment.topLeft,
                  end: Alignment.bottomRight,
                ),
              ),
              child: const Icon(Icons.savings_outlined,
                  size: 40, color: _accent),
            ),
            const SizedBox(height: 24),
            Text(
              AppLocalizations.of(context).savNoSavingsGoals,
              style: GoogleFonts.instrumentSerif(
                fontSize: 28,
                color: _textGhost,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context).savEmptyStateSubtitle,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(
                fontSize: 14,
                color: _textMuted,
                height: 1.5,
              ),
            ),
            const SizedBox(height: 32),
            _GlassButton(
              label: 'Create Goal',
              icon: Icons.add,
              isPrimary: true,
              onTap: _showCreateGoal,
            ),
          ],
        ),
      ),
    );
  }

  // ── Main content ─────────────────────────────────────

  Widget _buildContent() {
    final ov = _overview!;
    final familyGoals =
        ov.goals.where((g) => g.type == SavingsGoalType.family).toList();
    final personalGoals =
        ov.goals.where((g) => g.type != SavingsGoalType.family).toList();

    return RefreshIndicator(
      onRefresh: _refresh,
      color: _accent,
      child: SafeArea(
        bottom: false,
        child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 24, 16, 100),
        children: [
          _buildHeader(),
          const SizedBox(height: 20),
          _buildStatsRow(ov),
          const SizedBox(height: 20),
          _buildLeftoverCards(ov),
          if (ov.recentAchievements.isNotEmpty) ...[
            const SizedBox(height: 20),
            _buildAchievements(ov.recentAchievements),
          ],
          if (familyGoals.isNotEmpty) ...[
            const SizedBox(height: 24),
            _buildSectionTitle('Family Goals', Icons.family_restroom),
            const SizedBox(height: 12),
            ...familyGoals.map(_buildGoalCard),
          ],
          const SizedBox(height: 24),
          _buildSectionTitle(
            personalGoals.isNotEmpty ? 'Personal Goals' : 'Goals',
            Icons.person_outline,
          ),
          const SizedBox(height: 12),
          if (personalGoals.isNotEmpty)
            ...personalGoals.map(_buildGoalCard)
          else
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 16),
              child: Text(
                AppLocalizations.of(context).savNoPersonalGoalsYet,
                style: GoogleFonts.sora(color: _textMuted, fontSize: 14),
                textAlign: TextAlign.center,
              ),
            ),
        ],
        ),
      ),
    );
  }

  // ═══════════════════════════════════════════════════════
  //  SECTION BUILDERS
  // ═══════════════════════════════════════════════════════

  Widget _buildHeader() {
    return Container(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 16),
          decoration: BoxDecoration(
            color: _cardBg,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(color: _cardBorder),
          ),
          child: Row(
            children: [
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  borderRadius: BorderRadius.circular(10),
                  gradient: LinearGradient(
                    colors: [_income, _income.withValues(alpha: 0.6)],
                  ),
                ),
                child: const Icon(Icons.savings, size: 22, color: Colors.white),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      AppLocalizations.of(context).savSavingsGoalsTitle,
                      style: GoogleFonts.sora(
                        fontSize: 20,
                        fontWeight: FontWeight.w700,
                        color: _textGhost,
                      ),
                    ),
                    Text(
                      AppLocalizations.of(context).savTrackYourFinancialDreams,
                      style: GoogleFonts.sora(
                        fontSize: 12,
                        color: _textMuted,
                      ),
                    ),
                  ],
                ),
              ),
              _GlassButton(
                label: 'New Goal',
                icon: Icons.add,
                isPrimary: true,
                onTap: _showCreateGoal,
              ),
            ],
          ),
    );
  }

  Widget _buildStatsRow(SavingsOverview ov) {
    return Row(
      children: [
        Expanded(
          child: _StatTile(
            label: 'Total Saved',
            value: formatCurrency(ov.totalSaved),
            icon: Icons.attach_money,
            color: _income,
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _StatTile(
            label: 'Active Goals',
            value: '${ov.activeGoalsCount}',
            icon: Icons.trending_up,
            color: const Color(0xFF60A5FA),
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _StatTile(
            label: 'Completed',
            value: '${ov.completedGoalsCount}',
            icon: Icons.emoji_events,
            color: const Color(0xFFFBBF24),
          ),
        ),
      ],
    );
  }

  Widget _buildLeftoverCards(SavingsOverview ov) {
    return Row(
      children: [
        Expanded(
          child: _GlassCard(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    const Text('💰', style: TextStyle(fontSize: 16)),
                    const SizedBox(width: 8),
                    Text(
                      AppLocalizations.of(context).savUnallocatedBudget,
                      style: GoogleFonts.sora(
                        fontSize: 12,
                        color: _textMuted,
                        fontWeight: FontWeight.w500,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                if (ov.hasFamilyBudget) ...[
                  // Show personal and family separately
                  Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(AppLocalizations.of(context).savPersonal,
                                style: GoogleFonts.sora(
                                    fontSize: 10, color: _textMuted)),
                            const SizedBox(height: 2),
                            Text(
                              formatCurrency(ov.personalLeftover),
                              style: GoogleFonts.firaCode(
                                fontSize: 16,
                                fontWeight: FontWeight.w600,
                                color: ov.personalLeftover >= 0 ? _income : _destructive,
                              ),
                            ),
                          ],
                        ),
                      ),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(AppLocalizations.of(context).savFamily,
                                style: GoogleFonts.sora(
                                    fontSize: 10, color: _textMuted)),
                            const SizedBox(height: 2),
                            Text(
                              formatCurrency(ov.familyLeftover),
                              style: GoogleFonts.firaCode(
                                fontSize: 16,
                                fontWeight: FontWeight.w600,
                                color: ov.familyLeftover >= 0 ? _income : _destructive,
                              ),
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ] else ...[
                  Text(
                    formatCurrency(ov.personalLeftover),
                    style: GoogleFonts.firaCode(
                      fontSize: 18,
                      fontWeight: FontWeight.w600,
                      color: ov.personalLeftover >= 0 ? _income : _destructive,
                    ),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    AppLocalizations.of(context).savIncomeMinusBudgetAllocations,
                    style: GoogleFonts.sora(
                      fontSize: 10,
                      color: _textMuted,
                    ),
                  ),
                ],
              ],
            ),
          ),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: _GlassCard(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    const Text('📊', style: TextStyle(fontSize: 16)),
                    const SizedBox(width: 8),
                    Text(
                      AppLocalizations.of(context).savAvailableForGoals,
                      style: GoogleFonts.sora(
                        fontSize: 12,
                        color: _textMuted,
                        fontWeight: FontWeight.w500,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                Text(
                  formatCurrency(ov.unallocatedAmount),
                  style: GoogleFonts.firaCode(
                    fontSize: 18,
                    fontWeight: FontWeight.w600,
                    color: _accent,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  ov.unallocatedAmount > 0
                      ? AppLocalizations.of(context).savNotYetAssignedToGoals
                      : AppLocalizations.of(context).savFullyAllocated,
                  style: GoogleFonts.sora(
                    fontSize: 10,
                    color: ov.unallocatedAmount > 0 ? _textMuted : _income,
                  ),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildAchievements(List<SavingsAchievement> achievements) {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.emoji_events, color: Color(0xFFFBBF24), size: 20),
              const SizedBox(width: 8),
              Text(
                AppLocalizations.of(context).savRecentAchievements,
                style: GoogleFonts.sora(
                  fontSize: 14,
                  fontWeight: FontWeight.w600,
                  color: _textGhost,
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          ...achievements.map((a) => Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Row(
                  children: [
                    Container(
                      width: 32,
                      height: 32,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: _achievementColor(a.badgeColor)
                            .withValues(alpha: 0.2),
                      ),
                      child: Icon(
                        _achievementIcon(a.badgeIcon),
                        size: 16,
                        color: _achievementColor(a.badgeColor),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            a.title,
                            style: GoogleFonts.sora(
                              fontSize: 13,
                              fontWeight: FontWeight.w600,
                              color: _textGhost,
                            ),
                          ),
                          Text(
                            a.description,
                            style: GoogleFonts.sora(
                              fontSize: 11,
                              color: _textMuted,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              )),
        ],
      ),
    );
  }

  Color _achievementColor(String badge) {
    return switch (badge) {
      'gold' => const Color(0xFFFBBF24),
      'silver' => const Color(0xFF9CA3AF),
      'bronze' => const Color(0xFFFB923C),
      _ => _accent,
    };
  }

  IconData _achievementIcon(String icon) {
    return switch (icon) {
      'star' => Icons.star,
      'trophy' => Icons.emoji_events,
      'medal' => Icons.military_tech,
      'rocket' => Icons.rocket_launch,
      _ => Icons.emoji_events,
    };
  }

  Widget _buildSectionTitle(String title, IconData icon) {
    return Row(
      children: [
        Icon(icon, size: 18, color: _textMuted),
        const SizedBox(width: 8),
        Text(
          title,
          style: GoogleFonts.sora(
            fontSize: 16,
            fontWeight: FontWeight.w600,
            color: _textGhost,
          ),
        ),
      ],
    );
  }

  // ── Goal card ────────────────────────────────────────

  /// The point of linking an account: showing when the envelope total and the real
  /// balance have drifted apart. Only the over-allocated case is treated as a
  /// warning — money in the account that no goal has claimed is just unallocated
  /// savings, which is normal and shouldn't nag.
  ///
  /// The numbers are account-wide, not goal-specific, because several goals can
  /// share one account. The copy says "goals here" for that reason: attributing the
  /// whole gap to this one goal would be a lie whenever it has siblings.
  Widget _buildAccountReconciliation(SavingsGoal goal) {
    final unallocated = goal.accountUnallocated;
    if (unallocated == null) return const SizedBox.shrink();

    // Earmarked purchases whose money has not left the account yet are not
    // drift: the bank still holds that money, but it is spoken for. Subtract
    // them before deciding whether anything is truly unassigned.
    final inFlight = goal.pendingTransferAmount ?? 0;
    final trulyUnassigned = unallocated - inFlight;
    final over = goal.isOverAllocated;
    final tone = over ? const Color(0xFFFBBF24) : _textMuted;
    final label = over
        ? 'Goals here exceed the balance by ${formatCurrency(unallocated.abs())}'
        : inFlight > 0 && trulyUnassigned.abs() < 0.005
            ? AppLocalizations.of(context).savInFlight(formatCurrency(inFlight))
            : inFlight > 0
                ? '${formatCurrency(trulyUnassigned)} unassigned · ${AppLocalizations.of(context).savInFlight(formatCurrency(inFlight))}'
                : '${formatCurrency(unallocated)} in this account is unassigned';

    // How many goals share this account decides which fix we can offer. Alone, the
    // balance unambiguously belongs to this goal and one tap can adopt it; with
    // siblings, only the user knows how to split it, so we open the reconcile modal.
    final siblings = (_overview?.goals ?? const <SavingsGoal>[])
        .where((g) => g.accountId == goal.accountId)
        .toList(growable: false);
    final isSole = siblings.length == 1;
    final canAct = goal.canContribute && unallocated != 0;

    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Row(
        children: [
          Icon(over ? Icons.error_outline : Icons.account_balance_outlined,
              size: 13, color: tone),
          const SizedBox(width: 6),
          Expanded(
            child: Text(
              '${goal.accountName ?? AppLocalizations.of(context).savLinkedAccount} · $label',
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: GoogleFonts.sora(fontSize: 11, color: tone),
            ),
          ),
          if (canAct) ...[
            const SizedBox(width: 8),
            GestureDetector(
              onTap: () =>
                  isSole ? _syncGoalToAccount(goal) : _openReconcile(siblings, goal),
              behavior: HitTestBehavior.opaque,
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                child: Text(
                  isSole
                      ? AppLocalizations.of(context).savSync
                      : AppLocalizations.of(context).savReconcile,
                  style: GoogleFonts.sora(
                    fontSize: 11,
                    fontWeight: FontWeight.w600,
                    color: _accent,
                  ),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }

  Future<void> _syncGoalToAccount(SavingsGoal goal) async {
    try {
      await savingsService.syncGoalToAccount(goal.id);
      if (!mounted) return;
      showAppToast(context, 'Synced to ${goal.accountName ?? 'the account'}');
      await _loadData();
    } catch (e) {
      if (mounted) showAppToast(context, 'Could not sync this goal');
    }
  }

  Future<void> _openReconcile(List<SavingsGoal> goals, SavingsGoal tapped) async {
    final changed = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _ReconcileSheet(
        accountId: tapped.accountId!,
        accountName: tapped.accountName ?? 'Linked account',
        accountBalance: tapped.accountBalance ?? 0,
        goals: goals,
      ),
    );
    if (changed == true) await _loadData();
  }

  Widget _buildGoalCard(SavingsGoal goal) {
    final color = _resolveColor(goal.colorTheme);
    final icon = _resolveIcon(goal.iconName);
    final progress = goal.progressPercentage.clamp(0, 100).toDouble();
    final isComplete = goal.isCompleted;

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: GestureDetector(
        onTap: () => _showGoalDetails(goal),
        child: Container(
              padding: const EdgeInsets.all(16),
              decoration: BoxDecoration(
                color: _cardBg,
                borderRadius: BorderRadius.circular(14),
                border: Border.all(color: _cardBorder),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  // ── Header row ────────────────────────
                  Row(
                    children: [
                      Container(
                        width: 44,
                        height: 44,
                        decoration: BoxDecoration(
                          borderRadius: BorderRadius.circular(12),
                          color: color.withValues(alpha: 0.15),
                        ),
                        child: Icon(icon, color: color, size: 22),
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              goal.name,
                              style: GoogleFonts.sora(
                                fontSize: 15,
                                fontWeight: FontWeight.w600,
                                color: _textGhost,
                              ),
                            ),
                            if (goal.type == SavingsGoalType.family)
                              Text(
                                AppLocalizations.of(context)
                                    .savByOwner(goal.ownerName),
                                style: GoogleFonts.sora(
                                  fontSize: 11,
                                  color: _textMuted,
                                ),
                              ),
                          ],
                        ),
                      ),
                      if (isComplete)
                        Container(
                          padding: const EdgeInsets.symmetric(
                              horizontal: 8, vertical: 4),
                          decoration: BoxDecoration(
                            color: _income.withValues(alpha: 0.15),
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              const Icon(Icons.check_circle,
                                  size: 14, color: _income),
                              const SizedBox(width: 4),
                              Text(
                                AppLocalizations.of(context).savComplete,
                                style: GoogleFonts.sora(
                                  fontSize: 11,
                                  fontWeight: FontWeight.w600,
                                  color: _income,
                                ),
                              ),
                            ],
                          ),
                        )
                      else if (goal.targetDate != null &&
                          goal.daysRemaining > 0)
                        Text(
                          AppLocalizations.of(context)
                              .savDaysLeft(goal.daysRemaining),
                          style: GoogleFonts.firaCode(
                            fontSize: 12,
                            color: goal.isOnTrack
                                ? _income
                                : const Color(0xFFFBBF24),
                          ),
                        ),
                    ],
                  ),
                  const SizedBox(height: 14),

                  // ── Progress bar ──────────────────────
                  ClipRRect(
                    borderRadius: BorderRadius.circular(4),
                    child: LinearProgressIndicator(
                      value: progress / 100,
                      minHeight: 6,
                      backgroundColor: Colors.white.withValues(alpha: 0.08),
                      color: isComplete ? _income : color,
                    ),
                  ),
                  const SizedBox(height: 10),

                  // ── Amounts row ───────────────────────
                  Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            formatCurrency(goal.currentAmount),
                            style: GoogleFonts.firaCode(
                              fontSize: 16,
                              fontWeight: FontWeight.w600,
                              color: isComplete ? _income : color,
                            ),
                          ),
                          Text(
                            AppLocalizations.of(context)
                                .savOfTarget(formatCurrency(goal.targetAmount)),
                            style: GoogleFonts.sora(
                              fontSize: 11,
                              color: _textMuted,
                            ),
                          ),
                        ],
                      ),
                      Text(
                        '${progress.toStringAsFixed(0)}%',
                        style: GoogleFonts.firaCode(
                          fontSize: 22,
                          fontWeight: FontWeight.w700,
                          color: isComplete ? _income : color,
                        ),
                      ),
                    ],
                  ),

                  // ── Linked account reconciliation ─────
                  if (goal.isLinkedToAccount) _buildAccountReconciliation(goal),

                  // ── Allocation & date row ─────────────
                  if (goal.allocationPercentage > 0 ||
                      goal.targetDate != null) ...[
                    const SizedBox(height: 10),
                    Row(
                      children: [
                        if (goal.allocationPercentage > 0) ...[
                          Icon(Icons.pie_chart_outline,
                              size: 13, color: _textMuted),
                          const SizedBox(width: 4),
                          Text(
                            AppLocalizations.of(context).savPercentAllocation(
                                goal.allocationPercentage.toStringAsFixed(0)),
                            style: GoogleFonts.sora(
                              fontSize: 11,
                              color: _textMuted,
                            ),
                          ),
                        ],
                        if (goal.allocationPercentage > 0 &&
                            goal.targetDate != null)
                          Padding(
                            padding:
                                const EdgeInsets.symmetric(horizontal: 8),
                            child: Text('·',
                                style: GoogleFonts.sora(color: _textMuted)),
                          ),
                        if (goal.targetDate != null) ...[
                          Icon(Icons.calendar_today,
                              size: 13, color: _textMuted),
                          const SizedBox(width: 4),
                          Text(
                            formatDate(goal.targetDate!),
                            style: GoogleFonts.sora(
                              fontSize: 11,
                              color: _textMuted,
                            ),
                          ),
                        ],
                      ],
                    ),
                  ],
                ],
              ),
            ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  GOAL DETAILS SHEET
// ═══════════════════════════════════════════════════════════

class _GoalDetailsSheet extends StatelessWidget {
  final SavingsGoal goal;
  final VoidCallback onEdited;
  final VoidCallback onDeleted;
  final VoidCallback onReset;
  final VoidCallback onAddFunds;
  final VoidCallback onRefresh;

  const _GoalDetailsSheet({
    required this.goal,
    required this.onEdited,
    required this.onDeleted,
    required this.onReset,
    required this.onAddFunds,
    required this.onRefresh,
  });

  @override
  Widget build(BuildContext context) {
    final color = _resolveColor(goal.colorTheme);
    final icon = _resolveIcon(goal.iconName);
    final progress = goal.progressPercentage.clamp(0, 100).toDouble();
    final remaining = goal.remainingAmount;

    return DraggableScrollableSheet(
      initialChildSize: 0.6,
      minChildSize: 0.3,
      maxChildSize: 0.85,
      expand: false,
      builder: (context, scroll) => Container(
        decoration: BoxDecoration(
          color: _sheetBg,
          borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
          border: Border.all(color: _cardBorder),
        ),
        child: ListView(
          controller: scroll,
          padding: const EdgeInsets.all(24),
          children: [
            // ── Handle ──────────────────────────────
            Center(
              child: Container(
                width: 40,
                height: 4,
                decoration: BoxDecoration(
                  color: Colors.white.withValues(alpha: 0.2),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 20),

            // ── Header with icon ────────────────────
            Row(
              children: [
                Container(
                  width: 52,
                  height: 52,
                  decoration: BoxDecoration(
                    borderRadius: BorderRadius.circular(14),
                    color: color.withValues(alpha: 0.15),
                  ),
                  child: Icon(icon, color: color, size: 26),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        goal.name,
                        style: GoogleFonts.sora(
                          fontSize: 20,
                          fontWeight: FontWeight.w700,
                          color: _textGhost,
                        ),
                      ),
                      if (goal.description != null &&
                          goal.description!.isNotEmpty)
                        Text(
                          goal.description!,
                          style: GoogleFonts.sora(
                            fontSize: 12,
                            color: _textMuted,
                          ),
                        ),
                    ],
                  ),
                ),
                if (goal.isOwner)
                  PopupMenuButton<String>(
                    icon: const Icon(Icons.more_vert, color: _textMuted),
                    color: _sheetBg,
                    onSelected: (v) {
                      Navigator.pop(context);
                      if (v == 'edit') onEdited();
                      if (v == 'reset') onReset();
                      if (v == 'delete') onDeleted();
                    },
                    itemBuilder: (_) => [
                      PopupMenuItem(
                        value: 'edit',
                        child: Row(
                          children: [
                            const Icon(Icons.edit_outlined,
                                size: 18, color: _textGhost),
                            const SizedBox(width: 10),
                            Text(AppLocalizations.of(context).savEdit,
                                style: GoogleFonts.sora(color: _textGhost)),
                          ],
                        ),
                      ),
                      PopupMenuItem(
                        value: 'reset',
                        child: Row(
                          children: [
                            const Icon(Icons.restart_alt,
                                size: 18, color: _textGhost),
                            const SizedBox(width: 10),
                            Text(
                                AppLocalizations.of(context)
                                    .savResetToZero(primaryCurrencySymbol),
                                style: GoogleFonts.sora(color: _textGhost)),
                          ],
                        ),
                      ),
                      PopupMenuItem(
                        value: 'delete',
                        child: Row(
                          children: [
                            const Icon(Icons.delete_outline,
                                size: 18, color: _destructive),
                            const SizedBox(width: 10),
                            Text(AppLocalizations.of(context).savDelete,
                                style: GoogleFonts.sora(color: _destructive)),
                          ],
                        ),
                      ),
                    ],
                  ),
              ],
            ),
            const SizedBox(height: 20),

            // ── Progress ────────────────────────────
            Text(
              AppLocalizations.of(context)
                  .savPercentComplete(progress.toStringAsFixed(1)),
              style: GoogleFonts.firaCode(
                fontSize: 24,
                fontWeight: FontWeight.w700,
                color: goal.isCompleted ? _income : color,
              ),
            ),
            const SizedBox(height: 8),
            ClipRRect(
              borderRadius: BorderRadius.circular(5),
              child: LinearProgressIndicator(
                value: progress / 100,
                minHeight: 10,
                backgroundColor: Colors.white.withValues(alpha: 0.08),
                color: goal.isCompleted ? _income : color,
              ),
            ),
            const SizedBox(height: 20),

            // ── Detail rows ─────────────────────────
            _detailRow('Saved', formatCurrency(goal.currentAmount)),
            _detailRow('Target', formatCurrency(goal.targetAmount)),
            _detailRow('Remaining', formatCurrency(remaining > 0 ? remaining : 0)),
            if (goal.allocationPercentage > 0)
              _detailRow('Allocation',
                  '${goal.allocationPercentage.toStringAsFixed(0)}%'),
            if (goal.targetDate != null)
              _detailRow('Target Date', formatDate(goal.targetDate!)),
            if (goal.dailyRequiredSavings > 0 && !goal.isCompleted)
              _detailRow('Daily Required',
                  formatCurrency(goal.dailyRequiredSavings)),
            if (goal.type == SavingsGoalType.family)
              _detailRow('Owner', goal.ownerName),

            // ── Achievement box ─────────────────────
            if (goal.isCompleted) ...[
              const SizedBox(height: 16),
              _GlassCard(
                child: Row(
                  children: [
                    const Text('🎉', style: TextStyle(fontSize: 24)),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            AppLocalizations.of(context).savGoalAchieved,
                            style: GoogleFonts.sora(
                              fontSize: 15,
                              fontWeight: FontWeight.w700,
                              color: _income,
                            ),
                          ),
                          Text(
                            AppLocalizations.of(context).savGoalAchievedCongrats,
                            style: GoogleFonts.sora(
                              fontSize: 12,
                              color: _textMuted,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ],

            // ── Action buttons ──────────────────────
            if (!goal.isCompleted && goal.canContribute) ...[
              const SizedBox(height: 24),
              SizedBox(
                width: double.infinity,
                child: _GlassButton(
                  label: 'Add Funds',
                  icon: Icons.add,
                  isPrimary: true,
                  onTap: () {
                    Navigator.pop(context);
                    onAddFunds();
                  },
                ),
              ),
            ],
            const SizedBox(height: 16),
          ],
        ),
      ),
    );
  }

  Widget _detailRow(String label, String value) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Text(label, style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          Text(
            value,
            style: GoogleFonts.firaCode(
              fontSize: 14,
              fontWeight: FontWeight.w600,
              color: _textGhost,
            ),
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  GOAL FORM SHEET (shared create / edit — DRY)
// ═══════════════════════════════════════════════════════════

class _GoalFormSheet extends StatefulWidget {
  final ScrollController scrollController;
  final SavingsGoal? editingGoal;
  final List<SavingsGoal> existingGoals;
  final bool hasFamilyBudget;
  final double personalLeftover;
  final double familyLeftover;
  final ValueChanged<SavingsGoal> onSaved;

  const _GoalFormSheet({
    required this.scrollController,
    this.editingGoal,
    required this.existingGoals,
    required this.hasFamilyBudget,
    required this.personalLeftover,
    required this.familyLeftover,
    required this.onSaved,
  });

  bool get isEditing => editingGoal != null;

  @override
  State<_GoalFormSheet> createState() => _GoalFormSheetState();
}

class _GoalFormSheetState extends State<_GoalFormSheet> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _nameCtrl;
  late final TextEditingController _descCtrl;
  late final TextEditingController _targetCtrl;
  late final TextEditingController _allocCtrl;
  late final TextEditingController _initialAmtCtrl;

  late SavingsGoalType _goalType;
  late String _iconName;
  late String _colorTheme;
  DateTime? _targetDate;

  bool _isLoading = false;
  String? _error;

  // ── Budget tracking toggle ─
  late bool _countTowardBudget;

  // ── Allocation mode: true = percentage, false = dollar ─
  bool _allocIsPercent = true;

  // ── Optional account link ─
  // Null = unlinked, which stays a normal choice rather than an unfinished form.
  int? _accountId;
  List<Account> _accounts = const [];

  @override
  void initState() {
    super.initState();
    final g = widget.editingGoal;
    _nameCtrl = TextEditingController(text: g?.name ?? '');
    _descCtrl = TextEditingController(text: g?.description ?? '');
    _targetCtrl = TextEditingController(
      text: g != null ? g.targetAmount.toStringAsFixed(0) : '',
    );
    _allocCtrl = TextEditingController(
      text: g != null ? g.allocationPercentage.toStringAsFixed(0) : '',
    );
    _initialAmtCtrl = TextEditingController();
    _goalType = g?.type ?? SavingsGoalType.personal;
    _iconName = g?.iconName ?? 'target';
    _colorTheme = g?.colorTheme ?? 'primary';
    _targetDate = g?.targetDate;
    _countTowardBudget = g?.countWithdrawalsTowardBudget ?? false;
    _accountId = g?.accountId;
    _loadAccounts();
  }

  /// Accounts are optional garnish on this form, so a failure to load them must not
  /// block creating a goal — the picker simply stays hidden.
  Future<void> _loadAccounts() async {
    try {
      final accounts = await accountsService.getAccounts();
      // Only account types that can actually hold savings. The server rejects the
      // rest, so offering them would just be a dead option: a credit card's balance
      // is debt, and an investment's value fluctuates rather than being a balance you
      // reconcile an envelope against. Mirrors LinkableAccountTypes in SavingsService.
      const linkable = {0, 1, 5}; // Checking, Savings, Cash
      if (mounted) {
        setState(() =>
            _accounts = accounts.where((a) => linkable.contains(a.type)).toList());
      }
    } catch (_) {
      // Leave the picker hidden.
    }
  }

  @override
  void dispose() {
    _nameCtrl.dispose();
    _descCtrl.dispose();
    _targetCtrl.dispose();
    _allocCtrl.dispose();
    _initialAmtCtrl.dispose();
    super.dispose();
  }

  // ── Allocation helpers ───────────────────────────────

  double get _availableLeftover => _goalType == SavingsGoalType.family
      ? widget.familyLeftover
      : widget.personalLeftover;

  double get _otherGoalsAllocation {
    final sameType = widget.existingGoals
        .where((g) =>
            g.isActive &&
            !g.isCompleted &&
            g.type == _goalType &&
            g.id != (widget.editingGoal?.id ?? -1))
        .fold<double>(0, (sum, g) => sum + g.allocationPercentage);
    return sameType;
  }

  double get _maxAllocation => (100 - _otherGoalsAllocation).clamp(0, 100);

  // ── Date picker ──────────────────────────────────────

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final date = await showDatePicker(
      context: context,
      initialDate: _targetDate ?? now.add(const Duration(days: 365)),
      firstDate: now.add(const Duration(days: 1)),
      lastDate: now.add(const Duration(days: 365 * 10)),
      builder: (context, child) => Theme(
        data: ThemeData.dark().copyWith(
          colorScheme: const ColorScheme.dark(
            primary: _accent,
            surface: _sheetBg,
          ),
        ),
        child: child!,
      ),
    );
    if (date != null) setState(() => _targetDate = date);
  }

  // ── Submit ───────────────────────────────────────────

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      final target = double.parse(_targetCtrl.text);
      double allocPct;
      if (_allocIsPercent) {
        allocPct = parseFlexibleAmount(_allocCtrl.text) ?? 0;
      } else {
        // Convert dollar amount to percentage of available leftover
        final dollarAmt = parseFlexibleAmount(_allocCtrl.text) ?? 0;
        allocPct = _availableLeftover > 0
            ? (dollarAmt / _availableLeftover * 100)
                .clamp(0, _maxAllocation)
            : 0;
      }

      SavingsGoal result;
      if (widget.isEditing) {
        result = await savingsService.updateGoal(
          widget.editingGoal!.id,
          name: _nameCtrl.text.trim(),
          targetAmount: target,
          allocationPercentage: allocPct,
          description: _descCtrl.text.trim(),
          targetDate: _targetDate,
          type: _goalType != widget.editingGoal!.type ? _goalType : null,
          iconName: _iconName,
          colorTheme: _colorTheme,
          countWithdrawalsTowardBudget: _countTowardBudget,
          accountId: _accountId,
          // An edit that clears the picker must say so explicitly, or the server
          // preserves the old link.
          clearAccountLink: _accountId == null,
        );
      } else {
        final initialAmt = parseFlexibleAmount(_initialAmtCtrl.text) ?? 0;
        result = await savingsService.createGoal(
          name: _nameCtrl.text.trim(),
          targetAmount: target,
          allocationPercentage: allocPct,
          type: _goalType,
          initialAmount: initialAmt,
          description: _descCtrl.text.trim(),
          targetDate: _targetDate,
          iconName: _iconName,
          colorTheme: _colorTheme,
          countWithdrawalsTowardBudget: _countTowardBudget,
          accountId: _accountId,
        );
      }

      analytics.track(
        widget.isEditing
            ? AnalyticsEvents.savingsGoalUpdated
            : AnalyticsEvents.savingsGoalCreated,
      );
      widget.onSaved(result);
    } catch (e) {
      setState(() => _error = 'Failed to save goal');
    } finally {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  // ═══════════════════════════════════════════════════════
  //  BUILD
  // ═══════════════════════════════════════════════════════

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: _sheetBg,
        borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
        border: Border.all(color: _cardBorder),
      ),
      child: Column(
        children: [
          _buildSheetHeader(),
          const Divider(color: _cardBorder, height: 1),
          Expanded(
            child: Form(
              key: _formKey,
              child: ListView(
                controller: widget.scrollController,
                padding: const EdgeInsets.all(20),
                children: [
                  if (_error != null) _buildError(),

                  // ── Current progress (edit only) ──────
                  if (widget.isEditing) ...[
                    _buildProgressInfo(),
                    const SizedBox(height: 20),
                  ],

                  // ── Goal type toggle ──────────────────
                  if (widget.hasFamilyBudget) ...[
                    _buildGoalTypeSelector(),
                    const SizedBox(height: 20),
                  ],

                  // ── Name ──────────────────────────────
                  _buildTextField(
                    controller: _nameCtrl,
                    label: 'Goal Name',
                    hint: 'e.g., Emergency Fund',
                    textCapitalization: TextCapitalization.words,
                    validator: (v) =>
                        v == null || v.trim().isEmpty ? 'Name is required' : null,
                  ),
                  const SizedBox(height: 16),

                  // ── Description ───────────────────────
                  _buildTextField(
                    controller: _descCtrl,
                    label: 'Description (optional)',
                    hint: 'What are you saving for?',
                    maxLines: 2,
                  ),
                  const SizedBox(height: 16),

                  // ── Target amount ─────────────────────
                  _buildTextField(
                    controller: _targetCtrl,
                    label: 'Target Amount',
                    prefix: '$primaryCurrencySymbol ',
                    keyboardType:
                        const TextInputType.numberWithOptions(decimal: true),
                    validator: (v) {
                      if (v == null || v.isEmpty) return 'Required';
                      final amt = parseFlexibleAmount(v);
                      if (amt == null || amt <= 0) return 'Must be > ${primaryCurrencySymbol}0';
                      if (widget.isEditing &&
                          amt < widget.editingGoal!.currentAmount) {
                        return 'Cannot be less than current savings';
                      }
                      return null;
                    },
                  ),
                  const SizedBox(height: 16),

                  // ── Initial amount (create only) ──────
                  if (!widget.isEditing) ...[
                    _buildTextField(
                      controller: _initialAmtCtrl,
                      label: 'Amount Already Saved (optional)',
                      hint: 'How much have you saved so far?',
                      prefix: '$primaryCurrencySymbol ',
                      keyboardType:
                          const TextInputType.numberWithOptions(decimal: true),
                      validator: (v) {
                        if (v == null || v.isEmpty) return null;
                        final amt = parseFlexibleAmount(v);
                        if (amt == null || amt < 0) return 'Must be ${primaryCurrencySymbol}0 or more';
                        final target = parseFlexibleAmount(_targetCtrl.text) ?? 0;
                        if (target > 0 && amt > target) {
                          return 'Cannot exceed target amount';
                        }
                        return null;
                      },
                    ),
                    const SizedBox(height: 16),
                  ],

                  // ── Allocation ────────────────────────
                  _buildAllocationField(),
                  const SizedBox(height: 16),

                  // ── Target date ───────────────────────
                  _buildDateField(),
                  const SizedBox(height: 20),

                  // ── Icon selection ────────────────────
                  _buildIconSelector(),
                  const SizedBox(height: 20),

                  // ── Color selection ───────────────────
                  _buildColorSelector(),
                  const SizedBox(height: 20),

                  // ── Count toward budget toggle ────────
                  _buildBudgetToggle(),
                  const SizedBox(height: 12),
                  _buildAccountPicker(),
                  const SizedBox(height: 24),

                  // ── Tip card ──────────────────────────
                  _buildTipCard(),
                  const SizedBox(height: 32),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  // ── Budget toggle ─────────────────────────────────────

  Widget _buildBudgetToggle() {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 4),
      decoration: BoxDecoration(
        color: Colors.white.withValues(alpha: 0.04),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _cardBorder),
      ),
      child: SwitchListTile.adaptive(
        contentPadding: EdgeInsets.zero,
        title: Text(
          AppLocalizations.of(context).savCountWithdrawalsTowardBudget,
          style: GoogleFonts.sora(fontSize: 13, color: _textGhost),
        ),
        subtitle: Text(
          AppLocalizations.of(context).savCountWithdrawalsSubtitle,
          style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
        ),
        value: _countTowardBudget,
        onChanged: (v) => setState(() => _countTowardBudget = v),
        activeTrackColor: _accent.withValues(alpha: 0.5),
        activeThumbColor: _accent,
      ),
    );
  }

  // ── Optional account link ────────────────────────────

  /// Lets a goal name the real account its money sits in, so the app can tell you
  /// when the two have drifted apart. Entirely optional: "Not linked" is the
  /// default and a perfectly good answer for cash or a shared checking account.
  Widget _buildAccountPicker() {
    if (_accounts.isEmpty) return const SizedBox.shrink();

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
      decoration: BoxDecoration(
        color: Colors.white.withValues(alpha: 0.04),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _cardBorder),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const SizedBox(height: 8),
          Text(
            AppLocalizations.of(context).savHeldInAccountOptional,
            style: GoogleFonts.sora(fontSize: 13, color: _textGhost),
          ),
          const SizedBox(height: 2),
          Text(
            AppLocalizations.of(context).savAccountFlagHint,
            style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
          ),
          DropdownButtonHideUnderline(
            child: DropdownButton<int?>(
              value: _accountId,
              isExpanded: true,
              dropdownColor: const Color(0xFF141420),
              icon: const Icon(Icons.expand_more, color: _textMuted, size: 20),
              style: GoogleFonts.sora(fontSize: 13, color: _textGhost),
              items: [
                DropdownMenuItem<int?>(
                  value: null,
                  child: Text(
                    AppLocalizations.of(context).savNotLinked,
                    style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
                  ),
                ),
                ..._accounts.map(
                  (a) => DropdownMenuItem<int?>(
                    value: a.id,
                    child: Text(
                      a.nickname?.isNotEmpty == true ? a.nickname! : a.name,
                      overflow: TextOverflow.ellipsis,
                      style: GoogleFonts.sora(fontSize: 13, color: _textGhost),
                    ),
                  ),
                ),
              ],
              onChanged: (v) => setState(() => _accountId = v),
            ),
          ),
          const SizedBox(height: 4),
        ],
      ),
    );
  }

  // ── Sheet header ─────────────────────────────────────

  Widget _buildSheetHeader() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 12, 12),
      child: Row(
        children: [
          GestureDetector(
            onTap: () => Navigator.pop(context),
            child: Text(
              AppLocalizations.of(context).savCancel,
              style: GoogleFonts.sora(fontSize: 14, color: _textMuted),
            ),
          ),
          const Spacer(),
          Text(
            widget.isEditing
                ? AppLocalizations.of(context).savEditGoal
                : AppLocalizations.of(context).savNewSavingsGoal,
            style: GoogleFonts.sora(
              fontSize: 16,
              fontWeight: FontWeight.w700,
              color: _textGhost,
            ),
          ),
          const Spacer(),
          GestureDetector(
            onTap: _isLoading ? null : _submit,
            child: _isLoading
                ? const SizedBox(
                    width: 20,
                    height: 20,
                    child: CircularProgressIndicator(
                        strokeWidth: 2, color: _accent),
                  )
                : Text(
                    widget.isEditing
                        ? AppLocalizations.of(context).savSave
                        : AppLocalizations.of(context).savCreate,
                    style: GoogleFonts.sora(
                      fontSize: 14,
                      fontWeight: FontWeight.w600,
                      color: _accent,
                    ),
                  ),
          ),
          const SizedBox(width: 8),
        ],
      ),
    );
  }

  Widget _buildError() {
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Container(
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: _destructive.withValues(alpha: 0.1),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: _destructive.withValues(alpha: 0.3)),
        ),
        child: Text(
          _error!,
          style: GoogleFonts.sora(fontSize: 13, color: _destructive),
        ),
      ),
    );
  }

  Widget _buildProgressInfo() {
    final g = widget.editingGoal!;
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: _income.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _income.withValues(alpha: 0.2)),
      ),
      child: Row(
        children: [
          const Icon(Icons.savings, color: _income, size: 22),
          const SizedBox(width: 12),
          Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                AppLocalizations.of(context).savCurrentProgress,
                style: GoogleFonts.sora(
                  fontSize: 11,
                  color: _textMuted,
                ),
              ),
              Text(
                formatCurrency(g.currentAmount),
                style: GoogleFonts.firaCode(
                  fontSize: 16,
                  fontWeight: FontWeight.w700,
                  color: _income,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  // ── Goal type selector ───────────────────────────────

  Widget _buildGoalTypeSelector() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          AppLocalizations.of(context).savGoalType,
          style: GoogleFonts.sora(
            fontSize: 13,
            fontWeight: FontWeight.w500,
            color: _textMuted,
          ),
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            Expanded(
              child: _typeChip(
                SavingsGoalType.personal,
                'Personal',
                Icons.person_outline,
                widget.personalLeftover,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _typeChip(
                SavingsGoalType.family,
                'Family',
                Icons.family_restroom,
                widget.familyLeftover,
              ),
            ),
          ],
        ),
      ],
    );
  }

  Widget _typeChip(
    SavingsGoalType type,
    String label,
    IconData icon,
    double leftover,
  ) {
    final isSelected = _goalType == type;
    return GestureDetector(
      onTap: () => setState(() => _goalType = type),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 200),
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: isSelected ? _accent.withValues(alpha: 0.12) : _cardBg,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(
            color: isSelected ? _accent.withValues(alpha: 0.5) : _cardBorder,
          ),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon,
                    size: 18,
                    color: isSelected ? _accent : _textMuted),
                const SizedBox(width: 8),
                Text(
                  label,
                  style: GoogleFonts.sora(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: isSelected ? _textGhost : _textMuted,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 6),
            Text(
              AppLocalizations.of(context)
                  .savAmountAvailable(formatCurrency(leftover)),
              style: GoogleFonts.firaCode(
                fontSize: 11,
                color: isSelected ? _income : _textMuted,
              ),
            ),
          ],
        ),
      ),
    );
  }

  // ── Allocation field with %/$ toggle ─────────────────

  Widget _buildAllocationField() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Text(
              AppLocalizations.of(context).savMonthlyAllocation,
              style: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w500,
                color: _textMuted,
              ),
            ),
            const Spacer(),
            // Toggle % / $
            Container(
              decoration: BoxDecoration(
                color: _cardBg,
                borderRadius: BorderRadius.circular(6),
                border: Border.all(color: _cardBorder),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  _toggleBtn('%', _allocIsPercent, () {
                    if (!_allocIsPercent) _convertAllocation(toPercent: true);
                    setState(() => _allocIsPercent = true);
                  }),
                  _toggleBtn(primaryCurrencySymbol, !_allocIsPercent, () {
                    if (_allocIsPercent) _convertAllocation(toPercent: false);
                    setState(() => _allocIsPercent = false);
                  }),
                ],
              ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        _buildTextField(
          controller: _allocCtrl,
          label: '',
          hint: _allocIsPercent ? '0-100' : 'Dollar amount',
          prefix: _allocIsPercent ? '' : '$primaryCurrencySymbol ',
          suffix: _allocIsPercent ? '%' : null,
          keyboardType:
              const TextInputType.numberWithOptions(decimal: true),
          validator: (v) {
            if (v == null || v.isEmpty) return null; // 0% is valid
            final val = parseFlexibleAmount(v);
            if (val == null || val < 0) return 'Invalid amount';
            if (_allocIsPercent && val > _maxAllocation) {
              return 'Max ${_maxAllocation.toStringAsFixed(0)}% '
                  '(other goals use ${_otherGoalsAllocation.toStringAsFixed(0)}%)';
            }
            return null;
          },
        ),
        const SizedBox(height: 4),
        Text(
          AppLocalizations.of(context).savAvailableLeftoverMax(
              formatCurrency(_availableLeftover),
              _maxAllocation.toStringAsFixed(0)),
          style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
        ),
      ],
    );
  }

  Widget _toggleBtn(String label, bool active, VoidCallback onTap) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: active ? _accent.withValues(alpha: 0.2) : Colors.transparent,
          borderRadius: BorderRadius.circular(4),
        ),
        child: Text(
          label,
          style: GoogleFonts.firaCode(
            fontSize: 13,
            fontWeight: FontWeight.w600,
            color: active ? _accent : _textMuted,
          ),
        ),
      ),
    );
  }

  void _convertAllocation({required bool toPercent}) {
    final val = parseFlexibleAmount(_allocCtrl.text) ?? 0;
    if (toPercent && _availableLeftover > 0) {
      // Dollar → percent
      final pct = (val / _availableLeftover * 100).clamp(0, 100);
      _allocCtrl.text = pct.toStringAsFixed(0);
    } else if (!toPercent && _availableLeftover > 0) {
      // Percent → dollar
      final dollars = val / 100 * _availableLeftover;
      _allocCtrl.text = dollars.toStringAsFixed(2);
    }
  }

  // ── Date picker ──────────────────────────────────────

  Widget _buildDateField() {
    return GestureDetector(
      onTap: _pickDate,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: _cardBorder),
        ),
        child: Row(
          children: [
            Icon(Icons.calendar_today, size: 18, color: _textMuted),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    AppLocalizations.of(context).savTargetDate,
                    style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                  ),
                  Text(
                    _targetDate != null
                        ? formatDate(_targetDate!)
                        : AppLocalizations.of(context).savOptionalOpenEnded,
                    style: GoogleFonts.sora(
                      fontSize: 14,
                      color: _targetDate != null ? _textGhost : _textMuted,
                    ),
                  ),
                ],
              ),
            ),
            if (_targetDate != null)
              GestureDetector(
                onTap: () => setState(() => _targetDate = null),
                child: const Icon(Icons.close, size: 18, color: _textMuted),
              ),
          ],
        ),
      ),
    );
  }

  // ── Icon selector ────────────────────────────────────

  Widget _buildIconSelector() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          AppLocalizations.of(context).savIcon,
          style: GoogleFonts.sora(
            fontSize: 13,
            fontWeight: FontWeight.w500,
            color: _textMuted,
          ),
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 10,
          runSpacing: 10,
          children: _selectableIcons
              .map((name) => _iconChip(name, _resolveIcon(name)))
              .toList(),
        ),
      ],
    );
  }

  Widget _iconChip(String name, IconData icon) {
    final isSelected = _iconName == name;
    final color = _resolveColor(_colorTheme);
    return GestureDetector(
      onTap: () => setState(() => _iconName = name),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 200),
        width: 44,
        height: 44,
        decoration: BoxDecoration(
          color: isSelected ? color.withValues(alpha: 0.2) : _cardBg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: isSelected ? color.withValues(alpha: 0.6) : _cardBorder,
          ),
        ),
        child: Icon(
          icon,
          size: 20,
          color: isSelected ? color : _textMuted,
        ),
      ),
    );
  }

  // ── Color selector ───────────────────────────────────

  Widget _buildColorSelector() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          AppLocalizations.of(context).savColorTheme,
          style: GoogleFonts.sora(
            fontSize: 13,
            fontWeight: FontWeight.w500,
            color: _textMuted,
          ),
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 10,
          runSpacing: 10,
          children: _selectableColors
              .map((name) => _colorChip(name, _resolveColor(name)))
              .toList(),
        ),
      ],
    );
  }

  Widget _colorChip(String name, Color color) {
    final isSelected = _colorTheme == name;
    return GestureDetector(
      onTap: () => setState(() => _colorTheme = name),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 200),
        width: 36,
        height: 36,
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.3),
          shape: BoxShape.circle,
          border: Border.all(
            color: isSelected ? color : Colors.transparent,
            width: 2,
          ),
        ),
        child: isSelected
            ? Icon(Icons.check, size: 16, color: color)
            : null,
      ),
    );
  }

  // ── Tip card ─────────────────────────────────────────

  Widget _buildTipCard() {
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: _accent.withValues(alpha: 0.06),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _accent.withValues(alpha: 0.15)),
      ),
      child: Row(
        children: [
          const Icon(Icons.lightbulb_outline, size: 18, color: _accent),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              AppLocalizations.of(context).savTargetDateTip,
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
            ),
          ),
        ],
      ),
    );
  }

  // ── Shared text field ────────────────────────────────

  Widget _buildTextField({
    required TextEditingController controller,
    required String label,
    String? hint,
    String? prefix,
    String? suffix,
    int maxLines = 1,
    TextCapitalization textCapitalization = TextCapitalization.none,
    TextInputType? keyboardType,
    String? Function(String?)? validator,
  }) {
    return TextFormField(
      controller: controller,
      maxLines: maxLines,
      textCapitalization: textCapitalization,
      keyboardType: keyboardType,
      validator: validator,
      style: GoogleFonts.sora(fontSize: 14, color: _textGhost),
      decoration: InputDecoration(
        labelText: label.isNotEmpty ? label : null,
        hintText: hint,
        prefixText: prefix,
        suffixText: suffix,
        labelStyle: GoogleFonts.sora(fontSize: 13, color: _textMuted),
        hintStyle: GoogleFonts.sora(fontSize: 13, color: _textMuted.withValues(alpha: 0.6)),
        prefixStyle: GoogleFonts.firaCode(fontSize: 14, color: _textGhost),
        suffixStyle: GoogleFonts.firaCode(fontSize: 14, color: _textMuted),
        filled: true,
        fillColor: _cardBg,
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: BorderSide(color: _cardBorder),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: BorderSide(color: _cardBorder),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: const BorderSide(color: _accent),
        ),
        errorBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: const BorderSide(color: _destructive),
        ),
        errorStyle: GoogleFonts.sora(fontSize: 11, color: _destructive),
        contentPadding:
            const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  ALLOCATE FUNDS SHEET
// ═══════════════════════════════════════════════════════════

class _AllocateFundsSheet extends StatefulWidget {
  final SavingsGoal goal;
  final ValueChanged<SavingsGoal> onAllocated;

  const _AllocateFundsSheet({
    required this.goal,
    required this.onAllocated,
  });

  @override
  State<_AllocateFundsSheet> createState() => _AllocateFundsSheetState();
}

class _AllocateFundsSheetState extends State<_AllocateFundsSheet> {
  final _amountCtrl = TextEditingController();
  bool _isLoading = false;
  String? _error;

  double get _remaining =>
      (widget.goal.targetAmount - widget.goal.currentAmount)
          .clamp(0, double.infinity);

  @override
  void dispose() {
    _amountCtrl.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    final amount = parseFlexibleAmount(_amountCtrl.text);
    if (amount == null || amount <= 0) {
      setState(() => _error = 'Enter a valid amount');
      return;
    }

    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      final updated = await savingsService.allocateToGoal(
        widget.goal.id,
        amount: amount,
      );
      analytics.track(AnalyticsEvents.savingsGoalUpdated, {
        'is_fund': true,
        'amount': amount,
      });
      // Funding that reaches the target is the goal-completion signal.
      if (updated.currentAmount >= updated.targetAmount &&
          updated.targetAmount > 0) {
        analytics.track(AnalyticsEvents.savingsGoalCompleted);
      }
      if (mounted) {
        Navigator.pop(context);
        widget.onAllocated(updated);
      }
    } catch (e) {
      if (mounted) setState(() => _error = 'Failed to add funds');
    } finally {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  void _fillRemaining() {
    _amountCtrl.text = _remaining.toStringAsFixed(2);
    setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final g = widget.goal;
    final color = _resolveColor(g.colorTheme);
    final progress = g.progressPercentage.clamp(0, 100).toDouble();

    // Preview new progress
    final previewAmount = parseFlexibleAmount(_amountCtrl.text) ?? 0;
    final previewTotal = g.currentAmount + previewAmount;
    final previewPct =
        g.targetAmount > 0 ? (previewTotal / g.targetAmount * 100).clamp(0, 100) : 0.0;

    return Container(
      decoration: BoxDecoration(
        color: _sheetBg,
        borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
        border: Border.all(color: _cardBorder),
      ),
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // ── Handle ────────────────────────────────
          Center(
            child: Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: Colors.white.withValues(alpha: 0.2),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          const SizedBox(height: 20),

          Text(
            AppLocalizations.of(context).savAddFunds,
            style: GoogleFonts.sora(
              fontSize: 18,
              fontWeight: FontWeight.w700,
              color: _textGhost,
            ),
          ),
          const SizedBox(height: 4),
          Text(
            AppLocalizations.of(context).savToGoalName(g.name),
            style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
          ),
          const SizedBox(height: 16),

          // ── Current progress ──────────────────────
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text(
                formatCurrency(g.currentAmount),
                style: GoogleFonts.firaCode(
                  fontSize: 16,
                  fontWeight: FontWeight.w600,
                  color: color,
                ),
              ),
              Text(
                '${progress.toStringAsFixed(0)}%',
                style: GoogleFonts.firaCode(
                  fontSize: 14,
                  color: _textMuted,
                ),
              ),
              Text(
                formatCurrency(g.targetAmount),
                style: GoogleFonts.firaCode(
                  fontSize: 14,
                  color: _textMuted,
                ),
              ),
            ],
          ),
          const SizedBox(height: 6),
          ClipRRect(
            borderRadius: BorderRadius.circular(4),
            child: LinearProgressIndicator(
              value: progress / 100,
              minHeight: 6,
              backgroundColor: Colors.white.withValues(alpha: 0.08),
              color: color,
            ),
          ),
          const SizedBox(height: 4),
          Text(
            AppLocalizations.of(context)
                .savRemainingAmount(formatCurrency(_remaining)),
            style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
          ),
          const SizedBox(height: 20),

          // ── Amount input ──────────────────────────
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: _amountCtrl,
                  autofocus: true,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  style: GoogleFonts.firaCode(
                      fontSize: 16, color: _textGhost),
                  onChanged: (_) => setState(() => _error = null),
                  decoration: InputDecoration(
                    prefixText: '$primaryCurrencySymbol ',
                    prefixStyle: GoogleFonts.firaCode(
                        fontSize: 16, color: _textGhost),
                    hintText: '0.00',
                    hintStyle: GoogleFonts.firaCode(
                        fontSize: 16, color: _textMuted.withValues(alpha: 0.5)),
                    errorText: _error,
                    errorStyle:
                        GoogleFonts.sora(fontSize: 11, color: _destructive),
                    filled: true,
                    fillColor: _cardBg,
                    border: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(10),
                      borderSide: BorderSide(color: _cardBorder),
                    ),
                    enabledBorder: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(10),
                      borderSide: BorderSide(color: _cardBorder),
                    ),
                    focusedBorder: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(10),
                      borderSide: const BorderSide(color: _accent),
                    ),
                  ),
                ),
              ),
              if (_remaining > 0) ...[
                const SizedBox(width: 10),
                _GlassButton(
                  label: 'Fill',
                  icon: Icons.arrow_upward,
                  onTap: _fillRemaining,
                ),
              ],
            ],
          ),
          const SizedBox(height: 16),

          // ── Preview ───────────────────────────────
          if (previewAmount > 0)
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: _income.withValues(alpha: 0.06),
                borderRadius: BorderRadius.circular(10),
                border: Border.all(color: _income.withValues(alpha: 0.15)),
              ),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Text(
                    AppLocalizations.of(context).savNewBalance,
                    style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                  ),
                  Text(
                    '${formatCurrency(previewTotal)} (${previewPct.toStringAsFixed(1)}%)',
                    style: GoogleFonts.firaCode(
                      fontSize: 13,
                      fontWeight: FontWeight.w600,
                      color: _income,
                    ),
                  ),
                ],
              ),
            ),
          const SizedBox(height: 20),

          // ── Submit ────────────────────────────────
          SizedBox(
            width: double.infinity,
            height: 48,
            child: ElevatedButton(
              onPressed: _isLoading ? null : _submit,
              style: ElevatedButton.styleFrom(
                backgroundColor: _accent,
                foregroundColor: Colors.white,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(12),
                ),
                disabledBackgroundColor: _accent.withValues(alpha: 0.3),
              ),
              child: _isLoading
                  ? const SizedBox(
                      width: 20,
                      height: 20,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: Colors.white,
                      ),
                    )
                  : Text(
                      AppLocalizations.of(context).savAddFunds,
                      style: GoogleFonts.sora(
                        fontSize: 15,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
            ),
          ),
          SizedBox(height: MediaQuery.of(context).viewInsets.bottom + 16),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  SHARED UI COMPONENTS
// ═══════════════════════════════════════════════════════════

/// Glassmorphic card wrapper.
class _GlassCard extends StatelessWidget {
  final Widget child;
  const _GlassCard({required this.child});

  @override
  Widget build(BuildContext context) {
    return Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: _cardBg,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: _cardBorder),
          ),
          child: child,
    );
  }
}

/// Stat tile for the overview row.
class _StatTile extends StatelessWidget {
  final String label;
  final String value;
  final IconData icon;
  final Color color;

  const _StatTile({
    required this.label,
    required this.value,
    required this.icon,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 18, color: color),
          const SizedBox(height: 8),
          // Fixed-height, single-line value so long amounts (e.g. $25,895.00)
          // shrink to fit instead of wrapping — keeps all tiles the same height.
          SizedBox(
            height: 22,
            width: double.infinity,
            child: FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerLeft,
              child: Text(
                value,
                maxLines: 1,
                softWrap: false,
                style: GoogleFonts.firaCode(
                  fontSize: 16,
                  fontWeight: FontWeight.w700,
                  color: _textGhost,
                ),
              ),
            ),
          ),
          const SizedBox(height: 2),
          Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
          ),
        ],
      ),
    );
  }
}

/// Reusable glassmorphic button.
class _GlassButton extends StatelessWidget {
  final String label;
  final IconData icon;
  final VoidCallback onTap;
  final bool isPrimary;

  const _GlassButton({
    required this.label,
    required this.icon,
    required this.onTap,
    this.isPrimary = false,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
        decoration: BoxDecoration(
          color: isPrimary ? _accent.withValues(alpha: 0.15) : _cardBg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: isPrimary ? _accent.withValues(alpha: 0.4) : _cardBorder,
          ),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 16, color: isPrimary ? _accent : _textMuted),
            const SizedBox(width: 6),
            Text(
              label,
              style: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w600,
                color: isPrimary ? _accent : _textGhost,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Confirmation bottom sheet.
class _ConfirmSheet extends StatefulWidget {
  final String title;
  final String message;
  final String confirmLabel;
  final bool isDestructive;
  final Future<void> Function() onConfirm;

  const _ConfirmSheet({
    required this.title,
    required this.message,
    required this.confirmLabel,
    this.isDestructive = false,
    required this.onConfirm,
  });

  @override
  State<_ConfirmSheet> createState() => _ConfirmSheetState();
}

class _ConfirmSheetState extends State<_ConfirmSheet> {
  bool _isLoading = false;

  Future<void> _confirm() async {
    setState(() => _isLoading = true);
    try {
      await widget.onConfirm();
      if (mounted) Navigator.pop(context);
    } catch (e) {
      if (mounted) {
        setState(() => _isLoading = false);
        showAppToast(context, 'Failed: $e', type: AppToastType.error);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final confirmColor = widget.isDestructive ? _destructive : _accent;

    return Container(
      decoration: BoxDecoration(
        color: _sheetBg,
        borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
        border: Border.all(color: _cardBorder),
      ),
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
                color: Colors.white.withValues(alpha: 0.2),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          const SizedBox(height: 20),
          Text(
            widget.title,
            style: GoogleFonts.sora(
              fontSize: 18,
              fontWeight: FontWeight.w700,
              color: _textGhost,
            ),
          ),
          const SizedBox(height: 8),
          Text(
            widget.message,
            style: GoogleFonts.sora(
              fontSize: 14,
              color: _textMuted,
              height: 1.4,
            ),
          ),
          const SizedBox(height: 24),
          Row(
            children: [
              Expanded(
                child: OutlinedButton(
                  onPressed: () => Navigator.pop(context),
                  style: OutlinedButton.styleFrom(
                    foregroundColor: _textMuted,
                    side: BorderSide(color: _cardBorder),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(10),
                    ),
                    padding: const EdgeInsets.symmetric(vertical: 14),
                  ),
                  child: Text(AppLocalizations.of(context).savCancel,
                      style: GoogleFonts.sora(fontSize: 14)),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: ElevatedButton(
                  onPressed: _isLoading ? null : _confirm,
                  style: ElevatedButton.styleFrom(
                    backgroundColor: confirmColor,
                    foregroundColor: Colors.white,
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(10),
                    ),
                    padding: const EdgeInsets.symmetric(vertical: 14),
                    disabledBackgroundColor:
                        confirmColor.withValues(alpha: 0.3),
                  ),
                  child: _isLoading
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: Colors.white,
                          ),
                        )
                      : Text(
                          widget.confirmLabel,
                          style: GoogleFonts.sora(
                            fontSize: 14,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
        ],
      ),
    );
  }
}

// ── Reconcile sheet ─────────────────────────────────────────────────────────

/// Redistributes balances across the goals sharing one account.
///
/// Deliberately does NOT require the goals to sum exactly to the balance. Money in
/// the account that no goal claims is a legitimate place to stop — only the opposite
/// (goals promising more than the account holds) is a problem. Forcing an exact match
/// would trap someone who simply hasn't decided where the rest belongs yet.
class _ReconcileSheet extends StatefulWidget {
  const _ReconcileSheet({
    required this.accountId,
    required this.accountName,
    required this.accountBalance,
    required this.goals,
  });

  final int accountId;
  final String accountName;
  final double accountBalance;
  final List<SavingsGoal> goals;

  @override
  State<_ReconcileSheet> createState() => _ReconcileSheetState();
}

class _ReconcileSheetState extends State<_ReconcileSheet> {
  late final Map<int, TextEditingController> _controllers;
  bool _isSaving = false;
  String? _error;

  /// Goals the caller may not contribute to still count toward the totals — leaving a
  /// family member's goal out would make the arithmetic lie — but cannot be edited.
  late final List<SavingsGoal> _editable;

  @override
  void initState() {
    super.initState();
    _editable = widget.goals.where((g) => g.canContribute).toList();
    _controllers = {
      for (final g in widget.goals)
        g.id: TextEditingController(text: g.currentAmount.toStringAsFixed(2)),
    };
    for (final c in _controllers.values) {
      c.addListener(() => setState(() {}));
    }
  }

  @override
  void dispose() {
    for (final c in _controllers.values) {
      c.dispose();
    }
    super.dispose();
  }

  double _amountFor(SavingsGoal g) =>
      parseFlexibleAmount(_controllers[g.id]!.text.trim()) ?? g.currentAmount;

  double get _allocated =>
      widget.goals.fold<double>(0, (sum, g) => sum + _amountFor(g));

  double get _remaining => widget.accountBalance - _allocated;

  /// Cent tolerance, because these are parsed doubles and a user typing an exact
  /// split shouldn't be told they're a fraction of a penny over.
  bool get _isOver => _remaining < -0.004;

  bool get _hasNegative => widget.goals.any((g) => _amountFor(g) < 0);

  /// Spreads the account balance across editable goals in proportion to what each
  /// already holds, which is the least surprising default when they all drifted
  /// together. Equal split when nothing is allocated yet, since proportions of zero
  /// are undefined rather than equal.
  void _distributeProportionally() {
    final editableTotal =
        _editable.fold<double>(0, (sum, g) => sum + g.currentAmount);
    final lockedTotal = widget.goals
        .where((g) => !g.canContribute)
        .fold<double>(0, (sum, g) => sum + g.currentAmount);
    final available = widget.accountBalance - lockedTotal;
    if (_editable.isEmpty || available < 0) return;

    for (final g in _editable) {
      final share = editableTotal > 0
          ? available * (g.currentAmount / editableTotal)
          : available / _editable.length;
      _controllers[g.id]!.text = share.toStringAsFixed(2);
    }
    setState(() {});
  }

  Future<void> _apply() async {
    setState(() {
      _isSaving = true;
      _error = null;
    });
    try {
      await savingsService.reconcileAccount(
        widget.accountId,
        {for (final g in _editable) g.id: _amountFor(g)},
      );
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      setState(() => _error = 'Could not save these changes');
    } finally {
      if (mounted) setState(() => _isSaving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final remainingLabel = _isOver
        ? 'Goals exceed the balance by ${formatCurrency(_remaining.abs())}'
        : _remaining.abs() < 0.005
            ? 'Matches the account exactly'
            : '${formatCurrency(_remaining)} will stay unassigned';

    return Container(
      padding: EdgeInsets.only(
        bottom: MediaQuery.of(context).viewInsets.bottom,
      ),
      decoration: const BoxDecoration(
        color: Color(0xFF0B0B14),
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      child: SafeArea(
        top: false,
        child: SingleChildScrollView(
          padding: const EdgeInsets.fromLTRB(20, 16, 20, 20),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      widget.accountName,
                      style: GoogleFonts.sora(
                        fontSize: 16,
                        fontWeight: FontWeight.w600,
                        color: _textGhost,
                      ),
                    ),
                  ),
                  Text(
                    formatCurrency(widget.accountBalance),
                    style: GoogleFonts.firaCode(
                      fontSize: 16,
                      fontWeight: FontWeight.w700,
                      color: _income,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                AppLocalizations.of(context).savSetWhatEachGoalHolds,
                style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
              ),
              const SizedBox(height: 16),
              ...widget.goals.map(_buildGoalRow),
              const Divider(height: 28, color: _cardBorder),
              Row(
                children: [
                  Icon(
                    _isOver ? Icons.error_outline : Icons.check_circle_outline,
                    size: 15,
                    color: _isOver ? const Color(0xFFFBBF24) : _textMuted,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      remainingLabel,
                      style: GoogleFonts.sora(
                        fontSize: 12,
                        color: _isOver ? const Color(0xFFFBBF24) : _textMuted,
                      ),
                    ),
                  ),
                ],
              ),
              if (_error != null) ...[
                const SizedBox(height: 10),
                Text(
                  _error!,
                  style: GoogleFonts.sora(
                    fontSize: 12,
                    color: const Color(0xFFF87171),
                  ),
                ),
              ],
              const SizedBox(height: 16),
              Row(
                children: [
                  TextButton(
                    onPressed: _isSaving ? null : _distributeProportionally,
                    child: Text(
                      AppLocalizations.of(context).savDistributeProportionally,
                      style: GoogleFonts.sora(fontSize: 12, color: _accent),
                    ),
                  ),
                  const Spacer(),
                  ElevatedButton(
                    // Blocked only on genuinely invalid input, never on an unassigned
                    // surplus — that is a legitimate answer.
                    onPressed: (_isSaving || _hasNegative) ? null : _apply,
                    style: ElevatedButton.styleFrom(
                      backgroundColor: _accent,
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(10),
                      ),
                    ),
                    child: Text(
                      _isSaving
                          ? AppLocalizations.of(context).savSaving
                          : AppLocalizations.of(context).savApply,
                      style: GoogleFonts.sora(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: Colors.white,
                      ),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildGoalRow(SavingsGoal goal) {
    final locked = !goal.canContribute;
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  goal.name,
                  style: GoogleFonts.sora(fontSize: 13, color: _textGhost),
                ),
                if (locked)
                  Text(
                    AppLocalizations.of(context).savBelongsTo(goal.ownerName),
                    style: GoogleFonts.sora(fontSize: 10, color: _textMuted),
                  ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          SizedBox(
            width: 110,
            child: TextField(
              controller: _controllers[goal.id],
              enabled: !locked,
              keyboardType:
                  const TextInputType.numberWithOptions(decimal: true),
              textAlign: TextAlign.right,
              style: GoogleFonts.firaCode(
                fontSize: 13,
                color: locked ? _textMuted : _textGhost,
              ),
              decoration: InputDecoration(
                isDense: true,
                prefixText: r'$',
                prefixStyle:
                    GoogleFonts.firaCode(fontSize: 13, color: _textMuted),
                contentPadding:
                    const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(8),
                  borderSide: const BorderSide(color: _cardBorder),
                ),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(8),
                  borderSide: const BorderSide(color: _cardBorder),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
