import '../config/app_brand.dart';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:intl/intl.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/subscription_models.dart';
import '../models/transaction_models.dart';
import '../utils/currency.dart';
import '../services/subscriptions_service.dart';
import '../services/transactions_service.dart';
import '../utils/formatters.dart';
 import '../utils/pull_to_refresh.dart';
import '../widgets/app_toast.dart';

// ── VaporClinic palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _sheetBg = Color(0xFF0C0C18);

// ── Category → icon map ─────────────────────────────────
const _catIcons = <SubscriptionCategory, IconData>{
  SubscriptionCategory.streaming: Icons.play_circle_outline,
  SubscriptionCategory.software: Icons.code,
  SubscriptionCategory.utilities: Icons.bolt,
  SubscriptionCategory.insurance: Icons.shield_outlined,
  SubscriptionCategory.fitness: Icons.fitness_center,
  SubscriptionCategory.news: Icons.article_outlined,
  SubscriptionCategory.gaming: Icons.sports_esports,
  SubscriptionCategory.shopping: Icons.shopping_bag_outlined,
  SubscriptionCategory.education: Icons.school_outlined,
  SubscriptionCategory.professional: Icons.work_outline,
  SubscriptionCategory.other: Icons.more_horiz,
};

// ── Category → color map ────────────────────────────────
const _catColors = <SubscriptionCategory, Color>{
  SubscriptionCategory.streaming: Color(0xFFE040FB),
  SubscriptionCategory.software: Color(0xFF7B61FF),
  SubscriptionCategory.utilities: Color(0xFFFBBF24),
  SubscriptionCategory.insurance: Color(0xFF60A5FA),
  SubscriptionCategory.fitness: Color(0xFF34D399),
  SubscriptionCategory.news: Color(0xFFF0EFF4),
  SubscriptionCategory.gaming: Color(0xFFFF6B6B),
  SubscriptionCategory.shopping: Color(0xFFFFA726),
  SubscriptionCategory.education: Color(0xFF4DD0E1),
  SubscriptionCategory.professional: Color(0xFFA78BFA),
  SubscriptionCategory.other: Color(0xFF8A8A9A),
};

// ── Status → color ──────────────────────────────────────
Color _statusColor(SubscriptionStatus s) => switch (s) {
      SubscriptionStatus.active => _income,
      SubscriptionStatus.paused => _textMuted,
      SubscriptionStatus.cancelled => _expense,
      SubscriptionStatus.expired => const Color(0xFF666666),
    };

// ── BillingEventType → color ────────────────────────────
Color _eventColor(BillingEventType t) => switch (t) {
      BillingEventType.past => _textMuted,
      BillingEventType.next => _income,
      BillingEventType.future => _accent,
    };

// ── Confidence → color ──────────────────────────────────
Color _confidenceColor(double c) =>
    c >= 0.8 ? _income : c >= 0.5 ? const Color(0xFFFBBF24) : _expense;

// ── Snooze options ──────────────────────────────────────
const _snoozeOptions = [7, 14, 30, 60, 90];
const _snoozeLabels = ['1 week', '2 weeks', '1 month', '2 months', '3 months'];

// ═══════════════════════════════════════════════════════════
//  SubscriptionsPage
// ═══════════════════════════════════════════════════════════
class SubscriptionsPage extends StatefulWidget {
  const SubscriptionsPage({super.key});

  @override
  State<SubscriptionsPage> createState() =>
      _SubscriptionsPageState();
}

