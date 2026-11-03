import 'dart:ui';
import 'package:flutter/foundation.dart' show kDebugMode;
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import '../../l10n/generated/app_localizations.dart';
import '../../providers/auth_provider.dart';
import '../../config/server_capabilities.dart';
import '../../models/auth_models.dart';
import '../../models/nav_destination.dart';
import '../../models/billing_models.dart';
import '../../services/ai_status_service.dart';
import '../../services/referral_service.dart';
import '../../services/receipt_share_listener.dart';
import '../../utils/navigation_depth.dart';
import 'backgrounds/interactive_background_layer.dart';
import '../../services/nav_history.dart';
import 'paywall_overlay.dart';

// VaporClinic palette
const _bg = Color(0xFF04040A);
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _sidebarBg = Color(0x180A0A14); // ~9% — translucent enough for bubbles
const _sidebarBorder = Color(0x14FFFFFF);

/// Nav destinations live in `models/nav_destination.dart` so `NavigationDepth`
/// can derive transition direction from the SAME order the menu renders. Aliased
/// to the old private names to keep this file's call sites unchanged.
typedef _NavDestination = NavDestination;
const _allDestinations = kNavDestinations;

// Bottom nav shows these 4 + a "More" button on mobile
const _defaultBottomNavKeys = ['dashboard', 'transactions', 'budgets', 'savings'];
const _bottomNavStorageKey = 'buoyai_bottom_nav_keys';

/// Filter destinations based on user's subscription tier.
List<_NavDestination> _visibleDestinations(String? subscriptionTier) {
  final isAdmin = subscriptionTier != null &&
      SubscriptionTierExtension.fromString(subscriptionTier) ==
          SubscriptionTier.admin;
  return _allDestinations
      .where((d) => !d.isAdmin || isAdmin)
      // The broadcast screen exists on the hosted edition only.
      .where((d) => d.key != 'broadcast' || ServerEdition.current.value.hasAdminBroadcast)
      // debugOnly destinations (e.g. Insights) appear only in debug builds — hidden in
      // release (staging/prod) until formally launched.
      .where((d) => !d.debugOnly || kDebugMode)
      // AI Chat runs on Gemini only; a server with AI off (or on another provider) hides it.
      // Unknown (not asked yet, or an older server) shows it, as before.
      .where((d) => d.key != 'ai-chat' || aiAvailability.featureOn('chat'))
      .toList();
}

/// Height of the mobile bottom nav, excluding the bottom safe-area inset.
///
/// The nav is a `Positioned(bottom: 0)` child of the shell's `Stack` rather than
/// a `Scaffold.bottomNavigationBar`, so nothing insets for it automatically —
/// anything that must sit clear of it (page content, toasts) adds this itself.
const double kCinematicBottomNavHeight = 64;

/// Width above which the shell swaps the bottom nav for a left sidebar.
const double kCinematicDesktopBreakpoint = 800;

/// The MediaQuery a page under the shell sees: system insets the shell has
/// already consumed are removed so descendants can't consume them again.
///
/// - Padding (status bar, and on mobile the gesture bar): the shell insets the
///   content layer itself, so a page's own `SafeArea` must see zero.
/// - `viewInsets.bottom` (the keyboard): the shell's `Scaffold` resizes for it
///   (`resizeToAvoidBottomInset` default) and strips it from its body's
///   MediaQuery — but the shell used to rebuild the child's MediaQuery with
///   `MediaQuery.removePadding(context: context, …)`, whose data comes from
///   ABOVE the Scaffold, re-injecting the raw keyboard inset under the
///   already-shrunk body. Every nested page `Scaffold` then resized a second
///   time, and viewInsets-padded sheets padded on top of the shell's resize —
///   each reserving a second keyboard height. That double-count was the
///   keyboard-sized void on Android text fields (add-transaction sheet,
///   account nickname editor, transactions search). Verified by on-device
///   measurement: with the IME open the child's constraints were already
///   keyboard-shrunk while its MediaQuery still reported the full inset.
MediaQueryData stripShellOwnedInsets(MediaQueryData data,
    {required bool isDesktop}) {
  return data
      .removePadding(removeTop: true, removeBottom: !isDesktop)
      .removeViewInsets(removeBottom: true);
}

/// Cinematic responsive navigation shell.
///
/// - Desktop (>800px): Glassmorphic left sidebar
/// - Mobile (<=800px): Glassmorphic bottom nav bar with "More" drawer
class CinematicNavShell extends StatefulWidget {
  final Widget child;
  final GoRouterState state;

  const CinematicNavShell({
    super.key,
    required this.child,
    required this.state,
  });

  @override
  State<CinematicNavShell> createState() => _CinematicNavShellState();
}

