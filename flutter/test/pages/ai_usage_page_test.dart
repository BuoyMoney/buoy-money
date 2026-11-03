import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/ai_usage_page.dart';
import 'package:buoy_ai/services/ai_usage_service.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeService extends AiUsageService {
  _FakeService(this.summary, {this.fail = false, this.paygResult, this.paygError});

  AiUsageSummary summary;
  final bool fail;
  final AiUsageSummary? paygResult;
  final Object? paygError;
  final List<({bool enabled, int capMicros})> paygCalls = [];

  @override
  Future<AiUsageSummary> fetch() async {
    if (fail) throw Exception('404');
    return summary;
  }

  @override
  Future<AiUsageSummary> setPayg({required bool enabled, required int capMicros}) async {
    paygCalls.add((enabled: enabled, capMicros: capMicros));
    if (paygError != null) throw paygError!;
    return paygResult ?? summary;
  }
}

AiUsageSummary _summary({
  String source = 'cloud',
  bool unlimited = false,
  int allowance = 1500000,
  int used = 600000,
  int percent = 40,
  String state = 'ok',
  bool blocked = false,
  AiPaygStatus payg = const AiPaygStatus(available: true, maxCapMicros: 20000000),
  List<AiFeatureUsage> features = const [
    AiFeatureUsage(feature: 'categorization', calls: 40, costMicros: 500000),
    AiFeatureUsage(feature: 'insights', calls: 1, costMicros: 100000),
  ],
  int projected = 0,
}) =>
    AiUsageSummary(
      source: source,
      unlimited: unlimited,
      periodStart: DateTime(2026, 10, 1),
      periodEnd: DateTime(2026, 11, 1),
      allowanceMicros: allowance,
      usedMicros: used,
      percent: percent,
      state: state,
      blocked: blocked,
      payg: payg,
      byFeature: features,
      projectedMicros: projected,
    );

Widget _wrap(AiUsageService service, {bool admin = false}) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: AiUsagePage(service: service, isAdmin: admin),
    );

