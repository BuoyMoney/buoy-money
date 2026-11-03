import 'dart:async';
import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/budget_models.dart';
import '../models/family_models.dart';
import '../providers/auth_provider.dart';
import '../utils/currency.dart';
import '../services/analytics_service.dart';
import '../services/budget_service.dart';
import '../services/gamification_service.dart';
import '../utils/budget_period_label.dart';
import '../services/family_service.dart';
import '../utils/formatters.dart';
import '../utils/ticker_preferences.dart';
import '../widgets/cinematic/live_ticking_amount.dart';
import '../widgets/cinematic/rollover_display.dart';
import '../widgets/app_toast.dart';
import '../widgets/budget_modals.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _pending = Color(0xFFFBBF24);

/// Dual view modes — compact / count-up toggle
enum _ViewMode { compact, countUp }

/// Budget scope — personal / family toggle
enum _Scope { personal, family }

class BudgetsPage extends StatefulWidget {
  const BudgetsPage({super.key});

  @override
  State<BudgetsPage> createState() => _BudgetsPageState();
}

class _BudgetsPageState extends State<BudgetsPage>
    with SingleTickerProviderStateMixin {
  // Data
  Budget? _personalBudget;
  Budget? _familyBudget;
  Budget? get _activeBudget =>
      _scope == _Scope.family ? _familyBudget : _personalBudget;
  GroupedBudgetOverview? _groupedOverview;
  List<CountUpBudgetStatus> _countUpStatuses = [];
  List<BudgetCategory> _userAllowances = [];
  List<FamilyMember> _familyMembers = [];
  bool _isLoading = true;
  String? _error;

  // Period history
  List<BudgetPeriodListItem> _availablePeriods = [];
  BudgetPeriodDetail? _historicalPeriod;
  bool _isLoadingPeriod = false;
  bool get _isHistoricalMode => _historicalPeriod != null;

  // View state
  _ViewMode _viewMode = _ViewMode.compact;
  _Scope _scope = _Scope.personal;
  final Set<int> _collapsedGroups = {};
  bool _childBudgetsExpanded = false; // child budgets section is collapsed by default
  int _scopeSwitchToken = 0; // guards against stale async results

  // Animation
  late AnimationController _fadeController;
  late Animation<double> _fadeAnimation;

  // Count-up auto-refresh
  Timer? _countUpTimer;
  // Local time of the last count-up fetch — anchors the live ticker's
  // day-index math so it rolls over correctly at local midnight.
  DateTime? _countUpFetchedAt;

  @override
  void initState() {
    super.initState();
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnimation =
        CurvedAnimation(parent: _fadeController, curve: Curves.easeOut);
    _fadeController.forward();
    _loadBudgets();
    // Viewing the budget satisfies the "review your budget" daily challenge (best-effort).
    gamificationService.incrementChallengeProgress('review_budget');
  }

  @override
  void dispose() {
    _fadeController.dispose();
    _countUpTimer?.cancel();
    super.dispose();
  }

  // ── Data Loading ──────────────────────────────────────

  Future<void> _loadBudgets() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    try {
      // Load personal + family budgets in parallel
      final results = await Future.wait([
        budgetService.getActiveBudget(),
        _tryLoadFamilyBudgets(),
      ]);

      if (!mounted) return;
      final active = results[0] as Budget?;
      final familyBudgets = results[1] as List<Budget>;

      // Determine family budget (first family-type budget)
      Budget? familyBudget;
      final personalFromAllowances = <BudgetCategory>[];
      for (final b in familyBudgets) {
        if (b.type == BudgetType.family) {
          familyBudget ??= b;
          // Find allowances assigned to current user
          final auth = Provider.of<AuthProvider>(context, listen: false);
          final userId = int.tryParse(auth.user?.id ?? '');
          if (userId != null) {
            for (final c in b.categories) {
              if (c.isAllowance && c.assignedUserId == userId) {
                personalFromAllowances.add(c);
              }
            }
          }
        }
      }

      // Default scope: family if family budget exists but no personal
      if (familyBudget != null &&
          active == null &&
          _personalBudget == null &&
          _familyBudget == null) {
        _scope = _Scope.family;
      }

      _personalBudget = active;
      _familyBudget = familyBudget;
      _userAllowances = personalFromAllowances;

      // Load family members (used to gate + populate the allowance creator).
      if (familyBudget != null) {
        await _loadFamilyMembers();
      }

      // Load grouped overview for active budget
      if (_activeBudget != null) {
        await _loadBudgetDetails(_activeBudget!);
      }

      if (!mounted) return;
      setState(() {
        _isLoading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = 'Failed to load budget';
        _isLoading = false;
      });
    }
  }

  Future<List<Budget>> _tryLoadFamilyBudgets() async {
    try {
      return await budgetService.getFamilyBudgets();
    } catch (_) {
      return [];
    }
  }

  Future<void> _loadFamilyMembers() async {
    try {
      final members = await familyService.getFamilyMembers();
      if (mounted) _familyMembers = members;
    } catch (_) {
      // Non-critical — allowance creation simply stays unavailable.
    }
  }

  /// Whether the current user can manage the family budget (create allowances).
  /// Owners and Budget Admins qualify; Members and Read-Only do not.
  bool get _canManageFamily {
    final me = _familyMembers.where((m) => m.isCurrentUser);
    if (me.isEmpty) return false;
    final role = me.first.role;
    return role == FamilyMemberRole.owner ||
        role == FamilyMemberRole.budgetAdmin;
  }

  /// RFC #793 — F1. A category qualifies for "Make Personal Budget" when it is a
  /// plain expense line on the family budget (not income, not virtual, and not
  /// already funding a personal budget) and the caller can manage the family.
  bool _canMakePersonalBudget(BudgetCategory cat) {
    return _canManageFamily &&
        _scope == _Scope.family &&
        _activeBudget?.type == BudgetType.family &&
        !cat.isIncome &&
        !cat.isVirtual &&
        !cat.isAllowance &&
        !_isHistoricalMode;
  }

  Future<void> _loadBudgetDetails(Budget budget) async {
    final futures = await Future.wait([
      budgetService.getGroupedOverview(budget.id),
      _loadCountUpStatuses(budget),
    ]);

    if (!mounted) return;
    setState(() {
      _groupedOverview = futures[0] as GroupedBudgetOverview;
    });

    // Load period history in background (non-blocking)
    _loadPeriodHistory(budget);

    // Start count-up auto-refresh if needed
    _startCountUpTimer();
  }

  Future<void> _loadPeriodHistory(Budget budget) async {
    try {
      final periods = await budgetService.getBudgetPeriods(budget.id);
      if (mounted) setState(() => _availablePeriods = periods);
    } catch (e) {
      // Period history is non-critical — don't surface errors to the user
      debugPrint('Period history load failed for budget ${budget.id}: $e');
    }
  }

  Future<void> _selectPeriod(BudgetPeriodListItem? period) async {
    if (period == null) {
      setState(() => _historicalPeriod = null);
      return;
    }
    setState(() => _isLoadingPeriod = true);
    try {
      final detail = await budgetService.getBudgetPeriodDetail(
          _activeBudget!.id, period.id);
      if (mounted) {
        setState(() {
          _historicalPeriod = detail;
          _isLoadingPeriod = false;
        });
      }
    } catch (e) {
      if (mounted) {
        setState(() => _isLoadingPeriod = false);
        showAppToast(
          context,
          'Failed to load period: $e',
          type: AppToastType.error,
        );
      }
    }
  }

  Future<void> _editHistoricalCategoryAmount(
      BudgetPeriodCategoryHistory cat) async {
    final controller = TextEditingController(
      text: cat.allocatedAmount.toStringAsFixed(0),
    );
    final result = await showDialog<double>(
      context: context,
      builder: (ctx) => _InlineEditDialog(
        controller: controller,
        categoryName: cat.categoryName,
      ),
    );
    controller.dispose();
    if (result == null || result == cat.allocatedAmount) return;
    // Guard: scope switch or navigation while dialog was open can null these
    if (!mounted || _activeBudget == null || _historicalPeriod == null) return;
    final budgetId = _activeBudget!.id;
    final snapshotId = _historicalPeriod!.id;
    try {
      final updated = await budgetService.updatePeriodCategoryAmount(
        budgetId,
        snapshotId,
        cat.id,
        result,
      );
      if (mounted && _historicalPeriod != null) {
        setState(() {
          final current = _historicalPeriod!;
          final categories = current.categories.map((c) {
            return c.id == updated.id ? updated : c;
          }).toList();
          _historicalPeriod = BudgetPeriodDetail(
            id: current.id,
            periodStart: current.periodStart,
            periodEnd: current.periodEnd,
            totalAllocated: categories
                .where((c) => !c.isIncome)
                .fold(0.0, (s, c) => s + c.allocatedAmount),
            totalSpent: current.totalSpent,
            totalIncome: current.totalIncome,
            categories: categories,
          );
        });
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Update failed: $e', type: AppToastType.error);
      }
    }
  }

  Future<List<CountUpBudgetStatus>> _loadCountUpStatuses(Budget budget) async {
    final hasCountUp = budget.categories.any((c) => c.isCountUpEnabled);
    if (!hasCountUp) {
      _countUpStatuses = [];
      return [];
    }
    try {
      final statuses = await budgetService.getCountUpStatus(budget.id);
      _countUpStatuses = statuses;
      _countUpFetchedAt = DateTime.now();
      return statuses;
    } catch (_) {
      return [];
    }
  }

  void _startCountUpTimer() {
    _countUpTimer?.cancel();
    if (_activeBudget == null) return;
    final hasCountUp =
        _activeBudget!.categories.any((c) => c.isCountUpEnabled);
    if (!hasCountUp) return;

    // Auto-refresh count-up every 60 seconds
    _countUpTimer = Timer.periodic(const Duration(seconds: 60), (_) async {
      if (!mounted || _activeBudget == null) return;
      try {
        final statuses =
            await budgetService.getCountUpStatus(_activeBudget!.id);
        if (mounted) {
          setState(() {
            _countUpStatuses = statuses;
            _countUpFetchedAt = DateTime.now();
          });
        }
      } catch (_) {}
    });
  }

  Future<void> _switchScope(_Scope scope) async {
    if (_scope == scope) return;

    // Bump token so any in-flight async load from a previous switch is ignored.
    final token = ++_scopeSwitchToken;

    // Switch the scope chip immediately for responsive feedback — the user
    // sees the toggle change right away even while data is loading.
    // Clear stale grouped overview / count-up data so the UI never shows the
    // previous scope's numbers while the new scope loads.
    setState(() {
      _scope = scope;
      _collapsedGroups.clear();
      _groupedOverview = null;
      _countUpStatuses = [];
      _historicalPeriod = null;
      _availablePeriods = [];
      _isLoadingPeriod = false;
    });

    final nextBudget = _activeBudget;

    if (nextBudget == null) {
      // No budget exists for the destination scope — already cleared above.
      return;
    }

    // Fetch the new overview + count-up statuses in the background.
    GroupedBudgetOverview? newOverview;
    List<CountUpBudgetStatus> newStatuses = [];
    try {
      final results = await Future.wait([
        budgetService.getGroupedOverview(nextBudget.id),
        _loadCountUpStatuses(nextBudget),
      ]);
      newOverview = results[0] as GroupedBudgetOverview?;
      newStatuses = results[1] as List<CountUpBudgetStatus>;
    } catch (_) {
      // On error, leave cleared — better to show empty than stale data.
    }

    // Only apply results if this is still the latest scope switch.
    if (!mounted || token != _scopeSwitchToken) return;
    setState(() {
      _groupedOverview = newOverview;
      _countUpStatuses = newStatuses;
      _historicalPeriod = null;
      _availablePeriods = [];
      _isLoadingPeriod = false;
    });
    // Reload period history for the new scope's budget
    _loadPeriodHistory(nextBudget);
  }

  // ── Category Actions ──────────────────────────────────

  void _editCategory(BudgetCategory cat) async {
    final result = await showEditBudgetCategorySheet(context, cat, budget: _activeBudget);
    if (result != null && mounted) _loadBudgets();
  }

  void _transferFunds(BudgetCategory cat) async {
    if (_activeBudget == null) return;
    final result = await showTransferFundsSheet(
      context,
      targetCategory: cat,
      budget: _activeBudget!,
    );
    if (result == true && mounted) _loadBudgets();
  }

  void _transferToSavings(BudgetCategory cat) async {
    final result = await showTransferToSavingsSheet(
      context,
      category: cat,
    );
    if (result == true && mounted) _loadBudgets();
  }

  void _viewTransactions(BudgetCategory cat) {
    // Navigate to transactions page with category filter + 3-month lookback
    final now = DateTime.now();
    final start = DateTime(now.year, now.month - 3, now.day);
    context.go(
      '/transactions?categoryId=${cat.categoryId}'
      '&startDate=${start.toIso8601String().split('T')[0]}'
      '&endDate=${now.toIso8601String().split('T')[0]}',
    );
  }

  Future<void> _resetRollover(BudgetCategory cat) async {
    if (_activeBudget == null) return;
    final result = await showResetRolloversSheet(
      context,
      budget: _activeBudget!,
      singleCategory: cat,
    );
    if (result == true && mounted) await _loadBudgets();
  }

  Future<void> _resetAllRollovers() async {
    if (_activeBudget == null) return;
    final result = await showResetRolloversSheet(
      context,
      budget: _activeBudget!,
    );
    if (result == true && mounted) await _loadBudgets();
  }

  void _editBudget() async {
    if (_activeBudget == null) return;
    final result = await showEditBudgetSheet(context, _activeBudget!);
    if (result != null && mounted) _loadBudgets();
  }

  void _createBudget() async {
    final auth = Provider.of<AuthProvider>(context, listen: false);
    final result = await showCreateBudgetSheet(
      context,
      hasPersonalBudget: _personalBudget != null,
      hasFamilyBudget: _familyBudget != null,
      hasFamily: auth.user?.familyId != null,
      // RFC #793 — F2: enable the "fund from your family" picker.
      familyBudget: _familyBudget,
      canManageFamily: _canManageFamily,
    );
    if (result != null && mounted) _loadBudgets();
  }

  /// RFC #793 — F1. Turn a family expense category into a member's personal budget
  /// funded by the family. Owner/BudgetAdmin only (gated at the call site).
  void _makePersonalBudget(BudgetCategory cat) async {
    final auth = Provider.of<AuthProvider>(context, listen: false);
    final currentUserId = int.tryParse(auth.user?.id ?? '') ?? 0;
    final result = await showSpawnPersonalBudgetSheet(
      context,
      category: cat,
      familyMembers: _familyMembers,
      currentUserId: currentUserId,
    );
    if (result == true && mounted) _loadBudgets();
  }

  void _deleteBudget() async {
    if (_activeBudget == null) return;
    final confirmed = await showBudgetConfirmDialog(
      context,
      title: 'Delete Budget',
      message:
          'Permanently delete "${_activeBudget!.name}"?\nThis cannot be undone.',
      confirmLabel: 'Delete',
    );
    if (confirmed != true) return;
    try {
      await budgetService.deleteBudget(_activeBudget!.id);
      analytics.track(AnalyticsEvents.budgetDeleted);
      if (mounted) _loadBudgets();
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          'Failed to delete budget: $e',
          type: AppToastType.error,
        );
      }
    }
  }

  void _createGroup() async {
    if (_activeBudget == null) return;
    final result =
        await showCreateGroupSheet(context, budgetId: _activeBudget!.id);
    if (result != null && mounted) _loadBudgets();
  }

  void _deleteGroup(CategoryGroup group) async {
    final confirmed = await showBudgetConfirmDialog(
      context,
      title: 'Delete Group',
      message: 'Delete "${group.name}"? Categories will move to "Other".',
      confirmLabel: 'Delete',
    );
    if (confirmed != true) return;
    try {
      await budgetService.deleteCategoryGroup(group.id);
      if (mounted) _loadBudgets();
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          'Failed to delete group: $e',
          type: AppToastType.error,
        );
      }
    }
  }

  void _setupPersonalBudget() async {
    if (_familyBudget == null || _userAllowances.isEmpty) return;
    final result = await showPersonalBudgetFromAllowanceWizard(
      context,
      userAllowances: _userAllowances,
      familyBudget: _familyBudget,
    );
    if (result != null && mounted) _loadBudgets();
  }

  void _showCategoryMenu(BudgetCategory cat, TapDownDetails details) {
    final offset = details.globalPosition;
    showMenu<String>(
      context: context,
      position:
          RelativeRect.fromLTRB(offset.dx, offset.dy, offset.dx, offset.dy),
      color: const Color(0xFF0C0C18),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(color: _cardBorder),
      ),
      items: [
        _menuItem('edit', Icons.edit_outlined, 'Edit Category'),
        _menuItem(
            'transactions', Icons.receipt_long_outlined, 'View Transactions'),
        _menuItem('transfer', Icons.swap_horiz, 'Transfer Funds'),
        if (cat.remainingAmount > 0)
          _menuItem('savings', Icons.savings_outlined, 'Transfer to Savings'),
        if (cat.allowRollover && cat.rolloverBalance != 0)
          _menuItem('reset', Icons.restart_alt, 'Reset Rollover'),
        // RFC #793 — F1: turn a plain family expense category into a member's
        // personal budget funded by the family (Owner/BudgetAdmin only).
        if (_canMakePersonalBudget(cat)) ...[
          const PopupMenuDivider(),
          _menuItem('makePersonal', Icons.savings_outlined,
              'Make Personal Budget'),
        ],
      ],
    ).then((value) {
      if (value == null) return;
      switch (value) {
        case 'edit':
          _editCategory(cat);
        case 'transactions':
          _viewTransactions(cat);
        case 'transfer':
          _transferFunds(cat);
        case 'savings':
          _transferToSavings(cat);
        case 'reset':
          _resetRollover(cat);
        case 'makePersonal':
          _makePersonalBudget(cat);
      }
    });
  }

  PopupMenuItem<String> _menuItem(String value, IconData icon, String label,
      {Color color = _textGhost}) {
    return PopupMenuItem(
      value: value,
      child: Row(
        children: [
          Icon(icon, size: 16, color: color),
          const SizedBox(width: 8),
          Text(label, style: GoogleFonts.sora(fontSize: 13, color: color)),
        ],
      ),
    );
  }

  /// Inline amount editing — tap the allocated amount to quick-edit
  void _inlineEditAmount(BudgetCategory cat) async {
    final controller = TextEditingController(
      text: cat.allocatedAmount.toStringAsFixed(0),
    );
    final result = await showDialog<double>(
      context: context,
      builder: (ctx) => _InlineEditDialog(
        controller: controller,
        categoryName: cat.categoryName,
      ),
    );
    controller.dispose();
    if (result == null || result == cat.allocatedAmount) return;
    try {
      await budgetService.updateCategoryInline(
        cat.id,
        InlineUpdateCategoryRequest(allocatedAmount: result),
      );
      if (mounted) _loadBudgets();
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Update failed: $e', type: AppToastType.error);
      }
    }
  }

  // ── UI ────────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      // Inset the top for the status bar — the nav shell only reserves the bottom
      // safe area, so without this the scope toggle (first thing on the page) renders
      // under the status bar: visually cramped and, worse, untappable (the OS status
      // bar swallows the touch). bottom:false leaves the bottom nav's own inset intact.
      body: SafeArea(
        bottom: false,
        child: FadeTransition(
          opacity: _fadeAnimation,
          child: _isLoading
              ? _buildLoading()
              : _error != null
                  ? _buildError()
                  : _activeBudget == null
                      ? _buildEmpty()
                      : _buildContent(),
        ),
      ),
    );
  }

  Widget _buildLoading() {
    return const Center(
      child: CircularProgressIndicator(color: _accent),
    );
  }

  Widget _buildError() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.error_outline,
              size: 48, color: _expense.withValues(alpha: 0.7)),
          const SizedBox(height: 16),
          Text(_error!,
              style: GoogleFonts.sora(color: _textMuted, fontSize: 14)),
          const SizedBox(height: 16),
          TextButton(
            onPressed: _loadBudgets,
            child: Text(AppLocalizations.of(context).budRetry, style: GoogleFonts.sora(color: _accent)),
          ),
        ],
      ),
    );
  }

  Widget _buildEmpty() {
    // Check if user has allowances but no personal budget → show setup prompt
    final hasAllowances = _userAllowances.isNotEmpty;
    final hasNoBudget = _personalBudget == null;

    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Scope toggle if family exists
          if (_familyBudget != null) ...[
            _buildScopeToggle(),
            const SizedBox(height: 24),
          ],

          if (hasAllowances && hasNoBudget) ...[
            // Allowance setup prompt
            Container(
              margin: const EdgeInsets.symmetric(horizontal: 40),
              padding: const EdgeInsets.all(24),
              decoration: BoxDecoration(
                gradient: LinearGradient(
                  colors: [
                    _accent.withValues(alpha: 0.12),
                    _income.withValues(alpha: 0.06)
                  ],
                  begin: Alignment.topLeft,
                  end: Alignment.bottomRight,
                ),
                borderRadius: BorderRadius.circular(20),
                border: Border.all(color: _accent.withValues(alpha: 0.3)),
              ),
              child: Column(
                children: [
                  const Text('\u{1F4B0}', style: TextStyle(fontSize: 40)),
                  const SizedBox(height: 12),
                  Text(AppLocalizations.of(context).budFamilyFundedYou,
                      style: GoogleFonts.instrumentSerif(
                          fontSize: 24, color: _textGhost)),
                  const SizedBox(height: 8),
                  Text(
                    AppLocalizations.of(context).budAllocatedFromFamily(
                        formatCurrency(_userAllowances.fold(
                            0.0, (s, c) => s + c.allocatedAmount))),
                    style: GoogleFonts.sora(
                        fontSize: 13, color: _textMuted, height: 1.4),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 20),
                  ElevatedButton.icon(
                    onPressed: _setupPersonalBudget,
                    icon: const Icon(Icons.auto_fix_high, size: 18),
                    label: Text(AppLocalizations.of(context).budSetUpPersonalBudget,
                        style:
                            GoogleFonts.sora(fontWeight: FontWeight.w600)),
                    style: ElevatedButton.styleFrom(
                      backgroundColor: _accent,
                      foregroundColor: _textGhost,
                      padding: const EdgeInsets.symmetric(
                          horizontal: 24, vertical: 14),
                      shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(12)),
                    ),
                  ),
                ],
              ),
            ),
          ] else ...[
            Icon(Icons.account_balance_wallet_outlined,
                size: 56, color: _accent.withValues(alpha: 0.4)),
            const SizedBox(height: 16),
            Text(AppLocalizations.of(context).budNoBudgetYet,
                style: GoogleFonts.instrumentSerif(
                    fontSize: 28, color: _textGhost)),
            const SizedBox(height: 8),
            Text(AppLocalizations.of(context).budCreateFirstBudgetHint,
                style: GoogleFonts.sora(fontSize: 14, color: _textMuted)),
            const SizedBox(height: 24),
            ElevatedButton.icon(
              onPressed: _createBudget,
              icon: const Icon(Icons.add, size: 18),
              label: Text(AppLocalizations.of(context).budCreateBudget,
                  style: GoogleFonts.sora(fontWeight: FontWeight.w600)),
              style: ElevatedButton.styleFrom(
                backgroundColor: _accent,
                foregroundColor: _textGhost,
                padding:
                    const EdgeInsets.symmetric(horizontal: 24, vertical: 14),
                shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(12)),
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildContent() {
    return RefreshIndicator(
      onRefresh: _loadBudgets,
      color: _accent,
      backgroundColor: const Color(0xFF0A0A14),
      child: CustomScrollView(
        physics: const AlwaysScrollableScrollPhysics(),
        slivers: [
          // Budget scope toggle (if family budget exists)
          if (_familyBudget != null)
            SliverToBoxAdapter(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(24, 16, 24, 0),
                child: _buildScopeToggle(),
              ),
            ),
          // Period picker (shown when history is available)
          if (_availablePeriods.isNotEmpty)
            SliverToBoxAdapter(child: _buildPeriodPicker()),
          // Historical mode banner
          if (_isHistoricalMode)
            SliverToBoxAdapter(child: _buildHistoricalBanner()),
          // Header
          SliverToBoxAdapter(child: _buildHeader()),
          // Overview card
          if (!_isHistoricalMode) SliverToBoxAdapter(child: _buildOverviewCard()),
          // Historical overview card
          if (_isHistoricalMode) SliverToBoxAdapter(child: _buildHistoricalOverviewCard()),
          // "Needs personal budget" prompt (inside family view, current period only)
          if (!_isHistoricalMode &&
              _scope == _Scope.family &&
              _userAllowances.isNotEmpty &&
              _personalBudget == null)
            SliverToBoxAdapter(child: _buildSetupPromptBanner()),
          // Child budgets (family view, current period only)
          if (!_isHistoricalMode && _scope == _Scope.family && _activeBudget != null)
            ..._buildChildBudgetCards(),
          // View toggle + category content (current period)
          if (!_isHistoricalMode) ...[
            SliverToBoxAdapter(child: _buildViewToggle()),
            if (_viewMode == _ViewMode.compact)
              ..._buildGroupedView()
            else
              ..._buildCountUpView(),
            // Savings transfers (earmarked transactions), current period only.
            if (_viewMode == _ViewMode.compact) ..._buildSavingsTransfersSection(),
          ],
          // Historical category list
          if (_isHistoricalMode) ..._buildHistoricalCategoryList(),
          // Bottom padding
          const SliverPadding(padding: EdgeInsets.only(bottom: 100)),
        ],
      ),
    );
  }

  // ── Scope Toggle ──────────────────────────────────────

  Widget _buildScopeToggle() {
    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        _ScopeChip(
          label: 'Personal',
          icon: Icons.person_outline,
          selected: _scope == _Scope.personal,
          onTap: () => _switchScope(_Scope.personal),
        ),
        const SizedBox(width: 8),
        _ScopeChip(
          label: 'Family',
          icon: Icons.family_restroom_outlined,
          selected: _scope == _Scope.family,
          onTap: () => _switchScope(_Scope.family),
        ),
      ],
    );
  }

  // ── Period Picker ─────────────────────────────────────

  Widget _buildPeriodPicker() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(0, 12, 0, 0),
      child: SizedBox(
        height: 36,
        child: Row(
          children: [
            Expanded(
              child: ListView.separated(
                scrollDirection: Axis.horizontal,
                padding: const EdgeInsets.symmetric(horizontal: 24),
                itemCount: _availablePeriods.length + 1, // +1 for "Current"
                separatorBuilder: (_, __) => const SizedBox(width: 8),
                itemBuilder: (context, index) {
                  if (index == 0) {
                    return _PeriodChip(
                      label: 'Current',
                      isCurrent: true,
                      selected: !_isHistoricalMode,
                      onTap: () => _selectPeriod(null),
                    );
                  }
                  final period = _availablePeriods[index - 1];
                  return _PeriodChip(
                    label: budgetPeriodLabel(period.periodStart, period.periodEnd),
                    isCurrent: false,
                    selected: _historicalPeriod?.id == period.id,
                    onTap: () => _selectPeriod(period),
                  );
                },
              ),
            ),
            if (_isLoadingPeriod)
              Padding(
                padding: const EdgeInsets.only(right: 16),
                child: SizedBox(
                  width: 14,
                  height: 14,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color: _accent.withValues(alpha: 0.6),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }

  // ── Historical Banner ─────────────────────────────────

  Widget _buildHistoricalBanner() {
    final p = _historicalPeriod!;
    final label = budgetPeriodLabel(p.periodStart, p.periodEnd);
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 10, 24, 0),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
        decoration: BoxDecoration(
          color: _pending.withValues(alpha: 0.08),
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: _pending.withValues(alpha: 0.25)),
        ),
        child: Row(
          children: [
            const Icon(Icons.history, color: _pending, size: 16),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                AppLocalizations.of(context).budViewingPeriod(label),
                style: GoogleFonts.sora(fontSize: 12, color: _pending),
              ),
            ),
            GestureDetector(
              onTap: () => _selectPeriod(null),
              child: Text(
                AppLocalizations.of(context).budBackToCurrent,
                style: GoogleFonts.sora(
                  fontSize: 11,
                  color: _accent,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  // ── Historical Overview Card ───────────────────────────

  /// Label for the budget progress bar. Shows the real percentage so over-budget
  /// states stay honest (no longer capped at 200%), but caps the displayed figure
  /// at "900+%" so a runaway number stays readable.
  String _pctUsedLabel(double pct) => pct > 900
      ? AppLocalizations.of(context).budPercentUsedOverflow
      : AppLocalizations.of(context).budPercentUsed(pct.toStringAsFixed(0));

  Widget _buildHistoricalOverviewCard() {
    final p = _historicalPeriod!;
    final pctUsed = p.totalAllocated > 0
        ? (p.totalSpent / p.totalAllocated * 100).clamp(0.0, double.infinity).toDouble()
        : 0.0;
    final barColor =
        pctUsed > 100 ? _expense : (pctUsed > 80 ? _pending : _accent);

    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 8, 24, 8),
      child: Container(
        padding: const EdgeInsets.all(20),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: _cardBorder),
        ),
        child: Column(
          children: [
            LayoutBuilder(
              builder: (context, constraints) {
                final remaining = p.totalAllocated - p.totalSpent;
                final metrics = [
                  _OverviewMetric('Income',
                      formatCurrency(p.totalIncome), _income),
                  _OverviewMetric('Budgeted',
                      formatCurrency(p.totalAllocated),
                      p.totalAllocated > p.totalIncome
                          ? _expense
                          : _textGhost),
                  _OverviewMetric(
                      'Spent', formatCurrency(p.totalSpent), _expense),
                  _OverviewMetric(
                      'Remaining',
                      formatCurrency(remaining),
                      remaining >= 0 ? _income : _expense),
                ];
                if (constraints.maxWidth < 420) {
                  return Column(
                    children: [
                      Row(children: [
                        Expanded(child: metrics[0]),
                        Expanded(child: metrics[1]),
                      ]),
                      const SizedBox(height: 12),
                      Row(children: [
                        Expanded(child: metrics[2]),
                        Expanded(child: metrics[3]),
                      ]),
                    ],
                  );
                }
                return Row(
                  children: [for (final m in metrics) Expanded(child: m)],
                );
              },
            ),
            const SizedBox(height: 16),
            ClipRRect(
              borderRadius: BorderRadius.circular(4),
              child: LinearProgressIndicator(
                value: (pctUsed / 100).clamp(0.0, 1.0),
                backgroundColor: _cardBg,
                color: barColor,
                minHeight: 8,
              ),
            ),
            const SizedBox(height: 8),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(_pctUsedLabel(pctUsed),
                    style:
                        GoogleFonts.firaCode(fontSize: 12, color: barColor)),
                Text(AppLocalizations.of(context).budCompletedPeriod,
                    style:
                        GoogleFonts.firaCode(fontSize: 12, color: _textMuted)),
              ],
            ),
          ],
        ),
      ),
    );
  }

  // ── Historical Category List ───────────────────────────

  List<Widget> _buildHistoricalCategoryList() {
    final p = _historicalPeriod!;
    final income = p.categories.where((c) => c.isIncome).toList();
    final expenses = p.categories.where((c) => !c.isIncome).toList();

    final rows = <Widget>[];

    if (income.isNotEmpty) {
      rows.add(_SectionLabel('Income', color: _income));
      rows.addAll(income.map((cat) => _HistoricalCategoryRow(
            category: cat,
            onAmountTap: () => _editHistoricalCategoryAmount(cat),
          )));
      rows.add(const SizedBox(height: 8));
    }
    if (expenses.isNotEmpty) {
      rows.add(_SectionLabel('Expenses', color: _expense));
      rows.addAll(expenses.map((cat) => _HistoricalCategoryRow(
            category: cat,
            onAmountTap: () => _editHistoricalCategoryAmount(cat),
          )));
    }

    return [
      SliverList(
        delegate: SliverChildBuilderDelegate(
          (_, i) => rows[i],
          childCount: rows.length,
        ),
      ),
    ];
  }

  // ── Setup prompt banner ───────────────────────────────

  Widget _buildSetupPromptBanner() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 8, 24, 4),
      child: GestureDetector(
        onTap: _setupPersonalBudget,
        child: Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            gradient: LinearGradient(
              colors: [
                _accent.withValues(alpha: 0.1),
                _income.withValues(alpha: 0.05)
              ],
            ),
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: _accent.withValues(alpha: 0.25)),
          ),
          child: Row(
            children: [
              const Icon(Icons.auto_fix_high, color: _accent, size: 18),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(AppLocalizations.of(context).budSetUpPersonalBudgetPrompt,
                        style: GoogleFonts.sora(
                            fontSize: 13,
                            color: _textGhost,
                            fontWeight: FontWeight.w500)),
                    Text(
                        AppLocalizations.of(context).budAllocateFamilySetAside(
                            formatCurrency(_userAllowances.fold(
                                0.0, (s, c) => s + c.allocatedAmount))),
                        style: GoogleFonts.sora(
                            fontSize: 11, color: _textMuted)),
                  ],
                ),
              ),
              const Icon(Icons.arrow_forward_ios,
                  color: _textMuted, size: 14),
            ],
          ),
        ),
      ),
    );
  }

  // ── Allowance Cards ───────────────────────────────────

  List<Widget> _buildChildBudgetCards() {
    final children = _activeBudget!.childBudgets;
    if (children.isEmpty) return [];

    return [
      SliverToBoxAdapter(
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: () =>
              setState(() => _childBudgetsExpanded = !_childBudgetsExpanded),
          child: Padding(
            padding: const EdgeInsets.fromLTRB(24, 12, 24, 4),
            child: Row(
              children: [
                AnimatedRotation(
                  turns: _childBudgetsExpanded ? 0 : -0.25,
                  duration: const Duration(milliseconds: 200),
                  child: const Icon(Icons.expand_more, color: _accent, size: 20),
                ),
                const SizedBox(width: 8),
                Text(AppLocalizations.of(context).budChildBudgets,
                    style: GoogleFonts.sora(
                        fontSize: 11,
                        fontWeight: FontWeight.w600,
                        color: _accent,
                        letterSpacing: 1)),
                const SizedBox(width: 6),
                Text('${children.length}',
                    style:
                        GoogleFonts.firaCode(fontSize: 11, color: _textMuted)),
              ],
            ),
          ),
        ),
      ),
      if (_childBudgetsExpanded)
        SliverList(
          delegate: SliverChildBuilderDelegate(
            (_, i) => _ChildBudgetCard(budget: children[i]),
            childCount: children.length,
          ),
        ),
    ];
  }

  // ── Header ────────────────────────────────────────────

  Widget _buildHeader() {
    final budget = _activeBudget!;
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 24, 24, 8),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  budget.name,
                  style: GoogleFonts.sora(
                    fontSize: 28,
                    fontWeight: FontWeight.bold,
                    color: _isHistoricalMode
                        ? _textGhost.withValues(alpha: 0.7)
                        : _textGhost,
                  ),
                ),
                const SizedBox(height: 4),
                Row(
                  children: [
                    if (_isHistoricalMode) ...[
                      Container(
                        padding: const EdgeInsets.symmetric(
                            horizontal: 8, vertical: 2),
                        decoration: BoxDecoration(
                          color: _pending.withValues(alpha: 0.1),
                          borderRadius: BorderRadius.circular(10),
                          border: Border.all(
                              color: _pending.withValues(alpha: 0.3)),
                        ),
                        child: Text(
                          budgetPeriodLabel(_historicalPeriod!.periodStart,
                              _historicalPeriod!.periodEnd),
                          style:
                              GoogleFonts.sora(fontSize: 11, color: _pending),
                        ),
                      ),
                    ] else ...[
                      _PeriodBadge(budget.period),
                      if (budget.type == BudgetType.family) ...[
                        const SizedBox(width: 8),
                        Container(
                          padding: const EdgeInsets.symmetric(
                              horizontal: 8, vertical: 2),
                          decoration: BoxDecoration(
                            color: _income.withValues(alpha: 0.1),
                            borderRadius: BorderRadius.circular(10),
                            border: Border.all(
                                color: _income.withValues(alpha: 0.3)),
                          ),
                          child: Text(AppLocalizations.of(context).budFamilyBadge,
                              style: GoogleFonts.sora(
                                  fontSize: 11, color: _income)),
                        ),
                      ],
                      const SizedBox(width: 8),
                      Text(
                        AppLocalizations.of(context)
                            .budDaysLeft('${budget.daysRemaining}'),
                        style: GoogleFonts.sora(
                            fontSize: 12, color: _textMuted),
                      ),
                    ],
                  ],
                ),
              ],
            ),
          ),
          // Budget actions menu (hide in historical mode)
          if (_isHistoricalMode)
            const SizedBox(width: 48)
          else
          PopupMenuButton<String>(
            icon: Icon(Icons.more_vert,
                color: _textMuted.withValues(alpha: 0.7), size: 22),
            color: const Color(0xFF0C0C18),
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(12),
              side: BorderSide(color: _cardBorder),
            ),
            onSelected: (value) {
              switch (value) {
                case 'edit':
                  _editBudget();
                case 'create':
                  _createBudget();
                case 'addGroup':
                  _createGroup();
                case 'resetAll':
                  _resetAllRollovers();
                case 'delete':
                  _deleteBudget();
              }
            },
            itemBuilder: (_) => [
              _menuItem('edit', Icons.edit_outlined, 'Edit Budget'),
              _menuItem('create', Icons.add, 'New Budget'),
              _menuItem('addGroup', Icons.create_new_folder_outlined,
                  'New Category Group'),
              _menuItem(
                  'resetAll', Icons.restart_alt, 'Reset All Rollovers'),
              const PopupMenuDivider(),
              _menuItem('delete', Icons.delete_outline, 'Delete Budget',
                  color: _expense),
            ],
          ),
        ],
      ),
    );
  }

  // ── Overview Card ─────────────────────────────────────

  Widget _buildOverviewCard() {
    final budget = _activeBudget!;
    final pctUsed = budget.percentageUsed.clamp(0.0, double.infinity).toDouble();
    final barColor =
        pctUsed > 100 ? _expense : (pctUsed > 80 ? _pending : _accent);

    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 8, 24, 8),
      child: Container(
            padding: const EdgeInsets.all(20),
            decoration: BoxDecoration(
              color: _cardBg,
              borderRadius: BorderRadius.circular(20),
              border: Border.all(color: _cardBorder),
              boxShadow: [
                BoxShadow(
                  color: const Color(0xFF7B61FF).withValues(alpha: 0.03),
                  blurRadius: 24,
                  spreadRadius: 0,
                ),
              ],
            ),
            child: Column(
              children: [
                // Zero-based budgeting line (income minus expense allocations).
                // Three states: dollars still unassigned, fully assigned ("every
                // dollar has a job"), or over-assigned. Hidden when no income is
                // set, where the number would just be noise.
                _ReadyToAssignStrip(
                  income: budget.totalIncome,
                  allocated: budget.totalAllocated,
                ),
                // Summary row — stacks as a 2x2 grid on narrow screens so the
                // four metrics don't overlap. Threshold picked empirically so
                // phones wrap, tablets/desktop stay on a single row.
                LayoutBuilder(
                  builder: (context, constraints) {
                    final metrics = [
                      _OverviewMetric(
                          'Income',
                          formatCurrency(budget.totalIncome),
                          _income),
                      _OverviewMetric(
                          'Budgeted',
                          formatCurrency(budget.totalAllocated),
                          budget.totalAllocated > budget.totalIncome
                              ? _expense
                              : _textGhost),
                      _OverviewMetric(
                          'Spent',
                          formatCurrency(budget.totalSpent),
                          _expense),
                      _OverviewMetric(
                        'Remaining',
                        formatCurrency(budget.totalRemaining),
                        budget.totalRemaining >= 0 ? _income : _expense,
                      ),
                    ];
                    if (constraints.maxWidth < 420) {
                      return Column(
                        children: [
                          Row(
                            children: [
                              Expanded(child: metrics[0]),
                              Expanded(child: metrics[1]),
                            ],
                          ),
                          const SizedBox(height: 12),
                          Row(
                            children: [
                              Expanded(child: metrics[2]),
                              Expanded(child: metrics[3]),
                            ],
                          ),
                        ],
                      );
                    }
                    return Row(
                      children: [
                        for (final m in metrics) Expanded(child: m),
                      ],
                    );
                  },
                ),
                // RFC #793 — F3: surface that this personal budget is funded by
                // the family, with the allocation. Own income (if any) is additive,
                // so Income above can exceed the allocation shown here.
                if (budget.isFamilyFunded) ...[
                  const SizedBox(height: 12),
                  Container(
                    padding: const EdgeInsets.all(10),
                    decoration: BoxDecoration(
                      color: _income.withValues(alpha: 0.08),
                      borderRadius: BorderRadius.circular(10),
                      border:
                          Border.all(color: _income.withValues(alpha: 0.18)),
                    ),
                    child: Row(
                      children: [
                        const Icon(Icons.savings_outlined,
                            color: _income, size: 16),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            budget.familyAllocationAmount != null
                                ? AppLocalizations.of(context)
                                    .budFundedByFamilyWithAmount(formatCurrency(
                                        budget.familyAllocationAmount!))
                                : AppLocalizations.of(context)
                                    .budFundedByFamily,
                            style: GoogleFonts.sora(
                                fontSize: 12, color: _income),
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
                const SizedBox(height: 16),
                // Progress bar
                ClipRRect(
                  borderRadius: BorderRadius.circular(4),
                  child: LinearProgressIndicator(
                    value: (pctUsed / 100).clamp(0.0, 1.0),
                    backgroundColor: _cardBg,
                    color: barColor,
                    minHeight: 8,
                  ),
                ),
                const SizedBox(height: 8),
                // Progress label
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  children: [
                    Text(
                      _pctUsedLabel(pctUsed),
                      style:
                          GoogleFonts.firaCode(fontSize: 12, color: barColor),
                    ),
                    Text(
                      AppLocalizations.of(context).budDaysElapsedOfTotal(
                          '${budget.daysElapsed}',
                          '${budget.daysInCurrentPeriod}'),
                      style: GoogleFonts.firaCode(
                          fontSize: 12, color: _textMuted),
                    ),
                  ],
                ),
                // Period summary banner
                if (budget.lastPeriodSummary != null) ...[
                  const SizedBox(height: 12),
                  Container(
                    padding: const EdgeInsets.all(10),
                    decoration: BoxDecoration(
                      color: _accent.withValues(alpha: 0.08),
                      borderRadius: BorderRadius.circular(10),
                      border: Border.all(
                          color: _accent.withValues(alpha: 0.15)),
                    ),
                    child: Row(
                      children: [
                        const Icon(Icons.emoji_events_outlined,
                            color: _accent, size: 16),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            budget.lastPeriodSummary!.encouragementMessage
                                    .isNotEmpty
                                ? budget.lastPeriodSummary!
                                    .encouragementMessage
                                : AppLocalizations.of(context)
                                    .budLastPeriodSaved(formatCurrency(budget
                                        .lastPeriodSummary!.totalSavings)),
                            style: GoogleFonts.sora(
                                fontSize: 12, color: _accent),
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ],
            ),
          ),
    );
  }

  // ── View Toggle ───────────────────────────────────────

  Widget _buildViewToggle() {
    final hasCountUp =
        _activeBudget!.categories.any((c) => c.isCountUpEnabled);
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 12, 24, 4),
      child: Row(
        children: [
          // View mode chips
          _ScopeChip(
            label: 'Compact',
            icon: Icons.view_list,
            selected: _viewMode == _ViewMode.compact,
            onTap: () => setState(() => _viewMode = _ViewMode.compact),
          ),
          const SizedBox(width: 8),
          if (hasCountUp)
            _ScopeChip(
              label: 'Count-Up',
              icon: Icons.trending_up,
              selected: _viewMode == _ViewMode.countUp,
              onTap: () => setState(() => _viewMode = _ViewMode.countUp),
            ),
          const Spacer(),
          // Unallocated indicator — Flexible so it wraps text on narrow screens
          if (_activeBudget!.totalIncome - _activeBudget!.totalAllocated !=
              0)
            Flexible(
              child: Container(
                padding:
                    const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                decoration: BoxDecoration(
                  color: _pending.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(10),
                  border:
                      Border.all(color: _pending.withValues(alpha: 0.3)),
                ),
                child: Text(
                  _activeBudget!.totalIncome - _activeBudget!.totalAllocated > 0
                      ? AppLocalizations.of(context).budAmountUnallocated(
                          formatCurrency((_activeBudget!.totalIncome -
                                  _activeBudget!.totalAllocated)
                              .abs()))
                      : AppLocalizations.of(context).budAmountOver(
                          formatCurrency((_activeBudget!.totalIncome -
                                  _activeBudget!.totalAllocated)
                              .abs())),
                  style:
                      GoogleFonts.firaCode(fontSize: 11, color: _pending),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
            ),
        ],
      ),
    );
  }

  // ── Compact / Grouped View ────────────────────────────

  List<Widget> _buildGroupedView() {
    if (_groupedOverview == null || _groupedOverview!.groups.isEmpty) {
      return [_buildFlatCategoryList()];
    }

    final slivers = <Widget>[];
    for (final group in _groupedOverview!.groups) {
      // Allowance categories are surfaced in the dedicated Child Budgets section,
      // not as plain budget rows. Exclude them and skip any group left empty
      // (including the auto-created, otherwise-empty "Child Budgets" group).
      final cats = group.categories.where((c) => !c.isAllowance).toList();
      if (cats.isEmpty) continue;
      slivers.add(SliverToBoxAdapter(
        child: _GroupHeader(
          group: group,
          isCollapsed: _collapsedGroups.contains(group.id),
          onToggle: () => setState(() {
            if (_collapsedGroups.contains(group.id)) {
              _collapsedGroups.remove(group.id);
            } else {
              _collapsedGroups.add(group.id);
            }
          }),
          onDelete: () => _deleteGroup(group),
        ),
      ));

      if (!_collapsedGroups.contains(group.id)) {
        slivers.add(SliverList(
          delegate: SliverChildBuilderDelegate(
            (_, i) {
              final cat = cats[i];
              return _CompactCategoryRow(
                category: cat,
                onTap: () => _editCategory(cat),
                onSecondaryTap: (details) =>
                    _showCategoryMenu(cat, details),
                onAmountTap: () => _inlineEditAmount(cat),
              );
            },
            childCount: cats.length,
          ),
        ));
      }
    }

    return slivers;
  }

  Widget _buildFlatCategoryList() {
    final income = _activeBudget!.incomeCategories;
    final expenses = _activeBudget!.expenseCategories;

    return SliverList(
      delegate: SliverChildListDelegate([
        if (income.isNotEmpty) ...[
          _SectionLabel('Income', color: _income),
          ...income.map((cat) => _CompactCategoryRow(
                category: cat,
                onTap: () => _editCategory(cat),
                onSecondaryTap: (details) =>
                    _showCategoryMenu(cat, details),
                onAmountTap: () => _inlineEditAmount(cat),
              )),
          const SizedBox(height: 8),
        ],
        if (expenses.isNotEmpty) ...[
          _SectionLabel('Expenses', color: _expense),
          ...expenses.map((cat) => _CompactCategoryRow(
                category: cat,
                onTap: () => _editCategory(cat),
                onSecondaryTap: (details) =>
                    _showCategoryMenu(cat, details),
                onAmountTap: () => _inlineEditAmount(cat),
              )),
        ],
      ]),
    );
  }

  // ── Savings Transfers Section ─────────────────────────

  /// Earmarked transactions (deposits into / withdrawals from savings goals),
  /// rendered as a distinct section separate from Income / Expenses.
  List<Widget> _buildSavingsTransfersSection() {
    final transfers = _activeBudget?.savingsTransfers ?? const [];
    if (transfers.isEmpty) return [];

    final deposits = _activeBudget!.totalSavingsDeposits;
    final withdrawals = _activeBudget!.totalSavingsWithdrawals;

    return [
      SliverToBoxAdapter(
        child: const _SectionLabel('Savings transfers', color: _accent),
      ),
      SliverToBoxAdapter(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(24, 0, 24, 6),
          child: Row(
            children: [
              if (deposits > 0)
                Text(AppLocalizations.of(context).budSavingsIn(formatCurrency(deposits)),
                    style: GoogleFonts.firaCode(
                        fontSize: 11, color: _income)),
              if (deposits > 0 && withdrawals > 0)
                Text('  •  ',
                    style: GoogleFonts.firaCode(
                        fontSize: 11, color: _textMuted)),
              if (withdrawals > 0)
                Text(AppLocalizations.of(context).budSavingsOut(formatCurrency(withdrawals)),
                    style: GoogleFonts.firaCode(
                        fontSize: 11, color: _expense)),
            ],
          ),
        ),
      ),
      SliverList(
        delegate: SliverChildBuilderDelegate(
          (_, i) => _SavingsTransferRow(transfer: transfers[i]),
          childCount: transfers.length,
        ),
      ),
    ];
  }

  // ── Count-Up View ─────────────────────────────────────

  /// Live-ticker aggregate: interpolated earned minus spent, summed across
  /// every count-up category. Only called while the ticker pref is on and
  /// `_countUpFetchedAt` is non-null.
  double _liveAvailableTotal(DateTime now) => _countUpStatuses.fold(
      0.0,
      (sum, c) =>
          sum +
          interpolatedEarnedToDate(
            dailyAllowance: c.dailyAllowance,
            daysElapsed: c.daysElapsed,
            totalDays: c.totalDays,
            fetchedAt: _countUpFetchedAt!,
            now: now,
          ) -
          c.actualSpent);

  List<Widget> _buildCountUpView() {
    if (_countUpStatuses.isEmpty) {
      return [
        SliverToBoxAdapter(
          child: Padding(
            padding: const EdgeInsets.all(40),
            child: Center(
              child: Column(
                children: [
                  Icon(Icons.trending_up,
                      size: 48, color: _accent.withValues(alpha: 0.3)),
                  const SizedBox(height: 16),
                  Text(AppLocalizations.of(context).budNoCountUpCategories,
                      style: GoogleFonts.sora(
                          fontSize: 16, color: _textMuted)),
                  const SizedBox(height: 8),
                  Text(
                      AppLocalizations.of(context).budEnableCountUpHint,
                      style: GoogleFonts.sora(
                          fontSize: 13,
                          color: _textMuted.withValues(alpha: 0.6)),
                      textAlign: TextAlign.center),
                ],
              ),
            ),
          ),
        ),
      ];
    }

    // Summary totals
    final totalDaily =
        _countUpStatuses.fold(0.0, (s, c) => s + c.dailyAllowance);
    final totalAvailable =
        _countUpStatuses.fold(0.0, (s, c) => s + c.availableBalance);

    return [
      // Count-Up Summary Header
      SliverToBoxAdapter(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(24, 8, 24, 8),
          child: Container(
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    colors: [
                      _accent.withValues(alpha: 0.12),
                      const Color(0xFF1a1a2e).withValues(alpha: 0.3)
                    ],
                    begin: Alignment.topLeft,
                    end: Alignment.bottomRight,
                  ),
                  borderRadius: BorderRadius.circular(16),
                  border:
                      Border.all(color: _accent.withValues(alpha: 0.2)),
                ),
                child: Row(
                  children: [
                    const Icon(Icons.trending_up,
                        color: _accent, size: 24),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(AppLocalizations.of(context).budTodaysCountUpSummary,
                              style: GoogleFonts.sora(
                                  fontSize: 13,
                                  fontWeight: FontWeight.w600,
                                  color: _textGhost)),
                          Text(
                              AppLocalizations.of(context)
                                  .budCategoriesTracked(
                                      _countUpStatuses.length),
                              style: GoogleFonts.sora(
                                  fontSize: 11, color: _textMuted)),
                        ],
                      ),
                    ),
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.end,
                      children: [
                        Text(formatCurrency(totalDaily),
                            style: GoogleFonts.firaCode(
                                fontSize: 14,
                                color: _accent,
                                fontWeight: FontWeight.w600)),
                        Text(AppLocalizations.of(context).budDailyEarning,
                            style: GoogleFonts.sora(
                                fontSize: 10, color: _textMuted)),
                      ],
                    ),
                    const SizedBox(width: 16),
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.end,
                      children: [
                        ValueListenableBuilder<bool>(
                          valueListenable:
                              TickerPreferences.liveCountUpTicker,
                          builder: (context, live, _) {
                            TextStyle style(double v) =>
                                GoogleFonts.firaCode(
                                    fontSize: 14,
                                    color: v >= 0 ? _income : _expense,
                                    fontWeight: FontWeight.w600);
                            if (!live || _countUpFetchedAt == null) {
                              return Text(formatCurrency(totalAvailable),
                                  style: style(totalAvailable));
                            }
                            // Aggregate across all categories — ticks the
                            // fastest of any number on the page. The rate
                            // line under it is the idle-game "DPS" readout.
                            return Column(
                              crossAxisAlignment: CrossAxisAlignment.end,
                              children: [
                                LiveTickingAmount(
                                  compute: _liveAvailableTotal,
                                  styleBuilder: style,
                                ),
                                Text(
                                  AppLocalizations.of(context)
                                      .budPerMinuteRate(formatCurrencyLive(
                                          totalDaily / 1440)),
                                  style: GoogleFonts.firaCode(
                                      fontSize: 9,
                                      color: const Color(0xFF4ECDC4)
                                          .withValues(alpha: 0.9)),
                                ),
                              ],
                            );
                          },
                        ),
                        Text(AppLocalizations.of(context).budAvailableLabel,
                            style: GoogleFonts.sora(
                                fontSize: 10, color: _textMuted)),
                      ],
                    ),
                  ],
                ),
              ),
        ),
      ),
      // Count-up category cards
      SliverList(
        delegate: SliverChildBuilderDelegate(
          (_, i) => _CountUpCard(
            status: _countUpStatuses[i],
            fetchedAt: _countUpFetchedAt,
            onEdit: () {
              final cat = _countUpStatuses[i]
                  .findIn(_activeBudget?.categories ?? const []);
              if (cat == null) return;
              _editCategory(cat);
            },
          ),
          childCount: _countUpStatuses.length,
        ),
      ),
    ];
  }
}

