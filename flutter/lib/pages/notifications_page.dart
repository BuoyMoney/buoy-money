import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/notification_service.dart';
import '../utils/deep_link.dart';
import '../utils/formatters.dart';

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

// ── Notification type → color ───────────────────────────
Color _typeColor(String type) => switch (type) {
      'plaid_update_required' => _expense,
      'plaid_expiration_warning' => _warning,
      'plaid_reconnected' => _income,
      'account_alert' => const Color(0xFF60A5FA),
      'subscription_detection' => _accent,
      'transaction_subcategory_needed' => _warning,
      'money_wrapped' || 'money_wrapped_yearly' => _accent,
      _ => const Color(0xFF60A5FA),
    };

// ── Notification type → icon ────────────────────────────
IconData _typeIcon(String type) => switch (type) {
      'plaid_update_required' => Icons.warning_amber_rounded,
      'plaid_expiration_warning' => Icons.info_outline,
      'plaid_reconnected' => Icons.check_circle_outline,
      'account_alert' => Icons.account_balance_outlined,
      'subscription_detection' => Icons.auto_awesome,
      'transaction_subcategory_needed' => Icons.category_outlined,
      'money_wrapped' || 'money_wrapped_yearly' => Icons.card_giftcard_rounded,
      _ => Icons.notifications_outlined,
    };

// ═══════════════════════════════════════════════════════════
//  NotificationsPage
// ═══════════════════════════════════════════════════════════
class NotificationsPage extends StatefulWidget {
  const NotificationsPage({super.key});

  @override
  State<NotificationsPage> createState() =>
      _NotificationsPageState();
}

