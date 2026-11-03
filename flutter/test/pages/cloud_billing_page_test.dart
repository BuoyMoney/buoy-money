import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/cloud_billing_page.dart';
import 'package:buoy_ai/services/ai_status_service.dart';
import 'package:buoy_ai/services/ai_usage_service.dart';
import 'package:buoy_ai/services/cloud_service.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:buoy_ai/widgets/cinematic/glass_settings.dart';

class _FakeCloud extends CloudService {
  _FakeCloud({
    this.summary,
    this.summaryError,
    this.portalError,
    this.planValue = const CloudPlan(),
  });

  CloudBillingSummary? summary;
  Object? summaryError;
  final String portalUrl = 'https://billing.stripe.test/p/abc';
  final Object? portalError;
  final CloudPlan planValue;

  var summaryCalls = 0;
  final summarySelfHost = <bool>[];
  final portalSelfHost = <bool>[];
  final portalFromPortal = <bool>[];

  @override
  Future<CloudBillingSummary?> billingSummary({required bool selfHost}) async {
    summaryCalls++;
    summarySelfHost.add(selfHost);
    final error = summaryError;
    if (error != null) throw error;
    return summary;
  }

  @override
  Future<String> billingPortal({
    required bool selfHost,
    bool fromPortal = false,
  }) async {
    portalSelfHost.add(selfHost);
    portalFromPortal.add(fromPortal);
    if (portalError != null) throw portalError!;
    return portalUrl;
  }

  @override
  Future<CloudPlan> plan() async => planValue;
}

DioException _http(int status, [String? message, String? code]) => DioException(
  requestOptions: RequestOptions(path: '/x'),
  response: Response(
    requestOptions: RequestOptions(path: '/x'),
    statusCode: status,
    data: message == null && code == null
        ? null
        : {
            if (code != null) 'code': code,
            if (message != null) 'message': message,
          },
  ),
);

CloudInvoiceLine _line(
  String kind,
  String? product,
  int amount, {
  int quantity = 1,
  int unit = 0,
  String description = '',
  bool proration = false,
}) => CloudInvoiceLine(
  kind: kind,
  product: product,
  description: description,
  quantity: quantity,
  unitAmountCents: unit,
  amountCents: amount,
  proration: proration,
);

CloudPlanState _plan({
  bool founding = true,
  String interval = 'yearly',
  String status = 'active',
  bool cancels = false,
}) => CloudPlanState(
  status: status,
  interval: interval,
  founding: founding,
  currentPeriodEnd: DateTime(2027, 1, 15),
  cancelAtPeriodEnd: cancels,
  items: const [
    CloudPlanItem(
      product: 'bank_sync',
      quantity: 1,
      unitAmountCents: 2399,
      currency: 'usd',
    ),
    CloudPlanItem(
      product: 'extra_logins',
      quantity: 2,
      unitAmountCents: 900,
      currency: 'usd',
    ),
  ],
);

CloudUpcomingInvoice _invoice(
  List<CloudInvoiceLine> lines, {
  int total = 4199,
  String interval = 'yearly',
  bool founding = true,
}) => CloudUpcomingInvoice(
  interval: interval,
  founding: founding,
  nextInvoiceAt: DateTime(2027, 1, 15),
  currency: 'usd',
  totalCents: total,
  lines: lines,
);

CloudBillingSummary _typical({List<CloudInvoiceLine> extra = const []}) =>
    CloudBillingSummary(
      subscription: _plan(),
      upcoming: _invoice([
        _line('product', 'bank_sync', 2399, unit: 2399),
        _line('product', 'extra_logins', 1800, quantity: 2, unit: 900),
        ...extra,
      ]),
    );

AiStatus _aiOn() => const AiStatus(
  enabled: true,
  provider: 'buoy-cloud',
  edition: 'hosted',
  features: [],
);

AiUsageSummary _usage({AiPaygStatus payg = const AiPaygStatus()}) =>
    AiUsageSummary(
      source: 'cloud',
      unlimited: false,
      periodStart: DateTime(2026, 12, 15),
      periodEnd: DateTime(2027, 1, 15),
      allowanceMicros: 5000000,
      usedMicros: 2000000,
      percent: 40,
      state: 'ok',
      blocked: false,
      payg: payg,
      byFeature: const [],
      projectedMicros: 0,
    );

