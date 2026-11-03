// Billing Models for BuoyAI Flutter App
//
// These models mirror the backend Billing DTOs and support the
// web-redirect subscription model (no in-app purchases).

/// Subscription tier enum matching backend SubscriptionTier
enum SubscriptionTier {
  trial,
  standard,
  premium,
  premiumInherited,
  admin,
}

extension SubscriptionTierExtension on SubscriptionTier {
  String get displayName {
    return switch (this) {
      SubscriptionTier.trial => 'Trial',
      SubscriptionTier.standard => 'Standard',
      SubscriptionTier.premium => 'Premium',
      SubscriptionTier.premiumInherited => 'Premium (Family)',
      SubscriptionTier.admin => 'Admin',
    };
  }

  static SubscriptionTier fromInt(int value) {
    return switch (value) {
      0 => SubscriptionTier.trial,
      1 => SubscriptionTier.standard,
      2 => SubscriptionTier.premium,
      3 => SubscriptionTier.premiumInherited,
      99 => SubscriptionTier.admin,
      _ => SubscriptionTier.trial,
    };
  }

  static SubscriptionTier fromString(String value) {
    return switch (value.toLowerCase()) {
      'trial' => SubscriptionTier.trial,
      'standard' => SubscriptionTier.standard,
      'premium' => SubscriptionTier.premium,
      'premiuminherited' => SubscriptionTier.premiumInherited,
      'admin' => SubscriptionTier.admin,
      _ => SubscriptionTier.trial,
    };
  }
}

/// Billing cycle enum
enum BillingCycle {
  monthly,
  yearly,
}

extension BillingCycleExtension on BillingCycle {
  String get displayName {
    return switch (this) {
      BillingCycle.monthly => 'Monthly',
      BillingCycle.yearly => 'Yearly',
    };
  }

  static BillingCycle fromInt(int value) {
    // Backend: Monthly = 1, Yearly = 12
    return value >= 12 ? BillingCycle.yearly : BillingCycle.monthly;
  }
}

/// Subscription status enum
enum SubscriptionStatus {
  none,
  trialing,
  active,
  pastDue,
  cancelled,
  expired,
}

extension SubscriptionStatusExtension on SubscriptionStatus {
  String get displayName {
    return switch (this) {
      SubscriptionStatus.none => 'No Subscription',
      SubscriptionStatus.trialing => 'Trial',
      SubscriptionStatus.active => 'Active',
      SubscriptionStatus.pastDue => 'Past Due',
      SubscriptionStatus.cancelled => 'Cancelled',
      SubscriptionStatus.expired => 'Expired',
    };
  }

  // Backend: Active=0, PastDue=1, Canceled=2, Expired=3, Trialing=4, TrialExpired=5
  static SubscriptionStatus fromInt(int value) {
    return switch (value) {
      0 => SubscriptionStatus.active,
      1 => SubscriptionStatus.pastDue,
      2 => SubscriptionStatus.cancelled,
      3 => SubscriptionStatus.expired,
      4 => SubscriptionStatus.trialing,
      5 => SubscriptionStatus.expired,
      _ => SubscriptionStatus.none,
    };
  }
}

/// Current subscription details from the backend
class CurrentSubscription {
  final int? id;
  final String? stripeSubscriptionId;

  /// Stripe (web) or a store. Store subscriptions are managed in the store.
  final SubscriptionProvider provider;
  final SubscriptionTier tier;
  final BillingCycle interval;
  final SubscriptionStatus status;
  final DateTime? currentPeriodStart;
  final DateTime? currentPeriodEnd;
  final bool cancelAtPeriodEnd;
  final DateTime? trialEnd;
  final double amount;

  CurrentSubscription({
    this.id,
    this.stripeSubscriptionId,
    this.provider = SubscriptionProvider.stripe,
    required this.tier,
    required this.interval,
    required this.status,
    this.currentPeriodStart,
    this.currentPeriodEnd,
    this.cancelAtPeriodEnd = false,
    this.trialEnd,
    this.amount = 0.0,
  });

  /// Whether the subscription is active (trial, active, or past due)
  bool get isActive =>
      status == SubscriptionStatus.trialing ||
      status == SubscriptionStatus.active ||
      status == SubscriptionStatus.pastDue;

  /// Whether this is a trial subscription
  bool get isTrial => status == SubscriptionStatus.trialing;

  /// Days remaining in current period
  int? get daysRemaining {
    if (currentPeriodEnd == null) return null;
    final now = DateTime.now();
    if (currentPeriodEnd!.isBefore(now)) return 0;
    return currentPeriodEnd!.difference(now).inDays;
  }

