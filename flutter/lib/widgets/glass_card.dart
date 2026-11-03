import 'package:flutter/material.dart';

/// Canonical cinematic glass card — matches the Dashboard treatment.
///
/// - Translucent white fill (5% white) so the animated background bubbles
///   show through.
/// - Hairline white border at ~8% alpha.
/// - Subtle purple glow (`#7B61FF` at ~3% alpha, 24 blur) that gives the
///   cards their "alive" feeling on the dashboard.
///
/// Use this everywhere instead of rolling a bespoke `Container(decoration: ...)`
/// so the look stays consistent as pages evolve (#163).
class GlassCard extends StatelessWidget {
  static const Color kCardBg = Color(0x0DFFFFFF);
  static const Color kCardBorder = Color(0x14FFFFFF);
  static const Color kGlowColor = Color(0xFF7B61FF);

  final Widget child;
  final EdgeInsetsGeometry padding;
  final EdgeInsetsGeometry? margin;
  final double borderRadius;
  final Color? borderColor;
  final Color? backgroundColor;
  final bool glow;
  final double glowOpacity;
  final double? width;

  const GlassCard({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(20),
    this.margin,
    this.borderRadius = 16,
    this.borderColor,
    this.backgroundColor,
    this.glow = true,
    this.glowOpacity = 0.03,
    this.width,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      width: width,
      padding: padding,
      margin: margin,
      decoration: BoxDecoration(
        color: backgroundColor ?? kCardBg,
        borderRadius: BorderRadius.circular(borderRadius),
        border: Border.all(
          color: borderColor ?? kCardBorder,
          width: 1,
        ),
        boxShadow: glow
            ? [
                BoxShadow(
                  color: kGlowColor.withValues(alpha: glowOpacity),
                  blurRadius: 24,
                  spreadRadius: 0,
                ),
              ]
            : null,
      ),
      child: child,
    );
  }
}

/// Convenience: the canonical purple glow as a reusable `BoxShadow` list.
/// Attach to inline `Container(decoration: BoxDecoration(...))` when a full
/// [GlassCard] isn't practical (e.g. animated containers with hover states).
List<BoxShadow> cinematicGlow({double opacity = 0.03}) => [
      BoxShadow(
        color: GlassCard.kGlowColor.withValues(alpha: opacity),
        blurRadius: 24,
        spreadRadius: 0,
      ),
    ];
