import 'package:flutter/material.dart';
import '../../../utils/navigation_depth.dart';
import 'deep_water_current.dart';

class InteractiveBackgroundLayer extends StatefulWidget {
  final ValueNotifier<Offset> mouseNotifier;
  
  const InteractiveBackgroundLayer({super.key, required this.mouseNotifier});

  @override
  State<InteractiveBackgroundLayer> createState() => _InteractiveBackgroundLayerState();
}

class _InteractiveBackgroundLayerState extends State<InteractiveBackgroundLayer> with SingleTickerProviderStateMixin {
  late AnimationController _controller;
  // Use absolute time so particles don't jump when the controller
  // is recreated (e.g. navigating from login to dashboard).
  static final int _epoch = DateTime.now().microsecondsSinceEpoch;

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(
        vsync: this,
        duration: const Duration(days: 365)
    )..forward();
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // RepaintBoundary isolates this from the rest of the tree —
    // the background repaints ~60fps but doesn't force the
    // content/nav to repaint alongside it.
    return RepaintBoundary(
      child: ValueListenableBuilder<double>(
        valueListenable: NavigationDepth.depthNotifier,
        builder: (context, depth, _) => AnimatedBuilder(
          animation: _controller,
          builder: (context, child) {
            // Absolute time since first paint — continuous across controller restarts
            final double time =
                (DateTime.now().microsecondsSinceEpoch - _epoch).toDouble();
            final mousePos = widget.mouseNotifier.value;

            return CustomPaint(
              painter: DeepWaterCurrentPainter(
                time: time,
                mousePos: mousePos,
                depthFraction: depth,
              ),
              size: Size.infinite,
            );
          },
        ),
      ),
    );
  }
}