  factory CurrentSubscription.fromJson(Map<String, dynamic> json) {
    return CurrentSubscription(
      id: json['id'] as int?,
      stripeSubscriptionId: json['stripeSubscriptionId'] as String?,
      provider: SubscriptionProviderExtension.fromInt(
        int.tryParse((json['provider'] ?? json['Provider'] ?? 0).toString()) ?? 0,
      ),
      tier: SubscriptionTierExtension.fromInt(
        int.tryParse((json['tier'] ?? 0).toString()) ?? 0,
      ),
      interval: BillingCycleExtension.fromInt(
        int.tryParse((json['interval'] ?? 0).toString()) ?? 0,
      ),
      status: SubscriptionStatusExtension.fromInt(
        int.tryParse((json['status'] ?? 0).toString()) ?? 0,
      ),
      currentPeriodStart: json['currentPeriodStart'] != null
          ? DateTime.tryParse(json['currentPeriodStart'].toString())
          : null,
      currentPeriodEnd: json['currentPeriodEnd'] != null
          ? DateTime.tryParse(json['currentPeriodEnd'].toString())
          : null,
      cancelAtPeriodEnd: (json['cancelAtPeriodEnd'] as bool?) ?? false,
      trialEnd: json['trialEnd'] != null
          ? DateTime.tryParse(json['trialEnd'].toString())
          : null,
      amount: ((json['amount'] ?? 0) as num).toDouble(),
    );
  }
}

/// Trial status response from the backend
class TrialStatus {
  final bool isTrialActive;
  final int daysRemaining;
  final bool canStartTrial;

  TrialStatus({
    required this.isTrialActive,
    required this.daysRemaining,
    required this.canStartTrial,
  });

  factory TrialStatus.fromJson(Map<String, dynamic> json) {
    return TrialStatus(
      isTrialActive:
          (json['isTrialActive'] ?? json['IsTrialActive']) as bool? ?? false,
      daysRemaining: int.tryParse(
            (json['daysRemaining'] ?? json['DaysRemaining'] ?? 0).toString(),
          ) ??
          0,
      canStartTrial:
          (json['canStartTrial'] ?? json['CanStartTrial']) as bool? ?? false,
    );
  }
}

/// Who bills a subscription. Matches backend SubscriptionProvider.
enum SubscriptionProvider { stripe, appStore, playStore }

extension SubscriptionProviderExtension on SubscriptionProvider {
  static SubscriptionProvider fromInt(int value) => switch (value) {
        1 => SubscriptionProvider.appStore,
        2 => SubscriptionProvider.playStore,
        _ => SubscriptionProvider.stripe,
      };

  static SubscriptionProvider fromString(String value) =>
      switch (value.toLowerCase()) {
        'appstore' || 'app_store' || '1' => SubscriptionProvider.appStore,
        'playstore' || 'play_store' || '2' => SubscriptionProvider.playStore,
        _ => SubscriptionProvider.stripe,
      };

  String get displayName => switch (this) {
        SubscriptionProvider.stripe => 'Web',
        SubscriptionProvider.appStore => 'App Store',
        SubscriptionProvider.playStore => 'Google Play',
      };

  /// Billed by a store, so cancel / payment method live in that store's UI.
  bool get isStore => this != SubscriptionProvider.stripe;
}

/// Pricing plan from the backend
class PricingPlan {
  final String tier;
  final String name;
  final String description;
  final int monthlyPriceCents;
  final int yearlyPriceCents;
  final String monthlyPriceId;
  final String yearlyPriceId;

  /// App Store / Play product ids; the store quotes the price for these.
  final String storeProductIdMonthly;
  final String storeProductIdYearly;
  final String currency;
  final bool isPopular;
  final List<String> features;

  PricingPlan({
    required this.tier,
    required this.name,
    required this.description,
    required this.monthlyPriceCents,
    required this.yearlyPriceCents,
    required this.monthlyPriceId,
    required this.yearlyPriceId,
    this.storeProductIdMonthly = '',
    this.storeProductIdYearly = '',
    required this.currency,
    required this.isPopular,
    required this.features,
  });

  /// Monthly price as dollars
  double get monthlyPrice => monthlyPriceCents / 100.0;

  /// Yearly price as dollars
  double get yearlyPrice => yearlyPriceCents / 100.0;

  /// Effective monthly price when paying yearly
  double get yearlyMonthlyPrice => yearlyPrice / 12.0;

  /// Percentage saved with yearly billing
  int get yearlySavingsPercent {
    if (monthlyPriceCents == 0) return 0;
    final yearlyMonthlyEquiv = monthlyPriceCents * 12;
    return ((yearlyMonthlyEquiv - yearlyPriceCents) / yearlyMonthlyEquiv * 100).round();
  }

