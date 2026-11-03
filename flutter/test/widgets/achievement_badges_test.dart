import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/services/gamification_service.dart';
import 'package:buoy_ai/widgets/cinematic/achievement_badges.dart';

AchievementCatalogItem _item({
  String key = 'k',
  String? title,
  String? description,
  String? icon,
  String category = 'budget',
  int points = 50,
  String? tier,
  double percentEarned = 0,
  bool isNegative = false,
  bool isEarned = false,
  DateTime? earnedAt,
}) =>
    AchievementCatalogItem(
      key: key,
      title: title,
      description: description,
      icon: icon,
      category: category,
      points: points,
      tier: tier,
      percentEarned: percentEarned,
      isNegative: isNegative,
      isEarned: isEarned,
      earnedAt: earnedAt,
    );

void main() {
  Widget wrap(Widget child) => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(body: Center(child: child)),
      );

  group('AchievementCatalogItem.fromJson', () {
    test('locked good achievement stays a masked silhouette', () {
      final item = AchievementCatalogItem.fromJson({
        'key': 'budget_master',
        'title': null,
        'description': null,
        'icon': null,
        'category': 'budget',
        'points': 500,
        'isNegative': false,
        'isEarned': false,
        'earnedAt': null,
      });
      expect(item.title, isNull);
      expect(item.isSecret, isTrue); // locked + not negative -> render as silhouette
      expect(item.points, 500); // hint still available
    });

    test('earned achievement parses PascalCase keys and the date', () {
      final item = AchievementCatalogItem.fromJson({
        'Key': 'first_budget',
        'Title': 'First Budget',
        'Description': 'Created your first budget',
        'Icon': '🎯',
        'Category': 'budget',
        'Points': 50,
        'IsNegative': false,
        'IsEarned': true,
        'EarnedAt': '2026-06-01T00:00:00Z',
      });
      expect(item.title, 'First Budget');
      expect(item.isEarned, isTrue);
      expect(item.isSecret, isFalse);
      expect(item.earnedAt, isNotNull);
    });

    test('negative achievement is not treated as a secret', () {
      final item = _item(isNegative: true, isEarned: false, title: 'Time Traveler');
      expect(item.isSecret, isFalse); // always legible
    });
  });

  group('GoodAchievementBadge', () {
    testWidgets('earned badge reveals its title and shows the badge art (not locked)',
        (tester) async {
      await tester.pumpWidget(wrap(GoodAchievementBadge(
        item: _item(
          key: 'budget_master',
          title: 'Budget Master',
          icon: '👑',
          isEarned: true,
          earnedAt: DateTime(2026, 6, 1),
        ),
      )));
      // Title is revealed; the locked silhouette (lock + "???") is gone. Earned
      // badges render the pre-rendered art (Image.asset), not the emoji glyph.
      expect(find.text('Budget Master'), findsOneWidget);
      expect(find.text('???'), findsNothing);
      expect(find.byIcon(Icons.lock_outline), findsNothing);
      expect(find.byType(Image), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('locked badge is a silhouette — no title, shows lock + "???"',
        (tester) async {
      await tester.pumpWidget(wrap(GoodAchievementBadge(
        item: _item(title: null, isEarned: false, points: 500),
      )));
      expect(find.text('???'), findsOneWidget);
      expect(find.byIcon(Icons.lock_outline), findsOneWidget);
      expect(find.text('500 pts'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('OopsAchievementBadge', () {
    testWidgets('unearned oops badge is legible with its explanation', (tester) async {
      await tester.pumpWidget(wrap(OopsAchievementBadge(
        item: _item(
          title: 'Time Traveler',
          description: 'You spent next month\'s money.',
          icon: '⏩',
          isNegative: true,
          isEarned: false,
        ),
      )));
      // Not a silhouette: title + description are shown even though it's not earned.
      expect(find.text('Time Traveler'), findsOneWidget);
      expect(find.text('You spent next month\'s money.'), findsOneWidget);
      expect(find.text('NOT EARNED'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('earned oops badge shows the EARNED tag', (tester) async {
      await tester.pumpWidget(wrap(OopsAchievementBadge(
        item: _item(
          title: 'Time Traveler',
          description: 'You spent next month\'s money.',
          icon: '⏩',
          isNegative: true,
          isEarned: true,
          earnedAt: DateTime(2026, 7, 1),
        ),
      )));
      expect(find.text('Time Traveler'), findsOneWidget);
      expect(find.text('EARNED'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });
}