class _SubscriptionsPageState
    extends State<SubscriptionsPage>
    with SingleTickerProviderStateMixin {
  // ── Data ─────────────────────────────────────────────
  List<Subscription> _subscriptions = [];
  SubscriptionSummary _summary = SubscriptionSummary.empty();
  List<SuggestedSubscription> _suggestions = [];
  List<SubscriptionTimeline> _potentiallyCancelled = [];
  bool _isLoading = true;
  String? _error;
  bool _canRunDetection = true;

  // ── View ─────────────────────────────────────────────
  bool _calendarView = false;
  bool _filterAll = false; // false = current month only

  // ── Calendar ─────────────────────────────────────────
  late DateTime _calendarMonth;
  DateTime? _selectedDay;
  List<CalendarBillingEvent> _calendarEvents = [];
  bool _calendarLoading = false;

  // ── Suggestions expansion ────────────────────────────
  // (banners don't expand — kept for potential future use)

  // ── Animation ────────────────────────────────────────
  late AnimationController _fadeCtrl;
  late Animation<double> _fade;

  // ── Action-loading ids ───────────────────────────────
  final Set<int> _actionBusy = {};

  @override
  void initState() {
    super.initState();
    final now = DateTime.now();
    _calendarMonth = DateTime(now.year, now.month);
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

  // ═══════════════════════════════════════════════════════
  //  Data Loading
  // ═══════════════════════════════════════════════════════

  Future<void> _loadData() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        subscriptionsService.getSubscriptions(),
        subscriptionsService.getSubscriptionSummary(),
        subscriptionsService.getSuggestions(),
        subscriptionsService.getPotentiallyCancelled(),
        subscriptionsService.canRunDetection(),
      ]);
      if (!mounted) return;
      setState(() {
        _subscriptions = results[0] as List<Subscription>;
        _summary = results[1] as SubscriptionSummary;
        _suggestions = results[2] as List<SuggestedSubscription>;
        _potentiallyCancelled = results[3] as List<SubscriptionTimeline>;
        _canRunDetection = results[4] as bool;
        _isLoading = false;
      });
      if (_calendarView) _loadCalendarEvents();
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e.toString();
        _isLoading = false;
      });
    }
  }

  Future<void> _loadCalendarEvents() async {
    setState(() => _calendarLoading = true);
    try {
      final events = await subscriptionsService.getCalendarBillingEvents(
        year: _calendarMonth.year,
        month: _calendarMonth.month,
      );
      if (!mounted) return;
      setState(() {
        _calendarEvents = events;
        _calendarLoading = false;
      });
    } catch (_) {
      if (mounted) setState(() => _calendarLoading = false);
    }
  }

  // ═══════════════════════════════════════════════════════
  //  Actions
  // ═══════════════════════════════════════════════════════

  void _showAddSubscription() {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _AddSubscriptionSheet(
        onSaved: () {
          _loadData();
        },
      ),
    );
  }

  void _showEditSubscription(Subscription sub) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _SubscriptionFormSheet(
        subscription: sub,
        onSaved: () {
          _loadData();
        },
        onDeleted: () {
          _loadData();
        },
      ),
    );
  }

  void _showDetection() {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _DetectionSheet(onDone: () => _loadData()),
    );
  }

  Future<void> _toggleStatus(Subscription sub) async {
    final newStatus = sub.status == SubscriptionStatus.active
        ? SubscriptionStatus.paused
        : SubscriptionStatus.active;
    try {
      await subscriptionsService.updateSubscription(
        sub.id,
        UpdateSubscriptionRequest(status: newStatus),
      );
      _loadData();
    } catch (_) {
      if (mounted) {
        showAppToast(
          context,
          'Couldn\'t update subscription. Please try again.',
          type: AppToastType.error,
        );
      }
    }
  }

  /// Merchant name for a suggestion id, safe if the suggestion is no longer in
  /// the list (e.g. already removed by a concurrent action). Never throws.
  String _suggestionNameFor(int id) {
    for (final s in _suggestions) {
      if (s.id == id) return s.merchantName;
    }
    return 'Subscription';
  }

  Future<void> _confirmSuggestion(int id) async {
    setState(() => _actionBusy.add(id));
    // Let failures propagate to the caller's error handler
    // (_SuggestionReviewSheet._handleAction) so a failed write is NOT
    // silently treated as success. Only clear the busy state here.
    try {
      final name = _suggestionNameFor(id);
      await subscriptionsService.confirmSuggestion(id);
      setState(() => _suggestions.removeWhere((s) => s.id == id));
      _loadData();
      if (mounted) {
        showAppToast(
          context,
          'Added "$name" as subscription',
          type: AppToastType.success,
          actionLabel: 'UNDO',
          onAction: () => _undoSuggestionAction(id),
        );
      }
    } finally {
      if (mounted) setState(() => _actionBusy.remove(id));
    }
  }

  Future<void> _dismissSuggestion(int id, {bool permanent = false}) async {
    setState(() => _actionBusy.add(id));
    // Let failures propagate to the caller's error handler
    // (_SuggestionReviewSheet._handleAction) so a failed write is NOT
    // silently treated as success. Only clear the busy state here.
    try {
      final name = _suggestionNameFor(id);
      await subscriptionsService.dismissSuggestion(id, permanent: permanent);
      setState(() => _suggestions.removeWhere((s) => s.id == id));
      if (mounted) {
        final msg = permanent
            ? '"$name" permanently dismissed'
            : '"$name" dismissed';
        showAppToast(
          context,
          msg,
          actionLabel: permanent ? null : 'UNDO',
          onAction: permanent ? null : () => _undoSuggestionAction(id),
        );
      }
    } finally {
      if (mounted) setState(() => _actionBusy.remove(id));
    }
  }

  Future<void> _undoSuggestionAction(int id) async {
    try {
      await subscriptionsService.undoSuggestionAction(id);
      _loadData();
    } catch (_) {}
  }

  void _showSnoozeSuggestion(SuggestedSubscription s) {
    showModalBottomSheet(
      context: context,
      backgroundColor: Colors.transparent,
      builder: (_) => _SnoozeSheet(
        merchantName: s.merchantName,
        onSnooze: (days) async {
          Navigator.pop(context);
          setState(() => _actionBusy.add(s.id));
          try {
            await subscriptionsService.snoozeSuggestion(s.id, days);
            setState(() => _suggestions.removeWhere((x) => x.id == s.id));
            if (mounted) {
              showAppToast(
                context,
                '"${s.merchantName}" snoozed for $days days',
                actionLabel: 'UNDO',
                onAction: () => _undoSuggestionAction(s.id),
              );
            }
          } catch (_) {}
          if (mounted) setState(() => _actionBusy.remove(s.id));
        },
      ),
    );
  }

  Future<void> _keepSubscription(int subId) async {
    // Errors propagate to _CancelledReviewSheet._handleAction, which surfaces
    // them and keeps the card in place — do NOT swallow here.
    // Find the timeline entry to get the frequency for proper billing date calculation
    final timeline = _potentiallyCancelled
        .where((t) => t.subscriptionId == subId)
        .firstOrNull;
    final nextDue = timeline != null
        ? _getNextBillingDate(DateTime.now(), timeline.frequency)
        : DateTime.now().add(const Duration(days: 30));
    await subscriptionsService.updateSubscription(
      subId,
      UpdateSubscriptionRequest(nextDueDate: nextDue),
    );
    setState(
        () => _potentiallyCancelled.removeWhere((t) => t.subscriptionId == subId));
    _loadData();
  }

  Future<void> _cancelSubscription(int subId) async {
    // Errors propagate to _CancelledReviewSheet._handleAction, which surfaces
    // them and keeps the card in place — do NOT swallow here.
    await subscriptionsService.updateSubscription(
      subId,
      UpdateSubscriptionRequest(status: SubscriptionStatus.cancelled),
    );
    setState(
        () => _potentiallyCancelled.removeWhere((t) => t.subscriptionId == subId));
    _loadData();
  }

  /// Add [months] to [current], clamping to the last day of the target month
  /// (mirrors C# DateTime.AddMonths behavior).
  DateTime _addMonthsClamped(DateTime current, int months) {
    final totalMonths = (current.year * 12) + (current.month - 1) + months;
    final targetYear = totalMonths ~/ 12;
    final targetMonth = (totalMonths % 12) + 1;
    final lastDayOfTargetMonth = DateTime(targetYear, targetMonth + 1, 0).day;
    final targetDay =
        current.day > lastDayOfTargetMonth ? lastDayOfTargetMonth : current.day;
    return DateTime(targetYear, targetMonth, targetDay,
        current.hour, current.minute, current.second);
  }

  /// Calculate the next billing date from [current] based on [frequency].
  /// Mirrors backend's GetNextBillingDate logic.
  DateTime _getNextBillingDate(DateTime current, SubscriptionFrequency freq) {
    return switch (freq) {
      SubscriptionFrequency.weekly => current.add(const Duration(days: 7)),
      SubscriptionFrequency.biWeekly => current.add(const Duration(days: 14)),
      SubscriptionFrequency.monthly => _addMonthsClamped(current, 1),
      SubscriptionFrequency.quarterly => _addMonthsClamped(current, 3),
      SubscriptionFrequency.semiAnnual => _addMonthsClamped(current, 6),
      SubscriptionFrequency.annual => _addMonthsClamped(current, 12),
      SubscriptionFrequency.custom => _addMonthsClamped(current, 1),
    };
  }

  // ═══════════════════════════════════════════════════════
  //  Filtered list
  // ═══════════════════════════════════════════════════════

  List<Subscription> get _filteredSubscriptions {
    if (_calendarView) {
      return _subscriptions
          .where((s) => s.status == SubscriptionStatus.active)
          .toList();
    }
    if (_filterAll) return _subscriptions;
    final now = DateTime.now();
    return _subscriptions.where((s) {
      return s.nextDueDate.year == now.year &&
          s.nextDueDate.month == now.month;
    }).toList();
  }

  // ═══════════════════════════════════════════════════════
  //  Build
  // ═══════════════════════════════════════════════════════

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: FadeTransition(
        opacity: _fade,
        child: _isLoading
            ? _buildLoading()
            : _error != null
                ? _buildError()
                : _buildContent(),
      ),
    );
  }

  Widget _buildLoading() => const Center(
        child: CircularProgressIndicator(color: _accent),
      );

  Widget _buildError() => Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(Icons.error_outline, color: _expense, size: 48),
              const SizedBox(height: 16),
              Text(AppLocalizations.of(context).subFailedToLoad,
                  style: GoogleFonts.sora(color: _textGhost, fontSize: 16)),
              const SizedBox(height: 16),
              _GlassButton(label: 'Retry', onTap: _loadData, isPrimary: true),
            ],
          ),
        ),
      );

  Widget _buildContent() {
    return RefreshIndicator(
      color: _accent,
      backgroundColor: _sheetBg,
      onRefresh: _loadData,
      child: SingleChildScrollView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(20, 24, 20, 120),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _buildHeader(),
            const SizedBox(height: 24),
            _buildStatsRow(),
            const SizedBox(height: 24),
            if (_potentiallyCancelled.isNotEmpty) ...[
              _buildCancelledSection(),
              const SizedBox(height: 20),
            ],
            if (_suggestions.isNotEmpty) ...[
              _buildSuggestionsSection(),
              const SizedBox(height: 20),
            ],
            _buildViewToggle(),
            const SizedBox(height: 16),
            if (_calendarView) ...[
              _buildCalendar(),
              if (_selectedDay != null) ...[
                const SizedBox(height: 16),
                _buildCalendarDayEvents(),
              ],
            ] else ...[
              _buildFilterRow(),
              const SizedBox(height: 12),
              _buildSubscriptionsList(),
            ],
          ],
        ),
      ),
    );
  }

  // ═══════════════════════════════════════════════════════
  //  Header
  // ═══════════════════════════════════════════════════════

  Widget _buildHeader() {
    return Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const Icon(Icons.receipt_long, color: _accent, size: 28),
                  const SizedBox(width: 10),
                  Text(AppLocalizations.of(context).subTitle,
                      style: GoogleFonts.instrumentSerif(
                          color: _textGhost, fontSize: 28)),
                ],
              ),
              const SizedBox(height: 4),
              Text(AppLocalizations.of(context).subSubtitle,
                  style: GoogleFonts.sora(color: _textMuted, fontSize: 13)),
            ],
          ),
        ),
        _GlassButton(
          label: _canRunDetection ? 'Detect' : 'Cooldown',
          icon: _canRunDetection ? Icons.auto_awesome : Icons.hourglass_top,
          onTap: _canRunDetection ? _showDetection : null,
        ),
        const SizedBox(width: 8),
        _GlassButton(
          label: 'Add',
          icon: Icons.add,
          onTap: _showAddSubscription,
          isPrimary: true,
        ),
      ],
    );
  }

  // ═══════════════════════════════════════════════════════
  //  Stats Row — 4 tiles
  // ═══════════════════════════════════════════════════════

  Widget _buildStatsRow() {
    final cats = _summary.categoryBreakdown.length;
    return Wrap(
      spacing: 12,
      runSpacing: 12,
      children: [
        _StatTile(
          label: 'Monthly',
          value: formatCurrency(_summary.monthlyTotal),
          icon: Icons.calendar_month,
          color: const Color(0xFF60A5FA),
        ),
        _StatTile(
          label: 'Yearly',
          value: formatCurrency(_summary.yearlyTotal),
          icon: Icons.date_range,
          color: _accent,
        ),
        _StatTile(
          label: 'Active',
          value: '${_summary.activeCount}',
          icon: Icons.check_circle_outline,
          color: _income,
        ),
        _StatTile(
          label: 'Categories',
          value: '$cats',
          icon: Icons.category_outlined,
          color: const Color(0xFFFFA726),
        ),
      ],
    );
  }

  // ═══════════════════════════════════════════════════════
  //  Potentially Cancelled
  // ═══════════════════════════════════════════════════════

  Widget _buildCancelledSection() {
    return _GlassCard(
      child: InkWell(
        onTap: _showCancelledReview,
        borderRadius: BorderRadius.circular(12),
        child: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: _expense.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(10),
              ),
              child: const Icon(Icons.warning_amber_rounded,
                  color: _expense, size: 22),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                      AppLocalizations.of(context)
                          .subPossiblyCancelledCount(_potentiallyCancelled.length),
                      style: GoogleFonts.sora(
                          color: _textGhost,
                          fontSize: 15,
                          fontWeight: FontWeight.w600)),
                  const SizedBox(height: 2),
                  Text(AppLocalizations.of(context).subNoChargesTapReview,
                      style:
                          GoogleFonts.sora(color: _textMuted, fontSize: 12)),
                ],
              ),
            ),
            Container(
              padding:
                  const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
              decoration: BoxDecoration(
                color: _expense.withValues(alpha: 0.2),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(AppLocalizations.of(context).subReview,
                  style: GoogleFonts.sora(
                      color: _expense,
                      fontSize: 12,
                      fontWeight: FontWeight.w600)),
            ),
          ],
        ),
      ),
    );
  }

  void _showCancelledReview() {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _CancelledReviewSheet(
        items: _potentiallyCancelled,
        onKeep: _keepSubscription,
        onCancel: _cancelSubscription,
        onDone: () => _loadData(),
      ),
    );
  }

  // ═══════════════════════════════════════════════════════
  //  AI Suggestions
  // ═══════════════════════════════════════════════════════

  Widget _buildSuggestionsSection() {
    final highConf = _suggestions.where((s) => s.confidence >= 0.8).length;
    return _GlassCard(
      child: InkWell(
        onTap: _showSuggestionReview,
        borderRadius: BorderRadius.circular(12),
        child: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(10),
              ),
              child: const Icon(Icons.auto_awesome, color: _accent, size: 22),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                      AppLocalizations.of(context)
                          .subSuggestionsCount(_suggestions.length),
                      style: GoogleFonts.sora(
                          color: _textGhost,
                          fontSize: 15,
                          fontWeight: FontWeight.w600)),
                  const SizedBox(height: 2),
                  Text(
                    highConf > 0
                        ? AppLocalizations.of(context)
                            .subHighConfidenceTapReview(highConf)
                        : AppLocalizations.of(context).subTapToReviewPatterns,
                    style: GoogleFonts.sora(color: _textMuted, fontSize: 12),
                  ),
                ],
              ),
            ),
            Container(
              padding:
                  const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.2),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(AppLocalizations.of(context).subReview,
                  style: GoogleFonts.sora(
                      color: _accent,
                      fontSize: 12,
                      fontWeight: FontWeight.w600)),
            ),
          ],
        ),
      ),
    );
  }

  void _showSuggestionReview() {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _SuggestionReviewSheet(
        suggestions: _suggestions,
        actionBusy: _actionBusy,
        onConfirm: _confirmSuggestion,
        onDismiss: _dismissSuggestion,
        onSnooze: _showSnoozeSuggestion,
        onDone: () => _loadData(),
      ),
    );
  }

  // ═══════════════════════════════════════════════════════
  //  View Toggle
  // ═══════════════════════════════════════════════════════

  Widget _buildViewToggle() {
    return Row(
      children: [
        _ToggleChip(
          label: 'Calendar',
          icon: Icons.calendar_month,
          selected: _calendarView,
          onTap: () {
            setState(() => _calendarView = true);
            _loadCalendarEvents();
          },
        ),
        const SizedBox(width: 8),
        _ToggleChip(
          label: 'List',
          icon: Icons.list,
          selected: !_calendarView,
          onTap: () => setState(() => _calendarView = false),
        ),
      ],
    );
  }

  Widget _buildFilterRow() {
    return Row(
      children: [
        _ToggleChip(
          label: 'This Month',
          selected: !_filterAll,
          onTap: () => setState(() => _filterAll = false),
        ),
        const SizedBox(width: 8),
        _ToggleChip(
          label: 'All',
          selected: _filterAll,
          onTap: () => setState(() => _filterAll = true),
        ),
        const Spacer(),
        // Desktop-only — mobile uses pull-to-refresh (same _loadData).
        if (showsManualRefresh(context))
          GestureDetector(
            onTap: _loadData,
            child: const Icon(Icons.refresh, color: _textMuted, size: 20),
          ),
      ],
    );
  }

  // ═══════════════════════════════════════════════════════
  //  Calendar
  // ═══════════════════════════════════════════════════════

  Widget _buildCalendar() {
    final now = DateTime.now();
    final firstDayOfMonth =
        DateTime(_calendarMonth.year, _calendarMonth.month, 1);
    final daysInMonth =
        DateTime(_calendarMonth.year, _calendarMonth.month + 1, 0).day;
    final startWeekday = firstDayOfMonth.weekday % 7; // 0=Sun

    // Also compute previous month trailing days
    final prevMonthDays =
        DateTime(_calendarMonth.year, _calendarMonth.month, 0).day;

    // Group events by day number
    final eventsByDay = <int, List<CalendarBillingEvent>>{};
    for (final e in _calendarEvents) {
      if (e.date.year == _calendarMonth.year &&
          e.date.month == _calendarMonth.month) {
        eventsByDay.putIfAbsent(e.date.day, () => []).add(e);
      }
    }

    const dayNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    final totalCells = startWeekday + daysInMonth;
    final rows = (totalCells / 7).ceil();
    // Ensure we always show at least 5 rows for consistent height
    final displayRows = rows < 5 ? 5 : rows;

    // Get category color for a subscription event (fallback to event type color)
    Color eventChipColor(CalendarBillingEvent ev) {
      // Try to match subscription to get its category color
      for (final sub in _subscriptions) {
        if (sub.id == ev.subscriptionId) {
          return _catColors[sub.category] ?? _eventColor(ev.eventType);
        }
      }
      return _eventColor(ev.eventType);
    }

    const gridBorder = Color(0x0AFFFFFF);

    return Container(
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: _cardBorder),
      ),
      clipBehavior: Clip.antiAlias,
      child: Column(
        children: [
          // ── Month nav header ──────────────────────────
          Padding(
            padding:
                const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
            child: Row(
              children: [
                GestureDetector(
                  onTap: () {
                    setState(() => _calendarMonth = DateTime(
                        _calendarMonth.year, _calendarMonth.month - 1));
                    _selectedDay = null;
                    _loadCalendarEvents();
                  },
                  child: const Icon(Icons.chevron_left,
                      color: _textMuted, size: 22),
                ),
                const Spacer(),
                Text(
                  DateFormat.yMMMM().format(_calendarMonth),
                  style: GoogleFonts.sora(
                      color: _textGhost,
                      fontSize: 17,
                      fontWeight: FontWeight.w600),
                ),
                const Spacer(),
                GestureDetector(
                  onTap: () {
                    setState(() =>
                        _calendarMonth = DateTime(now.year, now.month));
                    _selectedDay = null;
                    _loadCalendarEvents();
                  },
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                        horizontal: 10, vertical: 4),
                    decoration: BoxDecoration(
                      border: Border.all(
                          color: Colors.white.withValues(alpha: 0.12)),
                      borderRadius: BorderRadius.circular(6),
                    ),
                    child: Text(AppLocalizations.of(context).subToday,
                        style: GoogleFonts.sora(
                            color: _textMuted, fontSize: 11)),
                  ),
                ),
                const SizedBox(width: 8),
                GestureDetector(
                  onTap: () {
                    setState(() => _calendarMonth = DateTime(
                        _calendarMonth.year, _calendarMonth.month + 1));
                    _selectedDay = null;
                    _loadCalendarEvents();
                  },
                  child: const Icon(Icons.chevron_right,
                      color: _textMuted, size: 22),
                ),
              ],
            ),
          ),

          // ── Day-of-week headers ───────────────────────
          Container(
            decoration: const BoxDecoration(
              border: Border(
                top: BorderSide(color: gridBorder),
                bottom: BorderSide(color: gridBorder),
              ),
            ),
            child: Row(
              children: dayNames
                  .map((d) => Expanded(
                        child: Padding(
                          padding: const EdgeInsets.symmetric(vertical: 8),
                          child: Center(
                            child: Text(d,
                                style: GoogleFonts.sora(
                                  color: _textMuted,
                                  fontSize: 11,
                                  fontWeight: FontWeight.w500,
                                  letterSpacing: 0.3,
                                )),
                          ),
                        ),
                      ))
                  .toList(),
            ),
          ),

          // ── Calendar grid ─────────────────────────────
          if (_calendarLoading)
            const Padding(
              padding: EdgeInsets.all(40),
              child: CircularProgressIndicator(
                  color: _accent, strokeWidth: 2),
            )
          else
            ...List.generate(displayRows, (row) {
              return Container(
                decoration: BoxDecoration(
                  border: row < displayRows - 1
                      ? const Border(
                          bottom: BorderSide(color: gridBorder))
                      : null,
                ),
                child: IntrinsicHeight(
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: List.generate(7, (col) {
                      final cellIndex = row * 7 + col;
                      final dayNum = cellIndex - startWeekday + 1;
                      final isCurrentMonth =
                          dayNum >= 1 && dayNum <= daysInMonth;

                      // Compute display number for out-of-month days
                      int displayNum;
                      bool isOutOfMonth;
                      if (dayNum < 1) {
                        displayNum = prevMonthDays + dayNum;
                        isOutOfMonth = true;
                      } else if (dayNum > daysInMonth) {
                        displayNum = dayNum - daysInMonth;
                        isOutOfMonth = true;
                      } else {
                        displayNum = dayNum;
                        isOutOfMonth = false;
                      }

                      final date = isCurrentMonth
                          ? DateTime(_calendarMonth.year,
                              _calendarMonth.month, dayNum)
                          : null;
                      final isToday = date != null &&
                          date.year == now.year &&
                          date.month == now.month &&
                          date.day == now.day;
                      final isSelected = date != null &&
                          _selectedDay != null &&
                          _selectedDay!.year == date.year &&
                          _selectedDay!.month == date.month &&
                          _selectedDay!.day == date.day;
                      final dayEvents =
                          isCurrentMonth ? (eventsByDay[dayNum] ?? []) : <CalendarBillingEvent>[];

                      return Expanded(
                        child: GestureDetector(
                          onTap: isCurrentMonth
                              ? () => setState(() => _selectedDay = date)
                              : null,
                          child: Container(
                            constraints:
                                const BoxConstraints(minHeight: 80),
                            decoration: BoxDecoration(
                              color: isSelected
                                  ? _accent.withValues(alpha: 0.08)
                                  : null,
                              border: col < 6
                                  ? const Border(
                                      right:
                                          BorderSide(color: gridBorder))
                                  : null,
                            ),
                            padding: const EdgeInsets.all(4),
                            child: Column(
                              crossAxisAlignment:
                                  CrossAxisAlignment.start,
                              children: [
                                // Day number (top-left) with today ring
                                Padding(
                                  padding: const EdgeInsets.only(
                                      left: 2, top: 1),
                                  child: isToday
                                      ? Container(
                                          width: 26,
                                          height: 26,
                                          decoration: BoxDecoration(
                                            color: _accent,
                                            borderRadius:
                                                BorderRadius.circular(8),
                                          ),
                                          alignment: Alignment.center,
                                          child: Text(
                                            '$displayNum',
                                            style: GoogleFonts.sora(
                                              color: Colors.white,
                                              fontSize: 12,
                                              fontWeight: FontWeight.w700,
                                            ),
                                          ),
                                        )
                                      : Padding(
                                          padding:
                                              const EdgeInsets.all(3),
                                          child: Text(
                                            '$displayNum',
                                            style: GoogleFonts.sora(
                                              color: isOutOfMonth
                                                  ? _textMuted
                                                      .withValues(
                                                          alpha: 0.35)
                                                  : isSelected
                                                      ? _accent
                                                      : _textGhost,
                                              fontSize: 12,
                                              fontWeight: FontWeight.w400,
                                            ),
                                          ),
                                        ),
                                ),

                                // Event chips (max 2 shown)
                                if (dayEvents.isNotEmpty) ...[
                                  const SizedBox(height: 2),
                                  ...dayEvents.take(2).map((ev) {
                                    final catColor = eventChipColor(ev);
                                    final typeColor =
                                        _eventColor(ev.eventType);
                                    return Padding(
                                      padding:
                                          const EdgeInsets.only(
                                              bottom: 2),
                                      child: Container(
                                        height: 16,
                                        decoration: BoxDecoration(
                                          color: typeColor
                                              .withValues(
                                                  alpha: 0.12),
                                          borderRadius:
                                              BorderRadius.circular(
                                                  3),
                                          border: Border.all(
                                            color: typeColor
                                                .withValues(
                                                    alpha: 0.3),
                                            width: 0.5,
                                          ),
                                        ),
                                        padding:
                                            const EdgeInsets.symmetric(
                                                horizontal: 3),
                                        child: Row(
                                          children: [
                                            Container(
                                              width: 3,
                                              height: 10,
                                              decoration:
                                                  BoxDecoration(
                                                color: catColor,
                                                borderRadius:
                                                    BorderRadius
                                                        .circular(2),
                                              ),
                                            ),
                                            const SizedBox(width: 3),
                                            Expanded(
                                              child: Text(
                                                ev.subscriptionName,
                                                maxLines: 1,
                                                overflow: TextOverflow
                                                    .ellipsis,
                                                style:
                                                    GoogleFonts.sora(
                                                  color: typeColor,
                                                  fontSize: 9,
                                                  fontWeight:
                                                      FontWeight.w500,
                                                ),
                                              ),
                                            ),
                                          ],
                                        ),
                                      ),
                                    );
                                  }),
                                  if (dayEvents.length > 2)
                                    Padding(
                                      padding: const EdgeInsets.only(
                                          left: 2),
                                      child: Text(
                                        AppLocalizations.of(context)
                                            .subMoreEvents(
                                                dayEvents.length - 2),
                                        style: GoogleFonts.sora(
                                          color: _textMuted,
                                          fontSize: 8,
                                          fontWeight: FontWeight.w500,
                                        ),
                                      ),
                                    ),
                                ],
                              ],
                            ),
                          ),
                        ),
                      );
                    }),
                  ),
                ),
              );
            }),

          // ── Legend ─────────────────────────────────────
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 10),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                _CalendarLegend(color: _textMuted, label: 'Past'),
                const SizedBox(width: 16),
                _CalendarLegend(color: _income, label: 'Next Due'),
                const SizedBox(width: 16),
                _CalendarLegend(color: _accent, label: 'Upcoming'),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildCalendarDayEvents() {
    final events = _calendarEvents
        .where((e) =>
            _selectedDay != null &&
            e.date.year == _selectedDay!.year &&
            e.date.month == _selectedDay!.month &&
            e.date.day == _selectedDay!.day)
        .toList();
    if (events.isEmpty) {
      return _GlassCard(
        child: Center(
          child: Padding(
            padding: const EdgeInsets.all(20),
            child: Column(
              children: [
                const Icon(Icons.event_available,
                    color: _textMuted, size: 28),
                const SizedBox(height: 8),
                Text(AppLocalizations.of(context).subNoBillingEventsDay,
                    style: GoogleFonts.sora(color: _textMuted, fontSize: 13)),
              ],
            ),
          ),
        ),
      );
    }

    // Get category color for event
    Color chipColor(CalendarBillingEvent ev) {
      for (final sub in _subscriptions) {
        if (sub.id == ev.subscriptionId) {
          return _catColors[sub.category] ?? _eventColor(ev.eventType);
        }
      }
      return _eventColor(ev.eventType);
    }

    final totalForDay = events.fold<double>(0, (s, e) => s + e.amount);

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: const Icon(Icons.calendar_today,
                    color: _accent, size: 16),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      DateFormat.yMMMMd().format(_selectedDay!),
                      style: GoogleFonts.sora(
                          color: _textGhost,
                          fontSize: 14,
                          fontWeight: FontWeight.w600),
                    ),
                    Text(
                      AppLocalizations.of(context).subEventCountTotal(
                          events.length, formatCurrency(totalForDay)),
                      style: GoogleFonts.sora(
                          color: _textMuted, fontSize: 11),
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 14),
          ...events.map((ev) {
            final catColor = chipColor(ev);
            final typeColor = _eventColor(ev.eventType);
            return Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Container(
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(
                    color: typeColor.withValues(alpha: 0.04),
                    borderRadius: BorderRadius.circular(10),
                    border: Border.all(
                        color: typeColor.withValues(alpha: 0.2)),
                  ),
                  child: Row(
                    children: [
                      // Category color bar
                      Container(
                        width: 4,
                        height: 32,
                        decoration: BoxDecoration(
                          color: catColor,
                          borderRadius: BorderRadius.circular(2),
                        ),
                      ),
                      const SizedBox(width: 10),
                      // Subscription name + type badge
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(ev.subscriptionName,
                                style: GoogleFonts.sora(
                                    color: _textGhost,
                                    fontSize: 13,
                                    fontWeight: FontWeight.w500)),
                            const SizedBox(height: 2),
                            _Badge(
                              label: ev.eventType.displayName,
                              color: _eventColor(ev.eventType),
                              small: true,
                            ),
                          ],
                        ),
                      ),
                      // Amount
                      Text(
                        formatCurrency(ev.amount),
                        style: GoogleFonts.firaCode(
                            color: _textGhost,
                            fontSize: 14,
                            fontWeight: FontWeight.w500),
                      ),
                    ],
                  ),
                ),
              );
          }),
        ],
      ),
    );
  }

  // ═══════════════════════════════════════════════════════
  //  Subscriptions List
  // ═══════════════════════════════════════════════════════

  Widget _buildSubscriptionsList() {
    final subs = _filteredSubscriptions;
    if (subs.isEmpty) {
      // The "This Month" filter can hide subscriptions due in other months.
      // Don't make that read as data loss — show the total and a one-tap escape.
      final hiddenByMonthFilter =
          !_calendarView && !_filterAll && _subscriptions.isNotEmpty;
      return _GlassCard(
        child: Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Column(
              children: [
                const Icon(Icons.inbox_outlined, color: _textMuted, size: 40),
                const SizedBox(height: 12),
                Text(
                    hiddenByMonthFilter
                        ? AppLocalizations.of(context)
                            .subNoneDueThisMonth(_subscriptions.length)
                        : AppLocalizations.of(context).subNoSubscriptionsFound,
                    textAlign: TextAlign.center,
                    style: GoogleFonts.sora(color: _textMuted, fontSize: 14)),
                if (hiddenByMonthFilter) ...[
                  const SizedBox(height: 14),
                  _GlassButton(
                    label: 'Show all',
                    icon: Icons.list,
                    onTap: () => setState(() => _filterAll = true),
                  ),
                ],
              ],
            ),
          ),
        ),
      );
    }
    return Column(
      children: subs.map(_buildSubscriptionTile).toList(),
    );
  }

  Widget _buildSubscriptionTile(Subscription sub) {
    final catIcon = _catIcons[sub.category] ?? Icons.more_horiz;
    final catColor = _catColors[sub.category] ?? _textMuted;
    final sColor = _statusColor(sub.status);
    final isActive = sub.status == SubscriptionStatus.active;

    return GestureDetector(
      onTap: () => _showEditSubscription(sub),
      child: Container(
        margin: const EdgeInsets.only(bottom: 8),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
          decoration: BoxDecoration(
            color: _cardBg,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: _cardBorder),
          ),
          child: Row(
                children: [
                  // Category icon
                  Container(
                    width: 38,
                    height: 38,
                    decoration: BoxDecoration(
                      color: catColor.withValues(alpha: 0.12),
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: Icon(catIcon, color: catColor, size: 18),
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
                              child: Text(sub.name,
                                  overflow: TextOverflow.ellipsis,
                                  style: GoogleFonts.sora(
                                      color: _textGhost,
                                      fontSize: 14,
                                      fontWeight: FontWeight.w500)),
                            ),
                            if (sub.isDetected) ...[
                              const SizedBox(width: 6),
                              _Badge(
                                  label: 'Auto',
                                  color: _income,
                                  small: true),
                            ],
                          ],
                        ),
                        const SizedBox(height: 3),
                        Row(
                          children: [
                            _Badge(
                              label: sub.status.displayName,
                              color: sColor,
                              small: true,
                            ),
                            const SizedBox(width: 8),
                            Text(
                              AppLocalizations.of(context).subFrequencyDue(
                                  sub.frequency.displayName,
                                  DateFormat.MMMd().format(sub.nextDueDate)),
                              style: GoogleFonts.sora(
                                  color: _textMuted, fontSize: 11),
                            ),
                          ],
                        ),
                      ],
                    ),
                  ),
                  // Amount
                  Text(
                    formatCurrency(sub.amount),
                    style: GoogleFonts.firaCode(
                        color: _textGhost,
                        fontSize: 15,
                        fontWeight: FontWeight.w500),
                  ),
                  const SizedBox(width: 8),
                  // Pause/Play
                  GestureDetector(
                    onTap: () => _toggleStatus(sub),
                    child: Icon(
                      isActive ? Icons.pause_circle_outline : Icons.play_circle_outline,
                      color: isActive ? _textMuted : _income,
                      size: 22,
                    ),
                  ),
                ],
              ),
            ),
          ),
        );
      }
    }

