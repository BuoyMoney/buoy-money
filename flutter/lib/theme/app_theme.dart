import 'package:flutter/cupertino.dart' show CupertinoPageTransitionsBuilder;
import 'package:flutter/material.dart';

/// BuoyAI Theme - Modern Material 3 design
/// 
/// Material 3 theme with Flutter enhancements:
/// - Dynamic color schemes
/// - Smooth animations
/// - Modern card styles with elevation
/// - Custom typography scale

class AppTheme {
  // Brand Colors
  static const Color primaryIndigo = Color(0xFF6366F1);
  static const Color accentGreen = Color(0xFF52C41A);
  static const Color warningOrange = Color(0xFFFA8C16);
  static const Color errorRed = Color(0xFFF5222D);
  
  // Account type colors
  static const Color checkingColor = Color(0xFF6366F1);  // Primary indigo
  static const Color savingsColor = Color(0xFF52C41A);   // Accent green
  static const Color creditCardColor = Color(0xFFFA8C16); // Warning orange
  static const Color investmentColor = Color(0xFF818CF8); // Light indigo
  static const Color loanColor = Color(0xFFF5222D);       // Error red
  
  // Transaction colors
  static const Color incomeColor = Color(0xFF52C41A);
  static const Color expenseColor = Color(0xFFF5222D);

  /// Route-transition builders. On Android, use PredictiveBackPageTransitionsBuilder so a
  /// back-swipe peeks the previous route (the native predictive-back animation) instead of a plain
  /// slide — pairs with android:enableOnBackInvokedCallback in the manifest.
  ///
  /// IMPORTANT: a partial `builders` map does NOT fall back to per-platform defaults — any platform
  /// absent from the map gets ZoomPageTransitionsBuilder (Android-style), NOT its native transition.
  /// So we must name iOS/macOS explicitly with CupertinoPageTransitionsBuilder, or Apple platforms
  /// silently lose their native horizontal slide *and* the left-edge interactive swipe-back gesture.
  // `final`, not `const`: PredictiveBackPageTransitionsBuilder() is not a const constructor.
  static final PageTransitionsTheme _pageTransitions = PageTransitionsTheme(
    builders: <TargetPlatform, PageTransitionsBuilder>{
      TargetPlatform.android: PredictiveBackPageTransitionsBuilder(),
      TargetPlatform.iOS: CupertinoPageTransitionsBuilder(),
      TargetPlatform.macOS: CupertinoPageTransitionsBuilder(),
    },
  );
  
  /// Light theme
  static ThemeData get lightTheme {
    final colorScheme = ColorScheme.fromSeed(
      seedColor: primaryIndigo,
      brightness: Brightness.light,
      primary: primaryIndigo,
      secondary: accentGreen,
      error: errorRed,
      surface: Colors.white,
      surfaceContainerHighest: const Color(0xFFF5F5F5),
    );
    
    return ThemeData(
      useMaterial3: true,
      pageTransitionsTheme: _pageTransitions,
      colorScheme: colorScheme,
      
      // Typography
      fontFamily: 'Roboto',
      textTheme: const TextTheme(
        displayLarge: TextStyle(fontSize: 32, fontWeight: FontWeight.bold),
        displayMedium: TextStyle(fontSize: 28, fontWeight: FontWeight.bold),
        displaySmall: TextStyle(fontSize: 24, fontWeight: FontWeight.bold),
        headlineMedium: TextStyle(fontSize: 20, fontWeight: FontWeight.w600),
        titleLarge: TextStyle(fontSize: 18, fontWeight: FontWeight.w600),
        titleMedium: TextStyle(fontSize: 16, fontWeight: FontWeight.w500),
        bodyLarge: TextStyle(fontSize: 16),
        bodyMedium: TextStyle(fontSize: 14),
        labelLarge: TextStyle(fontSize: 14, fontWeight: FontWeight.w500),
      ),
      
      // AppBar
      appBarTheme: AppBarTheme(
        elevation: 0,
        scrolledUnderElevation: 1,
        backgroundColor: colorScheme.surface,
        foregroundColor: colorScheme.onSurface,
        centerTitle: false,
        titleTextStyle: TextStyle(
          fontSize: 20,
          fontWeight: FontWeight.w600,
          color: colorScheme.onSurface,
        ),
      ),
      
      // Cards with modern elevation
      cardTheme: CardThemeData(
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: Colors.grey.shade200),
        ),
        clipBehavior: Clip.antiAlias,
      ),
      