class _NotificationsPageState
    extends State<NotificationsPage>
    with TickerProviderStateMixin {
  // Tab controller
  late TabController _tabController;

  // Data — Active tab
  List<AppNotification> _activeNotifications = [];
  bool _activeHasMore = false;
  int _activePage = 1;

  // Data — Archived tab
  List<AppNotification> _archivedNotifications = [];
  bool _archivedHasMore = false;
  int _archivedPage = 1;

  int _unreadCount = 0;
  bool _isLoading = true;
  bool _isLoadingMore = false;
  String? _error;

  // Entrance animation
  late AnimationController _fadeController;
  late Animation<double> _fadeAnim;

  static const _pageSize = 20;

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: 2, vsync: this);
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnim = CurvedAnimation(parent: _fadeController, curve: Curves.easeOut);
    _fadeController.forward();
    _loadInitial();
  }

  @override
  void dispose() {
    _tabController.dispose();
    _fadeController.dispose();
    super.dispose();
  }

  // ── Data loading ──────────────────────────────────────

  Future<void> _loadInitial() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        notificationService.getNotificationsPaginated(
          page: 1,
          pageSize: _pageSize,
          includeArchived: false,
        ),
        notificationService.getNotificationsPaginated(
          page: 1,
          pageSize: _pageSize,
          includeArchived: true,
        ),
        notificationService.getUnreadCount(),
      ]);

      final activePage = results[0] as PaginatedNotifications;
      final archivedPage = results[1] as PaginatedNotifications;
      final unread = results[2] as int;

      if (!mounted) return;
      setState(() {
        _activeNotifications = activePage.notifications
            .where((n) => !n.isArchived)
            .toList();
        _activeHasMore = activePage.hasMore;
        _activePage = 1;

        // The archived call includes everything — filter to archived only
        _archivedNotifications = archivedPage.notifications
            .where((n) => n.isArchived)
            .toList();
        _archivedHasMore = archivedPage.hasMore;
        _archivedPage = 1;

        _unreadCount = unread;
        _isLoading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _isLoading = false;
        _error = 'Failed to load notifications';
      });
    }
  }

  Future<void> _loadMoreActive() async {
    if (_isLoadingMore || !_activeHasMore) return;
    setState(() => _isLoadingMore = true);
    try {
      final result = await notificationService.getNotificationsPaginated(
        page: _activePage + 1,
        pageSize: _pageSize,
        includeArchived: false,
      );
      if (!mounted) return;
      setState(() {
        _activeNotifications.addAll(
          result.notifications.where((n) => !n.isArchived),
        );
        _activeHasMore = result.hasMore;
        _activePage++;
        _isLoadingMore = false;
      });
    } catch (_) {
      if (mounted) setState(() => _isLoadingMore = false);
    }
  }

  Future<void> _loadMoreArchived() async {
    if (_isLoadingMore || !_archivedHasMore) return;
    setState(() => _isLoadingMore = true);
    try {
      final result = await notificationService.getNotificationsPaginated(
        page: _archivedPage + 1,
        pageSize: _pageSize,
        includeArchived: true,
      );
      if (!mounted) return;
      setState(() {
        _archivedNotifications.addAll(
          result.notifications.where((n) => n.isArchived),
        );
        _archivedHasMore = result.hasMore;
        _archivedPage++;
        _isLoadingMore = false;
      });
    } catch (_) {
      if (mounted) setState(() => _isLoadingMore = false);
    }
  }

  // ── Actions ───────────────────────────────────────────

  Future<void> _markAsRead(AppNotification n) async {
    if (n.isRead) return;
    // Optimistic update
    setState(() {
      final idx = _activeNotifications.indexWhere((x) => x.id == n.id);
      if (idx >= 0) {
        _activeNotifications[idx] = AppNotification(
          id: n.id,
          type: n.type,
          title: n.title,
          message: n.message,
          actionUrl: n.actionUrl,
          actionText: n.actionText,
          relatedConnectionId: n.relatedConnectionId,
          institutionName: n.institutionName,
          isRead: true,
          readAt: DateTime.now(),
          isArchived: n.isArchived,
          createdAt: n.createdAt,
          metadata: n.metadata,
        );
      }
      _unreadCount = (_unreadCount - 1).clamp(0, 99999);
    });
    try {
      await notificationService.markAsRead(n.id);
    } catch (_) {
      // Revert on failure
      if (mounted) _loadInitial();
    }
  }

  Future<void> _markAllAsRead() async {
    if (_unreadCount == 0) return;
    final prev = List<AppNotification>.from(_activeNotifications);
    final prevCount = _unreadCount;
    setState(() {
      _activeNotifications = _activeNotifications.map((n) => AppNotification(
        id: n.id,
        type: n.type,
        title: n.title,
        message: n.message,
        actionUrl: n.actionUrl,
        actionText: n.actionText,
        relatedConnectionId: n.relatedConnectionId,
        institutionName: n.institutionName,
        isRead: true,
        readAt: n.readAt ?? DateTime.now(),
        isArchived: n.isArchived,
        createdAt: n.createdAt,
        metadata: n.metadata,
      )).toList();
      _unreadCount = 0;
    });
    try {
      await notificationService.markAllAsRead();
    } catch (_) {
      if (mounted) {
        setState(() {
          _activeNotifications = prev;
          _unreadCount = prevCount;
        });
      }
    }
  }

  Future<void> _archiveNotification(AppNotification n) async {
    setState(() {
      _activeNotifications.removeWhere((x) => x.id == n.id);
      _archivedNotifications.insert(0, AppNotification(
        id: n.id,
        type: n.type,
        title: n.title,
        message: n.message,
        actionUrl: n.actionUrl,
        actionText: n.actionText,
        relatedConnectionId: n.relatedConnectionId,
        institutionName: n.institutionName,
        isRead: n.isRead,
        readAt: n.readAt,
        isArchived: true,
        createdAt: n.createdAt,
        metadata: n.metadata,
      ));
      if (!n.isRead) _unreadCount = (_unreadCount - 1).clamp(0, 99999);
    });
    try {
      await notificationService.archive(n.id);
    } catch (_) {
      if (mounted) _loadInitial();
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
            ? _buildLoadingState()
            : _error != null
                ? _buildErrorState()
                : _buildContent(),
      ),
    );
  }

  // ── Content ───────────────────────────────────────────

  Widget _buildContent() {
    return Column(
      children: [
        _buildHeader(),
        _buildTabBar(),
        Expanded(
          child: TabBarView(
            controller: _tabController,
            children: [
              _buildNotificationList(
                _activeNotifications,
                isArchived: false,
                hasMore: _activeHasMore,
                onLoadMore: _loadMoreActive,
              ),
              _buildNotificationList(
                _archivedNotifications,
                isArchived: true,
                hasMore: _archivedHasMore,
                onLoadMore: _loadMoreArchived,
              ),
            ],
          ),
        ),
      ],
    );
  }

  // ── Header ────────────────────────────────────────────

  Widget _buildHeader() {
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 24, 24, 8),
      child: Row(
        children: [
          Text(
            AppLocalizations.of(context).uiNotificationsTitle,
            style: GoogleFonts.instrumentSerif(
              fontSize: 28,
              fontWeight: FontWeight.w400,
              color: _textGhost,
            ),
          ),
          if (_unreadCount > 0) ...[
            const SizedBox(width: 10),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.2),
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: _accent.withValues(alpha: 0.4)),
              ),
              child: Text(
                '$_unreadCount',
                style: GoogleFonts.firaCode(
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  color: _accent,
                ),
              ),
            ),
          ],
          const Spacer(),
          if (_unreadCount > 0)
            _GlassButton(
              label: 'Mark all read',
              icon: Icons.done_all_rounded,
              onTap: _markAllAsRead,
            ),
        ],
      ),
    );
  }

  // ── Tab bar ───────────────────────────────────────────

  Widget _buildTabBar() {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 24),
      child: Container(
            decoration: BoxDecoration(
              color: _cardBg,
              borderRadius: BorderRadius.circular(12),
              border: Border.all(color: _cardBorder),
              boxShadow: [
                BoxShadow(
                  color: const Color(0xFF7B61FF).withValues(alpha: 0.03),
                  blurRadius: 24,
                  spreadRadius: 0,
                ),
              ],
            ),
            child: TabBar(
              controller: _tabController,
              indicator: BoxDecoration(
                color: _accent.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(10),
              ),
              indicatorSize: TabBarIndicatorSize.tab,
              dividerColor: Colors.transparent,
              labelColor: _accent,
              unselectedLabelColor: _textMuted,
              labelStyle: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w600,
              ),
              unselectedLabelStyle: GoogleFonts.sora(
                fontSize: 13,
                fontWeight: FontWeight.w500,
              ),
              tabs: [
                Tab(
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(Icons.inbox_rounded, size: 18),
                      const SizedBox(width: 6),
                      Text(AppLocalizations.of(context).uiActiveTab),
                      if (_unreadCount > 0) ...[
                        const SizedBox(width: 6),
                        Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 6,
                            vertical: 2,
                          ),
                          decoration: BoxDecoration(
                            color: _accent.withValues(alpha: 0.3),
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: Text(
                            '$_unreadCount',
                            style: GoogleFonts.firaCode(
                              fontSize: 11,
                              color: _accent,
                            ),
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
                Tab(
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(Icons.archive_outlined, size: 18),
                      const SizedBox(width: 6),
                      Text(AppLocalizations.of(context).uiArchivedTab),
                    ],
                  ),
                ),
              ],
            ),
          ),
    );
  }

  // ── Notification list ─────────────────────────────────

  Widget _buildNotificationList(
    List<AppNotification> notifications, {
    required bool isArchived,
    required bool hasMore,
    required VoidCallback onLoadMore,
  }) {
    if (notifications.isEmpty) {
      return _buildEmptyState(isArchived);
    }

    return RefreshIndicator(
      color: _accent,
      backgroundColor: _sheetBg,
      onRefresh: _loadInitial,
      child: ListView.builder(
        padding: const EdgeInsets.fromLTRB(24, 16, 24, 100),
        itemCount: notifications.length + (hasMore ? 1 : 0),
        itemBuilder: (context, index) {
          if (index == notifications.length) {
            // Load more button
            return _buildLoadMoreButton(onLoadMore);
          }
          return Padding(
            padding: const EdgeInsets.only(bottom: 10),
            child: _NotificationCard(
              notification: notifications[index],
              onMarkRead: () => _markAsRead(notifications[index]),
              onArchive: isArchived
                  ? null
                  : () => _archiveNotification(notifications[index]),
            ),
          );
        },
      ),
    );
  }

  Widget _buildLoadMoreButton(VoidCallback onLoadMore) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 16),
      child: Center(
        child: _isLoadingMore
            ? const SizedBox(
                width: 24,
                height: 24,
                child: CircularProgressIndicator(
                  strokeWidth: 2,
                  color: _accent,
                ),
              )
            : _GlassButton(
                label: 'Load more',
                icon: Icons.expand_more_rounded,
                onTap: onLoadMore,
              ),
      ),
    );
  }

  // ── Empty state ───────────────────────────────────────

  Widget _buildEmptyState(bool isArchived) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            isArchived
                ? Icons.archive_outlined
                : Icons.notifications_none_rounded,
            size: 56,
            color: _textMuted.withValues(alpha: 0.4),
          ),
          const SizedBox(height: 16),
          Text(
            isArchived
                ? AppLocalizations.of(context).uiNoArchivedNotifications
                : AppLocalizations.of(context).uiAllCaughtUp,
            style: GoogleFonts.sora(
              fontSize: 16, fontWeight: FontWeight.w600, color: _textMuted,
            ),
          ),
          const SizedBox(height: 8),
          Text(
            isArchived
                ? AppLocalizations.of(context).uiArchivedEmptyHint
                : AppLocalizations.of(context).uiNoNewNotifications,
            style: GoogleFonts.sora(fontSize: 13, color: _textMuted.withValues(alpha: 0.6)),
          ),
        ],
      ),
    );
  }

  // ── Loading ───────────────────────────────────────────

  Widget _buildLoadingState() {
    return const Center(
      child: CircularProgressIndicator(color: _accent),
    );
  }

  // ── Error ─────────────────────────────────────────────

  Widget _buildErrorState() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.error_outline, size: 56, color: _expense.withValues(alpha: 0.7)),
          const SizedBox(height: 16),
          Text(
            _error ?? AppLocalizations.of(context).uiSomethingWentWrong,
            style: GoogleFonts.sora(fontSize: 14, color: _textMuted),
          ),
          const SizedBox(height: 20),
          _GlassButton(
            label: 'Retry',
            icon: Icons.refresh_rounded,
            onTap: _loadInitial,
          ),
        ],
      ),
    );
  }
}