class _CinematicNavShellState extends State<CinematicNavShell>
    with WidgetsBindingObserver {
  final ValueNotifier<Offset> _mouseNotifier = ValueNotifier(Offset.zero);
  List<String> _bottomNavKeys = List.from(_defaultBottomNavKeys);

  String get _currentPath => widget.state.uri.path;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    aiAvailability.addListener(_onAiAvailabilityChanged);
    aiAvailability.ensureLoaded();
    _loadBottomNavKeys();
    // Now that the user is in the authenticated shell, redeem any referral code that was
    // captured from the launch URL (best-effort, once).
    referralService.redeemPendingIfAny();
    // Share → BuoyAI: a screenshot shared from another app opens the receipt sheet (Android).
    ReceiptShareListener.attach(() => context);
  }

  void _onAiAvailabilityChanged() {
    if (mounted) setState(() {});
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // When the app/tab regains focus (e.g. returning from hosted Stripe Checkout),
    // re-fetch the profile so the paywall lifts once the subscription is active.
    if (state == AppLifecycleState.resumed && mounted) {
      final auth = Provider.of<AuthProvider>(context, listen: false);
      if (auth.user?.requiresPaidPlan == true) {
        auth.refreshUser();
      }
    }
  }

  Future<void> _loadBottomNavKeys() async {
    final prefs = await SharedPreferences.getInstance();
    final saved = prefs.getStringList(_bottomNavStorageKey);
    if (saved != null && saved.length == 4 && mounted) {
      // Validate keys exist in visible destinations for current user
      final auth = Provider.of<AuthProvider>(context, listen: false);
      final validKeys = _visibleDestinations(auth.user?.subscriptionTier)
          .map((d) => d.key)
          .toSet();
      final filtered = saved.where((k) => validKeys.contains(k)).toList();
      if (filtered.length == 4) {
        setState(() => _bottomNavKeys = filtered);
      } else {
        // Saved keys are stale — clear and fall back to defaults
        await prefs.remove(_bottomNavStorageKey);
      }
    }
  }

  Future<void> _saveBottomNavKeys(List<String> keys) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setStringList(_bottomNavStorageKey, keys);
    setState(() => _bottomNavKeys = keys);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    aiAvailability.removeListener(_onAiAvailabilityChanged);
    // Logging out disposes the shell; the next sign-in attaches a fresh listener.
    ReceiptShareListener.detach();
    _mouseNotifier.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // Record this tab so the Android back gesture can return to the previous one. `go` (which the
    // nav uses) keeps no history; this is the piece it drops. No-op for non-tab routes and repeats.
    navHistory.visit(_currentPath);

    final width = MediaQuery.of(context).size.width;
    final isDesktop = width > kCinematicDesktopBreakpoint;
    // Show the blocking paywall on every page except billing (where the user
    // completes payment). Watches auth so it lifts as soon as a plan is active.
    final requiresPaidPlan =
        context.watch<AuthProvider>().user?.requiresPaidPlan ?? false;
    final showPaywall = requiresPaidPlan && _currentPath != '/billing';

    // Back-gesture policy (predictive back on Android). Let the system pop only when there is a
    // real route to pop — a pushed sub-page (context.push) or an open modal, both of which
    // GoRouter/Navigator handle — or when we're at the true exit point (start destination with no
    // tab history). Otherwise intercept and go to the previous tab ourselves.
    final canPop = GoRouter.of(context).canPop() ||
        (_currentPath == NavHistory.startDestination && !navHistory.canGoBack);

    return PopScope(
      canPop: canPop,
      onPopInvokedWithResult: (didPop, _) {
        if (didPop) return; // the OS already popped a sub-page/modal, or we allowed exit
        _handleTabBack(context);
      },
      child: Scaffold(
        backgroundColor: _bg,
        body: Stack(
          children: [
            // Background layer with its own mouse listener —
            // uses Listener instead of MouseRegion to avoid
            // mouse_tracker conflicts with InkWell during animations.
            Positioned.fill(
              child: Listener(
                behavior: HitTestBehavior.translucent,
                onPointerHover: (event) =>
                    _mouseNotifier.value = event.localPosition,
                onPointerMove: (event) =>
                    _mouseNotifier.value = event.localPosition,
                child: InteractiveBackgroundLayer(mouseNotifier: _mouseNotifier),
              ),
            ),
            // Depth gradient — darkens as user navigates deeper (deep-sea effect)
            Positioned.fill(
              child: IgnorePointer(
                child: ValueListenableBuilder<double>(
                  valueListenable: NavigationDepth.depthNotifier,
                  builder: (context, depth, _) => AnimatedContainer(
                    duration: const Duration(milliseconds: 600),
                    curve: Curves.easeInOut,
                    decoration: BoxDecoration(
                      gradient: LinearGradient(
                        begin: Alignment.topCenter,
                        end: Alignment.bottomCenter,
                        stops: const [0.0, 0.4, 1.0],
                        colors: [
                          Color.lerp(
                            Colors.transparent,
                            const Color(0xFF010118),
                            depth * 0.35,
                          )!,
                          Color.lerp(
                            Colors.transparent,
                            const Color(0xFF020210),
                            depth * 0.55,
                          )!,
                          Color.lerp(
                            Colors.transparent,
                            const Color(0xFF030318),
                            depth * 0.75,
                          )!,
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ),
            // Content layer. Inset the top by the status-bar / notch / camera-cutout height so
            // page headers ("Accounts", "Transactions", …) never collide with the system status
            // bar. Applied here once for every authenticated page. Only the CONTENT is inset — the
            // full-bleed background layers above stay edge-to-edge. (No-op on desktop/web where the
            // top view padding is 0.)
            if (isDesktop)
              Row(
                children: [
                  _DesktopSidebar(
                    currentPath: _currentPath,
                    onNavigate: (path) => context.go(path),
                  ),
                  Expanded(
                    child: Padding(
                      padding: EdgeInsets.only(
                          top: MediaQuery.of(context).padding.top),
                      // The shell owns the system insets (status bar + keyboard);
                      // strip what it consumed so pages don't inset a second
                      // time — see [stripShellOwnedInsets].
                      child: MediaQuery(
                        data: stripShellOwnedInsets(MediaQuery.of(context),
                            isDesktop: true),
                        child: widget.child,
                      ),
                    ),
                  ),
                ],
              )
            else
              // Mobile: inset top for the status bar/notch, bottom for the nav bar overlay.
              Padding(
                padding: EdgeInsets.only(
                    top: MediaQuery.of(context).padding.top,
                    bottom: MediaQuery.of(context).padding.bottom +
                        kCinematicBottomNavHeight),
                // The shell owns the system insets (status bar, gesture bar,
                // keyboard); strip what it consumed so pages/sheets can't
                // inset a second time — see [stripShellOwnedInsets]. The
                // keyboard double-count was the Android keyboard-height void.
                child: MediaQuery(
                  data: stripShellOwnedInsets(MediaQuery.of(context),
                      isDesktop: false),
                  child: widget.child,
                ),
              ),
            // Mobile bottom nav — inside Stack so BackdropFilter blurs background
            if (!isDesktop)
              Positioned(
                left: 0,
                right: 0,
                bottom: 0,
                child: _MobileBottomNav(
                  currentPath: _currentPath,
                  bottomNavKeys: _bottomNavKeys,
                  onNavigate: (path) => context.go(path),
                  onSaveKeys: _saveBottomNavKeys,
                ),
              ),
            // Blocking paywall for returning users who must pick a paid plan.
            if (showPaywall) const PaywallOverlay(),
          ],
        ),
      ),
    );
  }

  /// Back-gesture landed on a tab with no route to pop. Return to the previous tab if there is one;
  /// otherwise, if we were deep-linked straight into a non-home tab, go to the start destination
  /// first (Material's "back returns to start before exit"); from the start destination we don't
  /// get here (canPop was true → the OS exits).
  void _handleTabBack(BuildContext context) {
    final prev = navHistory.goBack();
    if (prev != null) {
      context.go(prev);
    } else if (_currentPath != NavHistory.startDestination) {
      navHistory.clear();
      context.go(NavHistory.startDestination);
    }
  }
}

// ── Desktop Sidebar ──────────────────────────────────────

class _DesktopSidebar extends StatefulWidget {
  final String currentPath;
  final ValueChanged<String> onNavigate;

  const _DesktopSidebar({
    required this.currentPath,
    required this.onNavigate,
  });

  @override
  State<_DesktopSidebar> createState() => _DesktopSidebarState();
}

class _DesktopSidebarState extends State<_DesktopSidebar> {
  bool _collapsed = false;

  @override
  Widget build(BuildContext context) {
    final sidebarWidth = _collapsed ? 72.0 : 220.0;
    final auth = context.watch<AuthProvider>();
    final user = auth.user;

    return AnimatedContainer(
      duration: const Duration(milliseconds: 250),
      curve: Curves.easeInOut,
      width: sidebarWidth,
      clipBehavior: Clip.hardEdge,
      decoration: BoxDecoration(
        color: _sidebarBg,
        border: Border(
          right: BorderSide(color: _sidebarBorder, width: 1),
        ),
      ),
      child: Column(
              children: [
                const SizedBox(height: 24),
                // Logo + collapse toggle — uses LayoutBuilder to avoid
                // overflow during the AnimatedContainer width transition.
                LayoutBuilder(
                  builder: (context, constraints) {
                    if (constraints.maxWidth < 160) {
                      // Narrow / collapsed: stacked icon + toggle
                      return Column(
                        children: [
                          Image.asset('assets/icons/buoy_logo.png',
                              width: 30, height: 30),
                          const SizedBox(height: 8),
                          _SidebarIconButton(
                            icon: Icons.chevron_right,
                            onTap: () => setState(() => _collapsed = !_collapsed),
                            tooltip: _collapsed ? 'Expand' : 'Collapse',
                          ),
                        ],
                      );
                    }
                    // Wide / expanded: logo text + collapse button
                    return Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 16),
                      child: Row(
                        children: [
                          Image.asset('assets/icons/buoy_logo.png',
                              width: 30, height: 30),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Text(
                              AppLocalizations.of(context).appTitle,
                              style: GoogleFonts.sora(
                                fontSize: 18,
                                fontWeight: FontWeight.bold,
                                color: _textGhost,
                              ),
                            ),
                          ),
                          _SidebarIconButton(
                            icon: Icons.chevron_left,
                            onTap: () => setState(() => _collapsed = true),
                            tooltip: 'Collapse',
                          ),
                        ],
                      ),
                    );
                  },
                ),
                const SizedBox(height: 28),

                // Navigation items
                Expanded(
                  child: ListView(
                    padding: EdgeInsets.symmetric(
                        horizontal: _collapsed ? 8 : 12),
                    children: [
                      // Profile is reached via the tappable user footer below (and stays in the
                      // mobile "More" sheet, which has no such footer), so hide it from the sidebar.
                      for (final dest in _visibleDestinations(user?.subscriptionTier)
                          .where((d) => d.key != 'profile'))
                        _SidebarNavItem(
                          destination: dest,
                          isActive: widget.currentPath == dest.path,
                          collapsed: _collapsed,
                          onTap: () => widget.onNavigate(dest.path),
                        ),
                    ],
                  ),
                ),

                // User info + logout at bottom
                Padding(
                  padding: EdgeInsets.symmetric(
                      horizontal: _collapsed ? 8 : 12, vertical: 16),
                  child: Column(
                    children: [
                      Container(
                        height: 1,
                        color: Colors.white.withValues(alpha: 0.06),
                      ),
                      const SizedBox(height: 12),
                      // Tappable user chip → Profile page (replaces the removed Profile nav item).
                      if (user != null)
                        Padding(
                          padding: const EdgeInsets.only(bottom: 8),
                          child: Material(
                            color: Colors.transparent,
                            borderRadius: BorderRadius.circular(10),
                            clipBehavior: Clip.antiAlias,
                            child: InkWell(
                              onTap: () => widget.onNavigate('/profile'),
                              borderRadius: BorderRadius.circular(10),
                              child: Tooltip(
                                message: 'View profile',
                                child: Padding(
                                  padding: EdgeInsets.symmetric(
                                      horizontal: _collapsed ? 0 : 6,
                                      vertical: 6),
                                  child: Row(
                                    mainAxisAlignment: _collapsed
                                        ? MainAxisAlignment.center
                                        : MainAxisAlignment.start,
                                    children: [
                                      _UserAvatar(user: user, radius: 14),
                                      if (!_collapsed) ...[
                                        const SizedBox(width: 10),
                                        Expanded(
                                          child: Text(
                                            user.firstName.isNotEmpty
                                                ? user.firstName
                                                : user.email.split('@')[0],
                                            style: GoogleFonts.sora(
                                              fontSize: 12,
                                              color: _textMuted,
                                            ),
                                            maxLines: 1,
                                            overflow: TextOverflow.ellipsis,
                                          ),
                                        ),
                                        Icon(
                                          Icons.chevron_right,
                                          size: 16,
                                          color: _textMuted
                                              .withValues(alpha: 0.6),
                                        ),
                                      ],
                                    ],
                                  ),
                                ),
                              ),
                            ),
                          ),
                        ),
                      _SidebarNavItem(
                        destination: const _NavDestination(
                          key: 'logout',
                          label: 'Sign Out',
                          icon: Icons.logout,
                          selectedIcon: Icons.logout,
                          path: '',
                        ),
                        isActive: false,
                        collapsed: _collapsed,
                        onTap: () async {
                          await auth.logout();
                        },
                        color: const Color(0xFFF87171),
                      ),
                    ],
                  ),
                ),
              ],
            ),
    );
  }
}

class _SidebarNavItem extends StatefulWidget {
  final _NavDestination destination;
  final bool isActive;
  final bool collapsed;
  final VoidCallback onTap;
  final Color? color;

  const _SidebarNavItem({
    required this.destination,
    required this.isActive,
    required this.collapsed,
    required this.onTap,
    this.color,
  });

  @override
  State<_SidebarNavItem> createState() => _SidebarNavItemState();
}

class _SidebarNavItemState extends State<_SidebarNavItem> {
  @override
  Widget build(BuildContext context) {
    final effectiveColor = widget.color ?? _textGhost;
    final activeColor = widget.color ?? _accent;

    return Padding(
      padding: const EdgeInsets.only(bottom: 2),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: widget.onTap,
          borderRadius: BorderRadius.circular(10),
          hoverColor: Colors.white.withValues(alpha: 0.04),
          splashColor: Colors.white.withValues(alpha: 0.06),
          child: AnimatedContainer(
            duration: const Duration(milliseconds: 180),
            padding: EdgeInsets.symmetric(
              vertical: 10,
              horizontal: widget.collapsed ? 0 : 12,
            ),
            decoration: BoxDecoration(
              color: widget.isActive
                  ? _accent.withValues(alpha: 0.12)
                  : Colors.transparent,
              borderRadius: BorderRadius.circular(10),
              border: widget.isActive
                  ? Border.all(
                      color: _accent.withValues(alpha: 0.2), width: 1)
                  : null,
            ),
            child: LayoutBuilder(
              builder: (context, constraints) {
                final narrow = constraints.maxWidth < 80;
                return narrow
                ? Tooltip(
                    message: widget.destination.label,
                    child: Center(
                      child: Icon(
                        widget.isActive
                            ? widget.destination.selectedIcon
                            : widget.destination.icon,
                        size: 20,
                        color:
                            widget.isActive ? activeColor : effectiveColor.withValues(alpha: 0.6),
                      ),
                    ),
                  )
                : Row(
                    children: [
                      Icon(
                        widget.isActive
                            ? widget.destination.selectedIcon
                            : widget.destination.icon,
                        size: 18,
                        color:
                            widget.isActive ? activeColor : effectiveColor.withValues(alpha: 0.6),
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Text(
                          widget.destination.label,
                          style: GoogleFonts.sora(
                            fontSize: 13,
                            fontWeight: widget.isActive
                                ? FontWeight.w600
                                : FontWeight.w400,
                            color: widget.isActive
                                ? activeColor
                                : effectiveColor.withValues(alpha: 0.7),
                          ),
                        ),
                      ),
                      if (widget.destination.isPro)
                        Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 5,
                            vertical: 1,
                          ),
                          decoration: BoxDecoration(
                            color: _accent.withValues(alpha: 0.15),
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            AppLocalizations.of(context).uiBadgePro,
                            style: GoogleFonts.firaCode(
                              fontSize: 9,
                              fontWeight: FontWeight.w700,
                              color: _accent,
                            ),
                          ),
                        ),
                      if (widget.destination.isAdmin)
                        Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 5,
                            vertical: 1,
                          ),
                          decoration: BoxDecoration(
                            color: const Color(0xFFFBBF24).withValues(alpha: 0.15),
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            AppLocalizations.of(context).uiBadgeDev,
                            style: GoogleFonts.firaCode(
                              fontSize: 9,
                              fontWeight: FontWeight.w700,
                              color: const Color(0xFFFBBF24),
                            ),
                          ),
                        ),
                    ],
                  );
              },
            ),
          ),
        ),
      ),
    );
  }
}