Future<void> _pump(
  WidgetTester tester,
  _FakeCloud cloud, {
  bool selfHost = false,
  Future<AiStatus> Function()? ai,
  Future<AiUsageSummary> Function()? usage,
  List<String>? opened,
  bool portal = false,
}) async {
  tester.view.physicalSize = const Size(900, 3000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  ServerEdition.current.value = ServerCapabilities(
    edition: selfHost ? 'selfhost' : 'hosted',
  );
  addTearDown(() => ServerEdition.current.value = ServerCapabilities.hosted);
  await tester.pumpWidget(
    MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: CloudBillingPage(
        key: UniqueKey(),
        service: cloud,
        aiStatusLoader: ai ?? () async => _aiOn(),
        aiUsageLoader: usage ?? () async => _usage(),
        open: (url) async => opened?.add(url),
        portal: portal,
        animateBackground: false,
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  setUpAll(() => GoogleFonts.config.allowRuntimeFetching = false);

  group('line items', () {
    testWidgets('lists each product with its price and interval', (
      tester,
    ) async {
      await _pump(tester, _FakeCloud(summary: _typical()));

      expect(
        find.byKey(const ValueKey('cloud-bill-line-product-Bank sync')),
        findsOneWidget,
      );
      expect(find.text(r'$23.99 a year'), findsOneWidget);
    });

    testWidgets(
      'extra bank logins read quantity x unit price, then the line total',
      (tester) async {
        await _pump(tester, _FakeCloud(summary: _typical()));

        expect(find.text('Extra bank logins'), findsOneWidget);
        expect(find.text(r'2 x $9.00 a year'), findsOneWidget);
        expect(find.text(r'$18.00'), findsOneWidget);
      },
    );

    testWidgets('a monthly plan prices in months', (tester) async {
      await _pump(
        tester,
        _FakeCloud(
          summary: CloudBillingSummary(
            subscription: _plan(interval: 'monthly'),
            upcoming: _invoice(
              [_line('product', 'bank_sync', 279, unit: 279)],
              interval: 'monthly',
              total: 279,
            ),
          ),
        ),
      );

      expect(find.text(r'$2.79 a month'), findsOneWidget);
    });

    testWidgets('the bundle shows under its own name', (tester) async {
      await _pump(
        tester,
        _FakeCloud(
          summary: CloudBillingSummary(
            subscription: _plan(founding: false),
            upcoming: _invoice(
              [_line('product', 'bank_sync_ai_bundle', 5499, unit: 5499)],
              total: 5499,
              founding: false,
            ),
          ),
        ),
      );

      expect(find.text('Bank sync + AI bundle'), findsOneWidget);
      expect(find.text(r'$54.99 a year'), findsOneWidget);
    });

    testWidgets('pay-as-you-go spend so far is its own line', (tester) async {
      await _pump(
        tester,
        _FakeCloud(
          summary: _typical(extra: [_line('ai_overage', 'ai_overage', 275)]),
        ),
      );

      expect(find.text('AI pay-as-you-go'), findsOneWidget);
      expect(find.text('Used so far this period'), findsOneWidget);
      expect(find.text(r'$2.75'), findsOneWidget);
    });

    testWidgets('extra refreshes show only when there is a charge', (
      tester,
    ) async {
      await _pump(
        tester,
        _FakeCloud(
          summary: _typical(
            extra: [
              _line(
                'refresh_overage',
                null,
                0,
                description: 'Extra instant refresh x0',
              ),
            ],
          ),
        ),
      );
      expect(find.text('Extra instant refreshes'), findsNothing);

      await _pump(
        tester,
        _FakeCloud(
          summary: _typical(
            extra: [
              _line(
                'refresh_overage',
                null,
                48,
                description: 'Extra instant refresh x3',
              ),
            ],
          ),
        ),
      );
      expect(find.text('Extra instant refreshes'), findsOneWidget);
      expect(find.text(r'$0.48'), findsOneWidget);
    });

    testWidgets('a credit shows as a negative amount', (tester) async {
      await _pump(
        tester,
        _FakeCloud(
          summary: _typical(
            extra: [
              _line('other', null, -150, description: 'Unused time on AI'),
            ],
          ),
        ),
      );

      expect(find.text('Unused time on AI'), findsOneWidget);
      expect(find.text(r'-$1.50'), findsOneWidget);
    });

    testWidgets(
      'without an invoice preview the plan itself stands in, and says so',
      (tester) async {
        await _pump(
          tester,
          _FakeCloud(
            summary: CloudBillingSummary(
              subscription: _plan(),
              upcoming: null,
              lookupFailed: true,
            ),
          ),
        );

        expect(find.text(r'$23.99 a year'), findsOneWidget);
        expect(find.text(r'2 x $9.00 a year'), findsOneWidget);
        expect(
          find.byKey(const ValueKey('cloud-bill-no-invoice')),
          findsOneWidget,
        );
        expect(find.byKey(const ValueKey('cloud-bill-total')), findsNothing);
      },
    );

    testWidgets('nothing on the page offers or names instant refresh', (
      tester,
    ) async {
      await _pump(tester, _FakeCloud(summary: _typical()));

      expect(find.textContaining('efresh'), findsNothing);
    });
  });

  group('plan, badge and next invoice', () {
    testWidgets('founding and list customers get the matching badge', (
      tester,
    ) async {
      await _pump(tester, _FakeCloud(summary: _typical()));
      expect(find.byKey(const ValueKey('cloud-bill-founding')), findsOneWidget);
      expect(find.text('Founding price'), findsOneWidget);

      await _pump(
        tester,
        _FakeCloud(
          summary: CloudBillingSummary(
            subscription: _plan(founding: false),
            upcoming: _invoice(
              [_line('product', 'bank_sync', 3599, unit: 3599)],
              founding: false,
              total: 3599,
            ),
          ),
        ),
      );
      expect(find.byKey(const ValueKey('cloud-bill-list')), findsOneWidget);
      expect(find.text('List price'), findsOneWidget);
    });

    testWidgets('shows the next invoice date and the estimated total', (
      tester,
    ) async {
      await _pump(tester, _FakeCloud(summary: _typical()));

      expect(
        find.byKey(const ValueKey('cloud-bill-next-date')),
        findsOneWidget,
      );
      expect(
        tester
            .widget<Text>(find.byKey(const ValueKey('cloud-bill-next-date')))
            .data,
        allOf(startsWith('Billed on'), contains('2027')),
      );
      expect(find.text('Estimated total'), findsOneWidget);
      expect(
        tester
            .widget<Text>(find.byKey(const ValueKey('cloud-bill-total')))
            .data,
        r'$41.99',
      );
    });

    testWidgets('a plan that ends says when, and a late payment says so', (
      tester,
    ) async {
      await _pump(
        tester,
        _FakeCloud(
          summary: CloudBillingSummary(
            subscription: _plan(cancels: true, status: 'past_due'),
            upcoming: _invoice([
              _line('product', 'bank_sync', 2399, unit: 2399),
            ]),
          ),
        ),
      );

      expect(find.byKey(const ValueKey('cloud-bill-ends')), findsOneWidget);
      expect(find.textContaining('Payment past due'), findsOneWidget);
    });
  });

  group('AI usage', () {
    testWidgets('shows spend against the allowance', (tester) async {
      await _pump(tester, _FakeCloud(summary: _typical()));

      expect(find.byKey(const ValueKey('cloud-bill-ai-meter')), findsOneWidget);
      expect(find.text(r'$2.00 of $5.00 used'), findsOneWidget);
    });

    testWidgets('with pay-as-you-go on, shows the spend and the cap', (
      tester,
    ) async {
      await _pump(
        tester,
        _FakeCloud(summary: _typical()),
        usage: () async => _usage(
          payg: const AiPaygStatus(
            available: true,
            enabled: true,
            capMicros: 10000000,
            usedMicros: 2750000,
          ),
        ),
      );

      expect(find.byKey(const ValueKey('cloud-bill-payg-on')), findsOneWidget);
      expect(
        find.text(
          r'Pay-as-you-go is on. $2.75 spent beyond the allowance, capped at $10.00.',
        ),
        findsOneWidget,
      );
    });

    testWidgets('with it off, says AI pauses at the allowance', (tester) async {
      await _pump(
        tester,
        _FakeCloud(summary: _typical()),
        usage: () async => _usage(payg: const AiPaygStatus(available: true)),
      );

      expect(find.byKey(const ValueKey('cloud-bill-payg-off')), findsOneWidget);
    });

    testWidgets('no card when AI is off or the meter will not load', (
      tester,
    ) async {
      await _pump(
        tester,
        _FakeCloud(summary: _typical()),
        ai: () async => const AiStatus(
          enabled: false,
          provider: 'none',
          edition: 'hosted',
          features: [],
        ),
      );
      expect(find.byKey(const ValueKey('cloud-bill-ai')), findsNothing);

      await _pump(
        tester,
        _FakeCloud(summary: _typical()),
        usage: () async => throw _http(500),
      );
      expect(find.byKey(const ValueKey('cloud-bill-ai')), findsNothing);
      // The rest of the page is unaffected.
      expect(find.byKey(const ValueKey('cloud-bill-plan')), findsOneWidget);
    });
  });

  group('payment method and invoices', () {
    testWidgets('the button opens the Stripe page', (tester) async {
      final cloud = _FakeCloud(summary: _typical());
      final opened = <String>[];
      await _pump(tester, cloud, opened: opened);

      expect(find.text('Manage payment method and invoices'), findsOneWidget);
      await tester.tap(find.byKey(const ValueKey('cloud-bill-manage')));
      await tester.pumpAndSettle();

      expect(opened, ['https://billing.stripe.test/p/abc']);
      expect(cloud.portalSelfHost, [false]);
    });

    testWidgets('a self-hosted server asks through its own endpoint', (
      tester,
    ) async {
      final cloud = _FakeCloud(summary: _typical());
      await _pump(tester, cloud, selfHost: true, opened: []);

      await tester.tap(find.byKey(const ValueKey('cloud-bill-manage')));
      await tester.pumpAndSettle();

      expect(cloud.summarySelfHost, [true]);
      expect(cloud.portalSelfHost, [true]);
    });

    testWidgets('a refusal shows the server message and opens nothing', (
      tester,
    ) async {
      final cloud = _FakeCloud(
        summary: _typical(),
        portalError: _http(503, 'Billing could not be reached.'),
      );
      final opened = <String>[];
      await _pump(tester, cloud, opened: opened);

      await tester.tap(find.byKey(const ValueKey('cloud-bill-manage')));
      await tester.pumpAndSettle();

      expect(opened, isEmpty);
      expect(find.text('Billing could not be reached.'), findsOneWidget);
    });
  });

  group('empty, loading and error states', () {
    testWidgets('shows a spinner while loading', (tester) async {
      tester.view.physicalSize = const Size(900, 3000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: CloudBillingPage(
            service: _FakeCloud(summary: _typical()),
            aiStatusLoader: () async => _aiOn(),
            aiUsageLoader: () async => _usage(),
          ),
        ),
      );

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
      await tester.pumpAndSettle();
    });

    testWidgets('no Cloud plan is an empty state with a way to the add-ons', (
      tester,
    ) async {
      await _pump(tester, _FakeCloud(summary: const CloudBillingSummary()));

      expect(find.byKey(const ValueKey('cloud-bill-empty')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-bill-addons')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-bill-manage')), findsNothing);
    });

    testWidgets('a self-hosted server with no plan has no add-ons button', (
      tester,
    ) async {
      await _pump(
        tester,
        _FakeCloud(summary: const CloudBillingSummary()),
        selfHost: true,
      );

      expect(find.byKey(const ValueKey('cloud-bill-empty')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-bill-addons')), findsNothing);
    });

    testWidgets('a failed load shows an error and tries again', (tester) async {
      final cloud = _FakeCloud(summaryError: _http(500));
      await _pump(tester, cloud);
      expect(find.byKey(const ValueKey('cloud-bill-failed')), findsOneWidget);

      cloud.summaryError = null;
      cloud.summary = _typical();
      await tester.tap(find.byKey(const ValueKey('cloud-bill-retry')));
      await tester.pumpAndSettle();

      expect(cloud.summaryCalls, 2);
      expect(find.byKey(const ValueKey('cloud-bill-plan')), findsOneWidget);
    });

    testWidgets('a billing outage is "could not load", never "no plan"', (
      tester,
    ) async {
      await _pump(
        tester,
        _FakeCloud(summary: const CloudBillingSummary(lookupFailed: true)),
      );

      expect(find.byKey(const ValueKey('cloud-bill-failed')), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-bill-empty')), findsNothing);
    });

    testWidgets('someone who is not the administrator is told so', (
      tester,
    ) async {
      await _pump(tester, _FakeCloud(summaryError: _http(403)), selfHost: true);

      expect(
        find.byKey(const ValueKey('cloud-bill-forbidden')),
        findsOneWidget,
      );
      expect(find.text("Your server's owner manages this."), findsOneWidget);
      expect(find.byKey(const ValueKey('cloud-bill-failed')), findsNothing);
    });

    testWidgets(
      'an unconfirmed pairing says to confirm by email, not "admin only"',
      (tester) async {
        await _pump(
          tester,
          _FakeCloud(
            summaryError: _http(
              403,
              'Check your email and confirm this server first.',
              'awaiting_owner_confirmation',
            ),
          ),
          selfHost: true,
        );

        expect(
          find.byKey(const ValueKey('cloud-bill-awaiting')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('cloud-bill-forbidden')),
          findsNothing,
        );
      },
    );

    testWidgets(
      'a server that is not linked shows a calm note and the link action, not an error',
      (tester) async {
        await _pump(
          tester,
          _FakeCloud(
            summaryError: _http(
              409,
              'This server is not paired with the Cloud.',
              'not_paired',
            ),
          ),
          selfHost: true,
        );

        expect(
          find.byKey(const ValueKey('cloud-bill-not-linked')),
          findsOneWidget,
        );
        expect(find.byKey(const ValueKey('cloud-bill-link')), findsOneWidget);
        expect(find.text('Link with BuoyAI Cloud'), findsOneWidget);
        expect(find.byKey(const ValueKey('cloud-bill-failed')), findsNothing);
        // The server's raw words never show, and nothing on the page is a red error.
        expect(find.textContaining('not paired'), findsNothing);
        final notes = tester.widgetList<GlassNote>(find.byType(GlassNote));
        expect(notes, isNotEmpty);
        expect(notes.every((n) => n.tone == GlassNoteTone.info), isTrue);
      },
    );

    testWidgets(
      'opening the billing page on a server that lost its link shows the link card, not a red toast',
      (tester) async {
        await _pump(
          tester,
          _FakeCloud(
            summary: _typical(),
            portalError: _http(
              409,
              'This server is not paired with the Cloud.',
              'not_paired',
            ),
          ),
          selfHost: true,
        );

        await tester.tap(find.byKey(const ValueKey('cloud-bill-manage')));
        await tester.pumpAndSettle();

        expect(find.textContaining('not paired'), findsNothing);
        expect(
          find.byKey(const ValueKey('cloud-bill-not-linked')),
          findsOneWidget,
        );
      },
    );

    testWidgets('a server with no Cloud billing says it is not available', (
      tester,
    ) async {
      await _pump(tester, _FakeCloud(summary: null), selfHost: true);

      expect(
        find.byKey(const ValueKey('cloud-bill-unavailable')),
        findsOneWidget,
      );
    });
  });

  group('the older hosted plan', () {
    testWidgets(
      'a user who also holds a Standard or Premium plan gets a link to Billing',
      (tester) async {
        await _pump(
          tester,
          _FakeCloud(
            summary: _typical(),
            planValue: const CloudPlan(
              hasSubscription: true,
              hasLivePlan: true,
            ),
          ),
        );

        expect(
          find.byKey(const ValueKey('cloud-bill-hosted-plan')),
          findsOneWidget,
        );
        expect(
          find.byKey(const ValueKey('cloud-bill-open-billing')),
          findsOneWidget,
        );
      },
    );

    testWidgets('the link is there even with no Cloud plan', (tester) async {
      await _pump(
        tester,
        _FakeCloud(
          summary: const CloudBillingSummary(),
          planValue: const CloudPlan(hasSubscription: true, hasLivePlan: true),
        ),
      );

      expect(
        find.byKey(const ValueKey('cloud-bill-open-billing')),
        findsOneWidget,
      );
    });

    testWidgets('no link for a user without one, or on a self-hosted server', (
      tester,
    ) async {
      await _pump(tester, _FakeCloud(summary: _typical()));
      expect(
        find.byKey(const ValueKey('cloud-bill-open-billing')),
        findsNothing,
      );

      await _pump(
        tester,
        _FakeCloud(
          summary: _typical(),
          planValue: const CloudPlan(hasLivePlan: true),
        ),
        selfHost: true,
      );
      expect(
        find.byKey(const ValueKey('cloud-bill-open-billing')),
        findsNothing,
      );
    });
  });

  group('in the Cloud portal (buoy-ai.com/cloud/billing)', () {
    testWidgets('the Stripe page is asked to come back to the portal', (
      tester,
    ) async {
      final cloud = _FakeCloud(summary: _typical());
      final opened = <String>[];
      await _pump(tester, cloud, opened: opened, portal: true);

      await tester.tap(find.byKey(const ValueKey('cloud-bill-manage')));
      await tester.pumpAndSettle();

      expect(opened, ['https://billing.stripe.test/p/abc']);
      expect(cloud.portalFromPortal, [true]);
    });

    testWidgets('has no links into the hosted app', (tester) async {
      await _pump(
        tester,
        _FakeCloud(
          summary: _typical(),
          planValue: const CloudPlan(hasSubscription: true, hasLivePlan: true),
        ),
        portal: true,
      );

      expect(find.byKey(const ValueKey('cloud-bill-open-billing')), findsNothing);
      expect(find.byKey(const ValueKey('cloud-bill-ai-usage')), findsNothing);
    });

    testWidgets('the app itself still asks for the app return', (tester) async {
      final cloud = _FakeCloud(summary: _typical());
      await _pump(tester, cloud);

      await tester.tap(find.byKey(const ValueKey('cloud-bill-manage')));
      await tester.pumpAndSettle();

      expect(cloud.portalFromPortal, [false]);
    });
  });
}
