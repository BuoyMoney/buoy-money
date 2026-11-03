import '../config/server_capabilities.dart';
import 'dart:async';
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/transaction_models.dart';
import '../models/savings_models.dart';
import '../models/suspected_transfer.dart';
import '../services/ai_service.dart';
import '../services/ai_status_service.dart';
import '../services/transactions_service.dart';
import '../services/categories_service.dart';
import '../services/savings_service.dart';
import '../utils/api_error.dart';
import '../utils/formatters.dart';
import '../utils/savings_earmark.dart';
import '../widgets/ai_sparkle.dart';
import '../widgets/app_toast.dart';
import '../widgets/transaction_modals.dart';
import '../widgets/ai_consent_sheet.dart';

// VaporClinic palette
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _pending = Color(0xFFFBBF24);

/// Date range preset for quick filtering
enum _DatePreset { all, week, month, quarter, year, ytd }

extension _DatePresetLabel on _DatePreset {
  String get label => switch (this) {
    _DatePreset.all => 'All',
    _DatePreset.week => '7D',
    _DatePreset.month => '30D',
    _DatePreset.quarter => '90D',
    _DatePreset.year => '1Y',
    _DatePreset.ytd => 'YTD',
  };

  ({DateTime? start, DateTime? end}) get range {
    final now = DateTime.now();
    return switch (this) {
      _DatePreset.all => (start: null, end: null),
      _DatePreset.week => (start: now.subtract(const Duration(days: 7)), end: now),
      _DatePreset.month => (start: now.subtract(const Duration(days: 30)), end: now),
      _DatePreset.quarter => (start: now.subtract(const Duration(days: 90)), end: now),
      _DatePreset.year => (start: now.subtract(const Duration(days: 365)), end: now),
      _DatePreset.ytd => (start: DateTime(now.year, 1, 1), end: now),
    };
  }
}

class TransactionsPage extends StatefulWidget {
  /// Optional deep-link filters (e.g. from a budget category's "View
  /// Transactions" action): seed the page already filtered to a category and
  /// date window. Passed as raw query-param strings from the router.
  final String? initialCategoryId;
  final String? initialStartDate;
  final String? initialEndDate;

  const TransactionsPage({
    super.key,
    this.initialCategoryId,
    this.initialStartDate,
    this.initialEndDate,
  });

  @override
  State<TransactionsPage> createState() =>
      _TransactionsPageState();
}

