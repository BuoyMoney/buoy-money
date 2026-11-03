import 'package:buoy_ai/models/billing_models.dart';
import 'package:buoy_ai/services/store_billing_service.dart';
import 'package:flutter_test/flutter_test.dart';

/// In-app purchase contract with the backend: the provider on the current
/// subscription (store vs Stripe decides which management UI is shown) and the
/// store product ids on pricing plans (what the app asks the store to sell).
void main() {
  group('SubscriptionProvider', () {
    test('maps the backend enum values and tolerates PascalCase', () {
      expect(SubscriptionProviderExtension.fromInt(0), SubscriptionProvider.stripe);
      expect(SubscriptionProviderExtension.fromInt(1), SubscriptionProvider.appStore);
      expect(SubscriptionProviderExtension.fromInt(2), SubscriptionProvider.playStore);
      expect(SubscriptionProviderExtension.fromInt(99), SubscriptionProvider.stripe);
      expect(SubscriptionProviderExtension.fromString('AppStore'), SubscriptionProvider.appStore);
      expect(SubscriptionProviderExtension.fromString('play_store'), SubscriptionProvider.playStore);
      expect(SubscriptionProvider.appStore.isStore, isTrue);
      expect(SubscriptionProvider.stripe.isStore, isFalse);
      expect(SubscriptionProvider.appStore.displayName, 'App Store');
    });

    test('a store subscription parses from GET /billing/current', () {
      final sub = CurrentSubscription.fromJson({
        'id': 12,
        'stripeSubscriptionId': null,
        'provider': 1,
        'tier': 2,
        'interval': 1,
        'status': 0, // backend UserSubscriptionStatus.Active
        'currentPeriodStart': '2026-09-12T00:00:00Z',
        'currentPeriodEnd': '2026-10-12T00:00:00Z',
        'cancelAtPeriodEnd': true,
        'amount': 17.99,
      });
      expect(sub.provider, SubscriptionProvider.appStore);
      expect(sub.provider.isStore, isTrue);
      expect(sub.stripeSubscriptionId, isNull);
      expect(sub.cancelAtPeriodEnd, isTrue);
      expect(sub.isActive, isTrue);
    });

    test('defaults to Stripe when the field is absent (older backend)', () {
      final sub = CurrentSubscription.fromJson({'tier': 1, 'interval': 1, 'status': 0});
      expect(sub.provider, SubscriptionProvider.stripe);
    });
  });

  group('PricingPlan store product ids', () {
    test('parse from the plans endpoint in either casing', () {
      final plan = PricingPlan.fromJson({
        'tier': 'Premium',
        'name': 'Premium',
        'description': '',
        'monthlyPriceCents': 1500,
        'yearlyPriceCents': 15000,
        'monthlyPriceId': 'price_m',
        'yearlyPriceId': 'price_y',
        'StoreProductIdMonthly': 'buoyai_premium_monthly',
        'storeProductIdYearly': 'buoyai_premium_yearly',
        'currency': 'usd',
        'isPopular': true,
        'features': <String>[],
      });
      expect(plan.storeProductIdMonthly, 'buoyai_premium_monthly');
      expect(plan.storeProductIdYearly, 'buoyai_premium_yearly');
    });

    test('are empty, not null, when the backend predates them', () {
      final plan = PricingPlan.fromJson({
        'tier': 'Standard',
        'name': 'Standard',
        'description': '',
        'monthlyPriceCents': 1000,
        'yearlyPriceCents': 10000,
        'monthlyPriceId': '',
        'yearlyPriceId': '',
        'currency': 'usd',
        'isPopular': false,
        'features': <String>[],
      });
      expect(plan.storeProductIdMonthly, '');
    });
  });

  group('StoreBillingService.normalizeProductId', () {
    test('drops a Play base-plan suffix and keeps App Store ids as-is', () {
      expect(StoreBillingService.normalizeProductId('buoyai_premium_monthly:monthly'), 'buoyai_premium_monthly');
      expect(StoreBillingService.normalizeProductId('buoyai_premium_monthly'), 'buoyai_premium_monthly');
    });
  });
}
