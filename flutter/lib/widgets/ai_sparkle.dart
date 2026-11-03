import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';

import '../providers/auth_provider.dart';

/// BuoyAI's "AI-powered" design language — a premium visual cue applied to
/// content that was generated or enhanced by AI (auto-categorized transactions,
/// AI insight descriptions, etc.). See issue #163.
///
/// Uses a cyan → purple → pink gradient that nods to the VaporClinic palette
/// while feeling distinctly AI-flavored. The gradient is only applied for
/// premium subscribers; free/trial users see a solid accent color instead.

/// Canonical "AI" gradient — cyan → violet → pink.
const LinearGradient kAiGradient = LinearGradient(
  colors: [
    Color(0xFF22D3EE), // cyan
    Color(0xFFA78BFA), // violet
    Color(0xFFF472B6), // pink
  ],
  begin: Alignment.centerLeft,
  end: Alignment.centerRight,
);

/// Muted solid-accent "gradient" (single color expressed as a two-stop
/// linear gradient for API compatibility with [kAiGradient]). Available for
/// non-premium AI surfaces (suggestion chips, hover-free row accents) where
/// we still want a hint of AI flavor without the full rainbow pop. Note:
/// [AiBadge] does not use this — it falls back to solid confidence colors.
const LinearGradient kAiGradientMuted = LinearGradient(
  colors: [
    Color(0xFF7B61FF),
    Color(0xFF7B61FF),
  ],
  begin: Alignment.centerLeft,
  end: Alignment.centerRight,
);

/// Reads the current user's subscription tier from [AuthProvider] and returns
/// true when they should see the premium AI visual treatment. Returns false
/// if no [AuthProvider] is available in the widget tree (e.g. isolated
/// widget tests that don't wrap the tree in providers).
bool userHasPremiumAiStyling(BuildContext context) {
  // Nullable lookup — returns null when no AuthProvider is above this widget
  // in the tree (common in isolated widget tests). Avoids using exceptions
  // for control flow.
  final auth = context.watch<AuthProvider?>();
  final tier = (auth?.user?.subscriptionTier ?? '').toLowerCase();
  return tier == 'premium' || tier == 'premiuminherited' || tier == 'admin';
}

/// A small `auto_awesome` icon + confidence label that renders with the AI
/// gradient when the user is premium, and as a flat accent-colored badge
/// otherwise. Drop-in replacement for the prior inline `_AiBadge`.
class AiBadge extends StatelessWidget {
  final double? confidence;

  /// When true, forces premium styling regardless of the current user's tier.
  /// Useful for testing/screenshots.
  final bool forcePremium;

  const AiBadge({super.key, this.confidence, this.forcePremium = false});

  @override
  Widget build(BuildContext context) {
    final premium = forcePremium || userHasPremiumAiStyling(context);
    final conf = confidence ?? 0;
    final confLabel =
        confidence != null ? '${(conf * 100).toInt()}%' : null;

    if (!premium) {
      // Solid-color treatment for non-premium tiers.
      final color = conf >= 0.8
          ? const Color(0xFF34D399)
          : conf >= 0.5
              ? const Color(0xFFFBBF24)
              : const Color(0xFFF87171);
      return Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.auto_awesome,
              size: 11, color: color.withValues(alpha: 0.7)),
          if (confLabel != null) ...[
            const SizedBox(width: 2),
            Text(confLabel,
                style: GoogleFonts.firaCode(fontSize: 9, color: color)),
          ],
        ],
      );
    }

    // Premium: gradient icon + gradient confidence text
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        ShaderMask(
          shaderCallback: (bounds) => kAiGradient.createShader(bounds),
          blendMode: BlendMode.srcIn,
          child: const Icon(Icons.auto_awesome, size: 12, color: Colors.white),
        ),
        if (confLabel != null) ...[
          const SizedBox(width: 3),
          ShaderMask(
            shaderCallback: (bounds) => kAiGradient.createShader(bounds),
            blendMode: BlendMode.srcIn,
            child: Text(confLabel,
                style: GoogleFonts.firaCode(
                  fontSize: 9,
                  fontWeight: FontWeight.w600,
                  color: Colors.white,
                )),
          ),
        ],
      ],
    );
  }
}

/// Subtle AI gradient accent applied to the LEFT edge of a transaction row
/// when it was auto-categorized by AI. Premium-only; renders nothing when
/// the user is on a lower tier so the UI does not regress.
class AiRowAccent extends StatelessWidget {
  final Widget child;
  final bool active;

  const AiRowAccent({super.key, required this.child, this.active = true});

