import 'package:flutter/widgets.dart';

import '../widgets/cinematic/cinematic_nav_shell.dart' show kCinematicDesktopBreakpoint;

/// Whether to show a manual refresh/sync button on this layout.
///
/// Pull-to-refresh is a touch gesture: it works on mobile (native and mobile web) but not with a
/// desktop mouse, which can't overscroll-drag a scroll view. So the button is desktop-only —
/// mobile relies on the pull gesture, desktop keeps the button. Uses the same breakpoint at which
/// the nav shell swaps its bottom nav for a sidebar, i.e. the actual mobile↔desktop split.
bool showsManualRefresh(BuildContext context) =>
    MediaQuery.sizeOf(context).width > kCinematicDesktopBreakpoint;