class _TransactionsPageState extends State<TransactionsPage>
    with SingleTickerProviderStateMixin {
  final _scrollController = ScrollController();
  final _searchController = TextEditingController();
  Timer? _debounce;

  // Data
  List<Transaction> _transactions = [];
  PaginationMetadata? _pagination;
  TransactionSummary? _summary;
  bool _isLoading = true;
  bool _isLoadingMore = false;
  String? _error;

  // Monotonic request token. Bumped whenever a page-1 reload starts so an
  // in-flight _loadMore (or a superseded reload) can detect it raced and
  // discard its stale result instead of clobbering the current list/pagination.
  int _loadGeneration = 0;

  // Separate token for summary-only refreshes (see _refreshSummary).
  int _summaryGeneration = 0;

  // Filters
  TransactionFilter _filter = TransactionFilter();
  _DatePreset _datePreset = _DatePreset.all;
  String _debouncedSearch = '';

  // AI Review
  ReviewStatistics? _reviewStats;
  int _suspectedTransferCount = 0;
  bool _isAICategorizing = false;
  int _aiCategorizeCompleted = 0;
  int _aiCategorizeTotal = 0;

  // Background AI categorization (ingest pipeline). Non-zero while the server's
  // drainer is still working through this user's freshly synced transactions;
  // the banner shows the analyzing indicator and we poll until it lands.
  int _queuedForAi = 0;
  Timer? _queuePoll;
  static const _queuePollInterval = Duration(seconds: 3);

  // Animation
  late AnimationController _fadeController;
  late Animation<double> _fadeAnimation;

  @override
  void initState() {
    super.initState();
    _applyInitialFilters();
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnimation =
        CurvedAnimation(parent: _fadeController, curve: Curves.easeOut);
    _fadeController.forward();
    _scrollController.addListener(_onScroll);
    aiAvailability.addListener(_onAiAvailabilityChanged);
    _loadTransactions();
    _loadReviewStats();
  }

  /// Seeds [_filter] from any deep-link query params so the first load is
  /// already scoped (e.g. arriving from a budget category's "View
  /// Transactions"). A supplied date range maps to the closest preset so the
  /// date selector reflects the active window.
  void _applyInitialFilters() {
    final categoryId = int.tryParse(widget.initialCategoryId ?? '');
    final startDate = DateTime.tryParse(widget.initialStartDate ?? '');
    final endDate = DateTime.tryParse(widget.initialEndDate ?? '');
    if (categoryId == null && startDate == null && endDate == null) return;

    _filter = _filter.copyWith(
      categoryId: categoryId,
      startDate: startDate,
      endDate: endDate,
    );
    if (startDate != null || endDate != null) {
      _datePreset = _DatePreset.quarter;
    }
  }

  void _onAiAvailabilityChanged() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    aiAvailability.removeListener(_onAiAvailabilityChanged);
    _scrollController.dispose();
    _searchController.dispose();
    _debounce?.cancel();
    _queuePoll?.cancel();
    _fadeController.dispose();
    super.dispose();
  }

  // ── Data Loading ──────────────────────────────────────

  Future<void> _loadTransactions() async {
    // Bump the generation so any in-flight _loadMore (or an earlier reload)
    // knows it has been superseded and must drop its result.
    final generation = ++_loadGeneration;
    setState(() {
      _isLoading = true;
      _error = null;
      // A page-1 reload cancels any in-flight load-more append.
      _isLoadingMore = false;
    });

    try {
      final response = await transactionsService.getTransactions(
        _filter.copyWith(page: 1),
      );
      if (!mounted || generation != _loadGeneration) return;
      setState(() {
        _transactions = response.transactions;
        _pagination = response.pagination;
        _summary = response.summary;
        _isLoading = false;
      });
      // The first page can land without filling the viewport past the trigger
      // zone, in which case no scroll is possible and nothing would ask for
      // page 2.
      _loadMoreIfViewportWants();
    } catch (e) {
      if (!mounted || generation != _loadGeneration) return;
      setState(() {
        _error = 'Failed to load transactions';
        _isLoading = false;
      });
    }
  }

  /// Pull-to-refresh. Deliberately reloads the review stats too: those drive the
  /// "N transactions need review" banner pinned above the list, and pulling down
  /// only ever refreshed the list underneath it, so the banner kept showing a
  /// count the user had already worked through.
  Future<void> _handlePullToRefresh() async {
    await Future.wait([_loadTransactions(), _loadReviewStats()]);
  }

  /// Refreshes only the period summary in the header.
  ///
  /// Editing one row's category shifts the backend-computed Income/Expenses/Net
  /// totals, so those have to be re-fetched — but a full [_loadTransactions]
  /// resets to page 1, which discards every page the user scrolled in and snaps
  /// them back to the top of the first batch. That fires after every single save,
  /// i.e. constantly while categorizing, which is the one flow where being deep
  /// in the list is normal. The row itself is already updated in place by the
  /// caller, so only the totals need to move.
  ///
  /// pageSize 1 because the summary is computed over the whole date range
  /// server-side, independent of the page slice — we want the totals, not rows.
  Future<void> _refreshSummary() async {
    // Two guards: _loadGeneration so a full reload in flight wins (it sets the
    // summary itself), and _summaryGeneration so rapid-fire categorizing can't
    // land an older total on top of a newer one out of order.
    final loadGeneration = _loadGeneration;
    final summaryGeneration = ++_summaryGeneration;
    try {
      final response = await transactionsService.getTransactions(
        _filter.copyWith(page: 1, pageSize: 1),
      );
      if (!mounted ||
          loadGeneration != _loadGeneration ||
          summaryGeneration != _summaryGeneration) {
        return;
      }
      setState(() => _summary = response.summary);
    } catch (_) {
      // Non-critical — the list and the edited row are already correct.
    }
  }

  Future<void> _loadMore() async {
    if (_isLoadingMore || _pagination == null || !_pagination!.hasNextPage) {
      return;
    }

    final generation = _loadGeneration;
    setState(() => _isLoadingMore = true);

    try {
      final response = await transactionsService.getTransactions(
        _filter.copyWith(page: _pagination!.currentPage + 1),
      );
      // Discard if a page-1 reload started (or completed) while this was in
      // flight — otherwise we'd append this filter's page-2 onto a different
      // list and rewind currentPage.
      if (!mounted || generation != _loadGeneration) return;
      setState(() {
        // Dedupe by id so a concurrent reload can't leave duplicate rows.
        final existingIds = _transactions.map((t) => t.id).toSet();
        _transactions.addAll(
          response.transactions.where((t) => !existingIds.contains(t.id)),
        );
        _pagination = response.pagination;
        _isLoadingMore = false;
      });
      // A page that lands without extending the content past the trigger zone
      // has to ask for the next one itself — see _loadMoreIfViewportWants.
      _loadMoreIfViewportWants();
    } catch (e) {
      if (mounted && generation == _loadGeneration) {
        setState(() => _isLoadingMore = false);
      }
    }
  }

  /// Distance from the bottom of the list at which the next page is requested.
  static const _loadMoreTriggerPx = 300.0;

  void _onScroll() {
    if (_scrollController.position.pixels >=
        _scrollController.position.maxScrollExtent - _loadMoreTriggerPx) {
      _loadMore();
    }
  }

  /// Requests the next page when the viewport is *already sitting* in the
  /// trigger zone, instead of waiting for the user to move.
  ///
  /// [_onScroll] only runs while the scroll position is actually changing, so
  /// pagination is driven by gestures rather than by position. Two reachable
  /// ways that stalls:
  ///
  ///  - The user scrolls fast to the bottom while a load is already in flight.
  ///    That call is dropped by the `_isLoadingMore` guard, and by the time the
  ///    load lands the user has stopped moving, so nothing asks again.
  ///  - A page arrives that still doesn't extend the content past the trigger
  ///    zone (few rows, or a tall desktop window), so no scroll is even possible.
  ///
  /// Either way the list looks like it has hit a hard cap, and nudging the
  /// scroll a few pixels "unsticks" it — which is the tell for this bug.
  ///
  /// Runs post-frame because the new rows must be laid out before
  /// maxScrollExtent accounts for them. Terminates because every call advances
  /// currentPage and [_loadMore] stops once hasNextPage is false.
  void _loadMoreIfViewportWants() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_scrollController.hasClients) return;
      final position = _scrollController.position;
      if (position.maxScrollExtent - position.pixels <= _loadMoreTriggerPx) {
        _loadMore();
      }
    });
  }

  // ── AI Review ─────────────────────────────────────────

  Future<void> _loadReviewStats() async {
    try {
      final stats = await aiService.getReviewStatistics();
      if (mounted) {
        setState(() {
          _reviewStats = stats;
          _queuedForAi = stats.queuedForAiCount;
        });
        _watchBackgroundCategorization();
      }
    } catch (_) {
      // Non-critical — silently ignore
    }
    // Suspected-transfer count drives the slim entry banner; the confirm UI itself lives
    // in the review sheet so this page stays focused on the transaction list. Non-critical.
    try {
      final transfers = await transactionsService.getSuspectedTransfers();
      if (mounted) setState(() => _suspectedTransferCount = transfers.length);
    } catch (_) {}
  }

  /// While the server is still categorizing freshly synced rows, poll the queue
  /// count so the banner's indicator is live and the list refreshes the moment
  /// the suggestions land. Rare in practice (it rides on a sync), which is why
  /// it is worth showing: the user opened the page and can see the app at work.
  void _watchBackgroundCategorization() {
    if (_queuedForAi <= 0) {
      _queuePoll?.cancel();
      _queuePoll = null;
      return;
    }
    if (_queuePoll != null) return;
    _queuePoll = Timer.periodic(_queuePollInterval, (_) => _pollBackgroundCategorization());
  }

  Future<void> _pollBackgroundCategorization() async {
    try {
      final remaining = await aiService.getQueuedCount();
      if (!mounted) return;
      if (remaining > 0) {
        setState(() => _queuedForAi = remaining);
        return;
      }
      _queuePoll?.cancel();
      _queuePoll = null;
      setState(() => _queuedForAi = 0);
      // Suggestions landed: refresh the list and the banner counts together.
      await _loadTransactions();
      await _loadReviewStats();
    } catch (_) {
      // Transient; the next tick tries again.
    }
  }

  Future<void> _handleAICategorize() async {
    if (_isAICategorizing) return;
    // Auto-categorize sends merchant/amount data to Gemini: consent first (5.1.2(i)).
    if (!await ensureAiConsent(context)) return;
    if (!mounted) return;
    setState(() {
      _isAICategorizing = true;
      _aiCategorizeCompleted = 0;
      _aiCategorizeTotal = _reviewStats?.aiEligibleCount ?? 0;
    });
    try {
      final result = await aiService.batchCategorize(
        onProgress: (completed, total) {
          if (mounted) {
            setState(() {
              _aiCategorizeCompleted = completed;
              _aiCategorizeTotal = total;
            });
          }
        },
      );
      if (!mounted) return;
      final parts = <String>[];
      if (result.successfullyCategorized > 0) {
        parts.add('${result.successfullyCategorized} categorized');
      }
      if (result.failed > 0) {
        parts.add('${result.failed} couldn\'t be auto-categorized');
      }
      if (result.needingReview > 0) {
        parts.add('${result.needingReview} ready for review');
      }
      // A run can stop early if a round kept failing (e.g. a dropped connection).
      // The progress so far is real; tell the user more remains rather than
      // showing a scary error that hides what already succeeded.
      if (result.interrupted) {
        final summary = parts.isEmpty ? 'Some' : parts.join(', ');
        showAppToast(
          context,
          '$summary so far. Tap Auto-Categorize to finish the rest.',
          type: AppToastType.neutral,
        );
      } else {
        showAppToast(context, parts.join(', '), type: AppToastType.success);
      }
      await _loadTransactions();
      await _loadReviewStats();
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          'AI categorization failed: $e',
          type: AppToastType.error,
        );
      }
    } finally {
      if (mounted) setState(() => _isAICategorizing = false);
    }
  }

  void _openReviewModal() {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _TransactionReviewSheet(
        onComplete: () {
          _loadTransactions();
          _loadReviewStats();
        },
      ),
    );
  }

  // ── Filter Handlers ───────────────────────────────────

  void _onSearchChanged(String value) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 400), () {
      if (!mounted) return;
      if (_debouncedSearch != value) {
        _debouncedSearch = value;
        setState(() {
          _filter = _filter.copyWith(search: value.isEmpty ? '' : value);
        });
        _loadTransactions();
      }
    });
  }

  void _onDatePresetChanged(_DatePreset preset) {
    // Cancel any pending search debounce so a stale keystroke can't re-apply
    // a search the user has moved on from.
    _debounce?.cancel();
    final range = preset.range;
    setState(() {
      _datePreset = preset;
      _filter = _filter.copyWith(
        startDate: range.start,
        endDate: range.end,
        clearStartDate: range.start == null,
        clearEndDate: range.end == null,
      );
    });
    _loadTransactions();
  }

  void _clearAllFilters() {
    // Cancel a pending debounce so a stale 'coffee' timer can't silently
    // re-filter after the box has been cleared.
    _debounce?.cancel();
    _searchController.clear();
    _debouncedSearch = '';
    setState(() {
      _filter = TransactionFilter();
      _datePreset = _DatePreset.all;
    });
    _loadTransactions();
  }

  // ── Modal Handlers ────────────────────────────────────

  void _openTransactionDetail(Transaction tx) {
    showTransactionDetailSheet(
      context,
      tx,
      onChanged: (result) => _applyDetailResult(tx, result),
    );
  }

  /// Applies a detail-sheet edit to the loaded list in place.
  ///
  /// Only one row ever changes, so reloading the whole list to learn about it
  /// was always disproportionate — and worse, it reset pagination to page 1 and
  /// dropped the user back to the top. The sheet cannot change a transaction's
  /// amount or date, so an edit there can never move a row within the
  /// date-ordered list: splicing it back is correct, not merely cheaper. A split
  /// is the exception, since it creates child rows this page has never seen.
  void _applyDetailResult(Transaction original, TransactionDetailResult result) {
    if (!mounted) return;

    if (result.restructured) {
      _loadTransactions();
      _loadReviewStats();
      return;
    }

    setState(() => _applyRowChange(result.transactionId, result.transaction));
    _refreshSummary();

    // The review banner counts rows that still need a category, so only a delete
    // or an actual categorization can move it. Skip the two extra requests for a
    // rename, a note or a tag edit.
    final updated = result.transaction;
    final categoryChanged = updated != null &&
        (updated.categoryId != original.categoryId ||
            updated.isUserCategorized != original.isUserCategorized ||
            updated.needsReview != original.needsReview);
    if (result.deleted || categoryChanged) {
      _loadReviewStats();
    }
  }

  /// Splices one edited row back into the loaded list, or drops it when the
  /// transaction was deleted ([updated] null) or no longer matches an active
  /// category filter. Keeps `totalItems` in step so the header count doesn't
  /// drift away from the rows actually on screen. Call inside setState.
  void _applyRowChange(int transactionId, Transaction? updated) {
    final idx = _transactions.indexWhere((t) => t.id == transactionId);
    if (idx == -1) return;

    final evicted = updated == null ||
        (_filter.categoryId != null &&
            updated.categoryId != _filter.categoryId);

    if (!evicted) {
      _transactions[idx] = updated;
      return;
    }

    _transactions.removeAt(idx);
    final pagination = _pagination;
    if (pagination != null) {
      _pagination = pagination.copyWith(
        totalItems: pagination.totalItems > 0 ? pagination.totalItems - 1 : 0,
      );
    }
  }

  void _openCreateTransaction() async {
    final created = await showCreateTransactionSheet(
      context,
      defaultAccountId: _filter.accountId,
      onCreated: () {
        _loadTransactions();
        _loadReviewStats();
      },
    );
    if (created != null && mounted) {
      showAppToast(
        context,
        'Added “${created.description}”',
        type: AppToastType.success,
      );
    }
  }

  void _openAdvancedFilters() async {
    // Cancel a pending debounce so it can't clobber the filter the user just
    // applied here.
    _debounce?.cancel();
    final result = await showAdvancedFiltersSheet(context, _filter);
    if (result != null && mounted) {
      setState(() {
        _filter = result;
        _datePreset = _DatePreset.all;
      });
      _loadTransactions();
    }
  }

  void _openEditCategory(Transaction tx) async {
    final updated = await showEditCategorySheet(context, tx);
    if (updated != null && mounted) {
      // Update the single row in place rather than reloading the list — a
      // reload resets to page 1 and bounces the user back up to the first batch
      // after every save, so categorizing anything past it meant scrolling back
      // down each time.
      setState(() => _applyRowChange(tx.id, updated));
      // A category/earmark change (e.g. earmarking to a savings goal, which is
      // excluded from budget spend, or recategorizing to Transfer) shifts the
      // backend-computed Income/Expenses/Net totals, so refresh those — just the
      // totals, not the list.
      _refreshSummary();
      // Refresh review stats so the "X need review" banner and count stay accurate
      if (tx.needsReview && !tx.isUserCategorized && updated.isUserCategorized) {
        _loadReviewStats();
      }
    }
  }

  void _openSplitTransaction(Transaction tx) async {
    final result = await showSplitTransactionSheet(context, tx);
    if (result == true && mounted) {
      _loadTransactions();
    }
  }

  // ── Context Menu ──────────────────────────────────────

  void _showContextMenu(Transaction tx, TapDownDetails details) {
    final offset = details.globalPosition;
    showMenu<String>(
      context: context,
      position: RelativeRect.fromLTRB(offset.dx, offset.dy, offset.dx, offset.dy),
      color: const Color(0xFF0C0C18),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(color: _cardBorder),
      ),
      items: [
        _menuItem('details', Icons.info_outline, 'View Details'),
        _menuItem('category', Icons.category_outlined, 'Edit Category'),
        _menuItem('split', Icons.call_split, tx.isSplit ? 'Edit Split' : 'Split'),
        _menuItem('delete', Icons.delete_outline, 'Delete', color: _expense),
      ],
    ).then((value) {
      if (value == null) return;
      switch (value) {
        case 'details':
          _openTransactionDetail(tx);
        case 'category':
          _openEditCategory(tx);
        case 'split':
          _openSplitTransaction(tx);
        case 'delete':
          _deleteTransaction(tx);
      }
    });
  }

  PopupMenuItem<String> _menuItem(
    String value,
    IconData icon,
    String label, {
    Color color = _textGhost,
  }) {
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

  Future<void> _deleteTransaction(Transaction tx) async {
    // Fall back through merchant → description; a manual transaction can have an
    // empty merchant (not just null), so guard on isNotEmpty, not just `??`,
    // otherwise the prompt reads `Delete ""?`.
    final merchant = tx.merchantName?.trim() ?? '';
    final desc = tx.description.trim();
    final name = merchant.isNotEmpty ? merchant : desc;
    final prompt = name.isNotEmpty
        ? AppLocalizations.of(context).txnDeleteNamedPrompt(name)
        : AppLocalizations.of(context).txnDeleteGenericPrompt;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: const Color(0xFF0C0C18),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: _cardBorder),
        ),
        title: Text(AppLocalizations.of(ctx).txnDeleteTransactionTitle,
            style: GoogleFonts.sora(color: _textGhost, fontWeight: FontWeight.bold)),
        content: Text(AppLocalizations.of(ctx).txnDeleteConfirmBody(prompt),
            style: GoogleFonts.sora(color: _textMuted, fontSize: 14)),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(AppLocalizations.of(ctx).txnCancel,
                style: GoogleFonts.sora(color: _textMuted)),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(AppLocalizations.of(ctx).txnDelete,
                style: GoogleFonts.sora(color: _expense)),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await transactionsService.deleteTransaction(tx.id);
      if (mounted) {
        // Through the shared helper so the "N total" count and the header
        // totals move with the row, like every other single-row change. This
        // path used to drop the row and leave both stale.
        setState(() => _applyRowChange(tx.id, null));
        _refreshSummary();
        showAppToast(context, 'Deleted "$name"', type: AppToastType.success);
      }
    } catch (_) {
      if (mounted) {
        showAppToast(
          context,
          'Failed to delete transaction',
          type: AppToastType.error,
        );
      }
    }
  }

  // ── Active Filter Count ───────────────────────────────

  int get _activeFilterCount {
    int count = 0;
    if (_filter.categoryId != null) count++;
    if (_filter.accountId != null) count++;
    if (_filter.minAmount != null || _filter.maxAmount != null) count++;
    if (_filter.startDate != null || _filter.endDate != null) count++;
    if (_filter.search != null && _filter.search!.isNotEmpty) count++;
    return count;
  }

  // ── Helpers ───────────────────────────────────────────

  bool _isSameDay(DateTime a, DateTime b) =>
      a.year == b.year && a.month == b.month && a.day == b.day;

  // ── UI ────────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      floatingActionButton: FloatingActionButton.extended(
        onPressed: _openCreateTransaction,
        backgroundColor: _accent,
        foregroundColor: Colors.white,
        icon: const Icon(Icons.add),
        label: Text(
          AppLocalizations.of(context).txnAdd,
          style: GoogleFonts.sora(fontWeight: FontWeight.w600),
        ),
      ),
      body: FadeTransition(
        opacity: _fadeAnimation,
        child: _isLoading && _transactions.isEmpty
            ? _buildLoading()
            : _error != null && _transactions.isEmpty
                ? _buildError()
                : _buildContent(),
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
          Icon(Icons.error_outline, size: 48, color: _expense.withValues(alpha: 0.7)),
          const SizedBox(height: 16),
          Text(_error!, style: GoogleFonts.sora(color: _textMuted, fontSize: 14)),
          const SizedBox(height: 16),
          TextButton(
            onPressed: _loadTransactions,
            child: Text(AppLocalizations.of(context).txnRetry,
                style: GoogleFonts.sora(color: _accent)),
          ),
        ],
      ),
    );
  }

  Widget _buildContent() {
    return RefreshIndicator(
      onRefresh: _handlePullToRefresh,
      color: _accent,
      backgroundColor: const Color(0xFF0A0A14),
      child: CustomScrollView(
        controller: _scrollController,
        physics: const AlwaysScrollableScrollPhysics(),
        slivers: [
          // Header + summary
          SliverToBoxAdapter(child: _buildHeader()),
          // AI Review banner — show when there are uncategorized or review-pending transactions,
          // or while the background pipeline is still categorizing newly synced ones.
          if (_reviewStats != null &&
              (_reviewStats!.totalNeedingReview > 0 ||
                  _reviewStats!.uncategorizedCount > 0 ||
                  _queuedForAi > 0))
            SliverToBoxAdapter(child: _buildReviewBanner()),
          // Search bar + filter button
          SliverToBoxAdapter(child: _buildSearchRow()),
          // Date preset chips
          SliverToBoxAdapter(child: _buildDatePresets()),
          // Active filter indicator
          if (_filter.hasActiveFilters)
            SliverToBoxAdapter(child: _buildActiveFilterBar()),
          // Slim entry for suspected internal transfers — the full confirm UI lives in the
          // review sheet, so the transactions list (not this prompt) stays the focus here.
          if (_suspectedTransferCount > 0)
            SliverToBoxAdapter(child: _buildTransfersEntry()),
          // Transaction list grouped by date
          _buildTransactionList(),
          // Loading more indicator
          if (_isLoadingMore)
            const SliverToBoxAdapter(
              child: Padding(
                padding: EdgeInsets.all(24),
                child: Center(
                  child: SizedBox(
                    width: 24,
                    height: 24,
                    child: CircularProgressIndicator(
                        strokeWidth: 2, color: _accent),
                  ),
                ),
              ),
            ),
          // Bottom padding
          const SliverPadding(padding: EdgeInsets.only(bottom: 32)),
        ],
      ),
    );
  }

  // ── Header with summary ───────────────────────────────

  Widget _buildHeader() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 24, 24, 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  AppLocalizations.of(context).txnPageTitle,
                  style: GoogleFonts.sora(
                    fontSize: 28,
                    fontWeight: FontWeight.bold,
                    color: _textGhost,
                  ),
                ),
              ),
              if (_pagination != null)
                Text(
                  AppLocalizations.of(context)
                      .txnTotalCount(_pagination!.totalItems),
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
                ),
            ],
          ),
          if (_summary != null) ...[
            const SizedBox(height: 16),
            _buildSummaryRow(),
          ],
        ],
      ),
    );
  }

  Widget _buildSummaryRow() {
    return Row(
      children: [
        _SummaryChip(
          label: 'Income',
          value: formatCurrency(_summary!.totalIncome),
          color: _income,
        ),
        const SizedBox(width: 12),
        _SummaryChip(
          label: 'Expenses',
          value: formatCurrency(_summary!.totalExpenses),
          color: _expense,
        ),
        const SizedBox(width: 12),
        _SummaryChip(
          label: 'Net',
          value: formatCurrency(_summary!.netAmount),
          color: _summary!.netAmount >= 0 ? _income : _expense,
        ),
      ],
    );
  }

  Widget _buildReviewBanner() {
    final total = _reviewStats!.totalNeedingReview;
    final aiEligible = _reviewStats!.aiEligibleCount;
    final older = _reviewStats!.olderCount;
    final thisMonth = _reviewStats!.thisMonthCount;
    // The AI button is offered only for rows the AI has never attempted (Plaid low-confidence
    // ones included); rows it already tried and couldn't place are review-only. Re-sending
    // them would not make the model more confident.
    final hasUncategorized = aiEligible > 0;
    // The ingest pipeline is still working through freshly synced rows on the server.
    final analyzing = _queuedForAi > 0 && !_isAICategorizing;

    // Header text adapts: uncategorized → prompt to auto-categorize, otherwise → ready for
    // review; with nothing to review yet but AI still working, say that instead.
    final headerText = total == 0 && analyzing
        ? AppLocalizations.of(context).txnAiAnalyzing
        : hasUncategorized
            ? AppLocalizations.of(context).txnNeedReviewCount(total)
            : AppLocalizations.of(context).txnReadyForReviewCount(total);
    final headerIcon = total == 0 && analyzing
        ? Icons.auto_awesome
        : hasUncategorized
            ? Icons.rate_review_outlined
            : Icons.checklist;
    final headerColor = hasUncategorized || analyzing ? _accent : _income;

    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 8, 24, 0),
      child: Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          gradient: LinearGradient(
            colors: [headerColor.withValues(alpha: 0.12), headerColor.withValues(alpha: 0.05)],
          ),
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: headerColor.withValues(alpha: 0.25)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(headerIcon, size: 20, color: headerColor),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    headerText,
                    style: GoogleFonts.sora(fontSize: 13, color: _textGhost, fontWeight: FontWeight.w500),
                  ),
                ),
              ],
            ),
            // #737: split this-month vs older so the modal (which starts with this month)
            // reads sensibly against the total.
            if (older > 0) ...[
              const SizedBox(height: 4),
              Padding(
                padding: const EdgeInsets.only(left: 30),
                child: Text(
                  AppLocalizations.of(context)
                      .txnThisMonthVsOlder(thisMonth, older),
                  style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                ),
              ),
            ],
            if (hasUncategorized) ...[
              const SizedBox(height: 4),
              Padding(
                padding: const EdgeInsets.only(left: 30),
                child: Text(
                  AppLocalizations.of(context)
                      .txnAiEligibleHint(aiEligible),
                  style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                ),
              ),
            ],
            // Live indicator for the background pipeline: same spinner + bar as the manual
            // run, indeterminate because the server works the queue in its own batches.
            if (analyzing) ...[
              const SizedBox(height: 12),
              Row(
                children: [
                  const SizedBox(
                    width: 14, height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2, color: _accent),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context).txnAiCategorizingNew(_queuedForAi),
                      style: GoogleFonts.sora(fontSize: 12, color: _textGhost, fontWeight: FontWeight.w500),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              ClipRRect(
                borderRadius: BorderRadius.circular(4),
                child: LinearProgressIndicator(
                  backgroundColor: _accent.withValues(alpha: 0.1),
                  color: _accent,
                  minHeight: 6,
                ),
              ),
            ],
            if (total > 0 || _isAICategorizing) const SizedBox(height: 12),
            if (_isAICategorizing) ...[
              Row(
                children: [
                  const SizedBox(
                    width: 14, height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2, color: _accent),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      _aiCategorizeTotal > 0
                          ? AppLocalizations.of(context).txnCategorizingProgress(
                              _aiCategorizeCompleted, _aiCategorizeTotal)
                          : AppLocalizations.of(context).txnCategorizing,
                      style: GoogleFonts.sora(fontSize: 12, color: _textGhost, fontWeight: FontWeight.w500),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              ClipRRect(
                borderRadius: BorderRadius.circular(4),
                child: LinearProgressIndicator(
                  value: _aiCategorizeTotal > 0
                      ? _aiCategorizeCompleted / _aiCategorizeTotal
                      : null,
                  backgroundColor: _accent.withValues(alpha: 0.1),
                  color: _accent,
                  minHeight: 6,
                ),
              ),
            ] else if (hasUncategorized)
              Row(
                children: [
                  // No AI on this server: the one-tap categorize button would only error.
                  if (aiAvailability.featureOn('categorization')) ...[
                    Expanded(
                      child: GestureDetector(
                        onTap: _handleAICategorize,
                        child: Container(
                          padding: const EdgeInsets.symmetric(vertical: 10),
                          decoration: BoxDecoration(
                            color: _accent.withValues(alpha: 0.15),
                            borderRadius: BorderRadius.circular(8),
                            border: Border.all(color: _accent.withValues(alpha: 0.3)),
                          ),
                          child: Row(
                            mainAxisAlignment: MainAxisAlignment.center,
                            children: [
                              Icon(Icons.auto_fix_high, size: 14, color: _accent),
                              const SizedBox(width: 6),
                              Text(
                                AppLocalizations.of(context).txnAutoCategorize,
                                style: GoogleFonts.sora(fontSize: 12, color: _accent, fontWeight: FontWeight.w600),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(width: 10),
                  ],
                  Expanded(
                    child: GestureDetector(
                      onTap: _openReviewModal,
                      child: Container(
                        padding: const EdgeInsets.symmetric(vertical: 10),
                        decoration: BoxDecoration(
                          color: _income.withValues(alpha: 0.15),
                          borderRadius: BorderRadius.circular(8),
                          border: Border.all(color: _income.withValues(alpha: 0.3)),
                        ),
                        child: Row(
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            Icon(Icons.checklist, size: 14, color: _income),
                            const SizedBox(width: 6),
                            Text(
                              AppLocalizations.of(context).txnReview,
                              style: GoogleFonts.sora(fontSize: 12, color: _income, fontWeight: FontWeight.w600),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ],
              )
            else if (total > 0)
              GestureDetector(
                onTap: _openReviewModal,
                child: Container(
                  width: double.infinity,
                  padding: const EdgeInsets.symmetric(vertical: 10),
                  decoration: BoxDecoration(
                    color: _income.withValues(alpha: 0.15),
                    borderRadius: BorderRadius.circular(8),
                    border: Border.all(color: _income.withValues(alpha: 0.3)),
                  ),
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Icon(Icons.checklist, size: 14, color: _income),
                      const SizedBox(width: 6),
                      Text(
                        AppLocalizations.of(context).txnReviewTransactions,
                        style: GoogleFonts.sora(fontSize: 12, color: _income, fontWeight: FontWeight.w600),
                      ),
                    ],
                  ),
                ),
              ),
            // "Forget the backlog": clearing hundreds of old transactions one at a time is
            // not a real option, so offer the whole pre-this-month slice as one action.
            // Only shown when there IS a backlog, and never while categorizing.
            if (older > 0 && !_isAICategorizing) ...[
              const SizedBox(height: 8),
              GestureDetector(
                onTap: _handleDismissOlder,
                child: Container(
                  width: double.infinity,
                  padding: const EdgeInsets.symmetric(vertical: 9),
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Icon(Icons.notifications_off_outlined,
                          size: 13, color: _textMuted),
                      const SizedBox(width: 6),
                      Flexible(
                        child: Text(
                          AppLocalizations.of(context).txnForgetOlderLink(older),
                          style: GoogleFonts.sora(
                              fontSize: 11,
                              color: _textMuted,
                              fontWeight: FontWeight.w500),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  /// Permanently clears the pre-this-month review backlog after confirmation.
  ///
  /// The cutoff is the server's, not ours — we only report the count the banner already
  /// showed. Confirmation is mandatory because there is no undo in the UI.
  Future<void> _handleDismissOlder() async {
    final older = _reviewStats?.olderCount ?? 0;
    if (older == 0) return;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        backgroundColor: const Color(0xFF12121C),
        title: Text(AppLocalizations.of(context).txnForgetOlderTitle(older),
            style: GoogleFonts.sora(fontSize: 16, color: _textGhost)),
        content: Text(
          AppLocalizations.of(context).txnForgetOlderBody,
          style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: Text(AppLocalizations.of(context).txnCancel,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          ),
          TextButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(AppLocalizations.of(context).txnForgetThem,
                style: GoogleFonts.sora(
                    fontSize: 13, color: _accent, fontWeight: FontWeight.w600)),
          ),
        ],
      ),
    );

    if (confirmed != true || !mounted) return;

    try {
      final dismissed = await aiService.dismissOlderReviews();
      if (!mounted) return;
      await _loadReviewStats();
      if (!mounted) return;
      showAppToast(
        context,
        'Dismissed $dismissed transaction${dismissed == 1 ? '' : 's'} from review',
        type: AppToastType.success,
      );
    } catch (_) {
      if (!mounted) return;
      showAppToast(
        context,
        "Couldn't dismiss those right now. Try again in a moment.",
        type: AppToastType.error,
      );
    }
  }

  /// Slim, tappable entry that opens the review sheet on its transfer step. Deliberately
  /// compact — a single line with breathing room — so the transactions list owns the page
  /// instead of a wall of transfer cards.
  Widget _buildTransfersEntry() {
    const accent = Color(0xFFF59E0B); // amber — "review this"
    final count = _suspectedTransferCount;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
      child: GestureDetector(
        onTap: _openReviewModal,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
          decoration: BoxDecoration(
            color: accent.withValues(alpha: 0.08),
            borderRadius: BorderRadius.circular(10),
            border: Border.all(color: accent.withValues(alpha: 0.30)),
          ),
          child: Row(
            children: [
              const Icon(Icons.swap_horiz, size: 16, color: accent),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  AppLocalizations.of(context).txnPossibleTransfersCount(count),
                  overflow: TextOverflow.ellipsis,
                  style: GoogleFonts.sora(
                      fontSize: 12.5, color: accent, fontWeight: FontWeight.w600),
                ),
              ),
              const SizedBox(width: 6),
              Text(AppLocalizations.of(context).txnReview,
                  style: GoogleFonts.sora(
                      fontSize: 12,
                      color: accent.withValues(alpha: 0.9),
                      fontWeight: FontWeight.w600)),
              const Icon(Icons.chevron_right, size: 18, color: accent),
            ],
          ),
        ),
      ),
    );
  }

  // ── Search Row with Filter Button ─────────────────────

  Widget _buildSearchRow() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 12, 24, 0),
      child: Row(
        children: [
          // Search bar
          Expanded(
            child: Container(
              decoration: BoxDecoration(
                color: _cardBg,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: _cardBorder),
              ),
              child: TextField(
                controller: _searchController,
                onChanged: _onSearchChanged,
                style: GoogleFonts.sora(color: _textGhost, fontSize: 14),
                decoration: InputDecoration(
                  hintText: 'Search merchant, description...',
                  hintStyle: GoogleFonts.sora(color: _textMuted, fontSize: 14),
                  prefixIcon: const Icon(Icons.search, color: _textMuted, size: 20),
                  suffixIcon: _searchController.text.isNotEmpty
                      ? IconButton(
                          icon: const Icon(Icons.clear, color: _textMuted, size: 18),
                          onPressed: () {
                            _searchController.clear();
                            _onSearchChanged('');
                          },
                        )
                      : null,
                  border: InputBorder.none,
                  contentPadding:
                      const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
                ),
              ),
            ),
          ),
          const SizedBox(width: 10),
          // Advanced filters button
          GestureDetector(
            onTap: _openAdvancedFilters,
            child: Container(
              width: 48,
              height: 48,
              decoration: BoxDecoration(
                color: _activeFilterCount > 0
                    ? _accent.withValues(alpha: 0.15)
                    : _cardBg,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(
                  color: _activeFilterCount > 0
                      ? _accent.withValues(alpha: 0.4)
                      : _cardBorder,
                ),
              ),
              child: Stack(
                alignment: Alignment.center,
                children: [
                  Icon(
                    Icons.tune,
                    size: 20,
                    color: _activeFilterCount > 0 ? _accent : _textMuted,
                  ),
                  if (_activeFilterCount > 0)
                    Positioned(
                      top: 6,
                      right: 6,
                      child: Container(
                        width: 16,
                        height: 16,
                        decoration: const BoxDecoration(
                          color: _accent,
                          shape: BoxShape.circle,
                        ),
                        child: Center(
                          child: Text(
                            '$_activeFilterCount',
                            style: GoogleFonts.sora(
                              fontSize: 9,
                              color: _textGhost,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  // ── Date Preset Chips ─────────────────────────────────

  Widget _buildDatePresets() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 12, 24, 0),
      child: SizedBox(
        height: 36,
        child: ListView.separated(
          scrollDirection: Axis.horizontal,
          itemCount: _DatePreset.values.length,
          separatorBuilder: (_, __) => const SizedBox(width: 8),
          itemBuilder: (_, i) {
            final preset = _DatePreset.values[i];
            final isSelected = _datePreset == preset;
            return GestureDetector(
              onTap: () => _onDatePresetChanged(preset),
              child: AnimatedContainer(
                duration: const Duration(milliseconds: 200),
                padding:
                    const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
                decoration: BoxDecoration(
                  color: isSelected ? _accent.withValues(alpha: 0.2) : _cardBg,
                  borderRadius: BorderRadius.circular(18),
                  border: Border.all(
                    color: isSelected
                        ? _accent.withValues(alpha: 0.5)
                        : _cardBorder,
                  ),
                ),
                child: Text(
                  preset.label,
                  style: GoogleFonts.sora(
                    fontSize: 12,
                    fontWeight: isSelected ? FontWeight.w600 : FontWeight.w400,
                    color: isSelected ? _accent : _textMuted,
                  ),
                ),
              ),
            );
          },
        ),
      ),
    );
  }

  // ── Active Filter Bar ─────────────────────────────────

  Widget _buildActiveFilterBar() {
    final chips = <Widget>[];
    if (_filter.categoryId != null) {
      chips.add(_FilterChip(label: 'Category', onRemove: () {
        setState(() => _filter = _filter.copyWith(clearCategoryId: true));
        _loadTransactions();
      }));
    }
    if (_filter.accountId != null) {
      chips.add(_FilterChip(label: 'Account', onRemove: () {
        setState(() => _filter = _filter.copyWith(clearAccountId: true));
        _loadTransactions();
      }));
    }
    if (_filter.minAmount != null || _filter.maxAmount != null) {
      final label = _filter.minAmount != null && _filter.maxAmount != null
          ? '$primaryCurrencySymbol${_filter.minAmount!.toInt()}–$primaryCurrencySymbol${_filter.maxAmount!.toInt()}'
          : _filter.minAmount != null
              ? '> $primaryCurrencySymbol${_filter.minAmount!.toInt()}'
              : '< $primaryCurrencySymbol${_filter.maxAmount!.toInt()}';
      chips.add(_FilterChip(label: label, onRemove: () {
        setState(() => _filter = _filter.copyWith(clearMinAmount: true, clearMaxAmount: true));
        _loadTransactions();
      }));
    }

    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 8, 24, 0),
      child: Row(
        children: [
          Icon(Icons.filter_list, size: 14, color: _accent.withValues(alpha: 0.7)),
          const SizedBox(width: 6),
          if (_pagination != null)
            Text(
              AppLocalizations.of(context)
                  .txnResultsCount(_pagination!.totalItems),
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
            ),
          const SizedBox(width: 8),
          Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(children: chips),
            ),
          ),
          const SizedBox(width: 8),
          GestureDetector(
            onTap: _clearAllFilters,
            child: Text(
              AppLocalizations.of(context).txnClearAll,
              style: GoogleFonts.sora(
                fontSize: 12,
                color: _accent,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
        ],
      ),
    );
  }

  // ── Transaction List (grouped by date) ────────────────

  Widget _buildTransactionList() {
    if (_transactions.isEmpty) {
      return SliverFillRemaining(
        hasScrollBody: false,
        child: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.receipt_long_outlined,
                  size: 56, color: _textMuted.withValues(alpha: 0.4)),
              const SizedBox(height: 16),
              Text(
                _filter.hasActiveFilters
                    ? AppLocalizations.of(context).txnNoMatchFilters
                    : AppLocalizations.of(context).txnNoTransactionsYet,
                style: GoogleFonts.sora(
                    fontSize: 16, color: _textMuted, fontWeight: FontWeight.w500),
              ),
              const SizedBox(height: 8),
              Text(
                _filter.hasActiveFilters
                    ? AppLocalizations.of(context).txnTryAdjustingFilters
                    : (ServerEdition.current.value.hasBankConnection
                        ? AppLocalizations.of(context).txnConnectBankToStart
                        : AppLocalizations.of(context).txnImportToStart),
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted.withValues(alpha: 0.6)),
              ),
            ],
          ),
        ),
      );
    }

    return SliverPadding(
      padding: const EdgeInsets.fromLTRB(24, 16, 24, 0),
      sliver: SliverList(
        delegate: SliverChildBuilderDelegate(
          (context, index) {
            final transaction = _transactions[index];
            final showDateHeader = index == 0 ||
                !_isSameDay(
                    _transactions[index - 1].date, transaction.date);

            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (showDateHeader) _buildDateHeader(transaction.date),
                _TransactionTile(
                  transaction: transaction,
                  onTap: () => _openTransactionDetail(transaction),
                  onSecondaryTapDown: (details) =>
                      _showContextMenu(transaction, details),
                  onEditCategory: () => _openEditCategory(transaction),
                ),
              ],
            );
          },
          childCount: _transactions.length,
        ),
      ),
    );
  }

  Widget _buildDateHeader(DateTime date) {
    return Padding(
      padding: const EdgeInsets.only(top: 16, bottom: 8),
      child: Text(
        formatRelativeDate(date),
        style: GoogleFonts.sora(
          fontSize: 12,
          fontWeight: FontWeight.w600,
          color: _textMuted,
          letterSpacing: 0.5,
        ),
      ),
    );
  }
}

// ── Summary Chip ──────────────────────────────────────────

class _SummaryChip extends StatelessWidget {
  final String label;
  final String value;
  final Color color;

  const _SummaryChip({
    required this.label,
    required this.value,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Expanded(
      child: Container(
        padding: const EdgeInsets.symmetric(vertical: 10, horizontal: 10),
        decoration: BoxDecoration(
          color: color.withValues(alpha: 0.08),
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: color.withValues(alpha: 0.15)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: GoogleFonts.sora(
                  fontSize: 10, color: color.withValues(alpha: 0.7), fontWeight: FontWeight.w500),
            ),
            const SizedBox(height: 2),
            // Auto-shrink the currency value to fit the card width so phones
            // don't end up with "$16,500..." ellipsis. FittedBox preserves
            // the original 13px font on desktop and only scales down when
            // the chip is narrow.
            FittedBox(
              fit: BoxFit.scaleDown,
              alignment: Alignment.centerLeft,
              child: Text(
                value,
                style: GoogleFonts.firaCode(
                    fontSize: 13, color: color, fontWeight: FontWeight.w600),
                maxLines: 1,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

// ── Filter Chip ───────────────────────────────────────────

class _FilterChip extends StatelessWidget {
  final String label;
  final VoidCallback onRemove;

  const _FilterChip({required this.label, required this.onRemove});

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(right: 6),
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: _accent.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(6),
        border: Border.all(color: _accent.withValues(alpha: 0.2)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(label,
              style: GoogleFonts.sora(
                  fontSize: 11, color: _accent, fontWeight: FontWeight.w500)),
          const SizedBox(width: 4),
          GestureDetector(
            onTap: onRemove,
            child: Icon(Icons.close, size: 12, color: _accent.withValues(alpha: 0.7)),
          ),
        ],
      ),
    );
  }
}

// ── Transaction Tile ──────────────────────────────────────

class _TransactionTile extends StatefulWidget {
  final Transaction transaction;
  final VoidCallback onTap;
  final GestureTapDownCallback? onSecondaryTapDown;
  final VoidCallback? onEditCategory;

  const _TransactionTile({
    required this.transaction,
    required this.onTap,
    this.onSecondaryTapDown,
    this.onEditCategory,
  });

  @override
  State<_TransactionTile> createState() => _TransactionTileState();
}

class _TransactionTileState extends State<_TransactionTile> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final tx = widget.transaction;
    final isIncome = tx.isIncome;
    final amountColor = isIncome ? _income : _expense;
    final amountPrefix = isIncome ? '+' : '-';
    final displayAmount = tx.amount.abs();
    final merchant = tx.displayTitle;

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        onTap: widget.onTap,
        onSecondaryTapDown: widget.onSecondaryTapDown,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 150),
          margin: const EdgeInsets.only(bottom: 2),
          padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 14),
          decoration: BoxDecoration(
            color: _hovered
                ? _accent.withValues(alpha: 0.04)
                : _cardBg,
            borderRadius: BorderRadius.circular(10),
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
          child: Row(
            children: [
              // Category icon
              _CategoryIcon(
                categoryName: tx.categoryName,
                categoryColor: tx.categoryColor,
                isPending: tx.isPending,
                isEarmarkedToGoal: tx.isEarmarkedToGoal,
              ),
              const SizedBox(width: 12),
              // Merchant + category + badges
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      merchant,
                      style: GoogleFonts.sora(
                        fontSize: 14,
                        fontWeight: FontWeight.w500,
                        color: _textGhost,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                    const SizedBox(height: 3),
                    Row(
                      children: [
                        // Category name (tappable to edit)
                        GestureDetector(
                          onTap: widget.onEditCategory,
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              // Earmarked money names its goal instead of a category —
                              // the two are mutually exclusive, and "Shopping" on money
                              // that went into a savings goal reads as a miscategorization.
                              if (tx.isEarmarkedToGoal)
                                const Padding(
                                  padding: EdgeInsets.only(right: 4),
                                  child: Icon(Icons.savings_outlined,
                                      size: 11, color: _income),
                                )
                              else if (tx.categoryColor != null)
                                Container(
                                  width: 6,
                                  height: 6,
                                  margin: const EdgeInsets.only(right: 4),
                                  decoration: BoxDecoration(
                                    color: _parseCategoryColor(tx.categoryColor),
                                    shape: BoxShape.circle,
                                  ),
                                ),
                              // Constrained so a long goal name ellipsizes rather than
                              // pushing the badges off the row.
                              ConstrainedBox(
                                constraints: const BoxConstraints(maxWidth: 160),
                                child: Text(
                                  tx.isEarmarkedToGoal
                                      ? (tx.savingsGoalName ??
                                          AppLocalizations.of(context)
                                              .txnSavingsGoalFallback)
                                      : tx.categoryName,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: GoogleFonts.sora(
                                    fontSize: 11,
                                    color:
                                        tx.isEarmarkedToGoal ? _income : _textMuted,
                                    decoration: TextDecoration.underline,
                                    decorationColor: (tx.isEarmarkedToGoal
                                            ? _income
                                            : _textMuted)
                                        .withValues(alpha: 0.3),
                                    decorationStyle: TextDecorationStyle.dotted,
                                  ),
                                ),
                              ),
                            ],
                          ),
                        ),
                        // Badges — reviewed and pending-review are mutually exclusive:
                        // if backend returns both, treat as reviewed.
                        if (tx.isUserCategorized) ...[
                          const SizedBox(width: 6),
                          const _MicroBadge(label: 'Reviewed', color: _income),
                        ],
                        if (tx.isPending) ...[
                          const SizedBox(width: 6),
                          _MicroBadge(label: 'Pending', color: _pending),
                        ],
                        if (tx.isRecurring) ...[
                          const SizedBox(width: 6),
                          _MicroBadge(label: 'Recurring', color: _accent),
                        ],
                        if (tx.isAICategorized) ...[
                          const SizedBox(width: 6),
                          AiBadge(confidence: tx.aiConfidence),
                        ],
                        if (tx.needsReview && !tx.isAICategorized && !tx.isUserCategorized) ...[
                          const SizedBox(width: 6),
                          const _ReviewDot(),
                        ],
                        if (tx.isSplit) ...[
                          const SizedBox(width: 6),
                          Icon(Icons.call_split,
                              size: 12,
                              color: _textMuted.withValues(alpha: 0.6)),
                        ],
                        if (tx.userNotes != null && tx.userNotes!.isNotEmpty) ...[
                          const SizedBox(width: 6),
                          Icon(Icons.sticky_note_2_outlined,
                              size: 12,
                              color: _textMuted.withValues(alpha: 0.5)),
                        ],
                      ],
                    ),
                  ],
                ),
              ),
              // Amount + date
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text(
                    '$amountPrefix${formatCurrency(displayAmount)}',
                    style: GoogleFonts.firaCode(
                      fontSize: 14,
                      fontWeight: FontWeight.w600,
                      color: amountColor,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    formatShortDate(tx.date),
                    style: GoogleFonts.sora(fontSize: 10, color: _textMuted),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                  ),
                ],
              ),
              // Action arrow on hover
              if (_hovered) ...[
                const SizedBox(width: 8),
                Icon(Icons.chevron_right,
                    size: 16, color: _accent.withValues(alpha: 0.5)),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

// ── Micro Badge ───────────────────────────────────────────

class _MicroBadge extends StatelessWidget {
  final String label;
  final Color color;

  const _MicroBadge({required this.label, required this.color});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(4),
      ),
      child: Text(
        label,
        style: GoogleFonts.sora(
            fontSize: 9, color: color, fontWeight: FontWeight.w600),
      ),
    );
  }
}

// ── Review Dot (small yellow indicator) ──────────────────

class _ReviewDot extends StatelessWidget {
  const _ReviewDot();

  @override
  Widget build(BuildContext context) {
    return Container(
      width: 8,
      height: 8,
      decoration: BoxDecoration(
        color: _pending.withValues(alpha: 0.85),
        shape: BoxShape.circle,
        boxShadow: [
          BoxShadow(
            color: _pending.withValues(alpha: 0.3),
            blurRadius: 4,
          ),
        ],
      ),
    );
  }
}

// ── AI Badge (moved to widgets/ai_sparkle.dart — see #163) ──────────────

// ── Category Icon ─────────────────────────────────────────

class _CategoryIcon extends StatelessWidget {
  final String categoryName;
  final String? categoryColor;
  final bool isPending;

  /// Money assigned to a savings goal rather than spent on a category. A
  /// transaction is one or the other, never both, so the piggy bank replaces the
  /// category icon outright — showing e.g. a shopping bag for money that went into
  /// an emergency fund is worse than showing nothing.
  final bool isEarmarkedToGoal;

  const _CategoryIcon({
    required this.categoryName,
    this.categoryColor,
    this.isPending = false,
    this.isEarmarkedToGoal = false,
  });

  @override
  Widget build(BuildContext context) {
    final icon =
        isEarmarkedToGoal ? Icons.savings_outlined : _iconForCategory(categoryName);
    final color = isEarmarkedToGoal
        ? _income
        : isPending
            ? _pending
            : categoryColor != null
                ? _parseCategoryColor(categoryColor)
                : _accent;
    return Container(
      width: 36,
      height: 36,
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(8),
      ),
      child: Icon(icon, size: 18, color: color.withValues(alpha: 0.8)),
    );
  }

  static IconData _iconForCategory(String name) {
    final lower = name.toLowerCase();
    if (lower.contains('grocer')) return Icons.shopping_cart_outlined;
    if (lower.contains('dining') || lower.contains('food') || lower.contains('restaurant')) return Icons.restaurant_outlined;
    if (lower.contains('transport') || lower.contains('gas') || lower.contains('fuel')) return Icons.directions_car_outlined;
    if (lower.contains('entertain')) return Icons.movie_outlined;
    if (lower.contains('shop')) return Icons.shopping_bag_outlined;
    if (lower.contains('health') || lower.contains('medical')) return Icons.local_hospital_outlined;
    if (lower.contains('travel') || lower.contains('hotel') || lower.contains('airline')) return Icons.flight_outlined;
    if (lower.contains('education')) return Icons.school_outlined;
    if (lower.contains('subscription') || lower.contains('software')) return Icons.subscriptions_outlined;
    if (lower.contains('rent') || lower.contains('mortgage')) return Icons.home_outlined;
    if (lower.contains('utilit')) return Icons.bolt_outlined;
    if (lower.contains('insurance')) return Icons.shield_outlined;
    if (lower.contains('loan') || lower.contains('payment')) return Icons.account_balance_outlined;
    if (lower.contains('salary') || lower.contains('income') || lower.contains('payroll')) return Icons.attach_money;
    if (lower.contains('invest')) return Icons.trending_up_outlined;
    if (lower.contains('transfer')) return Icons.swap_horiz_outlined;
    if (lower.contains('gift')) return Icons.card_giftcard_outlined;
    if (lower.contains('personal') || lower.contains('care')) return Icons.spa_outlined;
    if (lower.contains('uncategorized')) return Icons.help_outline;
    return Icons.receipt_outlined;
  }
}

Color _parseCategoryColor(String? hex) {
  if (hex == null || hex.isEmpty) return _accent;
  try {
    final clean = hex.replaceFirst('#', '');
    return Color(int.parse('FF$clean', radix: 16));
  } catch (_) {
    return _accent;
  }
}

// ═══════════════════════════════════════════════════════════════
//   Transaction Review Sheet
// ═══════════════════════════════════════════════════════════════

class _TransactionReviewSheet extends StatefulWidget {
  final VoidCallback onComplete;
  const _TransactionReviewSheet({required this.onComplete});
  @override
  State<_TransactionReviewSheet> createState() => _TransactionReviewSheetState();
}

class _TransactionReviewSheetState extends State<_TransactionReviewSheet> {
  List<Transaction> _queue = [];
  int _currentIndex = 0;
  bool _isLoading = true;
  bool _isProcessingQueue = false;
  bool _isSaving = false;
  bool _isSkipping = false;
  int _reviewedCount = 0;
  bool _showCelebration = false;

  // #737: default the review queue to the current month so it isn't overwhelming; when the
  // month is cleared, offer the older backlog explicitly so users can still clear everything.
  ReviewScope _scope = ReviewScope.thisMonth;
  bool _offerOlder = false;
  ReviewStatistics? _stats;

  // Transfer-confirmation step — runs before categorization so the highest-impact fixes
  // (transfers wrongly counted as spending) are handled first.
  List<SuspectedTransfer> _transfers = [];
  bool _transfersLoading = true;
  bool _transfersDone = false;
  bool _transfersChanged = false;
  final Set<int> _transferBusy = {};

  // Transactions the user has sorted/skipped THIS session — never re-surface them
  // when a background enrichment refresh comes back.
  final Set<int> _handledIds = {};
  // Ids we've already asked the AI to enrich, so we don't re-request ones that came
  // back high-confidence (auto-applied, dropped from needs-review) and loop forever.
  final Set<int> _enrichAttempted = {};

  // Category selection
  List<Category> _allCategories = [];
  int? _selectedSystemCategoryId;
  int? _selectedCustomCategoryId;

  // Savings goals — a review card can earmark straight to a goal instead of a
  // category, matching the transaction detail picker. Without this the only way
  // to pull a transaction from savings was to leave the review flow entirely.
  List<SavingsGoal> _savingsGoals = [];
  int? _selectedSavingsGoalId;

  @override
  void initState() {
    super.initState();
    _loadQueue();
    _loadCategories();
    _loadTransfers();
    _loadSavingsGoals();
  }

  Future<void> _loadSavingsGoals() async {
    try {
      final goals = await savingsService.getGoals();
      if (!mounted) return;
      setState(() => _savingsGoals = goals.where((g) => g.canContribute).toList());
    } catch (_) {
      // Non-fatal: the Savings Goals group is simply hidden.
    }
  }

  Future<void> _loadTransfers() async {
    try {
      final transfers = await transactionsService.getSuspectedTransfers();
      if (mounted) {
        setState(() {
          _transfers = transfers;
          _transfersLoading = false;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _transfersLoading = false);
    }
  }

  bool get _showTransfersStep => !_transfersDone && _transfers.isNotEmpty;

  Future<void> _actTransfer(SuspectedTransfer t, bool confirm) async {
    final key = t.transactionIds.isNotEmpty ? t.transactionIds.first : -1;
    setState(() => _transferBusy.add(key));
    final ok = confirm
        ? await transactionsService.confirmSuspectedTransfer(t.transactionIds)
        : await transactionsService.dismissSuspectedTransfer(t.transactionIds);
    if (!mounted) return;
    setState(() {
      _transferBusy.remove(key);
      if (ok) {
        _transfersChanged = true;
        _transfers.remove(t);
        if (_transfers.isEmpty) _transfersDone = true; // auto-advance to categorization
      }
    });
    if (ok) {
      showAppToast(
        context,
        confirm ? 'Marked as a transfer.' : 'Kept as-is.',
        type: AppToastType.success,
      );
    } else {
      // The service swallows 4xx/partial-confirm/parse failures and returns
      // false; surface it so the card no longer silently reappears with no
      // explanation (and the user doesn't keep tapping a failing action).
      showAppToast(
        context,
        "Couldn't update that transfer — please try again.",
        type: AppToastType.error,
      );
    }
  }

  Future<void> _loadCategories() async {
    try {
      final response = await categoriesService.getAll();
      if (mounted) setState(() => _allCategories = response.allCategories);
    } catch (_) {}
  }

  Future<void> _loadQueue() async {
    setState(() { _isLoading = true; _showCelebration = false; _offerOlder = false; });
    try {
      // Refresh the split so we know whether an "older" backlog exists to offer later.
      _stats = await aiService.getReviewStatistics();

      // Check for pending AI work up front so the empty state can say "analyzing"
      // (rather than flashing "All Caught Up") while suggestions are still coming in.
      final queueCount = await aiService.getQueuedCount();
      if (!mounted) return;
      if (queueCount > 0) setState(() => _isProcessingQueue = true);

      // Show whatever ALREADY needs review immediately — never block the whole modal
      // on the AI queue draining first (that made this button feel slower/worse than
      // the inline auto-categorize action). The user can start reviewing right away.
      final initial = await aiService.getTransactionsNeedingReview(limit: 50, scope: _scope);
      if (!mounted) return;
      setState(() {
        _queue = initial.where(_stillNeedsDeciding).toList();
        _isLoading = false;
        _currentIndex = 0;
        _reviewedCount = 0;
      });
      _syncCategorySelection();

      // Background work (non-blocking): the modal is already usable above.
      final needsEnrichment = _queue.any((t) => t.aiSuggestedCategoryId == null);
      if (queueCount > 0 || needsEnrichment) {
        setState(() => _isProcessingQueue = true);

        // 1) Drain any freshly-synced items sitting in the AI queue, merging new
        //    review items in as each batch lands. Only with AI consent: draining sends
        //    transaction details to Gemini, and the server refuses it otherwise (5.1.2(i)).
        if (queueCount > 0 && hasAiConsent(context)) {
          bool hasMore = true;
          while (hasMore && mounted) {
            final result = await aiService.processQueue(batchSize: 20);
            hasMore = result.hasMore;
            final refreshed = await aiService.getTransactionsNeedingReview(limit: 50, scope: _scope);
            if (!mounted) return;
            setState(() => _mergeIntoQueue(refreshed));
          }
        }

        // 2) Proactively enrich queued items that still lack an AI suggestion, a small
        //    batch at a time, streaming the richer data into the cards.
        await _enrichQueueSuggestions();

        if (mounted) setState(() => _isProcessingQueue = false);
      }
    } catch (_) {
      if (mounted) setState(() { _isLoading = false; _isProcessingQueue = false; });
    }
  }

  /// Whether the review card should still ask about this transaction.
  ///
  /// The server already filters these out; this is defence in depth for a queue held in
  /// memory while the underlying transaction changes. An earmarked transaction must never
  /// reach the card: the only action it offers is assigning a category, which reverses the
  /// earmark and destroys work the user deliberately did.
  static bool _stillNeedsDeciding(Transaction t) =>
      t.savingsGoalId == null && !t.isUserCategorized;

  /// Appends review items from a background refresh that aren't already in the queue and
  /// that the user hasn't already handled this session — without disturbing the current
  /// position or reviewed count. If the queue was empty (user on the analyzing
  /// placeholder), points the cursor at the first arrival.
  void _mergeIntoQueue(List<Transaction> incoming) {
    final existing = _queue.map((t) => t.id).toSet();
    final additions = incoming
        .where((t) =>
            !existing.contains(t.id) &&
            !_handledIds.contains(t.id) &&
            _stillNeedsDeciding(t))
        .toList();
    if (additions.isEmpty) return;
    final wasEmpty = _queue.isEmpty;
    _queue.addAll(additions);
    if (wasEmpty) {
      _currentIndex = 0;
      _syncCategorySelection();
    }
  }

  /// Proactively asks the AI to categorize queued transactions that don't yet have a
  /// suggestion, a small batch at a time, and patches the returned suggestions into the
  /// existing cards. Skips anything the user has already handled, and never re-requests
  /// an id (so high-confidence ones that auto-applied and left the queue don't loop).
  Future<void> _enrichQueueSuggestions() async {
    const batchSize = 8;
    while (mounted) {
      final pending = _queue
          .where((t) =>
              t.aiSuggestedCategoryId == null &&
              !_handledIds.contains(t.id) &&
              !_enrichAttempted.contains(t.id))
          .map((t) => t.id)
          .take(batchSize)
          .toList();
      if (pending.isEmpty) break;
      _enrichAttempted.addAll(pending);
      try {
        await aiService.categorizeByIds(pending);
      } catch (_) {
        // leave these marked attempted and move on to the next batch
      }
      if (!mounted) return;
      final refreshed = await aiService.getTransactionsNeedingReview(limit: 50, scope: _scope);
      if (!mounted) return;
      setState(() => _patchSuggestions(refreshed));
    }
  }

  /// Copies freshly-generated AI suggestions onto the matching cards already in the
  /// queue, in place — no reordering, and never onto items the user already handled.
  void _patchSuggestions(List<Transaction> refreshed) {
    final byId = {for (final t in refreshed) t.id: t};
    for (var i = 0; i < _queue.length; i++) {
      final cur = _queue[i];
      if (_handledIds.contains(cur.id)) continue;
      final updated = byId[cur.id];
      if (updated != null &&
          cur.aiSuggestedCategoryId == null &&
          updated.aiSuggestedCategoryId != null) {
        _queue[i] = updated;
        if (i == _currentIndex) _syncCategorySelection();
      }
    }
  }

  Transaction? get _current =>
      _queue.isNotEmpty && _currentIndex < _queue.length ? _queue[_currentIndex] : null;

  int get _progressPercent =>
      _queue.isNotEmpty ? ((_reviewedCount / _queue.length) * 100).round() : 0;

  String get _encouragingMessage {
    if (_queue.isEmpty) return '';
    final remaining = _queue.length - _reviewedCount;
    if (_progressPercent == 0) return "Let's get started! 💪";
    if (_progressPercent < 25) return 'Great start! Keep going! 🚀';
    if (_progressPercent < 50) return "You're doing amazing! 🌟";
    if (_progressPercent < 75) return 'Over halfway there! 🎯';
    if (_progressPercent < 100) return 'Almost done! Just $remaining left! 🏆';
    return 'All done! 🎉';
  }

  Color get _progressColor {
    if (_progressPercent < 25) return _accent;
    if (_progressPercent < 50) return const Color(0xFF06B6D4);
    if (_progressPercent < 75) return const Color(0xFF14B8A6);
    return _income;
  }

  void _syncCategorySelection() {
    final tx = _current;
    if (tx == null) return;
    // Default to AI suggestion, fallback to current category
    setState(() {
      _selectedSystemCategoryId = tx.aiSuggestedCategoryId ?? tx.categoryId;
      _selectedCustomCategoryId = null;
      _selectedSavingsGoalId = null;
    });
  }

  Color _confidenceColor(double? confidence) {
    if (confidence == null) return _accent;
    if (confidence == 0) return _textMuted;
    if (confidence >= 0.8) return _income;
    if (confidence >= 0.5) return _pending;
    return _expense;
  }

  String _confidenceLabel(double? confidence) {
    if (confidence == null) return 'Pending';
    if (confidence == 0) return 'Manual';
    if (confidence >= 0.8) return 'High';
    if (confidence >= 0.5) return 'Medium';
    return 'Low';
  }

  /// Suggested categories: AI suggestion + current + common for expense/income
  List<int> _getSuggestedCategoryIds() {
    final tx = _current;
    if (tx == null) return [];
    final suggestions = <int>{};
    final ai = tx.aiSuggestedCategoryId;
    if (ai != null && ai != 0) suggestions.add(ai);
    if (tx.categoryId != null && tx.categoryId != 0) suggestions.add(tx.categoryId!);
    // Common defaults
    if (tx.isIncome) {
      suggestions.addAll([1, 2, 9]); // Salary, Bonus, OtherIncome
    } else {
      suggestions.addAll([20, 21, 22, 23]); // Groceries, DiningOut, Shopping, Transportation
    }
    return suggestions.take(4).toList();
  }

  String _categoryName(int id) {
    final cat = _allCategories.where((c) => c.id == id).firstOrNull;
    return cat?.name ?? 'Category $id';
  }

  // ── Actions ──

  Future<void> _handleAccept() async {
    final tx = _current;
    if (tx == null || _isSaving) return;

    // ── Savings goal selected → earmark instead of categorizing ──
    // Confirmed first (it moves real money against the goal), mirroring the
    // transaction detail picker.
    final goalId = _selectedSavingsGoalId;
    if (goalId != null) {
      final goal = _savingsGoals.where((g) => g.id == goalId).firstOrNull;
      if (goal == null) return;
      final confirmed = await _confirmEarmark(tx, goal);
      if (confirmed != true || !mounted) return;

      setState(() => _isSaving = true);
      try {
        await transactionsService.markSavingsWithdrawal(tx.id, goalId);
        _handledIds.add(tx.id);
        _advance();
      } catch (e) {
        if (mounted) {
          showAppToast(
            context,
            apiErrorMessage(e, fallback: 'Failed to earmark to savings goal'),
            type: AppToastType.error,
          );
        }
      } finally {
        if (mounted) setState(() => _isSaving = false);
      }
      return;
    }

    setState(() => _isSaving = true);
    try {
      if (_selectedCustomCategoryId != null) {
        // User selected a custom category
        await transactionsService.updateTransactionCategory(
          tx.id, categoryId: _selectedCustomCategoryId,
        );
      } else if (_selectedSystemCategoryId == tx.aiSuggestedCategoryId) {
        await aiService.acceptAISuggestion(tx.id);
      } else if (_selectedSystemCategoryId != null) {
        await aiService.rejectAISuggestion(tx.id, _selectedSystemCategoryId!);
      }
      _handledIds.add(tx.id); // sorted this session — don't let enrichment re-surface it
      _advance();
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          apiErrorMessage(e, fallback: 'Failed to save'),
          type: AppToastType.error,
        );
      }
    } finally {
      if (mounted) setState(() => _isSaving = false);
    }
  }

  /// Direction-aware confirmation before earmarking to a savings goal — income
  /// deposits into the goal, an expense withdraws from it. Same wording as the
  /// transaction detail picker so the action reads identically in both places.
  Future<bool?> _confirmEarmark(Transaction tx, SavingsGoal goal) {
    final amount = formatCurrency(tx.amount.abs());
    final title = tx.isIncome
        ? AppLocalizations.of(context).txnEarmarkDepositTitle(amount, goal.name)
        : AppLocalizations.of(context).txnEarmarkPayTitle(amount, goal.name);
    final message = tx.isIncome
        ? AppLocalizations.of(context).txnEarmarkDepositBody(goal.name)
        : AppLocalizations.of(context)
            .txnEarmarkPayBody(tx.categoryName, goal.name);
    return showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: const Color(0xFF0C0C18),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: const BorderSide(color: _cardBorder),
        ),
        title: Text(title,
            style: GoogleFonts.sora(
                fontSize: 16,
                color: _textGhost,
                fontWeight: FontWeight.w600)),
        content: Text(message,
            style: GoogleFonts.sora(
                fontSize: 13, color: _textMuted, height: 1.4)),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(AppLocalizations.of(ctx).txnCancel,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          ),
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(
                tx.isIncome
                    ? AppLocalizations.of(ctx).txnDeposit
                    : AppLocalizations.of(ctx).txnPay,
                style: GoogleFonts.sora(
                    fontSize: 13,
                    color: _accent,
                    fontWeight: FontWeight.w600)),
          ),
        ],
      ),
    );
  }

  Future<void> _handleSkipTemporarily() async {
    final tx = _current;
    if (tx == null || _isSkipping) return;
    setState(() => _isSkipping = true);
    try {
      await aiService.skipTransaction(tx.id);
      // Remove from local list
      setState(() {
        _handledIds.add(tx.id); // dismissed this session — keep enrichment from re-adding it
        _queue.removeAt(_currentIndex);
        if (_currentIndex >= _queue.length && _queue.isNotEmpty) {
          _currentIndex = _queue.length - 1;
        }
      });
      if (_queue.isEmpty) {
        widget.onComplete();
        if (mounted) Navigator.of(context).pop();
      } else {
        _syncCategorySelection();
        if (mounted) {
          showAppToast(context, 'Skipped — will reappear in 2 days');
        }
      }
    } catch (_) {}
    finally {
      if (mounted) setState(() => _isSkipping = false);
    }
  }

  void _handleNext() {
    if (_currentIndex < _queue.length - 1) {
      setState(() => _currentIndex++);
      _syncCategorySelection();
    }
  }

  void _handlePrevious() {
    if (_currentIndex > 0) {
      if (_reviewedCount > _currentIndex) {
        setState(() => _reviewedCount = _currentIndex);
      }
      setState(() => _currentIndex--);
      _syncCategorySelection();
    }
  }

  void _advance() {
    setState(() => _reviewedCount++);
    if (_currentIndex < _queue.length - 1) {
      setState(() => _currentIndex++);
      _syncCategorySelection();
    } else {
      // #737: end of the current in-scope batch — top up / switch to older / finish,
      // rather than celebrating while more transactions still need review.
      _handleQueueDrained();
    }
  }

  /// End of the current batch: pull the next page of this scope (handled items have dropped
  /// out server-side, so a refetch returns the next ones). If the scope is truly clear and
  /// this was the current month, offer the older backlog; otherwise celebrate + close.
  Future<void> _handleQueueDrained() async {
    try {
      final more = await aiService.getTransactionsNeedingReview(limit: 50, scope: _scope);
      final fresh = more.where((t) => !_handledIds.contains(t.id)).toList();
      if (fresh.isNotEmpty) {
        if (!mounted) return;
        setState(() {
          _queue = fresh;
          _currentIndex = 0;
          _reviewedCount = 0;
        });
        _syncCategorySelection();
        return;
      }
    } catch (_) {}

    if (!mounted) return;
    final older = _stats?.olderCount ?? 0;
    if (_scope == ReviewScope.thisMonth && older > 0) {
      setState(() => _offerOlder = true); // month is clear — offer the historical backlog
      return;
    }
    _celebrateAndClose();
  }

  void _reviewOlder() {
    setState(() {
      _scope = ReviewScope.older;
      _offerOlder = false;
      _handledIds.clear(); // older items are a fresh set (different dates)
    });
    _loadQueue();
  }

  void _celebrateAndClose() {
    setState(() => _showCelebration = true);
    Future.delayed(const Duration(seconds: 4), () {
      if (mounted) {
        widget.onComplete();
        Navigator.of(context).pop();
      }
    });
  }

  void _handleClose() {
    if (_reviewedCount > 0 || _transfersChanged) widget.onComplete();
    Navigator.of(context).pop();
  }

  // Category and savings-goal selection are mutually exclusive: a transaction is
  // either budgeted to a category or earmarked to a goal, never both.
  void _selectSystemCategory(int id) {
    setState(() {
      _selectedSystemCategoryId = id;
      _selectedCustomCategoryId = null;
      _selectedSavingsGoalId = null;
    });
  }

  void _selectCustomCategory(int id) {
    setState(() {
      _selectedCustomCategoryId = id;
      _selectedSystemCategoryId = null;
      _selectedSavingsGoalId = null;
    });
  }

  void _selectSavingsGoal(int id) {
    setState(() {
      _selectedSavingsGoalId = id;
      _selectedSystemCategoryId = null;
      _selectedCustomCategoryId = null;
    });
  }

  // ── Build ──

  @override
  Widget build(BuildContext context) {
    return DraggableScrollableSheet(
      initialChildSize: 0.85,
      minChildSize: 0.4,
      maxChildSize: 0.95,
      builder: (_, scrollController) => Container(
        decoration: const BoxDecoration(
          color: Color(0xFF0C0C18),
          borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        ),
        child: _showCelebration
            ? _buildCelebration()
            : _showTransfersStep
                ? _buildTransfersPhase(scrollController)
                : Column(
                    children: [
                      _buildHeader(),
                      const Divider(color: _cardBorder, height: 1),
                      Expanded(
                        child: (_isLoading || _transfersLoading)
                            ? _buildLoading()
                            : _offerOlder
                                ? _buildOfferOlder()
                                : _queue.isEmpty
                                    ? (_isProcessingQueue ? _buildAnalyzing() : _buildEmpty())
                                    : _current != null
                                        ? _buildReviewContent(scrollController)
                                        : const SizedBox.shrink(),
                      ),
                      if (!_isLoading &&
                          !_transfersLoading &&
                          !_offerOlder &&
                          _queue.isNotEmpty &&
                          _current != null)
                        _buildFooter(),
                    ],
                  ),
      ),
    );
  }

  // #737: shown when the current month's review queue is cleared but older items remain,
  // so the user can page into the backlog instead of seeing a premature "All Caught Up".
  Widget _buildOfferOlder() {
    final older = _stats?.olderCount ?? 0;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.check_circle_outline, size: 56, color: _income),
            const SizedBox(height: 16),
            Text(AppLocalizations.of(context).txnMonthCaughtUp,
                textAlign: TextAlign.center,
                style: GoogleFonts.sora(fontSize: 18, fontWeight: FontWeight.w600, color: _textGhost)),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context).txnOlderStillNeedReview(older),
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
            ),
            const SizedBox(height: 24),
            SizedBox(
              width: double.infinity,
              child: ElevatedButton.icon(
                onPressed: _reviewOlder,
                icon: const Icon(Icons.history, size: 18),
                label: Text(
                    AppLocalizations.of(context).txnReviewOlderButton(older)),
                style: ElevatedButton.styleFrom(
                  backgroundColor: _accent,
                  foregroundColor: Colors.white,
                  padding: const EdgeInsets.symmetric(vertical: 14),
                ),
              ),
            ),
            const SizedBox(height: 8),
            TextButton(
              onPressed: _handleClose,
              child: Text(AppLocalizations.of(context).txnDoneForNow,
                  style: GoogleFonts.sora(color: _textMuted)),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildHeader() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 12, 16, 12),
      child: Column(
        children: [
          // Drag handle
          Center(
            child: Container(
              margin: const EdgeInsets.only(bottom: 12),
              width: 40, height: 4,
              decoration: BoxDecoration(
                color: _textMuted.withValues(alpha: 0.3),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          // Title row
          Row(
            children: [
              const Icon(Icons.auto_awesome, size: 20, color: _accent),
              const SizedBox(width: 8),
              Text(AppLocalizations.of(context).txnReviewAiCategories,
                  style: GoogleFonts.sora(fontSize: 18, fontWeight: FontWeight.bold, color: _textGhost)),
              const Spacer(),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Text(
                  AppLocalizations.of(context).txnPositionOfTotal(
                      _currentIndex + (_queue.isEmpty ? 0 : 1), _queue.length),
                  style: GoogleFonts.firaCode(fontSize: 12, color: _accent),
                ),
              ),
              const SizedBox(width: 4),
              IconButton(
                icon: const Icon(Icons.close, color: _textMuted, size: 20),
                onPressed: _handleClose,
              ),
            ],
          ),
          const SizedBox(height: 10),
          // Progress section
          if (_queue.isNotEmpty) ...[
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: _progressPercent >= 75
                    ? _income.withValues(alpha: 0.08)
                    : _cardBg,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(
                  color: _progressPercent >= 75
                      ? _income.withValues(alpha: 0.2)
                      : _cardBorder,
                ),
              ),
              child: Column(
                children: [
                  Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      Flexible(
                        child: Text(_encouragingMessage,
                            style: GoogleFonts.sora(fontSize: 12,
                                fontWeight: FontWeight.w500,
                                color: _progressPercent >= 75 ? _income : _textGhost)),
                      ),
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                        decoration: BoxDecoration(
                          color: _progressColor.withValues(alpha: 0.15),
                          borderRadius: BorderRadius.circular(6),
                        ),
                        child: Text('$_progressPercent%',
                            style: GoogleFonts.firaCode(fontSize: 11, color: _progressColor)),
                      ),
                    ],
                  ),
                  const SizedBox(height: 8),
                  ClipRRect(
                    borderRadius: BorderRadius.circular(4),
                    child: LinearProgressIndicator(
                      value: _queue.isNotEmpty ? _reviewedCount / _queue.length : 0,
                      backgroundColor: _cardBg,
                      color: _progressColor,
                      minHeight: 6,
                    ),
                  ),
                  const SizedBox(height: 6),
                  Row(
                    children: [
                      Text(
                          AppLocalizations.of(context)
                              .txnReviewedCount(_reviewedCount),
                          style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
                      const SizedBox(width: 8),
                      Text(
                          AppLocalizations.of(context).txnRemainingCount(
                              _queue.length - _reviewedCount),
                          style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
                    ],
                  ),
                ],
              ),
            ),
          ],
          // Queue processing indicator
          if (_isProcessingQueue) ...[
            const SizedBox(height: 8),
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: _accent.withValues(alpha: 0.2)),
              ),
              child: Row(
                children: [
                  const SizedBox(
                    width: 16, height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2, color: _accent),
                  ),
                  const SizedBox(width: 10),
                  Text(AppLocalizations.of(context).txnAiAnalyzing,
                      style: GoogleFonts.sora(fontSize: 12, fontWeight: FontWeight.w500, color: _accent)),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildLoading() {
    return const Center(child: CircularProgressIndicator(color: _accent));
  }

  // ── Transfer confirmation step (before categorization) ──

  static const _transferAccent = Color(0xFFF59E0B); // amber — "review this"

  Widget _buildTransfersPhase(ScrollController scrollController) {
    // While the categorization queue is still loading we can't yet know if there's a next
    // step, so assume there is and let the empty-state handle the "nothing left" case.
    final moreToDo = _isLoading || _queue.isNotEmpty;
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 12, 16, 12),
          child: Column(
            children: [
              Center(
                child: Container(
                  margin: const EdgeInsets.only(bottom: 12),
                  width: 40,
                  height: 4,
                  decoration: BoxDecoration(
                    color: _textMuted.withValues(alpha: 0.3),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
              ),
              Row(
                children: [
                  const Icon(Icons.swap_horiz, size: 20, color: _transferAccent),
                  const SizedBox(width: 8),
                  Text(AppLocalizations.of(context).txnConfirmTransfers,
                      style: GoogleFonts.sora(
                          fontSize: 18, fontWeight: FontWeight.bold, color: _textGhost)),
                  const Spacer(),
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                    decoration: BoxDecoration(
                      color: _transferAccent.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: Text(
                        AppLocalizations.of(context)
                            .txnTransfersLeft(_transfers.length),
                        style: GoogleFonts.firaCode(fontSize: 12, color: _transferAccent)),
                  ),
                  const SizedBox(width: 4),
                  IconButton(
                    icon: const Icon(Icons.close, color: _textMuted, size: 20),
                    onPressed: _handleClose,
                  ),
                ],
              ),
            ],
          ),
        ),
        const Divider(color: _cardBorder, height: 1),
        Expanded(
          child: ListView(
            controller: scrollController,
            padding: const EdgeInsets.fromLTRB(24, 16, 24, 8),
            children: [
              Text(
                AppLocalizations.of(context).txnTransfersExplainer,
                style: GoogleFonts.sora(fontSize: 12.5, color: _textMuted),
              ),
              const SizedBox(height: 12),
              ..._transfers.map(_buildTransferCard),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(24, 8, 24, 20),
          child: SizedBox(
            width: double.infinity,
            child: TextButton(
              onPressed: () {
                if (moreToDo) {
                  setState(() => _transfersDone = true);
                } else {
                  _handleClose();
                }
              },
              child: Text(
                moreToDo
                    ? AppLocalizations.of(context).txnSkipContinueCategorize
                    : AppLocalizations.of(context).txnDone,
                style: GoogleFonts.sora(
                    fontSize: 13, color: _textMuted, fontWeight: FontWeight.w600),
              ),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildTransferCard(SuspectedTransfer t) {
    final key = t.transactionIds.isNotEmpty ? t.transactionIds.first : -1;
    final busy = _transferBusy.contains(key);
    return Container(
      margin: const EdgeInsets.only(bottom: 10),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _cardBorder),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(t.reason, style: GoogleFonts.sora(fontSize: 12.5, color: _textGhost)),
          const SizedBox(height: 10),
          ...t.sides.map((s) => Padding(
                padding: const EdgeInsets.symmetric(vertical: 2),
                child: Row(
                  children: [
                    Icon(s.amount < 0 ? Icons.north_east : Icons.south_west,
                        size: 13, color: _textMuted),
                    const SizedBox(width: 6),
                    Expanded(
                      child: Text('${s.merchantName} · ${s.accountName}',
                          overflow: TextOverflow.ellipsis,
                          style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                    ),
                    Text(formatCurrency(s.amount.abs()),
                        style: GoogleFonts.firaCode(
                            fontSize: 12, fontWeight: FontWeight.w600, color: _textGhost)),
                  ],
                ),
              )),
          const SizedBox(height: 12),
          Row(
            mainAxisAlignment: MainAxisAlignment.end,
            children: [
              TextButton(
                onPressed: busy ? null : () => _actTransfer(t, false),
                child: Text(AppLocalizations.of(context).txnNotATransfer,
                    style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              ),
              const SizedBox(width: 4),
              FilledButton(
                onPressed: busy ? null : () => _actTransfer(t, true),
                style: FilledButton.styleFrom(
                  backgroundColor: _transferAccent,
                  padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
                ),
                child: busy
                    ? const SizedBox(
                        width: 14,
                        height: 14,
                        child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white),
                      )
                    : Text(AppLocalizations.of(context).txnMarkAsTransfer,
                        style: GoogleFonts.sora(
                            fontSize: 12, fontWeight: FontWeight.w600, color: Colors.white)),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _buildEmpty() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.check_circle_outline, size: 56, color: _income),
          const SizedBox(height: 12),
          Text(AppLocalizations.of(context).txnAllCaughtUp,
              style: GoogleFonts.sora(fontSize: 18, color: _income, fontWeight: FontWeight.w600)),
          const SizedBox(height: 4),
          Text(AppLocalizations.of(context).txnAiCategorizedEverything,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
        ],
      ),
    );
  }

  /// Shown when the review queue is momentarily empty but the AI is still analyzing
  /// (background queue draining). An "empty slot" placeholder so the modal is usable
  /// immediately instead of a blocking spinner, and so an empty queue doesn't read as
  /// "All Caught Up" before the suggestions have actually arrived.
  Widget _buildAnalyzing() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const SizedBox(
            width: 28, height: 28,
            child: CircularProgressIndicator(strokeWidth: 2.5, color: _accent),
          ),
          const SizedBox(height: 16),
          Text(AppLocalizations.of(context).txnAiAnalyzingYours,
              style: GoogleFonts.sora(fontSize: 15, color: _textGhost, fontWeight: FontWeight.w500)),
          const SizedBox(height: 4),
          Text(AppLocalizations.of(context).txnSuggestionsWillAppear,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
        ],
      ),
    );
  }

  Widget _buildCelebration() {
    return Padding(
      padding: const EdgeInsets.all(32),
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(Icons.emoji_events, size: 80, color: _pending),
          const SizedBox(height: 20),
          ShaderMask(
            shaderCallback: (bounds) => const LinearGradient(
              colors: [_income, _accent, Color(0xFF8B5CF6)],
            ).createShader(bounds),
            child: Text(AppLocalizations.of(context).txnAmazingWork,
                style: GoogleFonts.instrumentSerif(fontSize: 28, fontWeight: FontWeight.bold, color: Colors.white)),
          ),
          const SizedBox(height: 12),
          Text(AppLocalizations.of(context).txnYouReviewedAll(_reviewedCount),
              style: GoogleFonts.sora(fontSize: 16, fontWeight: FontWeight.w500, color: _textGhost)),
          const SizedBox(height: 24),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              _CelebrationStat(icon: Icons.star, label: 'Categories\nverified', color: _pending),
              const SizedBox(width: 32),
              _CelebrationStat(icon: Icons.bolt, label: 'Budget\nupdated', color: _accent),
              const SizedBox(width: 32),
              _CelebrationStat(icon: Icons.check_circle, label: 'All set!', color: _income),
            ],
          ),
          const SizedBox(height: 24),
          Text(AppLocalizations.of(context).txnClosingAutomatically,
              style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
        ],
      ),
    );
  }

  Widget _buildReviewContent(ScrollController scrollController) {
    final tx = _current!;
    final merchant = tx.displayTitle;
    final isIncome = tx.isIncome;

    return ListView(
      controller: scrollController,
      padding: const EdgeInsets.fromLTRB(24, 16, 24, 8),
      children: [
        // ── Transaction details card ──
        Container(
          padding: const EdgeInsets.all(20),
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
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(merchant,
                            style: GoogleFonts.sora(fontSize: 16, fontWeight: FontWeight.w600, color: _textGhost)),
                        if (tx.merchantName != null && tx.description != tx.merchantName) ...[
                          const SizedBox(height: 2),
                          Text(tx.description,
                              style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                        ],
                      ],
                    ),
                  ),
                  Text(
                    '${isIncome ? '+' : '-'}${formatCurrency(tx.amount.abs())}',
                    style: GoogleFonts.firaCode(
                        fontSize: 20, fontWeight: FontWeight.bold, color: isIncome ? _income : _expense),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Text(formatShortDate(tx.date),
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                  if (tx.accountName.isNotEmpty)
                    Text(tx.accountName,
                        style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
                ],
              ),
            ],
          ),
        ),
        const SizedBox(height: 12),

        // ── AI Confidence indicator ──
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
          decoration: BoxDecoration(
            color: _accent.withValues(alpha: 0.08),
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: _accent.withValues(alpha: 0.2)),
          ),
          child: Row(
            children: [
              const Icon(Icons.auto_awesome, size: 16, color: _accent),
              const SizedBox(width: 8),
              Text(AppLocalizations.of(context).txnAiConfidence,
                  style: GoogleFonts.sora(fontSize: 13, fontWeight: FontWeight.w500, color: _textGhost)),
              const Spacer(),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                decoration: BoxDecoration(
                  color: _confidenceColor(tx.aiConfidence).withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(6),
                ),
                child: Text(
                  _confidenceLabel(tx.aiConfidence),
                  style: GoogleFonts.sora(fontSize: 11, fontWeight: FontWeight.w600,
                      color: _confidenceColor(tx.aiConfidence)),
                ),
              ),
              const SizedBox(width: 6),
              Text(
                tx.aiConfidence != null ? '${(tx.aiConfidence! * 100).round()}%' : '—',
                style: GoogleFonts.firaCode(fontSize: 13, fontWeight: FontWeight.bold, color: _textGhost),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),

        // ── Quick category suggestions ──
        Text(AppLocalizations.of(context).txnQuickSelect,
            style: GoogleFonts.sora(fontSize: 13, fontWeight: FontWeight.w500, color: _textGhost)),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: _getSuggestedCategoryIds().map((catId) {
            final isSelected = _selectedSystemCategoryId == catId && _selectedCustomCategoryId == null;
            final isAI = catId == tx.aiSuggestedCategoryId;
            return GestureDetector(
              onTap: () => _selectSystemCategory(catId),
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                decoration: BoxDecoration(
                  color: isSelected ? _accent.withValues(alpha: 0.2) : _cardBg,
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(
                    color: isSelected ? _accent.withValues(alpha: 0.5) : _cardBorder,
                  ),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (isAI) ...[
                      Icon(Icons.auto_awesome, size: 12,
                          color: isSelected ? _accent : _textMuted),
                      const SizedBox(width: 4),
                    ],
                    Text(_categoryName(catId),
                        style: GoogleFonts.sora(fontSize: 12,
                            color: isSelected ? _accent : _textGhost,
                            fontWeight: isSelected ? FontWeight.w600 : FontWeight.w400)),
                    if (isAI) ...[
                      const SizedBox(width: 4),
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                        decoration: BoxDecoration(
                          color: _accent.withValues(alpha: 0.15),
                          borderRadius: BorderRadius.circular(4),
                        ),
                        child: Text(AppLocalizations.of(context).txnAiBadge,
                            style: GoogleFonts.firaCode(fontSize: 9, color: _accent)),
                      ),
                    ],
                  ],
                ),
              ),
            );
          }).toList(),
        ),
        const SizedBox(height: 16),

        // ── Full category list ──
        Row(
          children: [
            Text(AppLocalizations.of(context).txnOrChooseFromAll,
                style: GoogleFonts.sora(fontSize: 13, fontWeight: FontWeight.w500, color: _textGhost)),
            const Spacer(),
            if ((_selectedSystemCategoryId != null && _selectedSystemCategoryId != tx.aiSuggestedCategoryId) ||
                _selectedCustomCategoryId != null ||
                _selectedSavingsGoalId != null)
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                decoration: BoxDecoration(
                  color: _pending.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(4),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(Icons.info_outline, size: 10, color: _pending),
                    const SizedBox(width: 3),
                    Text(AppLocalizations.of(context).txnModified,
                        style: GoogleFonts.sora(fontSize: 10, color: _pending, fontWeight: FontWeight.w500)),
                  ],
                ),
              ),
          ],
        ),
        const SizedBox(height: 8),
        ..._buildCategoryGroups(),

        const SizedBox(height: 80), // bottom padding for footer
      ],
    );
  }

  List<Widget> _buildCategoryGroups() {
    if (_allCategories.isEmpty) {
      return [
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 12),
          child: Center(
            child: SizedBox(width: 20, height: 20,
                child: CircularProgressIndicator(strokeWidth: 2, color: _accent)),
          ),
        ),
      ];
    }


    // Group categories
    final groups = <String, List<Category>>{};
    for (final c in _allCategories) {
      final group = c.scope == 'personal'
          ? '📁 My Categories'
          : c.scope == 'family'
              ? '👨‍👩‍👧 Family'
              : c.isIncome
                  ? '💰 Income'
                  : c.id >= 10 && c.id <= 19
                      ? '🏠 Fixed Expenses'
                      : c.id >= 20 && c.id <= 29
                          ? '🛒 Variable Expenses'
                          : c.id >= 30 && c.id <= 39
                              ? '💳 Transfers'
                              : '📋 Other';
      groups.putIfAbsent(group, () => []).add(c);
    }

    final widgets = <Widget>[];
    for (final entry in groups.entries) {
      widgets.add(
        Padding(
          padding: const EdgeInsets.only(top: 8, bottom: 4),
          child: Text(entry.key,
              style: GoogleFonts.sora(fontSize: 11, fontWeight: FontWeight.w600, color: _textMuted)),
        ),
      );
      for (final cat in entry.value) {
        final isCustom = cat.scope == 'personal' || cat.scope == 'family';
        final isSelected = isCustom
            ? _selectedCustomCategoryId == cat.id
            : _selectedSystemCategoryId == cat.id && _selectedCustomCategoryId == null;
        widgets.add(
          GestureDetector(
            onTap: () => isCustom ? _selectCustomCategory(cat.id) : _selectSystemCategory(cat.id),
            child: Container(
              margin: const EdgeInsets.symmetric(vertical: 2),
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              decoration: BoxDecoration(
                color: isSelected ? _accent.withValues(alpha: 0.12) : Colors.transparent,
                borderRadius: BorderRadius.circular(8),
                border: isSelected
                    ? Border.all(color: _accent.withValues(alpha: 0.3))
                    : null,
              ),
              child: Row(
                children: [
                  Container(
                    width: 12, height: 12,
                    decoration: BoxDecoration(
                      color: _parseCategoryColor(cat.color),
                      borderRadius: BorderRadius.circular(3),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(cat.name,
                        style: GoogleFonts.sora(fontSize: 13,
                            color: isSelected ? _accent : _textGhost,
                            fontWeight: isSelected ? FontWeight.w600 : FontWeight.w400)),
                  ),
                  if (isSelected)
                    const Icon(Icons.check, size: 16, color: _accent),
                ],
              ),
            ),
          ),
        );
      }
    }

    widgets.addAll(_buildSavingsGoalGroup());
    return widgets;
  }

  /// ── Savings Goals group (bottom of the list) ──
  ///
  /// Parity with the transaction detail picker: a review card can send the
  /// transaction to a savings goal instead of a budget category. Shown last, and
  /// replaced by an explanation when the transaction can't be earmarked (split or
  /// still pending) rather than silently offering nothing.
  List<Widget> _buildSavingsGoalGroup() {
    final tx = _current;
    if (tx == null) return const [];

    final blockedReason = savingsEarmarkBlockedReason(tx);
    if (blockedReason == null && _savingsGoals.isEmpty) return const [];

    final widgets = <Widget>[
      Padding(
        padding: const EdgeInsets.only(top: 8, bottom: 4),
        child: Text(AppLocalizations.of(context).txnSavingsGoalsGroup,
            style: GoogleFonts.sora(
                fontSize: 11, fontWeight: FontWeight.w600, color: _textMuted)),
      ),
    ];

    if (blockedReason != null) {
      widgets.add(Padding(
        padding: const EdgeInsets.symmetric(vertical: 2, horizontal: 2),
        child: Text(blockedReason,
            style: GoogleFonts.sora(
                fontSize: 12, color: _textMuted, height: 1.4)),
      ));
      return widgets;
    }

    for (final goal in _savingsGoals) {
      final isSelected = _selectedSavingsGoalId == goal.id;
      widgets.add(
        GestureDetector(
          onTap: () => _selectSavingsGoal(goal.id),
          child: Container(
            margin: const EdgeInsets.symmetric(vertical: 2),
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            decoration: BoxDecoration(
              color: isSelected
                  ? _accent.withValues(alpha: 0.12)
                  : Colors.transparent,
              borderRadius: BorderRadius.circular(8),
              border: isSelected
                  ? Border.all(color: _accent.withValues(alpha: 0.3))
                  : null,
            ),
            child: Row(
              children: [
                const Icon(Icons.savings_outlined, size: 14, color: _accent),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(goal.name,
                      style: GoogleFonts.sora(
                          fontSize: 13,
                          color: isSelected ? _accent : _textGhost,
                          fontWeight: isSelected
                              ? FontWeight.w600
                              : FontWeight.w400)),
                ),
                if (isSelected)
                  const Icon(Icons.check, size: 16, color: _accent),
              ],
            ),
          ),
        ),
      );
    }
    return widgets;
  }

  Widget _buildFooter() {
    final tx = _current;
    final isAcceptingAI = _selectedSavingsGoalId == null &&
        _selectedCustomCategoryId == null &&
        _selectedSystemCategoryId == tx?.aiSuggestedCategoryId;

    return Container(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
      decoration: const BoxDecoration(
        color: Color(0xFF0C0C18),
        border: Border(top: BorderSide(color: _cardBorder)),
      ),
      child: Row(
        children: [
          // Left: Previous + Close
          IconButton(
            icon: const Icon(Icons.arrow_back, size: 20),
            color: _currentIndex > 0 ? _textGhost : _textMuted.withValues(alpha: 0.3),
            onPressed: _currentIndex > 0 && !_isSaving && !_isSkipping ? _handlePrevious : null,
            tooltip: 'Previous',
          ),
          GestureDetector(
            onTap: _isSaving || _isSkipping ? null : _handleClose,
            child: Text(AppLocalizations.of(context).txnClose,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
          ),
          const Spacer(),
          // Right: Skip 2 Days + Next + Accept/Save
          _ReviewActionButton(
            label: 'Skip 2d',
            icon: Icons.schedule,
            color: const Color(0xFFF97316),
            onTap: _isSkipping || _isSaving ? null : _handleSkipTemporarily,
            isLoading: _isSkipping,
          ),
          const SizedBox(width: 8),
          _ReviewActionButton(
            label: 'Next',
            icon: Icons.arrow_forward,
            color: _textMuted,
            onTap: _currentIndex < _queue.length - 1 && !_isSaving && !_isSkipping
                ? _handleNext : null,
          ),
          const SizedBox(width: 8),
          _ReviewActionButton(
            label: isAcceptingAI ? 'Accept' : 'Save',
            icon: Icons.check,
            color: _income,
            filled: true,
            onTap: _isSaving || _isSkipping ? null : _handleAccept,
            isLoading: _isSaving,
          ),
        ],
      ),
    );
  }
}