// ═══════════════════════════════════════════════════════════
//  _AddSubscriptionSheet — 3-step: selection → txn search → form
// ═══════════════════════════════════════════════════════════
class _AddSubscriptionSheet extends StatefulWidget {
  final VoidCallback onSaved;
  const _AddSubscriptionSheet({required this.onSaved});

  @override
  State<_AddSubscriptionSheet> createState() => _AddSubscriptionSheetState();
}

class _AddSubscriptionSheetState extends State<_AddSubscriptionSheet> {
  // 0 = selection, 1 = txn search, 2 = manual form
  int _step = 0;

  // Transaction search
  final _searchCtrl = TextEditingController();
  List<Transaction> _txResults = [];
  bool _searchLoading = false;
  Transaction? _selectedTx;

  // Form state (shared between step 1→2 prefill and step 2 manual)
  final _formKey = GlobalKey<FormState>();
  final _nameCtrl = TextEditingController();
  final _amountCtrl = TextEditingController();
  final _descCtrl = TextEditingController();
  SubscriptionFrequency _freq = SubscriptionFrequency.monthly;
  SubscriptionCategory _cat = SubscriptionCategory.other;
  DateTime _nextDue = DateTime.now().add(const Duration(days: 1));
  bool _submitting = false;
  String? _formError;