  factory PricingPlan.fromJson(Map<String, dynamic> json) {
    return PricingPlan(
      tier: (json['tier'] ?? json['Tier'] ?? '').toString(),
      name: (json['name'] ?? json['Name'] ?? '').toString(),
      description: (json['description'] ?? json['Description'] ?? '').toString(),
      monthlyPriceCents: int.tryParse(
            (json['monthlyPriceCents'] ?? json['MonthlyPriceCents'] ?? 0)
                .toString(),
          ) ??
          0,
      yearlyPriceCents: int.tryParse(
            (json['yearlyPriceCents'] ?? json['YearlyPriceCents'] ?? 0)
                .toString(),
          ) ??
          0,
      monthlyPriceId:
          (json['monthlyPriceId'] ?? json['MonthlyPriceId'] ?? '').toString(),
      yearlyPriceId:
          (json['yearlyPriceId'] ?? json['YearlyPriceId'] ?? '').toString(),
      storeProductIdMonthly:
          (json['storeProductIdMonthly'] ?? json['StoreProductIdMonthly'] ?? '')
              .toString(),
      storeProductIdYearly:
          (json['storeProductIdYearly'] ?? json['StoreProductIdYearly'] ?? '')
              .toString(),
      currency: (json['currency'] ?? json['Currency'] ?? 'usd').toString(),
      isPopular: (json['isPopular'] ?? json['IsPopular']) as bool? ?? false,
      features: List<String>.from(
        (json['features'] ?? json['Features'] ?? <dynamic>[]) as List<dynamic>,
      ),
    );
  }
}

/// Billing history payment record
class BillingHistoryItem {
  final int id;
  final double amount;
  final String status;
  final DateTime paymentDate;
  final String? failureReason;

  BillingHistoryItem({
    required this.id,
    required this.amount,
    required this.status,
    required this.paymentDate,
    this.failureReason,
  });

  factory BillingHistoryItem.fromJson(Map<String, dynamic> json) {
    return BillingHistoryItem(
      id: int.tryParse((json['id'] ?? json['Id'] ?? 0).toString()) ?? 0,
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      status: (json['status'] ?? json['Status'] ?? 'Unknown').toString(),
      paymentDate: DateTime.tryParse(
        (json['paymentDate'] ?? json['PaymentDate'] ?? '').toString(),
      ) ?? DateTime.now(),
      failureReason: (json['failureReason'] ?? json['FailureReason'])?.toString(),
    );
  }
}

/// A discount currently applied to the user's subscription.
///
/// Read live from Stripe on each request rather than cached: Stripe owns the lifecycle and a
/// discount expires on its own, so a stored copy would go stale with nothing to invalidate it.
class SubscriptionDiscount {
  final String code;
  final double? percentOff;
  final int? amountOff;
  final String? currency;
  final String duration;
  final int? durationInMonths;
  final DateTime? endsAt;

  /// When the discount is actually spent — the next invoice date. During a trial that is the
  /// trial end, because no invoice exists until then.
  final DateTime? appliesAt;

  /// True when the discount expires before it is ever charged against, which means it pays
  /// nothing. Surfaced so the UI can say so rather than showing a badge that means nothing.
  final bool expiresBeforeItApplies;

  /// Display string built server-side so web and mobile cannot word it differently.
  final String description;

  SubscriptionDiscount({
    required this.code,
    required this.description,
    required this.duration,
    this.percentOff,
    this.amountOff,
    this.currency,
    this.durationInMonths,
    this.endsAt,
    this.appliesAt,
    this.expiresBeforeItApplies = false,
  });

  factory SubscriptionDiscount.fromJson(Map<String, dynamic> json) => SubscriptionDiscount(
        code: (json['code'] ?? json['Code'] ?? '').toString(),
        description: (json['description'] ?? json['Description'] ?? '').toString(),
        duration: (json['duration'] ?? json['Duration'] ?? '').toString(),
        percentOff: double.tryParse(
            (json['percentOff'] ?? json['PercentOff'] ?? '').toString()),
        amountOff:
            int.tryParse((json['amountOff'] ?? json['AmountOff'] ?? '').toString()),
        currency: (json['currency'] ?? json['Currency'])?.toString(),
        durationInMonths: int.tryParse(
            (json['durationInMonths'] ?? json['DurationInMonths'] ?? '').toString()),
        endsAt: DateTime.tryParse(
            (json['endsAt'] ?? json['EndsAt'] ?? '').toString()),
        appliesAt: DateTime.tryParse(
            (json['appliesAt'] ?? json['AppliesAt'] ?? '').toString()),
        expiresBeforeItApplies:
            (json['expiresBeforeItApplies'] ?? json['ExpiresBeforeItApplies']) == true,
      );
}
