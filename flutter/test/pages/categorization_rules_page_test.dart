import 'dart:async';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/categorization_rule.dart';
import 'package:buoy_ai/pages/categorization_rules_page.dart';
import 'package:buoy_ai/services/categorization_rules_service.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeRulesService extends CategorizationRulesService {
  _FakeRulesService(this._load);

  final Future<List<CategorizationRule>> Function() _load;
  final deleted = <int>[];
  final toggled = <int>[];

  @override
  Future<List<CategorizationRule>> listRules() => _load();

  @override
  Future<CategorizationRule> toggleRule(int id) async {
    toggled.add(id);
    return const CategorizationRule(id: 1, name: 'r', isActive: false, sharedWithFamily: false);
  }

  @override
  Future<void> deleteRule(int id) async => deleted.add(id);
}

Widget wrap(Widget child) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: child,
    );

const _costco = CategorizationRule(
  id: 1,
  name: 'Costco rule',
  isActive: true,
  sharedWithFamily: false,
  merchantExact: 'Costco',
  assignCategoryName: 'Groceries',
  timesApplied: 3,
);

void main() {
  group('CategorizationRule.fromJson', () {
    test('reads camelCase', () {
      final r = CategorizationRule.fromJson({
        'id': 4, 'name': 'n', 'isActive': false, 'familyId': 9, 'ownerUserId': 12,
        'merchantExact': 'Costco', 'assignCategoryName': 'Groceries', 'timesApplied': 2,
      });
      expect(r.id, 4);
      expect(r.isActive, isFalse);
      expect(r.sharedWithFamily, isTrue);
      expect(r.ownerUserId, 12);
      expect(r.matchText, 'Costco');
      expect(r.timesApplied, 2);
    });

    test('reads PascalCase', () {
      final r = CategorizationRule.fromJson({
        'Id': 5, 'Name': 'n', 'IsActive': true, 'FamilyId': null, 'OwnerUserId': 3,
        'MerchantContains': 'Starbucks', 'AssignCategoryName': 'Coffee', 'TimesApplied': 0,
      });
      expect(r.id, 5);
      expect(r.sharedWithFamily, isFalse);
      expect(r.ownerUserId, 3);
      expect(r.matchText, 'Starbucks');
      expect(r.assignCategoryName, 'Coffee');
    });
  });

  group('CategorizationRulesPage', () {
    testWidgets('shows a spinner while loading', (tester) async {
      final pending = Completer<List<CategorizationRule>>();
      await tester.pumpWidget(wrap(CategorizationRulesPage(service: _FakeRulesService(() => pending.future))));
      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      pending.complete(const []);
      await tester.pumpAndSettle();
    });

    testWidgets('lists rules with what they file and how often', (tester) async {
      await tester.pumpWidget(wrap(CategorizationRulesPage(service: _FakeRulesService(() async => [_costco]))));
      await tester.pumpAndSettle();
      expect(find.text('Costco'), findsOneWidget);
      expect(find.text('Files as Groceries'), findsOneWidget);
      expect(find.textContaining('Used 3 times'), findsOneWidget);
      expect(find.text('Pause'), findsOneWidget);
    });

    testWidgets('explains how to make rules when there are none', (tester) async {
      await tester.pumpWidget(wrap(CategorizationRulesPage(service: _FakeRulesService(() async => const []))));
      await tester.pumpAndSettle();
      expect(find.textContaining('No rules yet'), findsOneWidget);
    });

    testWidgets('offers a retry when loading fails', (tester) async {
      await tester.pumpWidget(wrap(CategorizationRulesPage(
          service: _FakeRulesService(() async => throw Exception('offline')))));
      await tester.pumpAndSettle();
      expect(find.text("Couldn't load your rules."), findsOneWidget);
      expect(find.text('Try again'), findsOneWidget);
    });

    testWidgets('pausing and deleting call the service', (tester) async {
      final service = _FakeRulesService(() async => [_costco]);
      await tester.pumpWidget(wrap(CategorizationRulesPage(service: service)));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Pause'));
      await tester.pumpAndSettle();
      expect(service.toggled, [1]);
      expect(find.text('Resume'), findsOneWidget);

      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Delete'));
      await tester.pumpAndSettle();
      expect(service.deleted, [1]);
      expect(find.text('Costco'), findsNothing);
    });
  });
}