// ═══════════════════════════════════════════════════════════════
//  GROUP HEADER — collapsible, shows group total + management
// ═══════════════════════════════════════════════════════════════

class _GroupHeader extends StatefulWidget {
  final CategoryGroup group;
  final bool isCollapsed;
  final VoidCallback onToggle;
  final VoidCallback onDelete;

  const _GroupHeader({
    required this.group,
    required this.isCollapsed,
    required this.onToggle,
    required this.onDelete,
  });

  @override
  State<_GroupHeader> createState() => _GroupHeaderState();
}

class _GroupHeaderState extends State<_GroupHeader> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final totalAllocated = widget.group.categories
        .fold(0.0, (s, c) => s + c.allocatedAmount);
    final totalSpent =
        widget.group.categories.fold(0.0, (s, c) => s + c.spentAmount);
    final remaining = totalAllocated - totalSpent;

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onToggle,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(24, 12, 24, 4),
          child: Row(
            children: [
              AnimatedRotation(
                turns: widget.isCollapsed ? -0.25 : 0,
                duration: const Duration(milliseconds: 200),
                child: const Icon(Icons.expand_more,
                    color: _textMuted, size: 20),
              ),
              const SizedBox(width: 8),
              if (widget.group.icon != null &&
                  widget.group.icon!.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: Text(widget.group.icon!,
                      style: const TextStyle(fontSize: 14)),
                ),
              Expanded(
                child: Text(
                  widget.group.name,
                  style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: _textGhost,
                    letterSpacing: 0.5,
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Text(
                '${formatCurrency(totalSpent)} / ${formatCurrency(totalAllocated)}',
                style: GoogleFonts.firaCode(
                  fontSize: 11,
                  color: remaining >= 0 ? _textMuted : _expense,
                ),
              ),
              // Delete group button (on hover)
              if (_hovered)
                GestureDetector(
                  onTap: widget.onDelete,
                  child: Padding(
                    padding: const EdgeInsets.only(left: 8),
                    child: Icon(Icons.close,
                        size: 14,
                        color: _textMuted.withValues(alpha: 0.5)),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════════
//  COMPACT CATEGORY ROW — the main budget line item
// ═══════════════════════════════════════════════════════════════

class _CompactCategoryRow extends StatefulWidget {
  final BudgetCategory category;
  final VoidCallback onTap;
  final void Function(TapDownDetails) onSecondaryTap;
  final VoidCallback onAmountTap;

  const _CompactCategoryRow({
    required this.category,
    required this.onTap,
    required this.onSecondaryTap,
    required this.onAmountTap,
  });

  @override
  State<_CompactCategoryRow> createState() => _CompactCategoryRowState();
}

class _CompactCategoryRowState extends State<_CompactCategoryRow> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = widget.category;
    final pct = c.percentageUsed.clamp(0, 200).toDouble();
    final barColor = c.isIncome
        ? _income
        : (pct > 100
            ? _expense
            : (pct > 80 ? _pending : _accent));

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        onSecondaryTapDown: widget.onSecondaryTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          margin: const EdgeInsets.symmetric(horizontal: 24, vertical: 4),
          padding:
              const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
          decoration: BoxDecoration(
            color: _hovered
                ? _accent.withValues(alpha: 0.06)
                : _cardBg,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: _hovered
                  ? _accent.withValues(alpha: 0.15)
                  : _cardBorder,
            ),
            boxShadow: [
              BoxShadow(
                color: const Color(0xFF7B61FF).withValues(
                    alpha: _hovered ? 0.08 : 0.03),
                blurRadius: _hovered ? 18 : 24,
                spreadRadius: 0,
              ),
            ],
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Row 1: Category name + remaining amount
              Row(
                children: [
                  // Category color dot
                  Container(
                    width: 8,
                    height: 8,
                    decoration: BoxDecoration(
                      color: barColor,
                      shape: BoxShape.circle,
                    ),
                  ),
                  const SizedBox(width: 10),

                  // Category name + badges — full width
                  Expanded(
                    child: Row(
                      children: [
                        Flexible(
                          child: Text(
                            c.categoryName,
                            style: GoogleFonts.sora(
                              fontSize: 13,
                              color: _textGhost,
                              fontWeight: FontWeight.w500,
                            ),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        if (c.isCountUpEnabled)
                          Padding(
                            padding: const EdgeInsets.only(left: 6),
                            child: Icon(Icons.trending_up,
                                size: 12,
                                color: _accent.withValues(alpha: 0.6)),
                          ),
                        if (c.isAllowance)
                          Padding(
                            padding: const EdgeInsets.only(left: 4),
                            child: Tooltip(
                              message:
                                  'Personal budget${c.assignedUserName != null ? " \u2192 ${c.assignedUserName}" : ""}',
                              child: Icon(Icons.card_giftcard,
                                  size: 12,
                                  color: _income.withValues(alpha: 0.6)),
                            ),
                          ),
                      ],
                    ),
                  ),

                  const SizedBox(width: 8),

                  // Remaining amount — hero number
                  // For income categories, negative remaining = over-earned (good)
                  Text(
                    formatCurrency(c.remainingAmount),
                    style: GoogleFonts.firaCode(
                      fontSize: 13,
                      color: c.isIncome
                          ? (c.remainingAmount <= 0 ? _income : _textGhost)
                          : (c.remainingAmount >= 0 ? _income : _expense),
                      fontWeight: FontWeight.w600,
                    ),
                  ),

                  // 3-dot menu — always visible
                  SizedBox(
                    width: 28,
                    height: 28,
                    child: GestureDetector(
                      behavior: HitTestBehavior.opaque,
                      onTapDown: widget.onSecondaryTap,
                      child: Center(
                        child: Icon(Icons.more_vert,
                            size: 16,
                            color: _textMuted.withValues(alpha: 0.4)),
                      ),
                    ),
                  ),
                ],
              ),

              const SizedBox(height: 6),

              // Row 2: Budget / Spent subtitle
              Row(
                children: [
                  // Indent to align with name (dot width + spacing)
                  const SizedBox(width: 18),

                  // Budget amount — tappable for inline edit
                  GestureDetector(
                    onTap: widget.onAmountTap,
                    child: Text(
                      // The budget the user set stays constant month to month.
                      // Rollover is shown separately via the chip below, never
                      // baked into this number.
                      formatCurrency(c.allocatedAmount),
                      style: GoogleFonts.firaCode(
                        fontSize: 11,
                        color: _textMuted,
                        decoration:
                            _hovered ? TextDecoration.underline : null,
                        decorationColor:
                            _accent.withValues(alpha: 0.4),
                      ),
                    ),
                  ),
                  Text(
                    c.isIncome
                        ? AppLocalizations.of(context).budExpectedSeparator
                        : AppLocalizations.of(context).budBudgetedSeparator,
                    style: GoogleFonts.sora(
                      fontSize: 11,
                      color: _textMuted.withValues(alpha: 0.6),
                    ),
                  ),
                  Text(
                    formatCurrency(c.spentAmount),
                    style: GoogleFonts.firaCode(
                      fontSize: 11,
                      color: c.isIncome ? _income : _textGhost.withValues(alpha: 0.7),
                    ),
                  ),
                  Text(
                    c.isIncome
                        ? AppLocalizations.of(context).budEarnedSuffix
                        : AppLocalizations.of(context).budSpentSuffix,
                    style: GoogleFonts.sora(
                      fontSize: 11,
                      color: _textMuted.withValues(alpha: 0.6),
                    ),
                  ),
                  // Rollover shown separately, only when it's enabled and nonzero.
                  if (!c.isIncome && c.allowRollover && c.rolloverBalance != 0) ...[
                    const Spacer(),
                    RolloverChip(
                        rolloverBalance: c.rolloverBalance,
                        allocated: c.allocatedAmount),
                  ],
                ],
              ),

              // Prominent progress bar
              const SizedBox(height: 8),
              (!c.isIncome && c.allowRollover && c.rolloverBalance != 0)
                  ? RolloverSpendBar(
                      allocated: c.allocatedAmount,
                      spent: c.spentAmount,
                      rolloverBalance: c.rolloverBalance,
                    )
                  : ClipRRect(
                      borderRadius: BorderRadius.circular(3),
                      child: LinearProgressIndicator(
                        value: (pct / 100).clamp(0.0, 1.0),
                        backgroundColor: _cardBg,
                        color: barColor.withValues(alpha: 0.7),
                        minHeight: 6,
                      ),
                    ),
            ],
          ),
        ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════════
//  CHILD BUDGET CARD — family-owner summary of a child's allowance budget
// ═══════════════════════════════════════════════════════════════

class _ChildBudgetCard extends StatelessWidget {
  final Budget budget;

  const _ChildBudgetCard({required this.budget});

  @override
  Widget build(BuildContext context) {
    final allocated = budget.allocatedAmount ?? budget.totalExpenses;
    // totalExpenses is the child's actual spend (backend computes this for child
    // budgets); show real progress against the allocated allowance.
    final spent = budget.totalExpenses;
    final pct = allocated > 0
        ? (spent / allocated * 100).clamp(0, 200).toDouble()
        : 0.0;
    final remaining = allocated - spent;
    final barColor =
        pct > 100 ? _expense : (pct > 80 ? _pending : _income);
    final subtitle = budget.ownerName != null
        ? '${budget.ownerName} · ${budget.period.displayName} personal budget'
        : '${budget.period.displayName} personal budget';

    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 4, 24, 4),
      child: Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(14),
          border: Border.all(color: _cardBorder),
          boxShadow: [
            BoxShadow(
              color: const Color(0xFF7B61FF).withValues(alpha: 0.03),
              blurRadius: 24,
            ),
          ],
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  width: 38,
                  height: 38,
                  decoration: BoxDecoration(
                    color: _income.withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: const Icon(Icons.child_care_outlined,
                      size: 20, color: _income),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(budget.name,
                          style: GoogleFonts.sora(
                              fontSize: 14,
                              color: _textGhost,
                              fontWeight: FontWeight.w600)),
                      const SizedBox(height: 2),
                      Text(subtitle,
                          style: GoogleFonts.sora(
                              fontSize: 11, color: _textMuted)),
                    ],
                  ),
                ),
                const SizedBox(width: 8),
                Text(formatCurrency(allocated),
                    style: GoogleFonts.firaCode(
                        fontSize: 15,
                        color: _income,
                        fontWeight: FontWeight.w700)),
              ],
            ),
            const SizedBox(height: 12),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(AppLocalizations.of(context).budAmountSpent(formatCurrency(spent)),
                    style: GoogleFonts.sora(
                        fontSize: 11, color: _textMuted)),
                Text('${pct.toStringAsFixed(0)}%',
                    style: GoogleFonts.firaCode(
                        fontSize: 11, color: barColor)),
              ],
            ),
            const SizedBox(height: 6),
            ClipRRect(
              borderRadius: BorderRadius.circular(3),
              child: LinearProgressIndicator(
                value: (pct / 100).clamp(0.0, 1.0),
                backgroundColor: _income.withValues(alpha: 0.12),
                color: barColor,
                minHeight: 4,
              ),
            ),
            const SizedBox(height: 6),
            Text(AppLocalizations.of(context).budAmountRemaining(formatCurrency(remaining)),
                style: GoogleFonts.sora(
                    fontSize: 11,
                    color: remaining >= 0 ? _income : _expense)),
          ],
        ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════════
