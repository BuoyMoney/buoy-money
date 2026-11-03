import '../../l10n/generated/app_localizations.dart';
import 'dart:async';
import 'dart:math' as math;
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import '../../services/gamification_service.dart';
import '../../services/review_prompt_service.dart';
import '../../utils/celebration_fx.dart';

const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _income = Color(0xFF34D399);

const _tierColors = <String, Color>{
  'Bronze': Color(0xFFCD7F32),
  'Silver': Color(0xFFC0C0C0),
  'Gold': Color(0xFFFFD700),
  'Platinum': Color(0xFF9B8CFF),
};

/// Shows a celebratory "Achievement Unlocked!" moment for freshly-earned achievements.
/// No-op if the list is empty. Awaits dismissal.
Future<void> showAchievementCelebration(
  BuildContext context,
  List<AchievementCatalogItem> earned,
) async {
  if (earned.isEmpty) return;
  CelebrationFx.play(); // chime + medium haptic, fire-and-forget
  await showDialog<void>(
    context: context,
    barrierColor: Colors.black.withValues(alpha: 0.6),
    builder: (_) => _CelebrationDialog(earned: earned),
  );
  // A badge is the strongest "you just won" moment the app has, so it is the
  // canonical time to ask for a store review (never during the celebration,
  // only after it is dismissed and the win has landed). An oops badge is the
  // opposite: it opens a 24h no-ask quiet period.
  if (earned.any((e) => e.isNegative)) {
    unawaited(reviewPrompts.recordNegativeMoment());
  } else {
    unawaited(reviewPrompts.maybeRequest(ReviewMoment.badgeUnlocked));
  }
}

class _CelebrationDialog extends StatefulWidget {
  final List<AchievementCatalogItem> earned;
  const _CelebrationDialog({required this.earned});

  @override
  State<_CelebrationDialog> createState() => _CelebrationDialogState();
}

class _CelebrationDialogState extends State<_CelebrationDialog>
    with TickerProviderStateMixin {
  late final AnimationController _pop = AnimationController(
      vsync: this, duration: const Duration(milliseconds: 550))
    ..forward();
  late final AnimationController _rays = AnimationController(
      vsync: this, duration: const Duration(seconds: 12))
    ..repeat();
  int _index = 0;

  AchievementCatalogItem get _item => widget.earned[_index];
  Color get _tier =>
      _item.isNegative ? _income : (_tierColors[_item.tier ?? ''] ?? _accent);

  @override
  void dispose() {
    _pop.dispose();
    _rays.dispose();
    super.dispose();
  }

  void _next() {
    if (_index < widget.earned.length - 1) {
      setState(() => _index++);
      _pop.forward(from: 0);
      // The chime plays once when the celebration opens; chained badges get
      // a buzz so a multi-unlock doesn't turn into a jingle loop.
      CelebrationFx.buzz();
    } else {
      Navigator.of(context).pop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final pop = CurvedAnimation(parent: _pop, curve: Curves.elasticOut);
    final fade = CurvedAnimation(parent: _pop, curve: Curves.easeOut);
    final remaining = widget.earned.length - _index - 1;

    return Dialog(
      backgroundColor: Colors.transparent,
      elevation: 0,
      child: FadeTransition(
        opacity: fade,
        child: Container(
          width: 320,
          padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
          decoration: BoxDecoration(
            color: const Color(0xFF14141F),
            borderRadius: BorderRadius.circular(24),
            border: Border.all(color: _tier.withValues(alpha: 0.5)),
            boxShadow: [
              BoxShadow(color: _tier.withValues(alpha: 0.25), blurRadius: 40),
            ],
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                _item.isNegative ? 'Oops — badge earned' : 'Achievement Unlocked!',
                style: GoogleFonts.sora(
                    fontSize: 13,
                    fontWeight: FontWeight.w700,
                    letterSpacing: 1,
                    color: _tier),
              ),
              const SizedBox(height: 20),
              // Rotating rays behind the badge.
              SizedBox(
                width: 130,
                height: 130,
                child: Stack(
                  alignment: Alignment.center,
                  children: [
                    AnimatedBuilder(
                      animation: _rays,
                      builder: (_, __) => Transform.rotate(
                        angle: _rays.value * 2 * math.pi,
                        child: CustomPaint(
                          size: const Size(130, 130),
                          painter: _RaysPainter(_tier.withValues(alpha: 0.30)),
                        ),
                      ),
                    ),
                    ScaleTransition(
                      scale: pop,
                      // The pre-rendered badge art (assets/achievements/<key>.png)
                      // already carries its own coin rim; the tier-tinted disc behind
                      // it reads as a colored glow. Falls back to the emoji if missing.
                      child: Image.asset(
                        'assets/achievements/${_item.key}.png',
                        width: 92,
                        height: 92,
                        filterQuality: FilterQuality.medium,
                        errorBuilder: (_, __, ___) => Container(
                          width: 84,
                          height: 84,
                          alignment: Alignment.center,
                          decoration: BoxDecoration(
                            shape: BoxShape.circle,
                            color: _tier.withValues(alpha: 0.15),
                            border:
                                Border.all(color: _tier.withValues(alpha: 0.6)),
                          ),
                          child: Text(_item.icon ?? '🏆',
                              style: const TextStyle(fontSize: 40)),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 20),
              Text(_item.title ?? 'Achievement',
                  textAlign: TextAlign.center,
                  style: GoogleFonts.sora(
                      fontSize: 18,
                      fontWeight: FontWeight.bold,
                      color: _textGhost)),
              if (_item.tier != null) ...[
                const SizedBox(height: 4),
                Text(AppLocalizations.of(context).uiTierLabel(_item.tier ?? ''),
                    style: GoogleFonts.sora(fontSize: 12, color: _tier)),
              ],
              const SizedBox(height: 8),
              Text(_item.description ?? '',
                  textAlign: TextAlign.center,
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
              const SizedBox(height: 24),
              SizedBox(
                width: double.infinity,
                child: FilledButton(
                  style: FilledButton.styleFrom(
                    backgroundColor: _tier.withValues(alpha: 0.9),
                    padding: const EdgeInsets.symmetric(vertical: 14),
                  ),
                  onPressed: _next,
                  child: Text(
                    remaining > 0 ? 'Next ($remaining more)' : 'Nice!',
                    style: GoogleFonts.sora(
                        fontWeight: FontWeight.w700, color: Colors.white),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _RaysPainter extends CustomPainter {
  final Color color;
  _RaysPainter(this.color);

  @override
  void paint(Canvas canvas, Size size) {
    final center = size.center(Offset.zero);
    final paint = Paint()
      ..color = color
      ..strokeWidth = 3
      ..strokeCap = StrokeCap.round;
    const rayCount = 12;
    final r1 = size.width * 0.34;
    final r2 = size.width * 0.5;
    for (var i = 0; i < rayCount; i++) {
      final a = i * 2 * math.pi / rayCount;
      final d = Offset(math.cos(a), math.sin(a));
      canvas.drawLine(center + d * r1, center + d * r2, paint);
    }
  }

  @override
  bool shouldRepaint(covariant _RaysPainter old) => old.color != color;
}