  @override
  void dispose() {
    _searchCtrl.dispose();
    _nameCtrl.dispose();
    _amountCtrl.dispose();
    _descCtrl.dispose();
    super.dispose();
  }

  Future<void> _searchTransactions() async {
    if (_searchCtrl.text.trim().isEmpty) return;
    setState(() => _searchLoading = true);
    try {
      final result = await transactionsService.getTransactions(
        TransactionFilter(search: _searchCtrl.text.trim(), pageSize: 50),
      );
      if (!mounted) return;
      // Only show expense transactions
      setState(() {
        _txResults =
            result.transactions.where((t) => t.amount < 0).toList();
        _searchLoading = false;
      });
    } catch (_) {
      if (mounted) setState(() => _searchLoading = false);
    }
  }

  void _selectTransaction(Transaction tx) {
    _nameCtrl.text = (tx.merchantName?.isNotEmpty ?? false) ? tx.merchantName! : tx.description;
    _amountCtrl.text = tx.amount.abs().toStringAsFixed(2);
    _freq = SubscriptionFrequency.monthly;
    _cat = _mapTxCategory(tx.categoryId);
    _nextDue = tx.date.add(const Duration(days: 30));
    _descCtrl.text = 'Tracked from transaction: ${tx.description}';
    setState(() {
      _selectedTx = tx;
      _step = 2;
    });
  }

