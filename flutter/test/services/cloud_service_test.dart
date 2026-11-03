import 'package:buoy_ai/services/cloud_service.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('pairing status reads what the server sends', () {
    final s = CloudPairingStatus.fromJson({
      'configured': true,
      'paired': true,
      'name': 'Home',
      'pairedAt': '2026-10-12T10:00:00Z',
      'awaitingOwnerConfirmation': true,
    });

    expect(s.configured, isTrue);
    expect(s.paired, isTrue);
    expect(s.name, 'Home');
    expect(
        s.pairedAt!.isAtSameMomentAs(DateTime.utc(2026, 10, 12, 10)), isTrue);
    expect(s.awaitingOwnerConfirmation, isTrue);
  });

  test('the catalog keeps founding, prices in cents and what a product needs',
      () {
    final c = CloudCatalog.fromJson({
      'version': '1',
      'founding': true,
      'currency': 'usd',
      'products': [
        {
          'name': 'extra_logins',
          'displayName': 'Extra bank logins',
          'requires': ['bank_sync'],
          'monthly': {
            'lookupKey': 'x',
            'amountCents': 289,
            'currency': 'usd',
            'version': 1
          },
          'yearly': {
            'lookupKey': 'y',
            'amountCents': 2499,
            'currency': 'usd',
            'version': 1
          },
        },
      ],
    });

    expect(c.founding, isTrue);
    final p = c.products.single;
    expect(p.requires, ['bank_sync']);
    expect(p.monthly!.amountCents, 289);
    expect(p.yearly!.amountCents, 2499);
  });

  test('the plan says which products are held', () {
    final plan = CloudPlan.fromJson({
      'subscription': {'status': 'active'},
      'access': {'bankSync': true, 'ai': false},
      'hasLivePlan': true,
    });

    expect(plan.hasSubscription, isTrue);
    expect(plan.holds('bank_sync'), isTrue);
    expect(plan.holds('ai'), isFalse);
    expect(plan.holds('instant_refresh'), isFalse);
    expect(plan.hasLivePlan, isTrue);
    expect(plan.holds('unknown'), isFalse);
    expect(CloudPlan.fromJson({}).hasSubscription, isFalse);
  });

  test(
      'instant refresh is dropped from the catalog even if the service lists it',
      () {
    final c = CloudCatalog.fromJson({
      'founding': false,
      'products': [
        {'name': 'bank_sync', 'displayName': 'Bank sync'},
        {'name': 'instant_refresh', 'displayName': 'Instant refresh'},
      ],
    });

    expect(c.products.map((p) => p.name), ['bank_sync']);
  });

  test(
      'a plan held through a live Standard or Premium plan counts as having one',
      () {
    expect(CloudPlan.fromJson({'hasLivePlan': true}).hasSubscription, isTrue);
    expect(
        CloudPlan.fromJson({'subscriptionLookupFailed': true}).hasSubscription,
        isTrue);
  });

  test(
      'the billing summary reads the plan, the invoice lines and the lookup flag',
      () {
    final s = CloudBillingSummary.fromJson({
      'subscription': {
        'status': 'active',
        'interval': 'yearly',
        'founding': true,
        'currentPeriodEnd': '2027-01-15T00:00:00Z',
        'cancelAtPeriodEnd': false,
        'items': [
          {
            'product': 'extra_logins',
            'quantity': 2,
            'unitAmountCents': 900,
            'currency': 'usd'
          },
        ],
      },
      'upcoming': {
        'interval': 'yearly',
        'founding': true,
        'nextInvoiceAt': '2027-01-15T00:00:00Z',
        'currency': 'usd',
        'totalCents': 4199,
        'lines': [
          {
            'kind': 'product',
            'product': 'bank_sync',
            'description': 'Bank sync',
            'quantity': 1,
            'unitAmountCents': 2399,
            'amountCents': 2399,
            'proration': false
          },
          {'kind': 'ai_overage', 'amountCents': 275},
        ],
      },
      'lookupFailed': false,
    });

    expect(s.subscription!.founding, isTrue);
    expect(s.subscription!.interval, 'yearly');
    expect(s.subscription!.items.single.quantity, 2);
    expect(s.upcoming!.totalCents, 4199);
    expect(s.upcoming!.lines.map((l) => l.kind), ['product', 'ai_overage']);
    expect(s.upcoming!.lines.first.unitAmountCents, 2399);
    expect(s.upcoming!.lines.last.quantity, 1,
        reason: 'a line with no quantity counts as one');
    expect(s.lookupFailed, isFalse);
  });

  test('an empty billing summary means no plan, not an error', () {
    final s = CloudBillingSummary.fromJson(
        {'subscription': null, 'upcoming': null, 'lookupFailed': false});
    expect(s.subscription, isNull);
    expect(s.upcoming, isNull);
    expect(s.lookupFailed, isFalse);
    expect(CloudBillingSummary.fromJson({}).subscription, isNull);
  });

  group('code link', () {
    test('a started link reads camelCase', () {
      final l = CloudDeviceLink.fromJson({
        'userCode': 'BCDF-GHJK',
        'verificationUri': 'https://cloud.buoymoney.com/link',
        'verificationUriComplete':
            'https://cloud.buoymoney.com/link?code=BCDF-GHJK',
        'expiresAt': '2026-10-31T12:10:00Z',
        'interval': 5,
      });

      expect(l.userCode, 'BCDF-GHJK');
      expect(l.verificationUri, 'https://cloud.buoymoney.com/link');
      expect(l.verificationUriComplete,
          'https://cloud.buoymoney.com/link?code=BCDF-GHJK');
      expect(l.expiresAt!.isAtSameMomentAs(DateTime.utc(2026, 10, 31, 12, 10)),
          isTrue);
      expect(l.interval, 5);
    });

    test('a started link reads PascalCase and falls back to the plain page',
        () {
      final l = CloudDeviceLink.fromJson({
        'UserCode': 'BCDF-GHJK',
        'VerificationUri': 'https://cloud.buoymoney.com/link',
        'Interval': 7,
      });

      expect(l.userCode, 'BCDF-GHJK');
      expect(l.verificationUriComplete, 'https://cloud.buoymoney.com/link');
      expect(l.expiresAt, isNull);
      expect(l.interval, 7);
    });

    test('a waiting status carries the code so a reopened screen can show it',
        () {
      final s = CloudDeviceLinkStatus.fromJson({
        'state': 'waiting',
        'userCode': 'BCDF-GHJK',
        'expiresAt': '2026-10-31T12:10:00Z',
        'verificationUri': 'https://cloud.buoymoney.com/link',
        'verificationUriComplete':
            'https://cloud.buoymoney.com/link?code=BCDF-GHJK',
      });

      expect(s.state, CloudDeviceLinkState.waiting);
      expect(s.link!.userCode, 'BCDF-GHJK');
      expect(s.message, isNull);
    });

    test('a failed status reads PascalCase and keeps the reason', () {
      final s = CloudDeviceLinkStatus.fromJson(
          {'State': 'Failed', 'Message': 'Could not reach the BuoyAI Cloud.'});

      expect(s.state, CloudDeviceLinkState.failed);
      expect(s.link, isNull);
      expect(s.message, 'Could not reach the BuoyAI Cloud.');
    });

    test('an unknown state reads as none', () {
      expect(
          CloudDeviceLinkStateX.fromString('later'), CloudDeviceLinkState.none);
      expect(CloudDeviceLinkStateX.fromString(null), CloudDeviceLinkState.none);
      expect(CloudDeviceLinkStateX.fromInt(2), CloudDeviceLinkState.linked);
      expect(CloudDeviceLinkStateX.fromInt(99), CloudDeviceLinkState.none);
      expect(CloudDeviceLinkState.denied.displayName, 'Turned down');
    });
  });
}