      // Elevated buttons
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          elevation: 0,
          padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 14),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
        ),
      ),
      
      // Input fields
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: Colors.grey.shade50,
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.grey.shade300),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.grey.shade300),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: const BorderSide(color: primaryIndigo, width: 2),
        ),
        contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      ),
      
      // Bottom navigation with modern styling
      navigationBarTheme: NavigationBarThemeData(
        elevation: 0,
        height: 65,
        indicatorShape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
        ),
        labelBehavior: NavigationDestinationLabelBehavior.alwaysShow,
      ),
      
      // Floating action button
      floatingActionButtonTheme: const FloatingActionButtonThemeData(
        elevation: 2,
        highlightElevation: 4,
      ),
      
      // Dividers
      dividerTheme: DividerThemeData(
        color: Colors.grey.shade200,
        thickness: 1,
      ),
    );
  }
  
  /// Dark theme
  static ThemeData get darkTheme {
    final colorScheme = ColorScheme.fromSeed(
      seedColor: primaryIndigo,
      brightness: Brightness.dark,
      primary: const Color(0xFF818CF8), // Lighter indigo for dark mode
      secondary: const Color(0xFF73D13D), // Lighter green
      error: const Color(0xFFFF7875),
      surface: const Color(0xFF1E1E2E),
      surfaceContainerHighest: const Color(0xFF2A2A3E),
    );
    
    return ThemeData(
      useMaterial3: true,
      pageTransitionsTheme: _pageTransitions,
      colorScheme: colorScheme,
      fontFamily: 'Roboto',
      
      scaffoldBackgroundColor: const Color(0xFF141420),
      
      appBarTheme: AppBarTheme(
        elevation: 0,
        scrolledUnderElevation: 1,
        backgroundColor: colorScheme.surface,
        foregroundColor: colorScheme.onSurface,
        centerTitle: false,
      ),
      
      cardTheme: CardThemeData(
        elevation: 0,
        color: colorScheme.surface,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: Colors.white.withValues(alpha: 0.1)),
        ),
      ),
      
      navigationBarTheme: NavigationBarThemeData(
        elevation: 0,
        height: 65,
        backgroundColor: colorScheme.surface,
        indicatorColor: colorScheme.primary.withValues(alpha: 0.2),
        indicatorShape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
        ),
      ),
      
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: Colors.white.withValues(alpha: 0.05),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.white.withValues(alpha: 0.1)),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: Colors.white.withValues(alpha: 0.1)),
        ),
        focusedBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(12),
          borderSide: BorderSide(color: colorScheme.primary, width: 2),
        ),
      ),
      
      dividerTheme: DividerThemeData(
        color: Colors.white.withValues(alpha: 0.1),
        thickness: 1,
      ),
    );
  }
  
  /// Get color for account type
  static Color getAccountTypeColor(int accountType) {
    switch (accountType) {
      case 0: return checkingColor;    // Checking/Depository
      case 1: return savingsColor;     // Savings
      case 2: return creditCardColor;  // Credit Card
      case 3: return investmentColor;  // Investment
      case 4: return loanColor;        // Loan/Other
      default: return checkingColor;
    }
  }
  
  /// Get icon for account type
  static IconData getAccountTypeIcon(int accountType) {
    switch (accountType) {
      case 0: return Icons.account_balance;      // Checking
      case 1: return Icons.savings;              // Savings
      case 2: return Icons.credit_card;          // Credit Card
      case 3: return Icons.trending_up;          // Investment
      case 4: return Icons.account_balance_wallet; // Loan
      default: return Icons.account_balance;
    }
  }
  
  /// Get name for account type
  static String getAccountTypeName(int accountType) {
    switch (accountType) {
      case 0: return 'Checking';
      case 1: return 'Savings';
      case 2: return 'Credit Card';
      case 3: return 'Investment';
      case 4: return 'Loan';
      case 5: return 'Cash';
      default: return 'Account';
    }
  }
}
