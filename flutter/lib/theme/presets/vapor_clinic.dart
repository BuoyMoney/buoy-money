import 'package:flutter/material.dart';
import '../cinematic_theme.dart';

class VaporClinicTheme extends CinematicTheme {
  @override
  String get id => 'vapor_clinic';
  
  @override
  String get name => 'Vapor Clinic';
  
  @override
  String get description => 'A genome sequencing lab inside a Tokyo nightclub.';

  // Palette
  @override
  Color get primary => const Color(0xFF0A0A14); // Deep Void

  @override
  Color get accent => const Color(0xFF7B61FF); // Plasma

  @override
  Color get background => const Color(0xFF04040A); // True Void

  @override
  Color get textDark => const Color(0xFFF0EFF4); // Ghost

  // Typography
  @override
  String get headingFontFamily => 'Sora';

  @override
  String get dramaFontFamily => 'Instrument Serif';

  @override
  String get dataFontFamily => 'Fira Code';

  // Identity / Mood
  @override
  String get identity => "Neon Biotech";

  @override
  String get imageMood => "bioluminescence, dark water, neon reflections, microscopy";
}
