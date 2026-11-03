import 'package:flutter/material.dart';

class NoiseOverlay extends StatelessWidget {
  final double opacity;
  
  const NoiseOverlay({super.key, this.opacity = 0.05});

  @override
  Widget build(BuildContext context) {
    return IgnorePointer(
      child: Opacity(
        opacity: opacity,
        // Using a built-in Flutter noise texture approach or a simple image.
        // For a true Web/Flutter effect without an image asset, we could use a custom painter,
        // but for now, we'll use a semi-transparent black overlay with dither or just leave it as placeholder
        // until we add a noise.png to assets.
        child: Container(
          decoration: const BoxDecoration(
            // Placeholder: In a real app we'd load an SVG or PNG noise texture here
            // e.g., image: DecorationImage(image: AssetImage('assets/noise.png'), repeat: ImageRepeat.repeat),
            color: Colors.transparent,
          ),
        ),
      ),
    );
  }
}
