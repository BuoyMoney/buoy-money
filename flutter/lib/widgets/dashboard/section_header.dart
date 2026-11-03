import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);

/// A dashboard section header: leading icon + title, an optional count badge, and an optional
/// trailing action pinned to the section's right edge.
///
/// The trailing action's *visible* content (the "View All" text, the refresh glyph) is aligned
/// flush with the header's right edge — which equals the card's right edge, since the sections
/// stretch to the same width. This is the whole reason the widget owns the trailing slot:
/// Material's TextButton and IconButton each carry their own internal padding, so dropping a raw
/// button in leaves its glyph floating inward from the edge by a different amount per button
/// type. Every section-header alignment fix before this one patched a single button's padding at
/// its call site, which is why the *other* sections kept drifting. Here the padding is stripped
/// once, centrally, for whatever button is passed — so a new section can't reintroduce the drift.
class DashboardSectionHeader extends StatelessWidget {
  final String title;
  final IconData icon;
  final int? count;

  /// A button (TextButton / IconButton) shown flush against the right edge. Pass it with its
  /// normal styling — this widget neutralizes its intrinsic padding so it lands on the edge.
  final Widget? trailing;

  const DashboardSectionHeader({
    super.key,
    required this.title,
    required this.icon,
    this.count,
    this.trailing,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, size: 18, color: _accent),
        const SizedBox(width: 10),
        // A SINGLE Expanded owns all the space between the leading icon and the trailing action,
        // so the trailing lands flush on the right edge. Do NOT go back to `Flexible(title) + Spacer`:
        // that's two flex:1 children competing for the free space — the short title under-uses its
        // half and the leftover is dumped as empty space past the trailing action, dragging it inward
        // (invisibly at narrow widths, ~180px on a phone). The title still ellipsizes because the
        // inner Flexible shrinks within this Expanded.
        Expanded(
          child: Row(
            children: [
              Flexible(
                child: Text(
                  title,
                  style: GoogleFonts.sora(
                    fontSize: 16,
                    fontWeight: FontWeight.w600,
                    color: _textGhost,
                  ),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              if (count != null && count! > 0) ...[
                const SizedBox(width: 8),
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
                  decoration: BoxDecoration(
                    color: _accent.withValues(alpha: 0.15),
                    borderRadius: BorderRadius.circular(10),
                  ),
                  child: Text(
                    count.toString(),
                    style: GoogleFonts.firaCode(
                      fontSize: 11,
                      fontWeight: FontWeight.w600,
                      color: _accent,
                    ),
                  ),
                ),
              ],
            ],
          ),
        ),
        if (trailing != null) _FlushTrailing(child: trailing!),
      ],
    );
  }
}

/// Strips the intrinsic padding and minimum-size of whatever button sits in the trailing slot so
/// its glyph aligns flush to the header's right edge, regardless of button type. A theme override
/// (rather than wrapping) so it reaches TextButton and IconButton alike without the caller
/// restyling either. Tap target is preserved via the header row's own height and the surrounding
/// hit area — the visible content is what moves, not the touchable region's usefulness.
class _FlushTrailing extends StatelessWidget {
  final Widget child;
  const _FlushTrailing({required this.child});

  @override
  Widget build(BuildContext context) {
    final flush = ButtonStyle(
      padding: const WidgetStatePropertyAll(EdgeInsets.zero),
      minimumSize: const WidgetStatePropertyAll(Size.zero),
      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
      visualDensity: VisualDensity.compact,
    );
    return Theme(
      data: Theme.of(context).copyWith(
        textButtonTheme: TextButtonThemeData(style: flush),
        iconButtonTheme: IconButtonThemeData(style: flush),
      ),
      child: child,
    );
  }
}