// ── Celebration stat icon ──

class _CelebrationStat extends StatelessWidget {
  final IconData icon;
  final String label;
  final Color color;
  const _CelebrationStat({required this.icon, required this.label, required this.color});

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Icon(icon, size: 28, color: color),
        const SizedBox(height: 6),
        Text(label,
            textAlign: TextAlign.center,
            style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
      ],
    );
  }
}

// ── Compact action button for review footer ──

class _ReviewActionButton extends StatelessWidget {
  final String label;
  final IconData icon;
  final Color color;
  final bool filled;
  final VoidCallback? onTap;
  final bool isLoading;

  const _ReviewActionButton({
    required this.label,
    required this.icon,
    required this.color,
    this.filled = false,
    this.onTap,
    this.isLoading = false,
  });

  @override
  Widget build(BuildContext context) {
    final disabled = onTap == null;
    final effectiveColor = disabled ? color.withValues(alpha: 0.3) : color;
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: filled ? effectiveColor.withValues(alpha: 0.2) : _cardBg,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: effectiveColor.withValues(alpha: filled ? 0.4 : 0.2)),
        ),
        child: isLoading
            ? SizedBox(width: 16, height: 16,
                child: CircularProgressIndicator(strokeWidth: 2, color: effectiveColor))
            : Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(icon, size: 14, color: effectiveColor),
                  const SizedBox(width: 5),
                  Text(label,
                      style: GoogleFonts.sora(fontSize: 12, color: effectiveColor, fontWeight: FontWeight.w600)),
                ],
              ),
      ),
    );
  }
}