  SubscriptionCategory _mapTxCategory(int? catId) {
    if (catId == null) return SubscriptionCategory.other;
    // Rough mapping from Plaid categories
    if (catId >= 18000000 && catId < 19000000) return SubscriptionCategory.software;
    if (catId >= 17000000 && catId < 18000000) return SubscriptionCategory.streaming;
    if (catId >= 10000000 && catId < 11000000) return SubscriptionCategory.insurance;
    if (catId >= 12000000 && catId < 13000000) return SubscriptionCategory.utilities;
    return SubscriptionCategory.other;
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() {
      _submitting = true;
      _formError = null;
    });
    try {
      await subscriptionsService.createSubscription(CreateSubscriptionRequest(
        name: _nameCtrl.text.trim(),
        amount: double.parse(_amountCtrl.text),
        frequency: _freq,
        category: _cat,
        nextDueDate: _nextDue,
        description: _descCtrl.text.trim().isNotEmpty ? _descCtrl.text.trim() : null,
      ));
      if (!mounted) return;
      Navigator.pop(context);
      widget.onSaved();
    } catch (_) {
      if (mounted) {
        setState(() {
          // Surface a friendly message instead of a raw DioException string.
          _formError =
              "Couldn't save subscription. Please check the details and try again.";
          _submitting = false;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.of(context).size.height * 0.9,
      ),
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Container(
            width: 40,
            height: 4,
            margin: const EdgeInsets.only(top: 12, bottom: 8),
            decoration: BoxDecoration(
              color: Colors.white.withValues(alpha: 0.2),
              borderRadius: BorderRadius.circular(2),
            ),
          ),
          // Title
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
            child: Row(
              children: [
                if (_step > 0)
                  GestureDetector(
                    onTap: () => setState(() {
                      _step = _step == 2 && _selectedTx != null ? 1 : 0;
                    }),
                    child: const Icon(Icons.arrow_back,
                        color: _textMuted, size: 22),
                  ),
                if (_step > 0) const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    _step == 0
                        ? AppLocalizations.of(context).subAddSubscription
                        : _step == 1
                            ? AppLocalizations.of(context).subSearchTransactions
                            : AppLocalizations.of(context).subNewSubscription,
                    style: GoogleFonts.sora(
                        color: _textGhost,
                        fontSize: 18,
                        fontWeight: FontWeight.w600),
                  ),
                ),
                GestureDetector(
                  onTap: () => Navigator.pop(context),
                  child: const Icon(Icons.close, color: _textMuted, size: 22),
                ),
              ],
            ),
          ),
          const Divider(color: _cardBorder, height: 1),
          // Content
          Flexible(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(20),
              child: _step == 0
                  ? _buildSelection()
                  : _step == 1
                      ? _buildTxSearch()
                      : _buildForm(),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildSelection() {
    return Column(
      children: [
        _SelectionCard(
          icon: Icons.search,
          title: 'From Transaction',
          subtitle: 'Search recent transactions to pre-fill details',
          recommended: true,
          onTap: () => setState(() => _step = 1),
        ),
        const SizedBox(height: 12),
        _SelectionCard(
          icon: Icons.edit_outlined,
          title: 'Manual Entry',
          subtitle: 'Enter subscription details manually',
          onTap: () => setState(() => _step = 2),
        ),
      ],
    );
  }

  Widget _buildTxSearch() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Search bar
        Row(
          children: [
            Expanded(
              child: TextField(
                controller: _searchCtrl,
                style: GoogleFonts.sora(color: _textGhost, fontSize: 14),
                decoration: InputDecoration(
                  hintText: 'Search by merchant name...',
                  hintStyle: GoogleFonts.sora(color: _textMuted, fontSize: 14),
                  filled: true,
                  fillColor: Colors.white.withValues(alpha: 0.06),
                  border: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(10),
                    borderSide: BorderSide(color: _cardBorder),
                  ),
                  enabledBorder: OutlineInputBorder(
                    borderRadius: BorderRadius.circular(10),
                    borderSide: BorderSide(color: _cardBorder),
                  ),
                  contentPadding:
                      const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                ),
                onSubmitted: (_) => _searchTransactions(),
              ),
            ),
            const SizedBox(width: 8),
            _GlassButton(
              label: 'Search',
              onTap: _searchTransactions,
              isPrimary: true,
            ),
          ],
        ),
        const SizedBox(height: 16),
        if (_searchLoading)
          const Center(
            child:
                CircularProgressIndicator(color: _accent, strokeWidth: 2),
          )
        else if (_txResults.isEmpty)
          Center(
            child: Text(
              _searchCtrl.text.isEmpty
                  ? AppLocalizations.of(context).subSearchAbovePrompt
                  : AppLocalizations.of(context).subNoExpenseTransactions,
              style: GoogleFonts.sora(color: _textMuted, fontSize: 13),
            ),
          )
        else
          ..._txResults.take(20).map((tx) => GestureDetector(
                onTap: () => _selectTransaction(tx),
                child: Container(
                  margin: const EdgeInsets.only(bottom: 6),
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: Colors.white.withValues(alpha: 0.04),
                    borderRadius: BorderRadius.circular(10),
                    border: Border.all(color: _cardBorder),
                  ),
                  child: Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              (tx.merchantName?.isNotEmpty ?? false)
                                  ? tx.merchantName!
                                  : tx.description,
                              style: GoogleFonts.sora(
                                  color: _textGhost, fontSize: 13),
                            ),
                            Text(
                              DateFormat.yMMMd().format(tx.date),
                              style: GoogleFonts.sora(
                                  color: _textMuted, fontSize: 11),
                            ),
                          ],
                        ),
                      ),
                      Text(
                        formatCurrency(tx.amount.abs()),
                        style: GoogleFonts.firaCode(
                            color: _income, fontSize: 14),
                      ),
                    ],
                  ),
                ),
              )),
      ],
    );
  }

  Widget _buildForm() {
    return Form(
      key: _formKey,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (_formError != null) ...[
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: _expense.withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Text(_formError!,
                  style: GoogleFonts.sora(color: _expense, fontSize: 12)),
            ),
            const SizedBox(height: 12),
          ],
          _FormField(
            label: 'Name',
            child: TextFormField(
              controller: _nameCtrl,
              style: GoogleFonts.sora(color: _textGhost, fontSize: 14),
              decoration: _inputDeco('e.g. Netflix'),
              validator: (v) =>
                  v == null || v.trim().isEmpty ? 'Required' : null,
            ),
          ),
          _FormField(
            label: 'Amount',
            child: TextFormField(
              controller: _amountCtrl,
              style: GoogleFonts.firaCode(color: _textGhost, fontSize: 14),
              keyboardType: TextInputType.number,
              decoration: _inputDeco('0.00'),
              validator: (v) {
                if (v == null || v.isEmpty) return 'Required';
                final n = parseFlexibleAmount(v);
                if (n == null || n <= 0) return 'Must be > 0';
                return null;
              },
            ),
          ),
          _FormField(
            label: 'Frequency',
            child: _DropdownField<SubscriptionFrequency>(
              value: _freq,
              items: SubscriptionFrequency.values
                  .where((f) => f != SubscriptionFrequency.custom)
                  .map((f) =>
                      DropdownMenuItem(value: f, child: Text(f.displayName)))
                  .toList(),
              onChanged: (v) => setState(() => _freq = v!),
            ),
          ),
          _FormField(
            label: 'Category',
            child: _DropdownField<SubscriptionCategory>(
              value: _cat,
              items: SubscriptionCategory.values
                  .map((c) =>
                      DropdownMenuItem(value: c, child: Text(c.displayName)))
                  .toList(),
              onChanged: (v) => setState(() => _cat = v!),
            ),
          ),
          _FormField(
            label: 'Next Due Date',
            child: GestureDetector(
              onTap: () async {
                final d = await showDatePicker(
                  context: context,
                  initialDate: _nextDue,
                  firstDate: DateTime.now(),
                  lastDate: DateTime.now().add(const Duration(days: 365 * 5)),
                  builder: (ctx, child) => Theme(
                    data: ThemeData.dark().copyWith(
                      colorScheme: const ColorScheme.dark(primary: _accent),
                    ),
                    child: child!,
                  ),
                );
                if (d != null) setState(() => _nextDue = d);
              },
              child: Container(
                padding:
                    const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
                decoration: BoxDecoration(
                  color: Colors.white.withValues(alpha: 0.06),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(color: _cardBorder),
                ),
                child: Row(
                  children: [
                    const Icon(Icons.calendar_today,
                        color: _textMuted, size: 16),
                    const SizedBox(width: 10),
                    Text(DateFormat.yMMMd().format(_nextDue),
                        style: GoogleFonts.sora(
                            color: _textGhost, fontSize: 14)),
                  ],
                ),
              ),
            ),
          ),
          _FormField(
            label: 'Description',
            optional: true,
            child: TextFormField(
              controller: _descCtrl,
              style: GoogleFonts.sora(color: _textGhost, fontSize: 14),
              maxLines: 2,
              decoration: _inputDeco('Optional notes...'),
            ),
          ),
          const SizedBox(height: 20),
          SizedBox(
            width: double.infinity,
            child: _GlassButton(
              label: _submitting ? 'Creating...' : 'Create Subscription',
              onTap: _submitting ? null : _submit,
              isPrimary: true,
            ),
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _SubscriptionFormSheet — Edit (+ delete)
// ═══════════════════════════════════════════════════════════
class _SubscriptionFormSheet extends StatefulWidget {
  final Subscription subscription;
  final VoidCallback onSaved;
  final VoidCallback onDeleted;

  const _SubscriptionFormSheet({
    required this.subscription,
    required this.onSaved,
    required this.onDeleted,
  });

  @override
  State<_SubscriptionFormSheet> createState() => _SubscriptionFormSheetState();
}

class _SubscriptionFormSheetState extends State<_SubscriptionFormSheet> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _nameCtrl;
  late final TextEditingController _amountCtrl;
  late final TextEditingController _descCtrl;
  late SubscriptionFrequency _freq;
  late SubscriptionCategory _cat;
  late SubscriptionStatus _status;
  late DateTime _nextDue;
  bool _submitting = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final s = widget.subscription;
    _nameCtrl = TextEditingController(text: s.name);
    _amountCtrl = TextEditingController(text: s.amount.toStringAsFixed(2));
    _descCtrl = TextEditingController(text: s.description ?? '');
    _freq = s.frequency;
    _cat = s.category;
    _status = s.status;
    _nextDue = s.nextDueDate;
  }

  @override
  void dispose() {
    _nameCtrl.dispose();
    _amountCtrl.dispose();
    _descCtrl.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    if (!_formKey.currentState!.validate()) return;
    setState(() {
      _submitting = true;
      _error = null;
    });
    try {
      await subscriptionsService.updateSubscription(
        widget.subscription.id,
        UpdateSubscriptionRequest(
          name: _nameCtrl.text.trim(),
          amount: double.parse(_amountCtrl.text),
          frequency: _freq,
          category: _cat,
          status: _status,
          nextDueDate: _nextDue,
          description: _descCtrl.text.trim().isNotEmpty ? _descCtrl.text.trim() : null,
        ),
      );
      if (!mounted) return;
      Navigator.pop(context);
      widget.onSaved();
    } catch (e) {
      if (mounted) {
        setState(() {
          _error = e.toString();
          _submitting = false;
        });
      }
    }
  }

  void _showDeleteConfirm() {
    showModalBottomSheet(
      context: context,
      backgroundColor: Colors.transparent,
      builder: (_) => _DeleteOptionsSheet(
        subscription: widget.subscription,
        onDone: () {
          if (!mounted) return;
          Navigator.pop(context); // form sheet
          widget.onDeleted();
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.of(context).size.height * 0.9,
      ),
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Container(
            width: 40,
            height: 4,
            margin: const EdgeInsets.only(top: 12, bottom: 8),
            decoration: BoxDecoration(
              color: Colors.white.withValues(alpha: 0.2),
              borderRadius: BorderRadius.circular(2),
            ),
          ),
          // Title bar
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                      AppLocalizations.of(context)
                          .subEditName(widget.subscription.name),
                      style: GoogleFonts.sora(
                          color: _textGhost,
                          fontSize: 18,
                          fontWeight: FontWeight.w600)),
                ),
                GestureDetector(
                  onTap: _showDeleteConfirm,
                  child: const Icon(Icons.delete_outline,
                      color: _expense, size: 22),
                ),
                const SizedBox(width: 12),
                GestureDetector(
                  onTap: () => Navigator.pop(context),
                  child: const Icon(Icons.close, color: _textMuted, size: 22),
                ),
              ],
            ),
          ),
          if (widget.subscription.isDetected)
            Container(
              margin: const EdgeInsets.symmetric(horizontal: 20),
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  const Icon(Icons.auto_awesome, color: _accent, size: 14),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context).subAutoDetectedReview,
                      style: GoogleFonts.sora(color: _textMuted, fontSize: 12),
                    ),
                  ),
                ],
              ),
            ),
          const Divider(color: _cardBorder, height: 1),
          Flexible(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(20),
              child: Form(
                key: _formKey,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (_error != null) ...[
                      Container(
                        padding: const EdgeInsets.all(10),
                        decoration: BoxDecoration(
                          color: _expense.withValues(alpha: 0.12),
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: Text(_error!,
                            style: GoogleFonts.sora(
                                color: _expense, fontSize: 12)),
                      ),
                      const SizedBox(height: 12),
                    ],
                    _FormField(
                      label: 'Name',
                      child: TextFormField(
                        controller: _nameCtrl,
                        style:
                            GoogleFonts.sora(color: _textGhost, fontSize: 14),
                        decoration: _inputDeco('Subscription name'),
                        validator: (v) =>
                            v == null || v.trim().isEmpty ? 'Required' : null,
                      ),
                    ),
                    _FormField(
                      label: 'Amount',
                      child: TextFormField(
                        controller: _amountCtrl,
                        style: GoogleFonts.firaCode(
                            color: _textGhost, fontSize: 14),
                        keyboardType: TextInputType.number,
                        decoration: _inputDeco('0.00'),
                        validator: (v) {
                          if (v == null || v.isEmpty) return 'Required';
                          final n = parseFlexibleAmount(v);
                          if (n == null || n <= 0) return 'Must be > 0';
                          return null;
                        },
                      ),
                    ),
                    _FormField(
                      label: 'Frequency',
                      child: _DropdownField<SubscriptionFrequency>(
                        value: _freq,
                        items: SubscriptionFrequency.values
                            .where((f) => f != SubscriptionFrequency.custom)
                            .map((f) => DropdownMenuItem(
                                value: f, child: Text(f.displayName)))
                            .toList(),
                        onChanged: (v) => setState(() => _freq = v!),
                      ),
                    ),
                    _FormField(
                      label: 'Category',
                      child: _DropdownField<SubscriptionCategory>(
                        value: _cat,
                        items: SubscriptionCategory.values
                            .map((c) => DropdownMenuItem(
                                value: c, child: Text(c.displayName)))
                            .toList(),
                        onChanged: (v) => setState(() => _cat = v!),
                      ),
                    ),
                    _FormField(
                      label: 'Status',
                      child: _DropdownField<SubscriptionStatus>(
                        value: _status,
                        items: SubscriptionStatus.values
                            .map((s) => DropdownMenuItem(
                                value: s, child: Text(s.displayName)))
                            .toList(),
                        onChanged: (v) => setState(() => _status = v!),
                      ),
                    ),
                    _FormField(
                      label: 'Next Due Date',
                      child: GestureDetector(
                        onTap: () async {
                          final d = await showDatePicker(
                            context: context,
                            initialDate: _nextDue,
                            firstDate:
                                DateTime.now().subtract(const Duration(days: 365)),
                            lastDate:
                                DateTime.now().add(const Duration(days: 365 * 5)),
                            builder: (ctx, child) => Theme(
                              data: ThemeData.dark().copyWith(
                                colorScheme:
                                    const ColorScheme.dark(primary: _accent),
                              ),
                              child: child!,
                            ),
                          );
                          if (d != null) setState(() => _nextDue = d);
                        },
                        child: Container(
                          padding: const EdgeInsets.symmetric(
                              horizontal: 14, vertical: 14),
                          decoration: BoxDecoration(
                            color: Colors.white.withValues(alpha: 0.06),
                            borderRadius: BorderRadius.circular(10),
                            border: Border.all(color: _cardBorder),
                          ),
                          child: Row(
                            children: [
                              const Icon(Icons.calendar_today,
                                  color: _textMuted, size: 16),
                              const SizedBox(width: 10),
                              Text(DateFormat.yMMMd().format(_nextDue),
                                  style: GoogleFonts.sora(
                                      color: _textGhost, fontSize: 14)),
                            ],
                          ),
                        ),
                      ),
                    ),
                    _FormField(
                      label: 'Description',
                      optional: true,
                      child: TextFormField(
                        controller: _descCtrl,
                        style:
                            GoogleFonts.sora(color: _textGhost, fontSize: 14),
                        maxLines: 2,
                        decoration: _inputDeco('Optional notes...'),
                      ),
                    ),
                    const SizedBox(height: 20),
                    SizedBox(
                      width: double.infinity,
                      child: _GlassButton(
                        label: _submitting ? 'Saving...' : 'Save Changes',
                        onTap: _submitting ? null : _save,
                        isPrimary: true,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _DetectionSheet — detect → review → confirm
// ═══════════════════════════════════════════════════════════
class _DetectionSheet extends StatefulWidget {
  final VoidCallback onDone;
  const _DetectionSheet({required this.onDone});

  @override
  State<_DetectionSheet> createState() => _DetectionSheetState();
}

class _DetectionSheetState extends State<_DetectionSheet> {
  SubscriptionDetectionResponse? _result;
  bool _loading = true;
  bool _confirming = false;
  String? _error;
  final Set<int> _selected = {};

  @override
  void initState() {
    super.initState();
    _detect();
  }

  Future<void> _detect() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final result = await subscriptionsService.detectSubscriptionsFull();
      if (!mounted) return;
      setState(() {
        _result = result;
        _loading = false;
        // Auto-select high confidence
        for (var i = 0; i < result.detectedSubscriptions.length; i++) {
          if (result.detectedSubscriptions[i].confidence >= 0.8) {
            _selected.add(i);
          }
        }
      });
    } catch (e) {
      if (mounted) {
        setState(() {
          _error = e.toString();
          _loading = false;
        });
      }
    }
  }

  Future<void> _confirmSelected() async {
    if (_result == null || _selected.isEmpty) return;
    setState(() => _confirming = true);
    var count = 0;
    for (final idx in _selected) {
      try {
        await subscriptionsService
            .confirmDetected(_result!.detectedSubscriptions[idx]);
        count++;
      } catch (_) {}
    }
    if (!mounted) return;
    Navigator.pop(context);
    widget.onDone();
    showAppToast(
      context,
      '$count subscription(s) added',
      type: AppToastType.success,
    );
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.of(context).size.height * 0.85,
      ),
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Container(
            width: 40,
            height: 4,
            margin: const EdgeInsets.only(top: 12, bottom: 8),
            decoration: BoxDecoration(
              color: Colors.white.withValues(alpha: 0.2),
              borderRadius: BorderRadius.circular(2),
            ),
          ),
          // Header
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(AppLocalizations.of(context).subDetectedSubscriptions,
                          style: GoogleFonts.sora(
                              color: _textGhost,
                              fontSize: 18,
                              fontWeight: FontWeight.w600)),
                      Text(
                          AppLocalizations.of(context).subReviewConfirmPatterns,
                          style: GoogleFonts.sora(
                              color: _textMuted, fontSize: 12)),
                    ],
                  ),
                ),
                if (!_loading)
                  GestureDetector(
                    onTap: _detect,
                    child: const Icon(Icons.refresh,
                        color: _textMuted, size: 22),
                  ),
                const SizedBox(width: 12),
                GestureDetector(
                  onTap: () => Navigator.pop(context),
                  child: const Icon(Icons.close, color: _textMuted, size: 22),
                ),
              ],
            ),
          ),
          const Divider(color: _cardBorder, height: 1),
          // Content
          Flexible(
            child: _loading
                ? Padding(
                    padding: const EdgeInsets.all(48),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        const CircularProgressIndicator(
                            color: _accent, strokeWidth: 2),
                        const SizedBox(height: 16),
                        Text(
                          AppLocalizations.of(context).subAnalyzingTransactions,
                          textAlign: TextAlign.center,
                          style: GoogleFonts.sora(
                              color: _textMuted, fontSize: 13),
                        ),
                      ],
                    ),
                  )
                : _error != null
                    ? Padding(
                        padding: const EdgeInsets.all(32),
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            const Icon(Icons.error_outline,
                                color: _expense, size: 40),
                            const SizedBox(height: 12),
                            Text(_error!,
                                style: GoogleFonts.sora(
                                    color: _textMuted, fontSize: 13)),
                            const SizedBox(height: 12),
                            _GlassButton(
                                label: 'Retry',
                                onTap: _detect,
                                isPrimary: true),
                          ],
                        ),
                      )
                    : _buildResults(),
          ),
        ],
      ),
    );
  }

  Widget _buildResults() {
    final subs = _result!.detectedSubscriptions;
    if (subs.isEmpty) {
      return Padding(
        padding: const EdgeInsets.all(32),
        child: Text(AppLocalizations.of(context).subNoPatternsDetected,
            style: GoogleFonts.sora(color: _textMuted, fontSize: 14)),
      );
    }
    return Column(
      children: [
        // Summary bar
        Container(
          margin: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: _accent.withValues(alpha: 0.08),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  AppLocalizations.of(context).subFoundPotential(
                      _result!.totalDetected, _result!.highConfidenceCount),
                  style: GoogleFonts.sora(color: _textGhost, fontSize: 12),
                ),
              ),
              if (_result!.isAIEnhanced)
                _Badge(label: 'AI Enhanced', color: _accent, icon: Icons.auto_awesome),
            ],
          ),
        ),
        // Select all
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 4),
          child: Row(
            children: [
              GestureDetector(
                onTap: () {
                  setState(() {
                    if (_selected.length == subs.length) {
                      _selected.clear();
                    } else {
                      _selected.addAll(
                          List.generate(subs.length, (i) => i));
                    }
                  });
                },
                child: Row(
                  children: [
                    Icon(
                      _selected.length == subs.length
                          ? Icons.check_box
                          : Icons.check_box_outline_blank,
                      color: _accent,
                      size: 20,
                    ),
                    const SizedBox(width: 8),
                    Text(AppLocalizations.of(context).subSelectAll,
                        style: GoogleFonts.sora(
                            color: _textMuted, fontSize: 12)),
                  ],
                ),
              ),
              const Spacer(),
              Text(
                  AppLocalizations.of(context)
                      .subSelectedOf(_selected.length, subs.length),
                  style: GoogleFonts.sora(color: _textMuted, fontSize: 12)),
            ],
          ),
        ),
        // List
        Flexible(
          child: ListView.builder(
            padding: const EdgeInsets.symmetric(horizontal: 20),
            itemCount: subs.length,
            itemBuilder: (_, i) => _buildDetectedItem(subs[i], i),
          ),
        ),
        // Footer
        Container(
          padding: const EdgeInsets.all(20),
          child: SizedBox(
            width: double.infinity,
            child: _GlassButton(
              label: _confirming
                  ? 'Confirming...'
                  : 'Confirm ${_selected.length} Selected',
              onTap:
                  _confirming || _selected.isEmpty ? null : _confirmSelected,
              isPrimary: true,
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildDetectedItem(DetectedSubscription d, int index) {
    final isChecked = _selected.contains(index);
    final confColor = _confidenceColor(d.confidence);
    return GestureDetector(
      onTap: () {
        setState(() {
          isChecked ? _selected.remove(index) : _selected.add(index);
        });
      },
      child: Container(
        margin: const EdgeInsets.only(bottom: 8),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(
          color: isChecked
              ? _accent.withValues(alpha: 0.08)
              : Colors.white.withValues(alpha: 0.03),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: isChecked
                ? _accent.withValues(alpha: 0.3)
                : _cardBorder,
          ),
        ),
        child: Row(
          children: [
            Icon(
              isChecked ? Icons.check_box : Icons.check_box_outline_blank,
              color: _accent,
              size: 20,
            ),
            const SizedBox(width: 10),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Flexible(
                        child: Text(d.merchantName,
                            style: GoogleFonts.sora(
                                color: _textGhost,
                                fontSize: 13,
                                fontWeight: FontWeight.w500)),
                      ),
                      const SizedBox(width: 6),
                      _Badge(
                        label: '${d.relatedTransactionIds.length} txns',
                        color: _textMuted,
                        small: true,
                      ),
                    ],
                  ),
                  const SizedBox(height: 4),
                  Row(
                    children: [
                      Text(d.frequency.displayName,
                          style: GoogleFonts.sora(
                              color: _textMuted, fontSize: 11)),
                      const SizedBox(width: 8),
                      if (d.aiSuggestedCategory != null) ...[
                        Icon(
                          _catIcons[d.aiSuggestedCategory!] ??
                              Icons.category,
                          color: _catColors[d.aiSuggestedCategory!] ??
                              _textMuted,
                          size: 12,
                        ),
                        const SizedBox(width: 4),
                        Text(d.aiSuggestedCategory!.displayName,
                            style: GoogleFonts.sora(
                                color: _textMuted, fontSize: 11)),
                      ],
                    ],
                  ),
                  if (d.aiInsight != null && d.aiInsight!.isNotEmpty) ...[
                    const SizedBox(height: 4),
                    Text(d.aiInsight!,
                        style: GoogleFonts.sora(
                            color: _accent.withValues(alpha: 0.8),
                            fontSize: 11)),
                  ],
                ],
              ),
            ),
            Column(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(formatCurrency(d.amount),
                    style: GoogleFonts.firaCode(
                        color: _income, fontSize: 14)),
                const SizedBox(height: 4),
                _Badge(
                  label: '${(d.confidence * 100).toInt()}%',
                  color: confColor,
                  small: true,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _SuggestionReviewSheet — paginated review of suggestions
// ═══════════════════════════════════════════════════════════
class _SuggestionReviewSheet extends StatefulWidget {
  final List<SuggestedSubscription> suggestions;
  final Set<int> actionBusy;
  final Future<void> Function(int id) onConfirm;
  final Future<void> Function(int id, {bool permanent}) onDismiss;
  final void Function(SuggestedSubscription s) onSnooze;
  final VoidCallback onDone;

  const _SuggestionReviewSheet({
    required this.suggestions,
    required this.actionBusy,
    required this.onConfirm,
    required this.onDismiss,
    required this.onSnooze,
    required this.onDone,
  });

  @override
  State<_SuggestionReviewSheet> createState() => _SuggestionReviewSheetState();
}

class _SuggestionReviewSheetState extends State<_SuggestionReviewSheet> {
  late List<SuggestedSubscription> _items;
  int _currentIndex = 0;
  late final PageController _pageCtrl;

  @override
  void initState() {
    super.initState();
    _items = List.of(widget.suggestions);
    _pageCtrl = PageController();
  }

  @override
  void dispose() {
    _pageCtrl.dispose();
    super.dispose();
  }

  void _handleAction(Future<void> Function() action) async {
    final id = _items[_currentIndex].id;
    try {
      await action();
    } catch (_) {
      if (!mounted) return;
      showAppToast(
        context,
        'Something went wrong. Please try again.',
        type: AppToastType.error,
      );
      return;
    }
    if (!mounted) return;
    setState(() {
      _items.removeWhere((s) => s.id == id);
      if (_items.isEmpty) {
        Navigator.pop(context);
        widget.onDone();
        return;
      }
      if (_currentIndex >= _items.length) {
        _currentIndex = _items.length - 1;
      }
    });
    // Jump to the updated page
    if (_items.isNotEmpty && _pageCtrl.hasClients) {
      _pageCtrl.jumpToPage(_currentIndex);
    }
  }

  // Permanently dismissing a merchant is irreversible (no undo), so gate it
  // behind an explicit confirmation instead of firing on a single tap.
  void _confirmPermanentDismiss() {
    final item = _items[_currentIndex];
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (sheetCtx) => _ConfirmSheet(
        title: 'Never suggest this merchant?',
        subtitle:
            '${AppBrand.name} will stop suggesting "${item.merchantName}" as a subscription. This can\'t be undone.',
        confirmLabel: 'Don\'t suggest',
        onConfirm: () async {
          Navigator.pop(sheetCtx);
          _handleAction(() => widget.onDismiss(item.id, permanent: true));
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    if (_items.isEmpty) {
      return const SizedBox.shrink();
    }
    return Container(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.of(context).size.height * 0.85,
      ),
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Container(
            width: 40,
            height: 4,
            margin: const EdgeInsets.only(top: 12, bottom: 8),
            decoration: BoxDecoration(
              color: Colors.white.withValues(alpha: 0.2),
              borderRadius: BorderRadius.circular(2),
            ),
          ),
          // Header
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(AppLocalizations.of(context).subReviewSuggestions,
                          style: GoogleFonts.sora(
                              color: _textGhost,
                              fontSize: 18,
                              fontWeight: FontWeight.w600)),
                      Text(
                          AppLocalizations.of(context)
                              .subOfRemaining(_currentIndex + 1, _items.length),
                          style: GoogleFonts.sora(
                              color: _textMuted, fontSize: 12)),
                    ],
                  ),
                ),
                GestureDetector(
                  onTap: () {
                    Navigator.pop(context);
                    widget.onDone();
                  },
                  child: const Icon(Icons.close, color: _textMuted, size: 22),
                ),
              ],
            ),
          ),
          const Divider(color: _cardBorder, height: 1),
          // Paged content
          Flexible(
            child: PageView.builder(
              controller: _pageCtrl,
              itemCount: _items.length,
              onPageChanged: (i) => setState(() => _currentIndex = i),
              itemBuilder: (_, i) => _buildSuggestionPage(_items[i]),
            ),
          ),
          // Page indicator dots
          if (_items.length > 1)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: List.generate(
                  _items.length.clamp(0, 10),
                  (i) => Container(
                    width: 6,
                    height: 6,
                    margin: const EdgeInsets.symmetric(horizontal: 3),
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      color: i == _currentIndex
                          ? _accent
                          : Colors.white.withValues(alpha: 0.2),
                    ),
                  ),
                ),
              ),
            ),
          // Action buttons
          Container(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
            child: Row(
              children: [
                Expanded(
                  child: _GlassButton(
                    label: 'Add',
                    icon: Icons.add,
                    onTap: () => _handleAction(
                        () => widget.onConfirm(_items[_currentIndex].id)),
                    isPrimary: true,
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: _GlassButton(
                    label: 'Snooze',
                    icon: Icons.snooze,
                    onTap: () {
                      Navigator.pop(context);
                      widget.onSnooze(_items[_currentIndex]);
                    },
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: _GlassButton(
                    label: 'Dismiss',
                    icon: Icons.close,
                    onTap: () => _handleAction(
                        () => widget.onDismiss(_items[_currentIndex].id)),
                    isDestructive: true,
                  ),
                ),
              ],
            ),
          ),
          // Permanent dismiss link
          Padding(
            padding: const EdgeInsets.only(bottom: 16),
            child: GestureDetector(
              onTap: _confirmPermanentDismiss,
              child: Text(AppLocalizations.of(context).subNeverSuggestAgain,
                  style: GoogleFonts.sora(
                      color: _textMuted,
                      fontSize: 12,
                      decoration: TextDecoration.underline,
                      decorationColor: _textMuted)),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildSuggestionPage(SuggestedSubscription s) {
    final confColor = _confidenceColor(s.confidence);
    return SingleChildScrollView(
      padding: const EdgeInsets.all(20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Merchant name + amount
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Text(s.merchantName,
                    style: GoogleFonts.instrumentSerif(
                        color: _textGhost, fontSize: 24)),
              ),
              Text(formatCurrency(s.amount),
                  style: GoogleFonts.firaCode(
                      color: _accent,
                      fontSize: 22,
                      fontWeight: FontWeight.w600)),
            ],
          ),
          const SizedBox(height: 12),
          // Badges
          Row(
            children: [
              _Badge(label: s.confidenceLabel, color: confColor),
              const SizedBox(width: 6),
              _Badge(
                label: '${(s.confidence * 100).toInt()}%',
                color: confColor,
              ),
              if (s.hasCrowdsourceBoost) ...[
                const SizedBox(width: 6),
                _Badge(
                    label: 'Community Verified',
                    color: _income,
                    icon: Icons.groups_outlined),
              ],
            ],
          ),
          const SizedBox(height: 16),
          // Confidence bar
          ClipRRect(
            borderRadius: BorderRadius.circular(4),
            child: LinearProgressIndicator(
              value: s.confidence.clamp(0, 1),
              backgroundColor: Colors.white.withValues(alpha: 0.08),
              valueColor: AlwaysStoppedAnimation(confColor),
              minHeight: 6,
            ),
          ),
          const SizedBox(height: 20),
          // Details grid
          _DetailRow('Frequency', s.frequency.displayName, Icons.repeat),
          if (s.predictedNextDate != null)
            _DetailRow('Next Date',
                DateFormat.yMMMd().format(s.predictedNextDate!), Icons.event),
          if (s.aiSuggestedCategory != null)
            _DetailRow(
                'Category',
                s.aiSuggestedCategory!.displayName,
                _catIcons[s.aiSuggestedCategory!] ?? Icons.category),
          _DetailRow('Transactions', '${s.matchedTransactionCount} matched',
              Icons.layers),
          // AI Insight
          if (s.aiInsight != null && s.aiInsight!.isNotEmpty) ...[
            const SizedBox(height: 16),
            Container(
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: _accent.withValues(alpha: 0.15)),
              ),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Icon(Icons.auto_awesome, color: _accent, size: 16),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(s.aiInsight!,
                        style:
                            GoogleFonts.sora(color: _textMuted, fontSize: 13)),
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// Detail row inside review sheets.
class _DetailRow extends StatelessWidget {
  final String label;
  final String value;
  final IconData icon;
  const _DetailRow(this.label, this.value, this.icon);

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Row(
        children: [
          Icon(icon, color: _textMuted, size: 16),
          const SizedBox(width: 10),
          Text('$label: ',
              style: GoogleFonts.sora(color: _textMuted, fontSize: 13)),
          Expanded(
            child: Text(value,
                style: GoogleFonts.sora(
                    color: _textGhost,
                    fontSize: 13,
                    fontWeight: FontWeight.w500)),
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _CancelledReviewSheet — paginated review of possibly cancelled
// ═══════════════════════════════════════════════════════════
class _CancelledReviewSheet extends StatefulWidget {
  final List<SubscriptionTimeline> items;
  final Future<void> Function(int subId) onKeep;
  final Future<void> Function(int subId) onCancel;
  final VoidCallback onDone;

  const _CancelledReviewSheet({
    required this.items,
    required this.onKeep,
    required this.onCancel,
    required this.onDone,
  });

  @override
  State<_CancelledReviewSheet> createState() => _CancelledReviewSheetState();
}

class _CancelledReviewSheetState extends State<_CancelledReviewSheet> {
  late List<SubscriptionTimeline> _items;
  int _currentIndex = 0;
  late final PageController _pageCtrl;

  @override
  void initState() {
    super.initState();
    _items = List.of(widget.items);
    _pageCtrl = PageController();
  }

  @override
  void dispose() {
    _pageCtrl.dispose();
    super.dispose();
  }

  void _handleAction(Future<void> Function() action) async {
    final id = _items[_currentIndex].subscriptionId;
    try {
      await action();
    } catch (_) {
      if (!mounted) return;
      showAppToast(
        context,
        'Something went wrong. Please try again.',
        type: AppToastType.error,
      );
      return;
    }
    if (!mounted) return;
    setState(() {
      _items.removeWhere((t) => t.subscriptionId == id);
      if (_items.isEmpty) {
        Navigator.pop(context);
        widget.onDone();
        return;
      }
      if (_currentIndex >= _items.length) {
        _currentIndex = _items.length - 1;
      }
    });
    if (_items.isNotEmpty && _pageCtrl.hasClients) {
      _pageCtrl.jumpToPage(_currentIndex);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_items.isEmpty) return const SizedBox.shrink();
    return Container(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.of(context).size.height * 0.7,
      ),
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Container(
            width: 40,
            height: 4,
            margin: const EdgeInsets.only(top: 12, bottom: 8),
            decoration: BoxDecoration(
              color: Colors.white.withValues(alpha: 0.2),
              borderRadius: BorderRadius.circular(2),
            ),
          ),
          // Header
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(AppLocalizations.of(context).subPossiblyCancelledTitle,
                          style: GoogleFonts.sora(
                              color: _textGhost,
                              fontSize: 18,
                              fontWeight: FontWeight.w600)),
                      Text(
                          AppLocalizations.of(context)
                              .subOfRemaining(_currentIndex + 1, _items.length),
                          style: GoogleFonts.sora(
                              color: _textMuted, fontSize: 12)),
                    ],
                  ),
                ),
                GestureDetector(
                  onTap: () {
                    Navigator.pop(context);
                    widget.onDone();
                  },
                  child: const Icon(Icons.close, color: _textMuted, size: 22),
                ),
              ],
            ),
          ),
          const Divider(color: _cardBorder, height: 1),
          // Paged content
          Flexible(
            child: PageView.builder(
              controller: _pageCtrl,
              itemCount: _items.length,
              onPageChanged: (i) => setState(() => _currentIndex = i),
              itemBuilder: (_, i) => _buildCancelledPage(_items[i]),
            ),
          ),
          // Page indicator dots
          if (_items.length > 1)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: List.generate(
                  _items.length.clamp(0, 10),
                  (i) => Container(
                    width: 6,
                    height: 6,
                    margin: const EdgeInsets.symmetric(horizontal: 3),
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      color: i == _currentIndex
                          ? _expense
                          : Colors.white.withValues(alpha: 0.2),
                    ),
                  ),
                ),
              ),
            ),
          // Action buttons
          Container(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
            child: Row(
              children: [
                Expanded(
                  child: _GlassButton(
                    label: 'Keep Active',
                    icon: Icons.check,
                    onTap: () => _handleAction(() =>
                        widget.onKeep(_items[_currentIndex].subscriptionId)),
                    isPrimary: true,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: _GlassButton(
                    label: 'Cancel It',
                    icon: Icons.close,
                    onTap: () => _handleAction(() =>
                        widget.onCancel(
                            _items[_currentIndex].subscriptionId)),
                    isDestructive: true,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildCancelledPage(SubscriptionTimeline t) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Name + icon
          Row(
            children: [
              Icon(
                _catIcons[t.category] ?? Icons.more_horiz,
                color: _catColors[t.category] ?? _textMuted,
                size: 28,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Text(t.name,
                    style: GoogleFonts.instrumentSerif(
                        color: _textGhost, fontSize: 24)),
              ),
            ],
          ),
          const SizedBox(height: 16),
          // Amount
          Text(formatCurrency(t.amount),
              style: GoogleFonts.firaCode(
                  color: _expense, fontSize: 28, fontWeight: FontWeight.w600)),
          const SizedBox(height: 20),
          // Details
          _DetailRow('Frequency', t.frequency.displayName, Icons.repeat),
          _DetailRow(
              'Missed Cycles', '${t.missedBillingCycles}', Icons.error_outline),
          _DetailRow(
              'Category', t.category.displayName,
              _catIcons[t.category] ?? Icons.category),
          _DetailRow('Status', t.status.displayName,
              Icons.info_outline),
          const SizedBox(height: 12),
          // Warning box
          Container(
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              color: _expense.withValues(alpha: 0.08),
              borderRadius: BorderRadius.circular(12),
              border: Border.all(color: _expense.withValues(alpha: 0.15)),
            ),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Icon(Icons.warning_amber_rounded,
                    color: _expense, size: 16),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    AppLocalizations.of(context)
                        .subNoChargesDetected(t.missedBillingCycles),
                    style: GoogleFonts.sora(color: _textMuted, fontSize: 13),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _SnoozeSheet
// ═══════════════════════════════════════════════════════════
class _SnoozeSheet extends StatefulWidget {
  final String merchantName;
  final Future<void> Function(int days) onSnooze;
  const _SnoozeSheet({required this.merchantName, required this.onSnooze});

  @override
  State<_SnoozeSheet> createState() => _SnoozeSheetState();
}

class _SnoozeSheetState extends State<_SnoozeSheet> {
  int _selectedDays = 30;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      padding: const EdgeInsets.all(20),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Center(
            child: Container(
              width: 40,
              height: 4,
              margin: const EdgeInsets.only(bottom: 16),
              decoration: BoxDecoration(
                color: Colors.white.withValues(alpha: 0.2),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          Text(
              AppLocalizations.of(context)
                  .subSnoozeMerchant(widget.merchantName),
              style: GoogleFonts.sora(
                  color: _textGhost,
                  fontSize: 16,
                  fontWeight: FontWeight.w600)),
          const SizedBox(height: 4),
          Text(AppLocalizations.of(context).subChooseHowLong,
              style: GoogleFonts.sora(color: _textMuted, fontSize: 13)),
          const SizedBox(height: 16),
          ...List.generate(_snoozeOptions.length, (i) {
            final days = _snoozeOptions[i];
            final label = _snoozeLabels[i];
            final until =
                DateFormat.yMMMd().format(DateTime.now().add(Duration(days: days)));
            final isSel = _selectedDays == days;
            return GestureDetector(
              onTap: () => setState(() => _selectedDays = days),
              child: Container(
                margin: const EdgeInsets.only(bottom: 8),
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                decoration: BoxDecoration(
                  color: isSel
                      ? _accent.withValues(alpha: 0.12)
                      : Colors.white.withValues(alpha: 0.04),
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(
                    color: isSel
                        ? _accent.withValues(alpha: 0.4)
                        : _cardBorder,
                  ),
                ),
                child: Row(
                  children: [
                    Icon(
                      isSel
                          ? Icons.radio_button_checked
                          : Icons.radio_button_off,
                      color: isSel ? _accent : _textMuted,
                      size: 18,
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Text(label,
                          style: GoogleFonts.sora(
                              color: _textGhost, fontSize: 14)),
                    ),
                    Text(AppLocalizations.of(context).subUntilDate(until),
                        style: GoogleFonts.sora(
                            color: _textMuted, fontSize: 12)),
                  ],
                ),
              ),
            );
          }),
          const SizedBox(height: 12),
          SizedBox(
            width: double.infinity,
            child: _GlassButton(
              label: 'Snooze for ${_snoozeLabels[_snoozeOptions.indexOf(_selectedDays)]}',
              onTap: () => widget.onSnooze(_selectedDays),
              isPrimary: true,
            ),
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _DeleteOptionsSheet — 3-option delete
// ═══════════════════════════════════════════════════════════
class _DeleteOptionsSheet extends StatefulWidget {
  final Subscription subscription;
  final VoidCallback onDone;
  const _DeleteOptionsSheet(
      {required this.subscription, required this.onDone});

  @override
  State<_DeleteOptionsSheet> createState() => _DeleteOptionsSheetState();
}

enum _DeleteOption { all, future, cancel }

class _DeleteOptionsSheetState extends State<_DeleteOptionsSheet> {
  _DeleteOption _selected = _DeleteOption.all;
  bool _loading = false;

  Future<void> _execute() async {
    setState(() => _loading = true);
    try {
      if (_selected == _DeleteOption.cancel) {
        await subscriptionsService.updateSubscription(
          widget.subscription.id,
          UpdateSubscriptionRequest(status: SubscriptionStatus.cancelled),
        );
      } else {
        await subscriptionsService.deleteSubscription(widget.subscription.id);
      }
      if (!mounted) return;
      Navigator.pop(context); // close this sheet
      widget.onDone();
      final msg = switch (_selected) {
        _DeleteOption.all =>
          '${widget.subscription.name} and all history deleted',
        _DeleteOption.future =>
          '${widget.subscription.name} deleted, past links removed',
        _DeleteOption.cancel =>
          '${widget.subscription.name} cancelled, history preserved',
      };
      showAppToast(context, msg);
    } catch (e) {
      if (mounted) {
        setState(() => _loading = false);
        showAppToast(context, 'Error: $e', type: AppToastType.error);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
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
              margin: const EdgeInsets.only(bottom: 16),
              decoration: BoxDecoration(
                color: Colors.white.withValues(alpha: 0.2),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          Text(
              AppLocalizations.of(context)
                  .subDeleteName(widget.subscription.name),
              style: GoogleFonts.sora(
                  color: _textGhost,
                  fontSize: 16,
                  fontWeight: FontWeight.w600)),
          const SizedBox(height: 4),
          Text(AppLocalizations.of(context).subChooseHowToHandle,
              style: GoogleFonts.sora(color: _textMuted, fontSize: 13)),
          const SizedBox(height: 16),
          _DeleteOptionTile(
            label: 'Delete all occurrences',
            subtitle: 'Removes subscription and all linked transaction history',
            selected: _selected == _DeleteOption.all,
            color: _expense,
            onTap: () => setState(() => _selected = _DeleteOption.all),
          ),
          const SizedBox(height: 8),
          _DeleteOptionTile(
            label: 'Delete this and future',
            subtitle: 'Removes subscription but clears past transaction links',
            selected: _selected == _DeleteOption.future,
            color: _expense,
            onTap: () => setState(() => _selected = _DeleteOption.future),
          ),
          const SizedBox(height: 8),
          _DeleteOptionTile(
            label: 'Cancel subscription only',
            subtitle: 'Keeps historical data but marks as cancelled',
            selected: _selected == _DeleteOption.cancel,
            color: const Color(0xFFFFA726),
            onTap: () => setState(() => _selected = _DeleteOption.cancel),
          ),
          const SizedBox(height: 20),
          Row(
            children: [
              Expanded(
                child: _GlassButton(
                  label: 'Back',
                  onTap: _loading ? null : () => Navigator.pop(context),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: _GlassButton(
                  label: _loading
                      ? '...'
                      : _selected == _DeleteOption.cancel
                          ? 'Cancel Subscription'
                          : 'Delete',
                  onTap: _loading ? null : _execute,
                  isPrimary: true,
                  isDestructive: _selected != _DeleteOption.cancel,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _DeleteOptionTile extends StatelessWidget {
  final String label;
  final String subtitle;
  final bool selected;
  final Color color;
  final VoidCallback onTap;
  const _DeleteOptionTile({
    required this.label,
    required this.subtitle,
    required this.selected,
    required this.color,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        decoration: BoxDecoration(
          color: selected
              ? color.withValues(alpha: 0.12)
              : Colors.white.withValues(alpha: 0.04),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(
            color: selected ? color.withValues(alpha: 0.4) : _cardBorder,
          ),
        ),
        child: Row(
          children: [
            Icon(
              selected
                  ? Icons.radio_button_checked
                  : Icons.radio_button_off,
              color: selected ? color : _textMuted,
              size: 18,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(label,
                      style: GoogleFonts.sora(
                          color: _textGhost,
                          fontSize: 14,
                          fontWeight: FontWeight.w500)),
                  const SizedBox(height: 2),
                  Text(subtitle,
                      style: GoogleFonts.sora(
                          color: _textMuted, fontSize: 12)),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _ConfirmSheet
// ═══════════════════════════════════════════════════════════
class _ConfirmSheet extends StatefulWidget {
  final String title;
  final String subtitle;
  final String confirmLabel;
  final Future<void> Function() onConfirm;

  const _ConfirmSheet({
    required this.title,
    required this.subtitle,
    required this.confirmLabel,
    required this.onConfirm,
  });

  @override
  State<_ConfirmSheet> createState() => _ConfirmSheetState();
}

class _ConfirmSheetState extends State<_ConfirmSheet> {
  bool _loading = false;

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(widget.title,
              style: GoogleFonts.sora(
                  color: _textGhost,
                  fontSize: 16,
                  fontWeight: FontWeight.w600)),
          const SizedBox(height: 8),
          Text(widget.subtitle,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(color: _textMuted, fontSize: 13)),
          const SizedBox(height: 20),
          Row(
            children: [
              Expanded(
                child: _GlassButton(
                  label: 'Cancel',
                  onTap: () => Navigator.pop(context),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: _GlassButton(
                  label: _loading ? '...' : widget.confirmLabel,
                  onTap: _loading
                      ? null
                      : () async {
                          setState(() => _loading = true);
                          await widget.onConfirm();
                        },
                  isPrimary: true,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  Shared UI Components
// ═══════════════════════════════════════════════════════════

class _GlassCard extends StatelessWidget {
  final Widget child;
  const _GlassCard({required this.child});

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
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
      child: child,
    );
  }
}

class _StatTile extends StatelessWidget {
  final String label;
  final String value;
  final IconData icon;
  final Color color;
  const _StatTile(
      {required this.label,
      required this.value,
      required this.icon,
      required this.color});

  @override
  Widget build(BuildContext context) {
    return SizedBox(
      width: (MediaQuery.of(context).size.width - 52) / 2,
      child: Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: _cardBg,
          borderRadius: BorderRadius.circular(14),
          border: Border.all(color: _cardBorder),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon, color: color, size: 16),
                const SizedBox(width: 6),
                Text(label,
                    style: GoogleFonts.sora(
                        color: _textMuted, fontSize: 11)),
              ],
            ),
            const SizedBox(height: 6),
            // Currency values can be long; keep them on one line without
            // overflowing on narrow screens.
            FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerLeft,
              child: Text(value,
                  maxLines: 1,
                  style: GoogleFonts.firaCode(
                      color: _textGhost,
                      fontSize: 18,
                      fontWeight: FontWeight.w600)),
            ),
          ],
        ),
      ),
    );
  }
}

class _GlassButton extends StatelessWidget {
  final String label;
  final IconData? icon;
  final VoidCallback? onTap;
  final bool isPrimary;
  final bool isDestructive;

  const _GlassButton({
    required this.label,
    this.icon,
    this.onTap,
    this.isPrimary = false,
    this.isDestructive = false,
  });

  @override
  Widget build(BuildContext context) {
    final color = isDestructive
        ? _expense
        : isPrimary
            ? _accent
            : _textMuted;
    final bg = isPrimary || isDestructive
        ? color.withValues(alpha: 0.2)
        : Colors.white.withValues(alpha: 0.06);
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(
          horizontal: 14,
          vertical: 10,
        ),
        decoration: BoxDecoration(
          color: bg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: color.withValues(alpha: 0.25)),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            if (icon != null) ...[
              Icon(icon, color: color, size: 16),
              const SizedBox(width: 6),
            ],
            Text(label,
                style: GoogleFonts.sora(
                  color: isPrimary || isDestructive ? color : _textGhost,
                  fontSize: 13,
                  fontWeight: FontWeight.w500,
                )),
          ],
        ),
      ),
    );
  }
}

class _Badge extends StatelessWidget {
  final String label;
  final Color color;
  final IconData? icon;
  final bool small;
  const _Badge(
      {required this.label,
      required this.color,
      this.icon,
      this.small = false});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: EdgeInsets.symmetric(
        horizontal: small ? 5 : 7,
        vertical: small ? 1 : 2,
      ),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, color: color, size: small ? 10 : 12),
            const SizedBox(width: 3),
          ],
          Text(label,
              style: GoogleFonts.sora(
                color: color,
                fontSize: small ? 10 : 11,
                fontWeight: FontWeight.w500,
              )),
        ],
      ),
    );
  }
}

class _ToggleChip extends StatelessWidget {
  final String label;
  final IconData? icon;
  final bool selected;
  final VoidCallback onTap;
  const _ToggleChip(
      {required this.label,
      this.icon,
      required this.selected,
      required this.onTap});

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
        decoration: BoxDecoration(
          color: selected
              ? _accent.withValues(alpha: 0.18)
              : Colors.white.withValues(alpha: 0.05),
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
            if (icon != null) ...[
              Icon(icon, color: selected ? _accent : _textMuted, size: 16),
              const SizedBox(width: 6),
            ],
            Text(label,
                style: GoogleFonts.sora(
                  color: selected ? _accent : _textMuted,
                  fontSize: 13,
                  fontWeight: selected ? FontWeight.w500 : FontWeight.w400,
                )),
          ],
        ),
      ),
    );
  }
}

class _CalendarLegend extends StatelessWidget {
  final Color color;
  final String label;
  const _CalendarLegend({required this.color, required this.label});

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 8,
          height: 8,
          decoration: BoxDecoration(shape: BoxShape.circle, color: color),
        ),
        const SizedBox(width: 4),
        Text(label, style: GoogleFonts.sora(color: _textMuted, fontSize: 10)),
      ],
    );
  }
}

class _SelectionCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final bool recommended;
  final VoidCallback onTap;
  const _SelectionCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    this.recommended = false,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: Colors.white.withValues(alpha: 0.04),
          borderRadius: BorderRadius.circular(12),
          border: Border.all(
            color: recommended
                ? _accent.withValues(alpha: 0.3)
                : _cardBorder,
          ),
        ),
        child: Row(
          children: [
            Container(
              width: 40,
              height: 40,
              decoration: BoxDecoration(
                color: (recommended ? _accent : _textMuted)
                    .withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Icon(icon,
                  color: recommended ? _accent : _textMuted, size: 20),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Text(title,
                          style: GoogleFonts.sora(
                              color: _textGhost,
                              fontSize: 14,
                              fontWeight: FontWeight.w500)),
                      if (recommended) ...[
                        const SizedBox(width: 8),
                        _Badge(
                          label: 'Recommended',
                          color: _accent,
                          small: true,
                        ),
                      ],
                    ],
                  ),
                  const SizedBox(height: 2),
                  Text(subtitle,
                      style:
                          GoogleFonts.sora(color: _textMuted, fontSize: 12)),
                ],
              ),
            ),
            const Icon(Icons.chevron_right, color: _textMuted, size: 20),
          ],
        ),
      ),
    );
  }
}