//  COUNT-UP CARD — gamified daily allowance view with achievements
// ═══════════════════════════════════════════════════════════════

class _CountUpCard extends StatelessWidget {
  final CountUpBudgetStatus status;
  final DateTime? fetchedAt;
  final VoidCallback onEdit;

  const _CountUpCard(
      {required this.status, required this.fetchedAt, required this.onEdit});

  @override
  Widget build(BuildContext context) {
    final earnedPct = status.totalDays > 0
        ? (status.daysElapsed / status.totalDays * 100)
            .clamp(0, 100)
            .toDouble()
        : 0.0;

    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 8, 24, 4),
      child: GestureDetector(
        onTap: onEdit,
        child: Container(
              padding: const EdgeInsets.all(16),
              decoration: BoxDecoration(
                color: _cardBg,
                borderRadius: BorderRadius.circular(16),
                border: Border.all(color: _cardBorder),
                boxShadow: [
                  BoxShadow(
                    color: const Color(0xFF7B61FF).withValues(alpha: 0.03),
                    blurRadius: 24,
                    spreadRadius: 0,
                  ),
                ],
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  // Header
                  Row(
                    children: [
                      const Icon(Icons.trending_up,
                          color: _accent, size: 18),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(status.categoryName,
                            style: GoogleFonts.sora(
                                fontSize: 15,
                                fontWeight: FontWeight.w600,
                                color: _textGhost)),
                      ),
                      // Daily allowance badge
                      Container(
                        padding: const EdgeInsets.symmetric(
                            horizontal: 8, vertical: 3),
                        decoration: BoxDecoration(
                          color: _accent.withValues(alpha: 0.15),
                          borderRadius: BorderRadius.circular(10),
                        ),
                        child: Text(
                          AppLocalizations.of(context).budPerDayRate(
                              formatCurrency(status.dailyAllowance)),
                          style: GoogleFonts.firaCode(
                              fontSize: 11,
                              color: _accent,
                              fontWeight: FontWeight.w600),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 14),

                  // Day progress
                  Text(
                      AppLocalizations.of(context).budDayOf(
                          '${status.daysElapsed}', '${status.totalDays}'),
                      style: GoogleFonts.sora(
                          fontSize: 11, color: _textMuted)),
                  const SizedBox(height: 6),

                  // Unified earned + spent progress bar
                  _UnifiedCountUpBar(
                    earnedPct: earnedPct,
                    earnedToDate: status.earnedToDate,
                    actualSpent: status.actualSpent,
                    allocatedAmount: status.allocatedAmount,
                  ),
                  const SizedBox(height: 14),

                  // Available Balance — prominent display
                  Container(
                    width: double.infinity,
                    padding: const EdgeInsets.symmetric(
                        vertical: 10, horizontal: 12),
                    decoration: BoxDecoration(
                      color: (status.availableBalance >= 0
                              ? _income
                              : _expense)
                          .withValues(alpha: 0.08),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: Row(
                      mainAxisAlignment: MainAxisAlignment.spaceBetween,
                      children: [
                        Text(AppLocalizations.of(context).budAvailableBalance,
                            style: GoogleFonts.sora(
                                fontSize: 12, color: _textMuted)),
                        ValueListenableBuilder<bool>(
                          valueListenable:
                              TickerPreferences.liveCountUpTicker,
                          builder: (context, live, _) {
                            TextStyle style(double v) =>
                                GoogleFonts.firaCode(
                                    fontSize: 18,
                                    color: v >= 0 ? _income : _expense,
                                    fontWeight: FontWeight.bold);
                            if (!live || fetchedAt == null) {
                              return Text(
                                  formatCurrency(status.availableBalance),
                                  style: style(status.availableBalance));
                            }
                            return LiveTickingAmount(
                              compute: (now) =>
                                  interpolatedEarnedToDate(
                                    dailyAllowance: status.dailyAllowance,
                                    daysElapsed: status.daysElapsed,
                                    totalDays: status.totalDays,
                                    fetchedAt: fetchedAt!,
                                    now: now,
                                  ) -
                                  status.actualSpent,
                              styleBuilder: style,
                            );
                          },
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 12),

                  // Stats row
                  Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      _CountUpStat(
                          'Projected Savings',
                          formatCurrency(status.projectedSavings),
                          status.projectedSavings > 0
                              ? _income
                              : _textMuted),
                      _CountUpStat(
                          'Streak',
                          '${status.consecutiveDaysUnderBudget}d',
                          _accent),
                      _CountUpStat('Zero Days',
                          '${status.zeroSpendingDays}', _pending),
                    ],
                  ),

                  // Achievements
                  if (status.achievements.isNotEmpty) ...[
                    const SizedBox(height: 12),
                    Wrap(
                      spacing: 6,
                      runSpacing: 4,
                      children: status.achievements
                          .map((a) => Tooltip(
                                message:
                                    '${a.title}: ${a.description}',
                                child: Container(
                                  padding: const EdgeInsets.symmetric(
                                      horizontal: 8, vertical: 3),
                                  decoration: BoxDecoration(
                                    color: _accent.withValues(
                                        alpha: 0.1),
                                    borderRadius:
                                        BorderRadius.circular(10),
                                    border: Border.all(
                                        color: _accent.withValues(
                                            alpha: 0.2)),
                                  ),
                                  child: Row(
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      Text(
                                          a.icon.isNotEmpty
                                              ? a.icon
                                              : '\u{1F3C6}',
                                          style: const TextStyle(
                                              fontSize: 12)),
                                      const SizedBox(width: 4),
                                      Text(a.title,
                                          style: GoogleFonts.sora(
                                              fontSize: 10,
                                              color: _accent)),
                                    ],
                                  ),
                                ),
                              ))
                          .toList(),
                    ),
                  ],
                ],
              ),
            ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════════
//  INLINE EDIT DIALOG — quick amount change
// ═══════════════════════════════════════════════════════════════

class _InlineEditDialog extends StatelessWidget {
  final TextEditingController controller;
  final String categoryName;

  const _InlineEditDialog({
    required this.controller,
    required this.categoryName,
  });

  @override
  Widget build(BuildContext context) {
    return Dialog(
      backgroundColor: Colors.transparent,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(20),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 16, sigmaY: 16),
          child: Container(
            width: 300,
            padding: const EdgeInsets.all(24),
            decoration: BoxDecoration(
              color: const Color(0xFF0C0C18).withValues(alpha: 0.95),
              borderRadius: BorderRadius.circular(20),
              border: Border.all(color: _cardBorder),
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(categoryName,
                    style: GoogleFonts.sora(
                        fontSize: 16,
                        fontWeight: FontWeight.bold,
                        color: _textGhost)),
                const SizedBox(height: 4),
                Text(AppLocalizations.of(context).budBudgetAmount,
                    style: GoogleFonts.sora(
                        fontSize: 12, color: _textMuted)),
                const SizedBox(height: 12),
                TextFormField(
                  controller: controller,
                  autofocus: true,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  style:
                      GoogleFonts.firaCode(fontSize: 20, color: _textGhost),
                  decoration: InputDecoration(
                    prefixText: '$primaryCurrencySymbol ',
                    prefixStyle: GoogleFonts.firaCode(
                        fontSize: 20, color: _textMuted),
                    filled: true,
                    fillColor: _cardBg,
                    border: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(12),
                      borderSide: BorderSide(color: _cardBorder),
                    ),
                    focusedBorder: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(12),
                      borderSide: const BorderSide(color: _accent),
                    ),
                  ),
                  onFieldSubmitted: (v) {
                    final amount =
                        parseFlexibleAmount(v);
                    Navigator.of(context).pop(amount);
                  },
                ),
                const SizedBox(height: 16),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(
                        onPressed: () => Navigator.of(context).pop(),
                        style: OutlinedButton.styleFrom(
                          foregroundColor: _textMuted,
                          side: BorderSide(color: _cardBorder),
                          shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(10)),
                        ),
                        child: Text(AppLocalizations.of(context).budCancel,
                            style: GoogleFonts.sora()),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: ElevatedButton(
                        onPressed: () {
                          final amount = parseFlexibleAmount(controller.text);
                          Navigator.of(context).pop(amount);
                        },
                        style: ElevatedButton.styleFrom(
                          backgroundColor: _accent,
                          foregroundColor: _textGhost,
                          shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(10)),
                        ),
                        child: Text(AppLocalizations.of(context).budSave,
                            style: GoogleFonts.sora(
                                fontWeight: FontWeight.w600)),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════════
//  SMALL HELPER WIDGETS
// ═══════════════════════════════════════════════════════════════

class _PeriodBadge extends StatelessWidget {
  final BudgetPeriod period;
  const _PeriodBadge(this.period);

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: _accent.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: _accent.withValues(alpha: 0.3)),
      ),
      child: Text(period.displayName,
          style: GoogleFonts.sora(fontSize: 11, color: _accent)),
    );
  }
}

class _OverviewMetric extends StatelessWidget {
  final String label;
  final String value;
  final Color color;

  const _OverviewMetric(this.label, this.value, this.color);

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Text(label,
            style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
        const SizedBox(height: 4),
        Text(value,
            style: GoogleFonts.firaCode(
                fontSize: 13, color: color, fontWeight: FontWeight.w600),
            maxLines: 1,
            overflow: TextOverflow.ellipsis),
      ],
    );
  }
}

class _ScopeChip extends StatelessWidget {
  final String label;
  final IconData icon;
  final bool selected;
  final VoidCallback onTap;

