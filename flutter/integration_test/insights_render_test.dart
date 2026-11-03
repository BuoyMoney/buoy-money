// Render check for the reworked Insights page — pumps the full page with faked
// services (level hero ring, streak heat, challenges, badge strip, AI insight
// cards, upgraded charts) and captures screenshots (saved to
// build/viz-screenshots/ by the driver). Same pattern as viz_render_test.dart:
// real pixels, no login, no seeded data.
//
// Run: flutter drive --driver test_driver/integration_test.dart \
//        --target integration_test/insights_render_test.dart -d web-server \
//        --browser-name chrome --driver-port 4444

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'package:buoy_ai/pages/insights_page.dart';
import 'package:buoy_ai/services/api_client.dart';
import 'package:buoy_ai/services/billing_service.dart';
import 'package:buoy_ai/services/gamification_service.dart';
import 'package:buoy_ai/services/insights_service.dart';
import 'package:buoy_ai/services/visualization_service.dart';

// ── Fakes ───────────────────────────────────────────────

class _FakeGamification extends GamificationService {
  // The page prefers check-achievements and falls back to the dashboard; both
  // must be faked or the base implementations stall under the test binding.
  @override
  Future<GamificationDashboard> checkAchievements() => getDashboard();

  @override
  Future<List<AchievementCatalogItem>> getAchievementCatalog() async => [];

  @override
  Future<void> markAchievementsSeen(List<String> keys) async {}

  @override
  Future<GamificationDashboard> getDashboard() async => GamificationDashboard(
        totalPoints: 1450,
        level: 'Master',
        pointsToNextLevel: 550,
        levelProgress: 0.45,
        achievementCount: 11,
        activeStreaks: [
          UserStreak(
            id: 1,
            streakType: 'under_budget',
            displayName: 'Under Budget',
            icon: '🔥',
            currentCount: 16,
            bestCount: 21,
            lastIncrementDate: DateTime(2026, 8, 12),
            streakStartDate: DateTime(2026, 7, 28),
            isAtRisk: false,
            streakFreezes: 2,
          ),
          UserStreak(
            id: 2,
            streakType: 'login',
            displayName: 'Daily Check-in',
            icon: '📅',
            currentCount: 5,
            bestCount: 30,
            lastIncrementDate: DateTime(2026, 8, 11),
            streakStartDate: DateTime(2026, 8, 8),
            isAtRisk: true,
            streakFreezes: 0,
          ),
        ],
        recentAchievements: [
          UserAchievement(
            id: 1,
            achievementKey: 'goal_achiever',
            title: 'Goal Achiever',
            description: 'Reached a savings goal',
            icon: '⭐',
            category: 'savings',
            points: 200,
            earnedAt: DateTime(2026, 8, 10),
            isNew: true,
          ),
          UserAchievement(
            id: 2,
            achievementKey: 'week_streak',
            title: 'Week Warrior',
            description: '7 day under-budget streak',
            icon: '🔥',
            category: 'streak',
            points: 75,
            earnedAt: DateTime(2026, 8, 4),
            isNew: false,
          ),
          UserAchievement(
            id: 3,
            achievementKey: 'hundred_club',
            title: r'$100 Club',
            description: r'Saved your first $100!',
            icon: '💯',
            category: 'savings',
            points: 75,
            earnedAt: DateTime(2026, 7, 22),
            isNew: false,
          ),
        ],
        activeChallenges: [
          UserChallenge(
            id: 1,
            challengeType: 'categorize_transactions',
            title: 'Categorization Champ',
            description: 'Categorize 5 transactions',
            targetValue: 5,
            currentValue: 4,
            rewardPoints: 25,
            startDate: DateTime(2026, 8, 13),
            endDate: DateTime.now().add(const Duration(hours: 20)),
            status: 'active',
          ),
        ],
        newAchievementIds: [],
      );
}

class _FakeBilling extends BillingService {
  _FakeBilling() : super(apiClient);
  @override
  Future<bool> canAccessFeature(String featureName) async => true;
}