class _FormField extends StatelessWidget {
  final String label;
  final bool optional;
  final Widget child;
  const _FormField(
      {required this.label, this.optional = false, required this.child});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text(label,
                  style: GoogleFonts.sora(
                      color: _textGhost,
                      fontSize: 13,
                      fontWeight: FontWeight.w500)),
              if (optional)
                Text(AppLocalizations.of(context).subOptionalSuffix,
                    style: GoogleFonts.sora(
                        color: _textMuted, fontSize: 11)),
            ],
          ),
          const SizedBox(height: 6),
          child,
        ],
      ),
    );
  }
}

class _DropdownField<T> extends StatelessWidget {
  final T value;
  final List<DropdownMenuItem<T>> items;
  final ValueChanged<T?> onChanged;
  const _DropdownField(
      {required this.value, required this.items, required this.onChanged});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14),
      decoration: BoxDecoration(
        color: Colors.white.withValues(alpha: 0.06),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: _cardBorder),
      ),
      child: DropdownButtonHideUnderline(
        child: DropdownButton<T>(
          value: value,
          items: items,
          onChanged: onChanged,
          isExpanded: true,
          dropdownColor: _sheetBg,
          style: GoogleFonts.sora(color: _textGhost, fontSize: 14),
          iconEnabledColor: _textMuted,
        ),
      ),
    );
  }
}

// ── Shared InputDecoration builder ──────────────────────
InputDecoration _inputDeco(String hint) => InputDecoration(
      hintText: hint,
      hintStyle: GoogleFonts.sora(color: _textMuted, fontSize: 14),
      filled: true,
      fillColor: Colors.white.withValues(alpha: 0.06),
      border: OutlineInputBorder(
        borderRadius: BorderRadius.circular(10),
        borderSide: const BorderSide(color: _cardBorder),
      ),
      enabledBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(10),
        borderSide: const BorderSide(color: _cardBorder),
      ),
      focusedBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(10),
        borderSide: const BorderSide(color: _accent),
      ),
      errorBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(10),
        borderSide: const BorderSide(color: _expense),
      ),
      contentPadding:
          const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
    );