  const _ScopeChip({
    required this.label,
    required this.icon,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 200),
        padding:
            const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        decoration: BoxDecoration(
          color:
              selected ? _accent.withValues(alpha: 0.15) : _cardBg,
          borderRadius: BorderRadius.circular(16),
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
                size: 14, color: selected ? _accent : _textMuted),
            const SizedBox(width: 6),
            Text(label,
                style: GoogleFonts.sora(
                    fontSize: 12,
                    color: selected ? _accent : _textMuted,
                    fontWeight: selected
                        ? FontWeight.w600
                        : FontWeight.normal)),
          ],
        ),
      ),
    );
  }
}

/// Compact row for a savings transfer (earmarked transaction). Green for a
/// deposit into the goal, red for a withdrawal from it.
class _SavingsTransferRow extends StatelessWidget {
  final BudgetSavingsTransfer transfer;

  const _SavingsTransferRow({required this.transfer});

  @override
  Widget build(BuildContext context) {
    final color = transfer.isDeposit ? _income : _expense;
    final sign = transfer.isDeposit ? '+' : '-';

    return Container(
      margin: const EdgeInsets.symmetric(horizontal: 24, vertical: 4),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
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
              Icon(Icons.savings_outlined, size: 14, color: color),
              const SizedBox(width: 10),
              Expanded(
                child: Text(
                  transfer.description,
                  style: GoogleFonts.sora(
                    fontSize: 13,
                    color: _textGhost,
                    fontWeight: FontWeight.w500,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              const SizedBox(width: 8),
              Text(
                '$sign${formatCurrency(transfer.amount.abs())}',
                style: GoogleFonts.firaCode(
                  fontSize: 13,
                  color: color,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ],
          ),
          const SizedBox(height: 4),
          Padding(
            padding: const EdgeInsets.only(left: 24),
            child: Row(
              children: [
                Icon(Icons.arrow_forward, size: 11, color: color),
                const SizedBox(width: 4),
                Flexible(
                  child: Text(
                    transfer.savingsGoalName,
                    style: GoogleFonts.sora(fontSize: 11, color: color),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                const SizedBox(width: 6),
                Text(
                  transfer.isDeposit
                      ? AppLocalizations.of(context).budDeposit
                      : AppLocalizations.of(context).budWithdrawal,
                  style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _SectionLabel extends StatelessWidget {
  final String text;
  final Color color;

  const _SectionLabel(this.text, {required this.color});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 12, 24, 4),
      child: Text(
        text.toUpperCase(),
        style: GoogleFonts.sora(
          fontSize: 11,
          fontWeight: FontWeight.w600,
          color: color,
          letterSpacing: 1,
        ),
      ),
    );
  }
}

class _CountUpStat extends StatelessWidget {
  final String label;
  final String value;
  final Color color;

  const _CountUpStat(this.label, this.value, this.color);

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Text(label,
            style: GoogleFonts.sora(fontSize: 10, color: _textMuted)),
        const SizedBox(height: 2),
        Text(value,
            style: GoogleFonts.firaCode(
                fontSize: 13, color: color, fontWeight: FontWeight.w600)),
      ],
    );
  }
}

/// Unified progress bar: purple = earned-to-date, green/red sub-bar = spent.
/// Green when spent < earned (under budget), red past earned (over budget).
/// Scales beyond 100% if spending exceeds the full allocation.
class _UnifiedCountUpBar extends StatelessWidget {
  final double earnedPct;
  final double earnedToDate;
  final double actualSpent;
  final double allocatedAmount;

  const _UnifiedCountUpBar({
    required this.earnedPct,
    required this.earnedToDate,
    required this.actualSpent,
    required this.allocatedAmount,
  });

  @override
  Widget build(BuildContext context) {
    // Compute spent as % of full allocation for the unified bar
    final spentOfTotal = allocatedAmount > 0
        ? (actualSpent / allocatedAmount * 100).clamp(0.0, 200.0)
        : 0.0;
    final isOverEarned = actualSpent > earnedToDate;
    // Scale: if spent > 100% of allocation, expand the bar reference to 150%
    final barScale = spentOfTotal > 100 ? 150.0 : 100.0;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Legend
        Row(
          children: [
            Container(
              width: 8,
              height: 8,
              decoration: BoxDecoration(
                color: _accent,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
            const SizedBox(width: 4),
            Text(AppLocalizations.of(context).budEarned,
                style: GoogleFonts.sora(fontSize: 10, color: _textMuted)),
            const SizedBox(width: 12),
            Container(
              width: 8,
              height: 8,
              decoration: BoxDecoration(
                color: isOverEarned ? _expense : _income,
                borderRadius: BorderRadius.circular(2),
              ),
            ),
            const SizedBox(width: 4),
            Text(AppLocalizations.of(context).budSpent,
                style: GoogleFonts.sora(fontSize: 10, color: _textMuted)),
            const Spacer(),
            Text(
              '${formatCurrency(actualSpent)} / ${formatCurrency(earnedToDate)}',
              style: GoogleFonts.firaCode(fontSize: 10, color: _textMuted),
            ),
          ],
        ),
        const SizedBox(height: 6),
        // Stacked bar
        SizedBox(
          height: 10,
          child: ClipRRect(
            borderRadius: BorderRadius.circular(5),
            child: Stack(
              children: [
                // Background (full bar)
                Container(color: _cardBg),
                // Purple earned bar
                FractionallySizedBox(
                  widthFactor: (earnedPct / barScale).clamp(0.0, 1.0),
                  child: Container(
                    decoration: BoxDecoration(
                      color: _accent.withValues(alpha: 0.4),
                    ),
                  ),
                ),
                // Spent bar (green if under earned, red if over)
                FractionallySizedBox(
                  widthFactor: (spentOfTotal / barScale).clamp(0.0, 1.0),
                  child: Container(
                    decoration: BoxDecoration(
                      color: isOverEarned ? _expense : _income,
                    ),
                  ),
                ),
                // Earned marker line
                if (earnedPct > 0 && earnedPct < barScale)
                  Positioned(
                    left: 0,
                    right: 0,
                    top: 0,
                    bottom: 0,
                    child: FractionallySizedBox(
                      alignment: Alignment.centerLeft,
                      widthFactor: (earnedPct / barScale).clamp(0.0, 1.0),
                      child: Align(
                        alignment: Alignment.centerRight,
                        child: Container(
                          width: 2,
                          color: _accent,
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 4),
        // Labels below bar
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Text(
              AppLocalizations.of(context)
                  .budEarnedLabel(formatCurrency(earnedToDate)),
              style: GoogleFonts.firaCode(
                  fontSize: 10, color: _accent, fontWeight: FontWeight.w500),
            ),
            Text(
              AppLocalizations.of(context)
                  .budBudgetLabel(formatCurrency(allocatedAmount)),
              style:
                  GoogleFonts.firaCode(fontSize: 10, color: _textMuted),
            ),
          ],
        ),
      ],
    );
  }
}

// ═══════════════════════════════════════════════════════════════
//  PERIOD CHIP — horizontal scrollable period selector
// ═══════════════════════════════════════════════════════════════

class _PeriodChip extends StatelessWidget {
  final String label;
  final bool isCurrent;
  final bool selected;
  final VoidCallback onTap;

  const _PeriodChip({
    required this.label,
    required this.isCurrent,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final activeColor = isCurrent ? _accent : _pending;
    return GestureDetector(
      onTap: onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 200),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
        decoration: BoxDecoration(
          color: selected
              ? activeColor.withValues(alpha: isCurrent ? 0.15 : 0.12)
              : _cardBg,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(
            color: selected
                ? activeColor.withValues(alpha: 0.4)
                : _cardBorder,
          ),
        ),
        child: Text(
          label,
          style: GoogleFonts.sora(
            fontSize: 12,
            color: selected ? activeColor : _textMuted,
            fontWeight: selected ? FontWeight.w600 : FontWeight.normal,
          ),
        ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════════
//  HISTORICAL CATEGORY ROW — read-only with editable allocation
// ═══════════════════════════════════════════════════════════════

class _HistoricalCategoryRow extends StatefulWidget {
  final BudgetPeriodCategoryHistory category;
  final VoidCallback onAmountTap;

  const _HistoricalCategoryRow({
    required this.category,
    required this.onAmountTap,
  });

  @override
  State<_HistoricalCategoryRow> createState() =>
      _HistoricalCategoryRowState();
}

class _HistoricalCategoryRowState extends State<_HistoricalCategoryRow> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = widget.category;
    final pct = c.percentageUsed.clamp(0, 200).toDouble();
    final barColor = c.isIncome
        ? _income
        : (pct > 100 ? _expense : (pct > 80 ? _pending : _accent));

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 150),
        margin: const EdgeInsets.symmetric(horizontal: 24, vertical: 4),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
        decoration: BoxDecoration(
          color: _hovered
              ? _pending.withValues(alpha: 0.04)
              : _cardBg,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(
            color: _hovered
                ? _pending.withValues(alpha: 0.2)
                : _cardBorder,
          ),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Container(
                  width: 8,
                  height: 8,
                  decoration: BoxDecoration(
                    color: barColor,
                    shape: BoxShape.circle,
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    c.categoryName,
                    style: GoogleFonts.sora(
                      fontSize: 13,
                      color: _textGhost,
                      fontWeight: FontWeight.w500,
                    ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                const SizedBox(width: 8),
                Text(
                  formatCurrency(c.remainingAmount),
                  style: GoogleFonts.firaCode(
                    fontSize: 13,
                    color: c.remainingAmount >= 0 ? _income : _expense,
                    fontWeight: FontWeight.w600,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 6),
            Row(
              children: [
                const SizedBox(width: 18),
                GestureDetector(
                  onTap: widget.onAmountTap,
                  child: Text(
                    formatCurrency(c.allocatedAmount),
                    style: GoogleFonts.firaCode(
                      fontSize: 11,
                      color: _textMuted,
                      decoration:
                          _hovered ? TextDecoration.underline : null,
                      decorationColor: _pending.withValues(alpha: 0.5),
                    ),
                  ),
                ),
                Text(
                  AppLocalizations.of(context).budBudgetedSeparator,
                  style: GoogleFonts.sora(
                    fontSize: 11,
                    color: _textMuted.withValues(alpha: 0.6),
                  ),
                ),
                Text(
                  formatCurrency(c.spentAmount),
                  style: GoogleFonts.firaCode(
                    fontSize: 11,
                    color: c.isIncome
                        ? _income
                        : _textGhost.withValues(alpha: 0.7),
                  ),
                ),
                Text(
                  AppLocalizations.of(context).budSpentSuffix,
                  style: GoogleFonts.sora(
                    fontSize: 11,
                    color: _textMuted.withValues(alpha: 0.6),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),
            ClipRRect(
              borderRadius: BorderRadius.circular(3),
              child: LinearProgressIndicator(
                value: (pct / 100).clamp(0.0, 1.0),
                backgroundColor: _cardBg,
                color: barColor.withValues(alpha: 0.5),
                minHeight: 6,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Zero-based budgeting indicator for the classic budget summary card.
/// income - allocated: positive shows the amount still unassigned, zero
/// celebrates full assignment, negative warns about over-assignment. Renders
/// nothing when income is unset so non-ZBB users never see it.
class _ReadyToAssignStrip extends StatelessWidget {
  final double income;
  final double allocated;

  const _ReadyToAssignStrip({required this.income, required this.allocated});

  @override
  Widget build(BuildContext context) {
    if (income <= 0) return const SizedBox.shrink();
    final ready = income - allocated;
    final assignedAll = ready.abs() < 0.005;

    final Color color;
    final IconData icon;
    final String label;
    if (assignedAll) {
      color = _income;
      icon = Icons.check_circle_outline;
      label = 'Every dollar has a job';
    } else if (ready > 0) {
      color = _accent;
      icon = Icons.assignment_outlined;
      label = '${formatCurrency(ready)} ready to assign';
    } else {
      color = _expense;
      icon = Icons.error_outline;
      label = '${formatCurrency(-ready)} over-assigned';
    }

    return Padding(
      padding: const EdgeInsets.only(bottom: 14),
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.10),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: color.withValues(alpha: 0.35)),
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(icon, size: 15, color: color),
            const SizedBox(width: 8),
            Flexible(
              child: Text(
                label,
                overflow: TextOverflow.ellipsis,
                style: GoogleFonts.sora(
                  fontSize: 12.5,
                  fontWeight: FontWeight.w600,
                  color: color,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