// ═══════════════════════════════════════════════════════════
//  _NotificationCard — glassmorphic notification card
// ═══════════════════════════════════════════════════════════
class _NotificationCard extends StatefulWidget {
  final AppNotification notification;
  final VoidCallback onMarkRead;
  final VoidCallback? onArchive;

  const _NotificationCard({
    required this.notification,
    required this.onMarkRead,
    this.onArchive,
  });

  @override
  State<_NotificationCard> createState() => _NotificationCardState();
}

class _NotificationCardState extends State<_NotificationCard> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final n = widget.notification;
    final color = _typeColor(n.type);

    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 200),
        curve: Curves.easeOut,
        transform: Matrix4.translationValues(0.0, _hovered ? -2.0 : 0.0, 0.0),
        child: Container(
              decoration: BoxDecoration(
                color: n.isRead
                    ? _cardBg
                    : _accent.withValues(alpha: 0.06),
                borderRadius: BorderRadius.circular(14),
                border: Border.all(
                  color: _hovered
                      ? color.withValues(alpha: 0.3)
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
              child: IntrinsicHeight(
                child: Row(
                  children: [
                    // Color left bar
                    Container(
                      width: 4,
                      decoration: BoxDecoration(
                        color: color,
                        borderRadius: const BorderRadius.only(
                          topLeft: Radius.circular(14),
                          bottomLeft: Radius.circular(14),
                        ),
                      ),
                    ),
                    Expanded(
                      child: Padding(
                        padding: const EdgeInsets.all(14),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            // Type icon
                            Container(
                              padding: const EdgeInsets.all(9),
                              decoration: BoxDecoration(
                                color: color.withValues(alpha: 0.12),
                                borderRadius: BorderRadius.circular(10),
                              ),
                              child: Icon(
                                _typeIcon(n.type),
                                color: color,
                                size: 18,
                              ),
                            ),
                            const SizedBox(width: 12),
                            // Content
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  // Title row
                                  Row(
                                    children: [
                                      Expanded(
                                        child: Text(
                                          n.title,
                                          style: GoogleFonts.sora(
                                            fontSize: 14,
                                            fontWeight: n.isRead
                                                ? FontWeight.w500
                                                : FontWeight.w700,
                                            color: _textGhost,
                                          ),
                                        ),
                                      ),
                                      if (!n.isRead)
                                        Container(
                                          width: 8,
                                          height: 8,
                                          decoration: const BoxDecoration(
                                            color: _accent,
                                            shape: BoxShape.circle,
                                          ),
                                        ),
                                    ],
                                  ),
                                  // Institution name
                                  if (n.institutionName != null &&
                                      n.institutionName!.isNotEmpty) ...[
                                    const SizedBox(height: 2),
                                    Text(
                                      n.institutionName!,
                                      style: GoogleFonts.sora(
                                        fontSize: 12,
                                        fontWeight: FontWeight.w600,
                                        color: color.withValues(alpha: 0.8),
                                      ),
                                    ),
                                  ],
                                  const SizedBox(height: 4),
                                  // Message
                                  Text(
                                    n.message,
                                    style: GoogleFonts.sora(
                                      fontSize: 13,
                                      color: _textMuted,
                                      height: 1.4,
                                    ),
                                    maxLines: 3,
                                    overflow: TextOverflow.ellipsis,
                                  ),
                                  const SizedBox(height: 10),
                                  // Bottom row: timestamp + actions
                                  Row(
                                    children: [
                                      Icon(Icons.access_time,
                                          size: 12, color: _textMuted.withValues(alpha: 0.6)),
                                      const SizedBox(width: 4),
                                      Text(
                                        formatRelativeDate(n.createdAt),
                                        style: GoogleFonts.firaCode(
                                          fontSize: 11,
                                          color: _textMuted.withValues(alpha: 0.6),
                                        ),
                                      ),
                                      const Spacer(),
                                      // Action button (if actionUrl present)
                                      if (n.actionUrl != null &&
                                          n.actionText != null) ...[
                                        _GlassChip(
                                          label: n.actionText!,
                                          color: color,
                                          onTap: () {
                                            // Navigate to action URL — use GoRouter.
                                            // Only handle in-app routes.
                                            final route =
                                                normalizeInAppRoute(n.actionUrl!);
                                            if (route != null) {
                                              // Acting on a notification implies
                                              // it's been seen — mark it read.
                                              if (!n.isRead) widget.onMarkRead();
                                              context.go(route);
                                            }
                                          },
                                        ),
                                        const SizedBox(width: 8),
                                      ],
                                      // Mark as read
                                      if (!n.isRead)
                                        _IconAction(
                                          icon: Icons.done_rounded,
                                          tooltip: 'Mark as read',
                                          color: _income,
                                          onTap: widget.onMarkRead,
                                        ),
                                      // Archive
                                      if (widget.onArchive != null)
                                        _IconAction(
                                          icon: Icons.archive_outlined,
                                          tooltip: 'Archive',
                                          color: _textMuted,
                                          onTap: widget.onArchive!,
                                        ),
                                    ],
                                  ),
                                ],
                              ),
                            ),
                          ],
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

// ═══════════════════════════════════════════════════════════
//  Shared micro-widgets
// ═══════════════════════════════════════════════════════════

/// Small glassmorphic action button
class _GlassButton extends StatefulWidget {
  final String label;
  final IconData icon;
  final VoidCallback onTap;

  const _GlassButton({required this.label, required this.icon, required this.onTap});

  @override
  State<_GlassButton> createState() => _GlassButtonState();
}

class _GlassButtonState extends State<_GlassButton> {
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
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
          decoration: BoxDecoration(
            color: _hovered ? _accent.withValues(alpha: 0.15) : _cardBg,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: _hovered ? _accent.withValues(alpha: 0.4) : _cardBorder,
            ),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(widget.icon, size: 16, color: _accent),
              const SizedBox(width: 6),
              Text(
                widget.label,
                style: GoogleFonts.sora(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: _accent,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Tiny chip-like action link
class _GlassChip extends StatefulWidget {
  final String label;
  final Color color;
  final VoidCallback onTap;

  const _GlassChip({required this.label, required this.color, required this.onTap});

  @override
  State<_GlassChip> createState() => _GlassChipState();
}

class _GlassChipState extends State<_GlassChip> {
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
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
          decoration: BoxDecoration(
            color: _hovered
                ? widget.color.withValues(alpha: 0.2)
                : widget.color.withValues(alpha: 0.1),
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: widget.color.withValues(alpha: 0.3)),
          ),
          child: Text(
            widget.label,
            style: GoogleFonts.sora(
              fontSize: 11,
              fontWeight: FontWeight.w600,
              color: widget.color,
            ),
          ),
        ),
      ),
    );
  }
}

/// Small icon button for card actions
class _IconAction extends StatefulWidget {
  final IconData icon;
  final String tooltip;
  final Color color;
  final VoidCallback onTap;

  const _IconAction({
    required this.icon,
    required this.tooltip,
    required this.color,
    required this.onTap,
  });

  @override
  State<_IconAction> createState() => _IconActionState();
}

class _IconActionState extends State<_IconAction> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: Tooltip(
        message: widget.tooltip,
        child: GestureDetector(
          onTap: widget.onTap,
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 150),
            padding: const EdgeInsets.all(6),
            decoration: BoxDecoration(
              color: _hovered
                  ? widget.color.withValues(alpha: 0.15)
                  : Colors.transparent,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Icon(widget.icon, size: 16, color: widget.color),
          ),
        ),
      ),
    );
  }
}