class _FakeInsights extends InsightsService {
  @override
  Future<List<AIInsight>> getInsights(
          {String context = 'dashboard', int maxInsights = 5}) async =>
      [
        AIInsight(
          id: '1',
          type: InsightType.alert,
          title: 'Dining out is pacing 32% over last month',
          description:
              'You have spent \$286 on dining so far — at this pace you will '
              'pass your \$450 budget around the 24th.',
          actionLabel: 'Review dining budget',
          actionRoute: '/budgets',
          createdAt: DateTime.now().subtract(const Duration(days: 1)),
          priority: 'high',
        ),
        AIInsight(
          id: '2',
          type: InsightType.opportunity,
          title: 'Your streak is compounding real savings',
          description:
              'Sixteen under-budget days have left \$212 unspent — sweeping it '
              'to your Japan Trip goal would put you 3 weeks ahead of schedule.',
          actionLabel: 'Move to savings',
          actionRoute: '/savings',
          createdAt: DateTime.now().subtract(const Duration(days: 3)),
          priority: 'medium',
        ),
      ];
}

class _FakeViz extends VisualizationService {
  static UserVisualization _viz(int id, String type, String title) =>
      UserVisualization(
        id: id,
        chartType: type,
        title: title,
        message: 'Where this month\'s spending actually went.',
        motivationalMessage: 'Groceries down 12% from July — the streak is working.',
        chartData: [
          ChartDataPoint(name: 'Rent', value: 1800),
          ChartDataPoint(name: 'Food', value: 720),
          ChartDataPoint(name: 'Transit', value: 240),
          ChartDataPoint(name: 'Fun', value: 410),
          ChartDataPoint(name: 'Save', value: 600),
        ],
        chartConfig: ChartConfig(valuePrefix: r'$'),
        category: 'spending',
        priority: 'medium',
        confidence: 0.9,
        isStarred: id == 1,
        isDismissed: false,
        viewCount: 0,
        lastViewedAt: null,
        generatedAt: DateTime.now().subtract(const Duration(days: 2)),
      );

  @override
  Future<VisualizationListResponse> getVisualizations(
      {int page = 1,
      int pageSize = 10,
      String? category,
      bool includeDismissed = false}) async {
    return VisualizationListResponse(
      visualizations: [
        _viz(1, 'donut', 'Spending by category'),
        _viz(2, 'area', 'Cash flow trend'),
      ],
      totalCount: 2,
      page: 1,
      pageSize: pageSize,
      totalPages: 1,
    );
  }

  @override
  Future<VisualizationListResponse> getStarredVisualizations(
      {int page = 1, int pageSize = 10}) async {
    return VisualizationListResponse(
      visualizations: [_viz(1, 'donut', 'Spending by category')],
      totalCount: 1,
      page: 1,
      pageSize: pageSize,
      totalPages: 1,
    );
  }
}

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    gamificationService = _FakeGamification();
    billingService = _FakeBilling();
    insightsService = _FakeInsights();
    visualizationService = _FakeViz();
  });

  Future<void> pumpPage(WidgetTester tester) async {
    await tester.pumpWidget(const MaterialApp(
      debugShowCheckedModeBanner: false,
      home: Scaffold(
        backgroundColor: Color(0xFF04040A),
        body: InsightsPage(),
      ),
    ));
    // Fixed pumps — fonts, ring/count-up animations, chart layout (no pumpAndSettle).
    for (var i = 0; i < 16; i++) {
      await tester.pump(const Duration(milliseconds: 120));
    }
  }

  testWidgets('render Progress & Rewards tab', (tester) async {
    await pumpPage(tester);
    expect(find.text('Master'), findsOneWidget); // level hero maps real names
    expect(find.text('550'), findsOneWidget);
    expect(find.textContaining('pts to Champion'), findsOneWidget); // gap is the hero number
    expect(find.textContaining('1450 lifetime'), findsOneWidget);
    expect(find.text('Categorization Champ'), findsOneWidget);
    await binding.takeScreenshot('insights-progress');
  });

  testWidgets('render AI Insights tab', (tester) async {
    await pumpPage(tester);
    await tester.tap(find.text('AI Insights'));
    for (var i = 0; i < 16; i++) {
      await tester.pump(const Duration(milliseconds: 120));
    }
    expect(find.text('Personalized Insights'), findsOneWidget);
    expect(find.textContaining('Dining out is pacing'), findsOneWidget);
    await binding.takeScreenshot('insights-ai');
  });
}
