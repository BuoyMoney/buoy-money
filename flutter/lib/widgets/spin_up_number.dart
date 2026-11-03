import 'package:flutter/material.dart';

/// A number that spins up from zero to [value] when [active] flips true
/// (and on first build if already active). Money Wrapped uses this so every
/// big number earns its reveal instead of just sitting there.
class SpinUpNumber extends StatefulWidget {
  final double value;
  final String Function(double value) format;
  final TextStyle? style;

  /// Animate only while the owning card is the one on screen; inactive cards
  /// hold at zero so the spin fires the moment the card is revealed.
  final bool active;
  final Duration duration;

  /// Beat of stillness before the spin starts. Encoded into the animation
  /// curve (not a Timer) so the whole reveal is one schedulable animation.
  final Duration delay;

  const SpinUpNumber({
    super.key,
    required this.value,
    required this.format,
    this.style,
    this.active = true,
    this.duration = const Duration(milliseconds: 1100),
    this.delay = const Duration(milliseconds: 120),
  });

  @override
  State<SpinUpNumber> createState() => _SpinUpNumberState();
}

class _SpinUpNumberState extends State<SpinUpNumber>
    with SingleTickerProviderStateMixin {
  late final AnimationController _ctrl = AnimationController(
      vsync: this, duration: widget.delay + widget.duration);
  late final Animation<double> _t = CurvedAnimation(
    parent: _ctrl,
    curve: Interval(
      widget.delay.inMilliseconds /
          (widget.delay.inMilliseconds + widget.duration.inMilliseconds),
      1.0,
      curve: Curves.easeOutCubic,
    ),
  );
  bool _played = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (widget.active && !_played) _play();
  }

  @override
  void didUpdateWidget(SpinUpNumber old) {
    super.didUpdateWidget(old);
    if (widget.active && !old.active) {
      _play();
    } else if (!widget.active && old.active) {
      _ctrl.value = 0;
      _played = false;
    }
  }

  void _play() {
    _played = true;
    // Respect OS reduce-motion: land on the final value immediately.
    final reduced = MediaQuery.maybeOf(context)?.disableAnimations ?? false;
    if (reduced) {
      _ctrl.value = 1;
    } else {
      _ctrl.forward(from: 0);
    }
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _t,
      builder: (_, __) =>
          Text(widget.format(widget.value * _t.value), style: widget.style),
    );
  }
}
