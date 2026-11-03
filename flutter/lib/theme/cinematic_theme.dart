import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

abstract class CinematicTheme {
  // Brand / Purpose Identifiers
  String get id;
  String get name;
  String get description;

  // Core Palette
  Color get primary;
  Color get accent;
  Color get background;
  Color get textDark; // For use on dark backgrounds (should be a light color)
  
  // Custom semantic colors
  Color get success => const Color(0xFF52C41A); // green
  Color get warning => const Color(0xFFFA8C16); // orange
  Color get error => const Color(0xFFF5222D);   // red

  // Typography Family Names (Google Fonts format)
  String get headingFontFamily;
  String get dramaFontFamily;
  String get dataFontFamily;

  // Aesthetic Identity / Mood
  String get identity;
  String get imageMood;

  // Dynamically constructed ThemeData for this preset
  ThemeData get themeData {
    return ThemeData(
      useMaterial3: true,
      scaffoldBackgroundColor: background,
      colorScheme: ColorScheme.dark(
        primary: primary,
        secondary: accent,
        surface: background,
        onSurface: textDark, 
        error: error,
      ),
      textTheme: TextTheme(
        // Hero / Large Statements
        displayLarge: GoogleFonts.getFont(headingFontFamily, 
          fontSize: 64, 
          fontWeight: FontWeight.w800, 
          color: textDark,
          letterSpacing: -1.0,
        ),
        // Drama Serifs
        displayMedium: GoogleFonts.getFont(dramaFontFamily, 
          fontSize: 48, 
          fontStyle: FontStyle.italic, 
          color: accent,
        ),
        // Standard Headings
        headlineLarge: GoogleFonts.getFont(headingFontFamily, 
          fontSize: 32, 
          fontWeight: FontWeight.w700, 
          color: textDark,
        ),
        headlineMedium: GoogleFonts.getFont(headingFontFamily, 
          fontSize: 24, 
          fontWeight: FontWeight.w600, 
          color: textDark,
        ),
        // Body Copy
        bodyLarge: GoogleFonts.getFont(headingFontFamily, 
          fontSize: 18, 
          color: textDark.withValues(alpha: 0.9),
          height: 1.6,
        ),
        bodyMedium: GoogleFonts.getFont(headingFontFamily, 
          fontSize: 16, 
          color: textDark.withValues(alpha: 0.7),
          height: 1.5,
        ),
        // Data / Mono
        labelSmall: GoogleFonts.getFont(dataFontFamily, 
          fontSize: 13, 
          color: accent,
          letterSpacing: 0.5,
        ),
      ),
      cardTheme: CardThemeData(
        color: background,
        elevation: 0,
        shape: RoundedRectangleBorder(
          // Strict 32px to 48px radius system, going with 32 (2rem)
          borderRadius: BorderRadius.circular(32), 
          side: BorderSide(color: textDark.withValues(alpha: 0.05)),
        ),
      ),
      appBarTheme: AppBarTheme(
        backgroundColor: Colors.transparent,
        elevation: 0,
        centerTitle: false,
        titleTextStyle: GoogleFonts.getFont(headingFontFamily,
          fontSize: 20,
          fontWeight: FontWeight.w600,
          color: textDark,
        ),
      ),
    );
  }
}
