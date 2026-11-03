import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import 'cinematic/cinematic_nav_shell.dart'
    show kCinematicBottomNavHeight, kCinematicDesktopBreakpoint;

/// Semantic kind of a toast shown by [showAppToast].
enum AppToastType { success, error, neutral }

// ── VaporClinic palette ─────────────────────────────────
const _success = Color(0xFF34D399);
const _error = Color(0xFFF87171);
const _neutralBg = Color(0xFF0C0C18);

// Foreground on the light success/error fills; the app background colour reads
// ~10:1 against both, where the usual ghost-white text would be under 2:1.
const _onColored = Color(0xFF04040A);
const _onNeutral = Color(0xFFF0EFF4);

/// Gap between the toast and the bottom nav above which it floats.
const double _kNavGap = 12;

/// Inset from the bottom edge when there is no bottom nav to clear.
const double _kEdgeGap = 16;

/// Shows an on-brand toast above the cinematic bottom nav.
///
/// Always prefer this over a bare `ScaffoldMessenger.showSnackBar`: the app
/// defines no `snackBarTheme`, so an unstyled SnackBar renders as the Material 3
/// default — a near-white bar on a `#04040A` app — and a default one is `fixed`,
/// pinning it flush to the bottom edge underneath the nav bar and inside the
/// Android gesture zone.
///
/// Set [clearsBottomNav] to false on routes outside `CinematicNavShell` (login,
/// register, invitation accept), which have no bottom nav to clear. The gesture
/// inset is always applied regardless.
void showAppToast(
  BuildContext context,
  String message, {
  AppToastType type = AppToastType.neutral,
  String? actionLabel,
  VoidCallback? onAction,
  Duration duration = const Duration(seconds: 4),
  bool clearsBottomNav = true,
}) {
  final background = switch (type) {
    AppToastType.success => _success,
    AppToastType.error => _error,
    AppToastType.neutral => _neutralBg,
  };
  final foreground = type == AppToastType.neutral ? _onNeutral : _onColored;

  // On desktop the nav is a sidebar, so only the mobile bottom nav needs
  // clearing. Uses the shell's own breakpoint so the two can't disagree —
  // a phone in landscape can exceed it. `sizeOf` is unaffected by the shell's
  // padding strip below.
  final isDesktop =
      MediaQuery.sizeOf(context).width > kCinematicDesktopBreakpoint;
  final showsBottomNav = clearsBottomNav && !isDesktop;

  // The shell wraps every page in `MediaQuery.removePadding(removeBottom: true)`,
  // which zeroes *both* `padding.bottom` and `viewPadding.bottom` — so a page
  // context cannot report the gesture-bar inset. Read it from the view, which
  // ignores ancestor MediaQuery overrides. A floating SnackBar builds itself
  // with `SafeArea(bottom: false)`, so this inset is ours to add — on every
  // route, since even a page with no bottom nav must clear the gesture bar.
  final safeBottom = MediaQueryData.fromView(View.of(context)).padding.bottom;

  final bottomInset = safeBottom +
      (showsBottomNav ? kCinematicBottomNavHeight + _kNavGap : _kEdgeGap);

  ScaffoldMessenger.of(context)
    // Replace rather than queue: acting on several rows in a row otherwise
    // banks up a backlog of toasts that each have to time out in turn.
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(
      content: Text(message, style: GoogleFonts.sora(fontSize: 13, color: foreground)),
      backgroundColor: background,
      behavior: SnackBarBehavior.floating,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
      margin: EdgeInsets.only(left: 16, right: 16, bottom: bottomInset),
      // The SnackBar default is `DismissDirection.down`, which on Android
      // fights the system notification-shade pull — the toast cannot be swiped
      // away at all. Sideways is unclaimed.
      dismissDirection: DismissDirection.horizontal,
      duration: duration,
      action: (actionLabel == null || onAction == null)
          ? null
          : SnackBarAction(
              label: actionLabel,
              textColor: foreground,
              onPressed: onAction,
            ),
    ));
}
