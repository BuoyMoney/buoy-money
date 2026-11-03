import 'dart:math';
import 'package:flutter/material.dart';
import '../../../utils/background_preferences.dart';

class Particle {
  double x, y, size, speedY, baseAlpha, phase;
  Particle(this.x, this.y, this.size, this.speedY, this.baseAlpha, this.phase);
}

class DeepWaterCurrentPainter extends CustomPainter {
  final double time;
  final Offset mousePos;
  final double depthFraction;
  static List<Particle>? _particles;
  static Size _lastSize = Size.zero;
  static int _generation = 0;
  static int _lastCount = -1; // particle count the cache was built with
  static const _currentGen = 2; // bump on Particle field changes to survive hot reload

  // Squared mouse influence radius — avoids sqrt per particle
  static const _influenceRadiusSq = 120000.0; // ~346px radius
  static const _influenceRadius = 346.0;

  DeepWaterCurrentPainter({
    required this.time,
    required this.mousePos,
    this.depthFraction = 0.0,
  });

  @override
  void paint(Canvas canvas, Size size) {
    // Particle count is user-controlled via the Settings > Appearance slider
    // (BackgroundPreferences). Regenerate the cache when the count changes.
    final count = BackgroundPreferences.particleCount.value;
    if (_particles == null ||
        _lastSize != size ||
        _generation != _currentGen ||
        _lastCount != count) {
      final rand = Random(42);
      _particles = List.generate(count, (i) => Particle(
        rand.nextDouble() * size.width,
        rand.nextDouble() * size.height,
        rand.nextDouble() * 2 + 1,
        rand.nextDouble() * 15 + 10,
        rand.nextDouble() * 0.4 + 0.1,
        rand.nextDouble() * pi * 2, // unique phase offset per bubble
      ));
      _lastSize = size;
      _generation = _currentGen;
      _lastCount = count;
    }

    // Depth-reactive colors — computed once per frame, not per particle
    final surfaceColor = const Color(0xFFB0C4FF);
    final deepColor = const Color(0xFF34D399);
    final bubbleBase = Color.lerp(surfaceColor, deepColor, depthFraction)!;
    final br = bubbleBase.r;
    final bg = bubbleBase.g;
    final bb = bubbleBase.b;

    // Background wash
    final bgColor = Color.lerp(
      const Color(0xFF0A1628),
      const Color(0xFF050508),
      depthFraction,
    )!;
    canvas.drawRect(Offset.zero & size, Paint()..color = bgColor);

    final paint = Paint();
    final double tSec = time / 1000000;
    final double mx = mousePos.dx;
    final double my = mousePos.dy;
    final double h = size.height;

    for (var p in _particles!) {
      double currentY = p.y - (tSec * p.speedY) % h;
      if (currentY < 0) currentY += h;

      // Per-particle sway using unique phase offset — prevents lockstep movement
      double currentX = p.x + sin(tSec * 2 + p.phase) * 15;

      // Squared distance check — skip sqrt for particles outside radius
      final double dx = currentX - mx;
      final double dy = currentY - my;
      final double distSq = dx * dx + dy * dy;

      if (distSq < _influenceRadiusSq && distSq > 0) {
        final double dist = sqrt(distSq);
        final double force = (_influenceRadius - dist) / _influenceRadius;
        currentX += (dx / dist) * force * 20;
        currentY += (dy / dist) * force * 20;
      }

      paint.color = Color.from(
        alpha: p.baseAlpha * 0.5,
        red: br,
        green: bg,
        blue: bb,
      );
      canvas.drawCircle(Offset(currentX, currentY), p.size, paint);
    }
  }

  @override
  bool shouldRepaint(DeepWaterCurrentPainter oldDelegate) => true;
}
