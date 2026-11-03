// Widget-test twin of integration_test/wrapped_render_test.dart (no screenshots).
// faked service and captures screenshots of key cards (saved to
// build/viz-screenshots/ by the driver). Same pattern as insights_render_test.
//
// Run: flutter drive --driver test_driver/integration_test.dart \
//        --target integration_test/wrapped_render_test.dart -d web-server \
//        --browser-name chrome --driver-port 4444

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';


import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/wrapped_page.dart';
import 'package:buoy_ai/services/wrapped_service.dart';

class _FakeWrapped extends WrappedService {
  @override
  Future<MonthlyWrapped> getMonthly(int year, int month) async => MonthlyWrapped(
        year: year,
        month: month,
        totalIncome: 7600,
        totalSpent: 2412.55,
        previousMonthSpent: 2890.10,
        topCategory: WrappedCategory(
            categoryId: 5, name: 'Dining Out', amount: 618.40, share: 25.6),
        topMerchant: WrappedMerchant(name: 'Saltline Bistro', visits: 9, amount: 312.20),
        zeroSpendDays: 9,
        longestStreak: 16,
        badgesEarned: [
          WrappedBadge(key: 'goal_achiever', title: 'Goal Achiever', icon: '⭐', points: 200),
          WrappedBadge(key: 'week_streak', title: 'Week Warrior', icon: '🔥', points: 75),
        ],
        savedToGoals: 600,
        biggestSpendDay: WrappedBiggestDay(date: DateTime(year, month, 16), amount: 289.99),
        transactionCount: 84,
        bonusCards: [
          WrappedBonusCard(
              factKey: 'weekend_share',
              kicker: 'weekend energy',
              headline: 'of it all happened on weekends',
              value: '62%',
              detail: r'$1,495 across Saturdays and Sundays',
              source: 'ai'),
          WrappedBonusCard(
              factKey: 'savings_rate',
              kicker: 'where you stand',
              headline: 'of Buoy Money savers this period',
              value: 'Top 10%',
              detail: 'Share of income moved to savings goals',
              source: 'percentile'),
        ],
      );

  @override
  Future<YearlyWrapped> getYearly(int year) async => YearlyWrapped(
        year: year,
        totalIncome: 91200,
        totalSpent: 30960.42,
        monthlySpend: [2400, 2210, 2680, 2350, 2900, 2500, 2412, 2600, 2450, 2800, 2658, 3000],
        topCategories: [
          WrappedCategory(categoryId: 5, name: 'Dining Out', amount: 7420, share: 24),
          WrappedCategory(categoryId: 3, name: 'Groceries', amount: 6210, share: 20),
          WrappedCategory(categoryId: 9, name: 'Shopping', amount: 4890, share: 16),
          WrappedCategory(categoryId: 7, name: 'Transport', amount: 3110, share: 10),
          WrappedCategory(categoryId: 11, name: 'Fun', amount: 2620, share: 8),
        ],
        topMerchant: WrappedMerchant(name: 'Trader Joe\'s', visits: 62, amount: 4180.30),
        zeroSpendDays: 74,
        longestStreak: 41,
        badgesEarnedCount: 9,
        bestBadge: WrappedBadge(
            key: 'savings_milestone_5k', title: 'High Roller', icon: '💎', points: 600),
        savedToGoals: 7200,
        biggestSpendDay: WrappedBiggestDay(date: DateTime(year, 11, 29), amount: 812.44),
        transactionCount: 1042,
      );
}

void main() {
  

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    wrappedService = _FakeWrapped();
  });

  Future<void> pump(WidgetTester tester, Widget page) async {
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      debugShowCheckedModeBanner: false,
      home: page,
    ));
    for (var i = 0; i < 14; i++) {
      await tester.pump(const Duration(milliseconds: 120));
    }
  }

  Future<void> advance(WidgetTester tester, int pages) async {
    for (var i = 0; i < pages; i++) {
      await tester.tapAt(const Offset(600, 400)); // right side = next
      for (var j = 0; j < 6; j++) {
        await tester.pump(const Duration(milliseconds: 100));
      }
    }
  }

  testWidgets('monthly story renders cover and wins card', (tester) async {
    await pump(tester, const WrappedPage(year: 2026, month: 7));
    expect(find.textContaining('Your July'), findsOneWidget);
    

    await advance(tester, 3); // damage -> where it went -> wins
    expect(find.text('zero-spend days'), findsOneWidget);

    await advance(tester, 2); // saved -> first bonus card
    expect(find.text('62%'), findsOneWidget);

    
  });

  testWidgets('yearly story renders cover and finale', (tester) async {
    await pump(tester, const WrappedPage(year: 2026));
    expect(find.textContaining('Your 2026'), findsOneWidget);
    

    await advance(tester, 7); // through to the finale
    expect(find.text('Share it'), findsOneWidget);
    
  });
}
