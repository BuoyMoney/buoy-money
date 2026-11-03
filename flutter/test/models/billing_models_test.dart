import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/billing_models.dart';

void main() {
  group('SubscriptionTier', () {
    test('displayName returns correct names', () {
      expect(SubscriptionTier.trial.displayName, 'Trial');
      expect(SubscriptionTier.standard.displayName, 'Standard');
      expect(SubscriptionTier.premium.displayName, 'Premium');
      expect(SubscriptionTier.premiumInherited.displayName, 'Premium (Family)');
      expect(SubscriptionTier.admin.displayName, 'Admin');
    });

    test('fromInt converts integers correctly', () {
      expect(SubscriptionTierExtension.fromInt(0), SubscriptionTier.trial);
      expect(SubscriptionTierExtension.fromInt(1), SubscriptionTier.standard);
      expect(SubscriptionTierExtension.fromInt(2), SubscriptionTier.premium);
      expect(SubscriptionTierExtension.fromInt(3), SubscriptionTier.premiumInherited);
      expect(SubscriptionTierExtension.fromInt(99), SubscriptionTier.admin);
      expect(SubscriptionTierExtension.fromInt(4), SubscriptionTier.trial); // Default
    });

    test('fromString converts strings correctly', () {
      expect(SubscriptionTierExtension.fromString('trial'), SubscriptionTier.trial);
      expect(SubscriptionTierExtension.fromString('PREMIUM'), SubscriptionTier.premium);
      expect(SubscriptionTierExtension.fromString('unknown'), SubscriptionTier.trial);
    });
  });

  group('BillingCycle', () {
    test('displayName returns correct names', () {
      expect(BillingCycle.monthly.displayName, 'Monthly');
      expect(BillingCycle.yearly.displayName, 'Yearly');
    });

    test('fromInt converts correctly', () {
      // Backend: Monthly = 1, Yearly = 12
      expect(BillingCycleExtension.fromInt(0), BillingCycle.monthly);
      expect(BillingCycleExtension.fromInt(1), BillingCycle.monthly);
      expect(BillingCycleExtension.fromInt(12), BillingCycle.yearly);
    });
  });

  group('SubscriptionStatus', () {
    test('displayName returns correct names', () {
      expect(SubscriptionStatus.none.displayName, 'No Subscription');
      expect(SubscriptionStatus.trialing.displayName, 'Trial');
      expect(SubscriptionStatus.active.displayName, 'Active');
      expect(SubscriptionStatus.pastDue.displayName, 'Past Due');
      expect(SubscriptionStatus.cancelled.displayName, 'Cancelled');
      expect(SubscriptionStatus.expired.displayName, 'Expired');
    });

    test('fromInt converts correctly', () {
      // Backend: Active=0, PastDue=1, Canceled=2, Expired=3, Trialing=4, TrialExpired=5
      expect(SubscriptionStatusExtension.fromInt(0), SubscriptionStatus.active);
      expect(SubscriptionStatusExtension.fromInt(1), SubscriptionStatus.pastDue);
      expect(SubscriptionStatusExtension.fromInt(2), SubscriptionStatus.cancelled);
      expect(SubscriptionStatusExtension.fromInt(3), SubscriptionStatus.expired);
      expect(SubscriptionStatusExtension.fromInt(4), SubscriptionStatus.trialing);
      expect(SubscriptionStatusExtension.fromInt(5), SubscriptionStatus.expired);
      expect(SubscriptionStatusExtension.fromInt(99), SubscriptionStatus.none);
    });
  });

  group('CurrentSubscription', () {
    test('fromJson parses subscription correctly', () {
      final json = {
        'id': 1,
        'stripeSubscriptionId': 'sub_123',
        'tier': 2,
        'interval': 12,
        'status': 0,
        'currentPeriodStart': '2024-01-01T00:00:00Z',
        'currentPeriodEnd': '2024-12-31T23:59:59Z',
        'cancelAtPeriodEnd': false,
        'trialEnd': null,
        'amount': 149.99,
      };

      final subscription = CurrentSubscription.fromJson(json);

      expect(subscription.id, 1);
      expect(subscription.stripeSubscriptionId, 'sub_123');
      expect(subscription.tier, SubscriptionTier.premium);
      expect(subscription.interval, BillingCycle.yearly);
      expect(subscription.status, SubscriptionStatus.active);
      expect(subscription.amount, 149.99);
      expect(subscription.cancelAtPeriodEnd, false);
    });

    test('isActive returns true for trial, active, and past due', () {
      expect(
        CurrentSubscription(
          tier: SubscriptionTier.standard,
          interval: BillingCycle.monthly,
          status: SubscriptionStatus.trialing,
        ).isActive,
        true,
      );
      expect(
        CurrentSubscription(
          tier: SubscriptionTier.standard,
          interval: BillingCycle.monthly,
          status: SubscriptionStatus.active,
        ).isActive,
        true,
      );
      expect(
        CurrentSubscription(
          tier: SubscriptionTier.standard,
          interval: BillingCycle.monthly,
          status: SubscriptionStatus.pastDue,
        ).isActive,
        true,
      );
      expect(
        CurrentSubscription(
          tier: SubscriptionTier.standard,
          interval: BillingCycle.monthly,
          status: SubscriptionStatus.cancelled,
        ).isActive,
        false,
      );
    });

    test('isTrial returns true only for trialing status', () {
      expect(
        CurrentSubscription(
          tier: SubscriptionTier.standard,
          interval: BillingCycle.monthly,
          status: SubscriptionStatus.trialing,
        ).isTrial,
        true,
      );
      expect(
        CurrentSubscription(
          tier: SubscriptionTier.standard,
          interval: BillingCycle.monthly,
          status: SubscriptionStatus.active,
        ).isTrial,
        false,
      );
    });
  });

  group('TrialStatus', () {
    test('fromJson parses correctly', () {
      final json = {
        'isTrialActive': true,
        'daysRemaining': 15,
        'canStartTrial': false,
      };

      final status = TrialStatus.fromJson(json);

      expect(status.isTrialActive, true);
      expect(status.daysRemaining, 15);
      expect(status.canStartTrial, false);
    });

    test('handles PascalCase keys', () {
      final json = {
        'IsTrialActive': false,
        'DaysRemaining': 0,
        'CanStartTrial': true,
      };

      final status = TrialStatus.fromJson(json);

      expect(status.isTrialActive, false);
      expect(status.daysRemaining, 0);
      expect(status.canStartTrial, true);
    });
  });

  group('PricingPlan', () {
    test('fromJson parses correctly', () {
      final json = {
        'tier': 'Premium',
        'name': 'Premium Plan',
        'description': 'Best for power users',
        'monthlyPriceCents': 1500,
        'yearlyPriceCents': 15000,
        'monthlyPriceId': 'price_monthly',
        'yearlyPriceId': 'price_yearly',
        'currency': 'usd',
        'isPopular': true,
        'features': ['Feature 1', 'Feature 2', 'Feature 3'],
      };

      final plan = PricingPlan.fromJson(json);

      expect(plan.tier, 'Premium');
      expect(plan.name, 'Premium Plan');
      expect(plan.monthlyPriceCents, 1500);
      expect(plan.yearlyPriceCents, 15000);
      expect(plan.isPopular, true);
      expect(plan.features.length, 3);
    });

    test('monthlyPrice converts cents to dollars', () {
      final plan = PricingPlan(
        tier: 'Standard',
        name: 'Standard',
        description: 'Basic plan',
        monthlyPriceCents: 1000,
        yearlyPriceCents: 10000,
        monthlyPriceId: '',
        yearlyPriceId: '',
        currency: 'usd',
        isPopular: false,
        features: [],
      );

      expect(plan.monthlyPrice, 10.0);
      expect(plan.yearlyPrice, 100.0);
    });

    test('yearlySavingsPercent calculates correctly', () {
      final plan = PricingPlan(
        tier: 'Standard',
        name: 'Standard',
        description: 'Basic plan',
        monthlyPriceCents: 1000, // $10/month = $120/year if paying monthly
        yearlyPriceCents: 10000, // $100/year = ~17% savings
        monthlyPriceId: '',
        yearlyPriceId: '',
        currency: 'usd',
        isPopular: false,
        features: [],
      );

      // ($120 - $100) / $120 * 100 = ~17%
      expect(plan.yearlySavingsPercent, 17);
    });
  });

  group('BillingHistoryItem', () {
    test('fromJson parses correctly', () {
      final json = {
        'id': 123,
        'amount': 14.99,
        'status': 'Completed',
        'paymentDate': '2024-03-15T10:30:00Z',
        'failureReason': null,
      };

      final item = BillingHistoryItem.fromJson(json);

      expect(item.id, 123);
      expect(item.amount, 14.99);
      expect(item.status, 'Completed');
      expect(item.failureReason, null);
    });
  });
}