  @override
  Widget build(BuildContext context) {
    if (!active || !userHasPremiumAiStyling(context)) {
      return child;
    }

    return Stack(
      children: [
        // Render the child first so the gradient strip paints ON TOP of it,
        // ensuring the accent stays visible even when child has an opaque
        // background.
        child,
        // Thin gradient strip on the left edge — reads as "AI touched this".
        Positioned(
          left: 0,
          top: 6,
          bottom: 6,
          child: IgnorePointer(
            child: Container(
              width: 2,
              decoration: const BoxDecoration(
                gradient: kAiGradient,
                borderRadius: BorderRadius.all(Radius.circular(2)),
              ),
            ),
          ),
        ),
      ],
    );
  }
}

/// Renders [text] with monetary amounts (e.g. "$123.45"), percentages
/// ("18%"), and a small set of finance keywords bolded. Premium users get
/// the gradient ShaderMask on the highlighted spans; non-premium users get
/// bold highlights, with numeric matches using the accent color and keyword
/// matches remaining in the base text color.
class AiHighlightedText extends StatelessWidget {
  final String text;
  final TextStyle baseStyle;
  final int? maxLines;
  final TextOverflow? overflow;
  final bool forcePremium;

  const AiHighlightedText({
    super.key,
    required this.text,
    required this.baseStyle,
    this.maxLines,
    this.overflow,
    this.forcePremium = false,
  });

  // Money ($12, $1234, $1,234.56), percentages (12%, 12.5%). Supports both
  // comma-grouped and plain-digit amounts of any length.
  static final RegExp _numericPattern = RegExp(
    r'(\$(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?|\d+(?:\.\d+)?%)',
    caseSensitive: false,
  );

  // Keywords worth emphasizing in finance insights. Case-insensitive whole-word.
  // Plural forms (subscriptions, expenses, etc.) handled via `s?` suffixes.
  static final RegExp _keywordPattern = RegExp(
    r'\b(?:savings?|budgets?|overspend(?:ing)?|incomes?|expenses?|balances?|subscriptions?|recurring|trend(?:ing|s)?|increase(?:d|s)?|decrease(?:d|s)?|saved|earned|spent|cashflow|goals?|streaks?)\b',
    caseSensitive: false,
  );

  @override
  Widget build(BuildContext context) {
    final premium = forcePremium || userHasPremiumAiStyling(context);
    final spans = _buildSpans(text, baseStyle, premium);

    return Text.rich(
      TextSpan(children: spans),
      maxLines: maxLines,
      overflow: overflow ?? TextOverflow.clip,
    );
  }

  List<InlineSpan> _buildSpans(String input, TextStyle base, bool premium) {
    // Collect non-overlapping match ranges from both patterns, sort by start.
    final matches = <_HighlightRange>[];
    for (final m in _numericPattern.allMatches(input)) {
      matches.add(_HighlightRange(m.start, m.end, _HighlightKind.numeric));
    }
    for (final m in _keywordPattern.allMatches(input)) {
      // Skip if overlapping with a numeric match (numerics win).
      final overlaps = matches.any((r) =>
          (m.start >= r.start && m.start < r.end) ||
          (m.end > r.start && m.end <= r.end));
      if (!overlaps) {
        matches.add(_HighlightRange(m.start, m.end, _HighlightKind.keyword));
      }
    }
    matches.sort((a, b) => a.start.compareTo(b.start));

    final spans = <InlineSpan>[];
    var cursor = 0;
    for (final r in matches) {
      if (cursor < r.start) {
        spans.add(TextSpan(text: input.substring(cursor, r.start), style: base));
      }
      final segment = input.substring(r.start, r.end);
      spans.add(_highlightSpan(segment, base, premium, r.kind));
      cursor = r.end;
    }
    if (cursor < input.length) {
      spans.add(TextSpan(text: input.substring(cursor), style: base));
    }
    return spans;
  }

  InlineSpan _highlightSpan(
      String segment, TextStyle base, bool premium, _HighlightKind kind) {
    final boldStyle = base.copyWith(fontWeight: FontWeight.w700);

    if (!premium) {
      // Muted/accent treatment for non-premium.
      final color = kind == _HighlightKind.numeric
          ? const Color(0xFFA78BFA)
          : base.color;
      return TextSpan(
        text: segment,
        style: boldStyle.copyWith(color: color),
      );
    }

    // Premium: apply the AI gradient to the bolded span via WidgetSpan.
    return WidgetSpan(
      alignment: PlaceholderAlignment.baseline,
      baseline: TextBaseline.alphabetic,
      child: ShaderMask(
        shaderCallback: (bounds) => kAiGradient.createShader(bounds),
        blendMode: BlendMode.srcIn,
        child: Text(
          segment,
          style: boldStyle.copyWith(color: Colors.white),
        ),
      ),
    );
  }
}

enum _HighlightKind { numeric, keyword }

class _HighlightRange {
  final int start;
  final int end;
  final _HighlightKind kind;
  _HighlightRange(this.start, this.end, this.kind);
}
