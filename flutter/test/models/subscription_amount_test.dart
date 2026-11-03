import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/subscription_models.dart';

Subscription _sub(double amount, SubscriptionFrequency freq) {
  final now = DateTime(2026, 1, 1);
  return Subscription(
    id: 1,
    name: 'Test',
    amount: amount,
    frequency: freq,
    nextDueDate: now,
    status: SubscriptionStatus.active,
    category: SubscriptionCategory.other,
    createdAt: now,
    updatedAt: now,
  );
}

void main() {
  group('Subscription.yearlyAmount', () {
    test('weekly is amount * 52, not derived from the monthly multiplier', () {
      // 10 * 4.33 * 12 = 519.6 (wrong); the true yearly total is 10 * 52 = 520.
      expect(_sub(10, SubscriptionFrequency.weekly).yearlyAmount, 520);
    });

    test('bi-weekly is amount * 26', () {
      // 10 * 2.17 * 12 = 260.4 (wrong); true yearly total is 10 * 26 = 260.
      expect(_sub(10, SubscriptionFrequency.biWeekly).yearlyAmount, 260);
    });

    test('monthly is amount * 12', () {
      expect(_sub(10, SubscriptionFrequency.monthly).yearlyAmount, 120);
    });

    test('quarterly is amount * 4', () {
      expect(_sub(30, SubscriptionFrequency.quarterly).yearlyAmount, 120);
    });

    test('semi-annual is amount * 2', () {
      expect(_sub(60, SubscriptionFrequency.semiAnnual).yearlyAmount, 120);
    });

    test('annual is amount * 1', () {
      expect(_sub(120, SubscriptionFrequency.annual).yearlyAmount, 120);
    });
  });
}
