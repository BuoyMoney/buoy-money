import 'dart:async';

import 'package:flutter/material.dart';

import '../../utils/celebration_fx.dart';
import '../../utils/formatters.dart';

/// Presentation-only continuous interpolation of a count-up category's
/// "earned to date".
///
/// The backend credits each day's allowance in full at local midnight
/// (`earnedToDate = dailyAllowance * daysElapsed`, inclusive day counting).
/// The live ticker instead presents today's allowance as earned gradually
/// across the local day, so it converges on the server value exactly at
/// midnight and never overstates it:
///
///   earned(now) = dailyAllowance * (dayIndex - 1 + fractionOfLocalDay)
///
/// On day 1 this starts from zero and reaches the first full allowance at
/// midnight. [daysElapsed] is the server's inclusive day count at the moment
/// the status was fetched ([fetchedAt]); the current day index is re-derived
/// from the device clock so the value rolls over correctly at local midnight
/// even if no refetch has happened yet.
double interpolatedEarnedToDate({
  required double dailyAllowance,
  required int daysElapsed,
  required int totalDays,
  required DateTime fetchedAt,
  required DateTime now,
}) {
  if (daysElapsed <= 0 || totalDays <= 0 || dailyAllowance <= 0) return 0;
  final fetchDate = DateTime(fetchedAt.year, fetchedAt.month, fetchedAt.day);
  final nowDate = DateTime(now.year, now.month, now.day);
  // Round rather than truncate: across a DST change a local "day" can be
  // 23 or 25 hours, which would make inDays under-count by one.
  final dayShift = (nowDate.difference(fetchDate).inHours / 24).round();
  final dayIndex = daysElapsed + dayShift;
  if (dayIndex > totalDays) return dailyAllowance * totalDays; // period over
  if (dayIndex < 1) return 0;
  final fractionOfDay =
      now.difference(nowDate).inMilliseconds / Duration.millisecondsPerDay;
  final earned = dailyAllowance * (dayIndex - 1 + fractionOfDay);
  return earned.clamp(0.0, dailyAllowance * totalDays);
}

/// A currency amount that visibly ticks in real time ($12.3456).
///
/// Recomputes its value from the device clock on every tick and rebuilds only
/// when the rendered string actually changes, so the timer frequency costs
/// almost nothing. Pauses while the app is backgrounded and recomputes from
/// the clock on resume (no drift). When the platform requests reduced motion,
/// renders a static two-decimal amount instead.
class LiveTickingAmount extends StatefulWidget {
  const LiveTickingAmount({
    super.key,
    required this.compute,
    required this.styleBuilder,
    this.clock,
    this.tickInterval = const Duration(milliseconds: 100),
  });

  /// Returns the value to display for the given instant.
  final double Function(DateTime now) compute;

  /// Style for the current value (lets callers color by sign).
  final TextStyle Function(double value) styleBuilder;

  /// Injectable clock for tests; defaults to [DateTime.now].
  final DateTime Function()? clock;

  final Duration tickInterval;

  @override
  State<LiveTickingAmount> createState() => _LiveTickingAmountState();
}

class _LiveTickingAmountState extends State<LiveTickingAmount>
    with WidgetsBindingObserver {
  Timer? _timer;
  double _value = 0;
  String _text = '';
  bool _reduceMotion = false;

  DateTime _now() => (widget.clock ?? DateTime.now)();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _recompute();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _reduceMotion =
        MediaQuery.maybeOf(context)?.disableAnimations ?? false;
    _recompute();
    _syncTimer();
  }

  @override
  void didUpdateWidget(LiveTickingAmount oldWidget) {
    super.didUpdateWidget(oldWidget);
    _recompute();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) {
      _recompute();
      _syncTimer();
    } else {
      _timer?.cancel();
      _timer = null;
    }
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _timer?.cancel();
    super.dispose();
  }

  void _syncTimer() {
    if (_reduceMotion) {
      _timer?.cancel();
      _timer = null;
      return;
    }
    _timer ??= Timer.periodic(widget.tickInterval, (_) => _tick());
  }

  void _recompute() {
    _value = widget.compute(_now());
    _text = _reduceMotion ? formatCurrency(_value) : formatCurrencyLive(_value);
  }

  void _tick() {
    if (!mounted) return;
    final value = widget.compute(_now());
    final text = formatCurrencyLive(value);
    if (text == _text) return;
    // Whole-dollar crossings get the subtlest haptic tick (mobile only) —
    // the idle-game "coin drop" you feel rather than hear.
    if (value.floor() > _value.floor()) CelebrationFx.milestoneTick();
    setState(() {
      _value = value;
      _text = text;
    });
  }

  @override
  Widget build(BuildContext context) {
    // Screen readers get a stable two-decimal amount, not every tick.
    return Semantics(
      label: formatCurrency(_value),
      excludeSemantics: true,
      child: _reduceMotion
          ? Text(_text, style: widget.styleBuilder(_value))
          : _RollingText(text: _text, style: widget.styleBuilder(_value)),
    );
  }
}

/// Odometer treatment: each character rolls in from above when it changes,
/// instead of the whole string snapping. Fira Code is monospaced, so per-char
/// switching never shifts layout. Characters that didn't change keep their
/// key and don't animate — on a typical tick only the last digit rolls.
class _RollingText extends StatelessWidget {
  final String text;
  final TextStyle style;

  const _RollingText({required this.text, required this.style});

  @override
  Widget build(BuildContext context) {
    final chars = text.split('');
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (var i = 0; i < chars.length; i++)
          ClipRect(
            child: AnimatedSwitcher(
              duration: const Duration(milliseconds: 220),
              switchInCurve: Curves.easeOutCubic,
              switchOutCurve: Curves.easeInCubic,
              transitionBuilder: (child, animation) => FadeTransition(
                opacity: animation,
                child: SlideTransition(
                  position: Tween<Offset>(
                    begin: const Offset(0, -0.9),
                    end: Offset.zero,
                  ).animate(animation),
                  child: child,
                ),
              ),
              child: Text(chars[i], key: ValueKey('$i-${chars[i]}'), style: style),
            ),
          ),
      ],
    );
  }
}