void main() {
  testWidgets('shows used of allowance, the percent and where it went', (tester) async {
    await tester.pumpWidget(_wrap(_FakeService(_summary())));
    await tester.pumpAndSettle();

    expect(find.text(r'$0.60 of $1.50'), findsOneWidget);
    expect(find.text('40% used'), findsWidgets);
    expect(find.text('Categorizing transactions'), findsOneWidget);
    expect(find.text(r'$0.50'), findsOneWidget);
    expect(find.text('40 calls'), findsOneWidget);
    expect(find.text('1 call'), findsOneWidget);
    // Nothing alarming at 40 percent.
    expect(find.textContaining('You have used'), findsNothing);
    expect(find.textContaining('used up'), findsNothing);
  });

  testWidgets('at 80 percent it warns', (tester) async {
    await tester.pumpWidget(_wrap(_FakeService(_summary(used: 1200000, percent: 80, state: 'warning'))));
    await tester.pumpAndSettle();

    expect(find.text("You have used 80% of this period's AI allowance."), findsOneWidget);
  });

  testWidgets('allowance used up and blocked says AI is paused', (tester) async {
    await tester.pumpWidget(_wrap(_FakeService(_summary(used: 1500000, percent: 100, state: 'over', blocked: true))));
    await tester.pumpAndSettle();

    expect(find.text('AI is paused until the next period, or until pay-as-you-go is turned on.'), findsOneWidget);
  });

  testWidgets('past the allowance on pay-as-you-go says so', (tester) async {
    await tester.pumpWidget(_wrap(_FakeService(_summary(
      used: 1700000,
      percent: 113,
      state: 'over',
      payg: const AiPaygStatus(available: true, enabled: true, capMicros: 5000000, usedMicros: 200000, maxCapMicros: 20000000),
    ))));
    await tester.pumpAndSettle();

    expect(find.text('You are past the allowance and using pay-as-you-go.'), findsOneWidget);
    expect(find.text(r'On. Up to $5.00 of extra spend this period. Used so far: $0.20.'), findsOneWidget);
  });

  testWidgets("a server's own monthly figure over 100 percent is a warning, never 'used up'", (tester) async {
    await tester.pumpWidget(_wrap(_FakeService(_summary(
      source: 'local',
      allowance: 5000000,
      used: 5500000,
      percent: 110,
      state: 'over',
      payg: const AiPaygStatus(),
    ))));
    await tester.pumpAndSettle();

    expect(find.text("You have used 110% of this period's AI allowance."), findsOneWidget);
    expect(find.text('Pay-as-you-go'), findsNothing);
  });

  testWidgets('an unlimited server shows spend as statistics and no bar', (tester) async {
    await tester.pumpWidget(_wrap(_FakeService(_summary(source: 'local', unlimited: true, allowance: 0, used: 2340000, payg: const AiPaygStatus()))));
    await tester.pumpAndSettle();

    expect(find.text('No limit on this server'), findsOneWidget);
    expect(find.text(r'$2.34 spent this period'), findsOneWidget);
    expect(find.textContaining('% used'), findsNothing);
    expect(find.text('Pay-as-you-go'), findsNothing);
  });

  testWidgets('no calls yet says so', (tester) async {
    await tester.pumpWidget(_wrap(_FakeService(_summary(used: 0, percent: 0, features: const []))));
    await tester.pumpAndSettle();

    expect(find.text('No AI calls yet this period.'), findsOneWidget);
  });

  testWidgets('a failed load offers a retry', (tester) async {
    final service = _FakeService(_summary(), fail: true);
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    expect(find.textContaining('Could not load AI usage'), findsOneWidget);
    expect(find.text('Try again'), findsOneWidget);
  });

  group('pay-as-you-go', () {
    testWidgets('a member sees the status but no switch', (tester) async {
      await tester.pumpWidget(_wrap(_FakeService(_summary())));
      await tester.pumpAndSettle();

      expect(find.text('Pay-as-you-go'), findsOneWidget);
      expect(find.text("Your server's owner manages this."), findsOneWidget);
      expect(find.text('Turn on'), findsNothing);
    });

    testWidgets('when it is not available on the account, nothing can be turned on', (tester) async {
      await tester.pumpWidget(_wrap(_FakeService(_summary(payg: const AiPaygStatus())), admin: true));
      await tester.pumpAndSettle();

      expect(find.text('Pay-as-you-go is not available on this account yet.'), findsOneWidget);
      expect(find.text('Turn on'), findsNothing);
    });

    testWidgets('the admin turns it on with a limit, sent as whole micro-dollars', (tester) async {
      final on = _summary(payg: const AiPaygStatus(available: true, enabled: true, capMicros: 5500000, maxCapMicros: 20000000));
      final service = _FakeService(_summary(), paygResult: on);
      await tester.pumpWidget(_wrap(service, admin: true));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), '5.50');
      await tester.tap(find.text('Turn on'));
      await tester.pumpAndSettle();

      expect(service.paygCalls, [(enabled: true, capMicros: 5500000)]);
      expect(find.text(r'On. Up to $5.50 of extra spend this period. Used so far: $0.00.'), findsOneWidget);
      expect(find.text('Turn off'), findsOneWidget);
    });

    testWidgets('a limit above the maximum or not an amount is refused before anything is sent', (tester) async {
      final service = _FakeService(_summary());
      await tester.pumpWidget(_wrap(service, admin: true));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), '21');
      await tester.tap(find.text('Turn on'));
      await tester.pumpAndSettle();
      expect(find.text(r'Enter an amount above zero and no more than $20.00.'), findsOneWidget);

      await tester.enterText(find.byType(TextField), 'lots');
      await tester.tap(find.text('Turn on'));
      await tester.pumpAndSettle();
      expect(find.text(r'Enter an amount above zero and no more than $20.00.'), findsOneWidget);
      expect(service.paygCalls, isEmpty);
    });

    testWidgets('the admin turns it off', (tester) async {
      final onSummary = _summary(payg: const AiPaygStatus(available: true, enabled: true, capMicros: 5000000, maxCapMicros: 20000000));
      final service = _FakeService(onSummary, paygResult: _summary());
      await tester.pumpWidget(_wrap(service, admin: true));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Turn off'));
      await tester.pumpAndSettle();

      expect(service.paygCalls, [(enabled: false, capMicros: 0)]);
      expect(find.text('Turn on'), findsOneWidget);
    });

    testWidgets("the server's own words are shown when it refuses", (tester) async {
      final service = _FakeService(
        _summary(),
        paygError: DioException(
          requestOptions: RequestOptions(path: '/aiusage/payg'),
          response: Response(
            requestOptions: RequestOptions(path: '/aiusage/payg'),
            statusCode: 400,
            data: {'message': r'Set a limit above $0 and at most $20.'},
          ),
        ),
      );
      await tester.pumpWidget(_wrap(service, admin: true));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), '5');
      await tester.tap(find.text('Turn on'));
      await tester.pumpAndSettle();

      expect(find.text(r'Set a limit above $0 and at most $20.'), findsOneWidget);
    });

    testWidgets('an unexpected failure says nothing was changed and the page stays usable', (tester) async {
      final service = _FakeService(_summary(), paygError: Exception('boom'));
      await tester.pumpWidget(_wrap(service, admin: true));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), '5');
      await tester.tap(find.text('Turn on'));
      await tester.pumpAndSettle();

      expect(find.text('That could not be changed. Try again.'), findsOneWidget);
      expect(find.text('Turn on'), findsOneWidget);
    });
  });
}