class _SidebarIconButton extends StatelessWidget {
  final IconData icon;
  final VoidCallback onTap;
  final String tooltip;

  const _SidebarIconButton({
    required this.icon,
    required this.onTap,
    required this.tooltip,
  });

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: tooltip,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(8),
          child: Container(
            padding: const EdgeInsets.all(6),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(8),
            ),
            child: Icon(icon, size: 18, color: _textMuted),
          ),
        ),
      ),
    );
  }
}

// ── Mobile Bottom Navigation ─────────────────────────────

class _MobileBottomNav extends StatelessWidget {
  final String currentPath;
  final List<String> bottomNavKeys;
  final ValueChanged<String> onNavigate;
  final ValueChanged<List<String>> onSaveKeys;

  const _MobileBottomNav({
    required this.currentPath,
    required this.bottomNavKeys,
    required this.onNavigate,
    required this.onSaveKeys,
  });

  @override
  Widget build(BuildContext context) {
    final bottomNavDests = _allDestinations
        // A saved layout may still hold Chat after the server reports it off.
        .where((d) => bottomNavKeys.contains(d.key) && (d.key != 'ai-chat' || aiAvailability.featureOn('chat')))
        .toList()
      ..sort((a, b) => bottomNavKeys.indexOf(a.key).compareTo(bottomNavKeys.indexOf(b.key)));

    return ClipRect(
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
        child: Container(
          decoration: BoxDecoration(
            color: const Color(0xFF04040A).withValues(alpha: 0.6),
            border: Border(
              top: BorderSide(
                  color: Colors.white.withValues(alpha: 0.06), width: 1),
            ),
          ),
          child: SafeArea(
            top: false,
            child: Padding(
              padding:
                  const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
              child: Row(
                // Equal-width slots so labels never crowd each other; each item
                // shrinks its own label to fit (see _BottomNavItem).
                children: [
                  for (final dest in bottomNavDests)
                    Expanded(
                      child: _BottomNavItem(
                        destination: dest,
                        isActive: currentPath == dest.path,
                        onTap: () => onNavigate(dest.path),
                      ),
                    ),
                  // "More" button
                  Expanded(
                    child: _BottomNavItem(
                      destination: const _NavDestination(
                        key: 'more',
                        label: 'More',
                        icon: Icons.grid_view_rounded,
                        selectedIcon: Icons.grid_view_rounded,
                        path: '',
                      ),
                      isActive: !bottomNavKeys.any((k) {
                        final dest = _allDestinations
                            .where((d) => d.key == k)
                            .firstOrNull;
                        return dest != null && dest.path == currentPath;
                      }),
                      onTap: () => _showMoreSheet(context),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  void _showMoreSheet(BuildContext context) {
    final auth = Provider.of<AuthProvider>(context, listen: false);
    final moreItems = _visibleDestinations(auth.user?.subscriptionTier)
        .where((d) => !bottomNavKeys.contains(d.key))
        .toList();

    showModalBottomSheet(
      context: context,
      backgroundColor: Colors.transparent,
      isScrollControlled: true,
      builder: (ctx) => _MoreSheet(
        items: moreItems,
        currentPath: currentPath,
        bottomNavKeys: bottomNavKeys,
        allVisibleDestinations: _visibleDestinations(auth.user?.subscriptionTier),
        onNavigate: (path) {
          Navigator.pop(ctx);
          onNavigate(path);
        },
        onSaveKeys: (keys) {
          Navigator.pop(ctx);
          onSaveKeys(keys);
        },
        onLogout: () async {
          Navigator.pop(ctx);
          final auth =
              Provider.of<AuthProvider>(context, listen: false);
          await auth.logout();
        },
      ),
    );
  }
}

class _BottomNavItem extends StatelessWidget {
  final _NavDestination destination;
  final bool isActive;
  final VoidCallback onTap;

  const _BottomNavItem({
    required this.destination,
    required this.isActive,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      behavior: HitTestBehavior.opaque,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 200),
        margin: const EdgeInsets.symmetric(horizontal: 3),
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 6),
        decoration: BoxDecoration(
          color: isActive
              ? _accent.withValues(alpha: 0.12)
              : Colors.transparent,
          borderRadius: BorderRadius.circular(12),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Icon(
              isActive ? destination.selectedIcon : destination.icon,
              size: 22,
              color: isActive ? _accent : _textMuted,
            ),
            const SizedBox(height: 3),
            // Show the FULL label, shrinking to fit the slot rather than
            // chopping characters ("Dashboa…"). FittedBox keeps it on one line.
            FittedBox(
              fit: BoxFit.scaleDown,
              child: Text(
                destination.label,
                maxLines: 1,
                style: GoogleFonts.sora(
                  fontSize: 10,
                  fontWeight: isActive ? FontWeight.w600 : FontWeight.w400,
                  color: isActive ? _accent : _textMuted,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

// ── More Sheet (mobile) ──────────────────────────────────

class _MoreSheet extends StatefulWidget {
  final List<_NavDestination> items;
  final String currentPath;
  final List<String> bottomNavKeys;
  final List<_NavDestination> allVisibleDestinations;
  final ValueChanged<String> onNavigate;
  final ValueChanged<List<String>> onSaveKeys;
  final VoidCallback onLogout;

  const _MoreSheet({
    required this.items,
    required this.currentPath,
    required this.bottomNavKeys,
    required this.allVisibleDestinations,
    required this.onNavigate,
    required this.onSaveKeys,
    required this.onLogout,
  });

  @override
  State<_MoreSheet> createState() => _MoreSheetState();
}

class _MoreSheetState extends State<_MoreSheet> {
  bool _isCustomizing = false;
  late List<String> _tempKeys;

  @override
  void initState() {
    super.initState();
    _tempKeys = List.from(widget.bottomNavKeys);
  }

  void _toggleItem(String key) {
    setState(() {
      if (_tempKeys.contains(key)) {
        if (_tempKeys.length > 1) _tempKeys.remove(key);
      } else if (_tempKeys.length < 4) {
        _tempKeys.add(key);
      }
    });
  }

  void _moveUp(String key) {
    final idx = _tempKeys.indexOf(key);
    if (idx > 0) {
      setState(() {
        final item = _tempKeys.removeAt(idx);
        _tempKeys.insert(idx - 1, item);
      });
    }
  }

  void _moveDown(String key) {
    final idx = _tempKeys.indexOf(key);
    if (idx < _tempKeys.length - 1) {
      setState(() {
        final item = _tempKeys.removeAt(idx);
        _tempKeys.insert(idx + 1, item);
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: 24, sigmaY: 24),
        child: Container(
          constraints: BoxConstraints(
            maxHeight: MediaQuery.of(context).size.height * 0.8,
          ),
          decoration: BoxDecoration(
            color: const Color(0xFF0A0A14).withValues(alpha: 0.92),
            borderRadius:
                const BorderRadius.vertical(top: Radius.circular(20)),
            border: Border(
              top: BorderSide(
                  color: Colors.white.withValues(alpha: 0.08), width: 1),
            ),
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              // Drag handle
              Container(
                margin: const EdgeInsets.only(top: 12),
                width: 40,
                height: 4,
                decoration: BoxDecoration(
                  color: _textMuted.withValues(alpha: 0.3),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
              // Header
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 16, 20, 8),
                child: Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  children: [
                    Text(
                      _isCustomizing
                          ? AppLocalizations.of(context).uiCustomizeQuickAccess
                          : AppLocalizations.of(context).uiMore,
                      style: GoogleFonts.sora(
                        fontSize: 18,
                        fontWeight: FontWeight.bold,
                        color: _textGhost,
                      ),
                    ),
                    if (!_isCustomizing)
                      GestureDetector(
                        onTap: () => setState(() => _isCustomizing = true),
                        child: Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Icon(Icons.edit_outlined,
                                size: 14, color: _accent),
                            const SizedBox(width: 4),
                            Text(
                              AppLocalizations.of(context).uiEdit,
                              style: GoogleFonts.sora(
                                fontSize: 13,
                                color: _accent,
                                fontWeight: FontWeight.w500,
                              ),
                            ),
                          ],
                        ),
                      ),
                  ],
                ),
              ),
              Container(
                height: 1,
                color: Colors.white.withValues(alpha: 0.06),
              ),
              // Content
              Flexible(
                child: _isCustomizing
                    ? _buildCustomizeView()
                    : _buildNormalView(),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildNormalView() {
    return ListView(
      shrinkWrap: true,
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      children: [
        // Quick access items
        if (widget.bottomNavKeys.isNotEmpty) ...[
          Padding(
            padding: const EdgeInsets.fromLTRB(8, 4, 8, 4),
            child: Text(
              AppLocalizations.of(context).uiQuickAccess,
              style: GoogleFonts.sora(
                fontSize: 10,
                letterSpacing: 1.2,
                fontWeight: FontWeight.w600,
                color: _textMuted,
              ),
            ),
          ),
          for (final key in widget.bottomNavKeys)
            if (_allDestinations.any((d) => d.key == key))
              _MoreSheetItem(
                destination: _allDestinations.firstWhere((d) => d.key == key),
                isActive: widget.currentPath ==
                    _allDestinations.firstWhere((d) => d.key == key).path,
                onTap: () => widget.onNavigate(
                    _allDestinations.firstWhere((d) => d.key == key).path),
              ),
          const SizedBox(height: 8),
          Container(
            height: 1,
            color: Colors.white.withValues(alpha: 0.06),
          ),
          const SizedBox(height: 8),
        ],
        // More items (not in quick access)
        for (final item in widget.items)
          _MoreSheetItem(
            destination: item,
            isActive: widget.currentPath == item.path,
            onTap: () => widget.onNavigate(item.path),
          ),
        const SizedBox(height: 8),
        Container(
          height: 1,
          color: Colors.white.withValues(alpha: 0.06),
        ),
        const SizedBox(height: 8),
        _MoreSheetItem(
          destination: const _NavDestination(
            key: 'logout',
            label: 'Sign Out',
            icon: Icons.logout,
            selectedIcon: Icons.logout,
            path: '',
          ),
          isActive: false,
          onTap: widget.onLogout,
          color: const Color(0xFFF87171),
        ),
        const SizedBox(height: 16),
      ],
    );
  }

  Widget _buildCustomizeView() {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 8, 20, 4),
          child: Text(
            AppLocalizations.of(context).uiQuickAccessInstructions,
            style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
          ),
        ),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 20),
          child: Text(
            AppLocalizations.of(context).uiSelectedCount(_tempKeys.length),
            style: GoogleFonts.firaCode(fontSize: 11, color: _textMuted),
          ),
        ),
        const SizedBox(height: 8),
        Flexible(
          child: ListView(
            shrinkWrap: true,
            padding: const EdgeInsets.symmetric(horizontal: 12),
            children: [
              for (final dest in widget.allVisibleDestinations)
                _buildCustomizeRow(dest),
            ],
          ),
        ),
        Container(
          height: 1,
          color: Colors.white.withValues(alpha: 0.06),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.end,
            children: [
              GestureDetector(
                onTap: () => setState(() {
                  _tempKeys = List.from(widget.bottomNavKeys);
                  _isCustomizing = false;
                }),
                child: Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
                  child: Text(
                    AppLocalizations.of(context).uiCancel,
                    style: GoogleFonts.sora(
                      fontSize: 13,
                      color: _textMuted,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              GestureDetector(
                onTap: _tempKeys.length == 4
                    ? () => widget.onSaveKeys(_tempKeys)
                    : null,
                child: Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
                  decoration: BoxDecoration(
                    color: _tempKeys.length == 4
                        ? _accent
                        : _accent.withValues(alpha: 0.3),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Text(
                    AppLocalizations.of(context).uiSave,
                    style: GoogleFonts.sora(
                      fontSize: 13,
                      color: Colors.white,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildCustomizeRow(_NavDestination dest) {
    final isSelected = _tempKeys.contains(dest.key);
    final canSelect = isSelected || _tempKeys.length < 4;
    final idx = _tempKeys.indexOf(dest.key);

    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: GestureDetector(
        onTap: canSelect ? () => _toggleItem(dest.key) : null,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: isSelected
                ? _accent.withValues(alpha: 0.08)
                : Colors.transparent,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
              color: isSelected
                  ? _accent.withValues(alpha: 0.25)
                  : Colors.white.withValues(alpha: 0.06),
            ),
          ),
          child: Row(
            children: [
              // Checkbox
              Container(
                width: 20,
                height: 20,
                decoration: BoxDecoration(
                  color: isSelected
                      ? _accent
                      : Colors.white.withValues(alpha: 0.06),
                  borderRadius: BorderRadius.circular(4),
                  border: isSelected
                      ? null
                      : Border.all(
                          color: Colors.white.withValues(alpha: 0.15)),
                ),
                child: isSelected
                    ? const Icon(Icons.check, size: 14, color: Colors.white)
                    : null,
              ),
              const SizedBox(width: 12),
              Icon(
                dest.icon,
                size: 18,
                color: isSelected
                    ? _accent
                    : _textGhost.withValues(alpha: 0.6),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Text(
                  dest.label,
                  style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight:
                        isSelected ? FontWeight.w600 : FontWeight.w400,
                    color:
                        isSelected ? _accent : _textGhost.withValues(alpha: 0.8),
                  ),
                ),
              ),
              if (dest.isPro)
                Padding(
                  padding: const EdgeInsets.only(right: 4),
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                        horizontal: 5, vertical: 1),
                    decoration: BoxDecoration(
                      color: _accent.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text(
                      AppLocalizations.of(context).uiBadgePro,
                      style: GoogleFonts.firaCode(
                          fontSize: 9,
                          fontWeight: FontWeight.w700,
                          color: _accent),
                    ),
                  ),
                ),
              if (dest.isAdmin)
                Padding(
                  padding: const EdgeInsets.only(right: 4),
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                        horizontal: 5, vertical: 1),
                    decoration: BoxDecoration(
                      color:
                          const Color(0xFFFBBF24).withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text(
                      AppLocalizations.of(context).uiBadgeDev,
                      style: GoogleFonts.firaCode(
                          fontSize: 9,
                          fontWeight: FontWeight.w700,
                          color: const Color(0xFFFBBF24)),
                    ),
                  ),
                ),
              if (isSelected) ...[
                // Position badge
                Container(
                  padding: const EdgeInsets.symmetric(
                      horizontal: 6, vertical: 2),
                  decoration: BoxDecoration(
                    color: _accent.withValues(alpha: 0.2),
                    borderRadius: BorderRadius.circular(4),
                  ),
                  child: Text(
                    '${idx + 1}',
                    style: GoogleFonts.firaCode(
                        fontSize: 10,
                        fontWeight: FontWeight.w600,
                        color: _accent),
                  ),
                ),
                const SizedBox(width: 4),
                // Move up
                GestureDetector(
                  onTap: idx > 0 ? () => _moveUp(dest.key) : null,
                  child: Icon(
                    Icons.keyboard_arrow_up,
                    size: 20,
                    color: idx > 0
                        ? _accent
                        : _textMuted.withValues(alpha: 0.3),
                  ),
                ),
                // Move down
                GestureDetector(
                  onTap: idx < _tempKeys.length - 1
                      ? () => _moveDown(dest.key)
                      : null,
                  child: Icon(
                    Icons.keyboard_arrow_down,
                    size: 20,
                    color: idx < _tempKeys.length - 1
                        ? _accent
                        : _textMuted.withValues(alpha: 0.3),
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

class _MoreSheetItem extends StatelessWidget {
  final _NavDestination destination;
  final bool isActive;
  final VoidCallback onTap;
  final Color? color;

  const _MoreSheetItem({
    required this.destination,
    required this.isActive,
    required this.onTap,
    this.color,
  });

  @override
  Widget build(BuildContext context) {
    final effectiveColor = color ?? _textGhost;

    return Padding(
      padding: const EdgeInsets.only(bottom: 2),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(12),
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
            decoration: BoxDecoration(
              color: isActive
                  ? _accent.withValues(alpha: 0.1)
                  : Colors.transparent,
              borderRadius: BorderRadius.circular(12),
            ),
            child: Row(
              children: [
                Icon(
                  isActive
                      ? destination.selectedIcon
                      : destination.icon,
                  size: 20,
                  color: isActive ? _accent : effectiveColor.withValues(alpha: 0.6),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Text(
                    destination.label,
                    style: GoogleFonts.sora(
                      fontSize: 14,
                      fontWeight:
                          isActive ? FontWeight.w600 : FontWeight.w400,
                      color: isActive ? _accent : effectiveColor.withValues(alpha: 0.8),
                    ),
                  ),
                ),
                if (destination.isPro)
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 2,
                    ),
                    decoration: BoxDecoration(
                      color: _accent.withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text(
                      AppLocalizations.of(context).uiBadgePro,
                      style: GoogleFonts.firaCode(
                        fontSize: 9,
                        fontWeight: FontWeight.w700,
                        color: _accent,
                      ),
                    ),
                  ),
                if (destination.isAdmin)
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 2,
                    ),
                    decoration: BoxDecoration(
                      color: const Color(0xFFFBBF24).withValues(alpha: 0.15),
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text(
                      AppLocalizations.of(context).uiBadgeDev,
                      style: GoogleFonts.firaCode(
                        fontSize: 9,
                        fontWeight: FontWeight.w700,
                        color: const Color(0xFFFBBF24),
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

/// Avatar for the signed-in user: the OAuth provider's photo when we have one,
/// otherwise initials.
///
/// Google supplies a photo URL and we refresh it on every login. Apple never
/// does (its ID token carries only email/email_verified), so Apple accounts land
/// on initials by design rather than by omission. A network failure or a stale
/// Google URL also falls back to initials instead of a broken-image box.
class _UserAvatar extends StatelessWidget {
  const _UserAvatar({required this.user, required this.radius});

  final User user;
  final double radius;

  @override
  Widget build(BuildContext context) {
    final initials = Text(
      user.initials,
      style: GoogleFonts.sora(
        fontSize: radius * 0.85,
        color: _accent,
        fontWeight: FontWeight.w600,
      ),
    );

    final url = user.effectiveAvatar;
    if (url == null) {
      return CircleAvatar(
        radius: radius,
        backgroundColor: _accent.withValues(alpha: 0.2),
        child: initials,
      );
    }

    return CircleAvatar(
      radius: radius,
      backgroundColor: _accent.withValues(alpha: 0.2),
      child: ClipOval(
        child: Image.network(
          url,
          width: radius * 2,
          height: radius * 2,
          fit: BoxFit.cover,
          errorBuilder: (_, __, ___) => Center(child: initials),
        ),
      ),
    );
  }
}
