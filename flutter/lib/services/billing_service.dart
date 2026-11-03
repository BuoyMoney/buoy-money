import 'package:dio/dio.dart';

import '../models/billing_models.dart';
import 'api_client.dart';

/// Billing Service - handles subscription and billing operations
/// 
/// Web and Android subscribe through hosted Stripe Checkout (a browser
/// redirect). iOS subscribes through the App Store via RevenueCat
/// (store_billing_service.dart); App Review requires it. Either way the
/// backend owns the tier and this service reads it.
class BillingService {
  final ApiClient _apiClient;
  
  BillingService(this._apiClient);
  
  /// After a store purchase or restore: the backend pulls the subscriber from
  /// RevenueCat and answers with the same shape as getCurrentSubscription, so
  /// the plan is live before the webhook arrives.
  Future<CurrentSubscription?> syncStorePurchase() async {
    final response = await _apiClient.post<dynamic>('/billing/revenuecat/sync');
    if (response == null || response is! Map<String, dynamic>) return null;
    return CurrentSubscription.fromJson(response);
  }

  /// Get user's current subscription status
  Future<CurrentSubscription?> getCurrentSubscription() async {
    try {
      final response = await _apiClient.get<Map<String, dynamic>?>('/billing/current');
      if (response == null) return null;
      return CurrentSubscription.fromJson(response);
    } catch (e) {
      rethrow;
    }
  }
  
  /// Redeems a promo code against the user's existing subscription (trial or paid).
  ///
  /// Returns null when the code is not usable — the API answers 404 for every reason
  /// (unknown, inactive, restricted, no subscription) so the form cannot be used to work out
  /// which codes exist. Anything else rethrows, because a Stripe outage must not be shown to
  /// the user as "your code is invalid".
  Future<SubscriptionDiscount?> redeemPromoCode(String code) async {
    try {
      final response = await _apiClient.post<Map<String, dynamic>>(
        '/billing/promo/redeem',
        data: {'promoCode': code.trim()},
      );
      return SubscriptionDiscount.fromJson(response);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// The discount currently applied, or null when there is none (API returns 204).
  Future<SubscriptionDiscount?> getActiveDiscount() async {
    try {
      final response =
          await _apiClient.get<Map<String, dynamic>?>('/billing/promo/active');
      if (response == null || response.isEmpty) return null;
      return SubscriptionDiscount.fromJson(response);
    } catch (_) {
      // Display-only. A failure here must degrade to "no badge", never break the card.
      return null;
    }
  }

  /// Get trial status for the current user
  Future<TrialStatus> getTrialStatus() async {
    try {
      final response = await _apiClient.get<Map<String, dynamic>>('/billing/trial/status');
      return TrialStatus.fromJson(response);
    } catch (e) {
      rethrow;
    }
  }
  
  /// Get available subscription plans with pricing
  Future<List<PricingPlan>> getPlans() async {
    try {
      final response = await _apiClient.get<Map<String, dynamic>>('/billing/plans');
      final plans = response['plans'] ?? response['Plans'] ?? [];
      return (plans as List)
          .map((p) => PricingPlan.fromJson(p as Map<String, dynamic>))
          .toList();
    } catch (e) {
      rethrow;
    }
  }
  
  /// Get billing history
  Future<List<BillingHistoryItem>> getBillingHistory() async {
    try {
      final response = await _apiClient.get<List<dynamic>>('/billing/history');
      return response
          .map((item) =>
              BillingHistoryItem.fromJson(item as Map<String, dynamic>))
          .toList();
    } catch (e) {
      rethrow;
    }
  }
  
  /// Check if user can access a specific feature
  Future<bool> canAccessFeature(String featureName) async {
    try {
      final response = await _apiClient.get<Map<String, dynamic>>(
        '/billing/access/$featureName',
      );
      return (response['canAccess'] ?? response['CanAccess'] ?? false) as bool;
    } catch (e) {
      return false;
    }
  }

  /// Create a hosted Stripe Checkout session for a new subscription and return the
  /// URL to open in the browser. The subscription is activated server-side via webhook
  /// when the user completes payment.
  Future<String> createCheckoutSession({
    required int tier,
    required int billingCycle,
    String? promoCode,
    String? platform,
  }) async {
    final response = await _apiClient.post<Map<String, dynamic>>(
      '/billing/checkout-session',
      data: {
        'tier': tier,
        'billingCycle': billingCycle,
        if (promoCode != null && promoCode.isNotEmpty) 'promoCode': promoCode,
        // On iOS, tells the backend to return a Universal-Link path URL so the
        // return from hosted Checkout deep-links back into the installed app.
        if (platform != null) 'platform': platform,
      },
    );
    return (response['url'] ?? response['Url'] ?? '') as String;
  }

  /// Create a hosted Stripe Customer Portal session (self-serve invoices, receipts,
  /// payment methods, cancellation) and return the URL to open in the browser.
  Future<String> createBillingPortalSession() async {
    final response = await _apiClient.post<Map<String, dynamic>>(
      '/billing/portal-session',
    );
    return (response['url'] ?? response['Url'] ?? '') as String;
  }

  /// Subscribe to a plan (new subscription)
  Future<CurrentSubscription> subscribe({
    required int tier,
    required int billingCycle,
    required String paymentMethodToken,
    String? promoCode,
  }) async {
    final response = await _apiClient.post<Map<String, dynamic>>(
      '/billing/subscribe',
      data: {
        'tier': tier,
        'billingCycle': billingCycle,
        'paymentMethodToken': paymentMethodToken,
        if (promoCode != null) 'promoCode': promoCode,
      },
    );
    return CurrentSubscription.fromJson(response);
  }

  /// Change subscription tier or billing cycle
  Future<CurrentSubscription> changePlan({
    int? newTier,
    int? newBillingCycle,
    String? promoCode,
  }) async {
    final response = await _apiClient.put<Map<String, dynamic>>(
      '/billing/change',
      data: {
        // Backend binds ChangeSubscriptionRequest via JsonPropertyName "tier"/"billingCycle"
        // (BillingDTOs.cs). Sending "newTier"/"newBillingCycle" would bind to null and the
        // plan change would silently fall back to the current plan — a no-op.
        if (newTier != null) 'tier': newTier,
        if (newBillingCycle != null) 'billingCycle': newBillingCycle,
        if (promoCode != null) 'promoCode': promoCode,
      },
    );
    return CurrentSubscription.fromJson(response);
  }

  /// Cancel subscription
  Future<void> cancelSubscription({
    bool cancelImmediately = false,
    String? cancellationReason,
  }) async {
    await _apiClient.post(
      '/billing/cancel',
      data: {
        'cancelImmediately': cancelImmediately,
        if (cancellationReason != null) 'cancellationReason': cancellationReason,
      },
    );
  }

  /// Validate a promo code
  Future<PromoCodeResult> validatePromoCode({
    required String promoCode,
    required String priceId,
  }) async {
    final response = await _apiClient.post<Map<String, dynamic>>(
      '/billing/validate-promo',
      data: {'promoCode': promoCode, 'priceId': priceId},
    );
    return PromoCodeResult.fromJson(response);
  }

  /// Start free trial
  Future<CurrentSubscription> startTrial({int tier = 1}) async {
    final response = await _apiClient.post<Map<String, dynamic>>(
      '/billing/trial/start',
      data: {'tier': tier},
    );
    return CurrentSubscription.fromJson(response);
  }
}

/// Promo code validation result
class PromoCodeResult {
  final bool isValid;
  final String promoCode;
  final double discountPercent;
  final double discountAmount;
  final double originalPrice;
  final double finalPrice;
  final String? description;
  final String? errorMessage;

  PromoCodeResult({
    required this.isValid,
    required this.promoCode,
    required this.discountPercent,
    required this.discountAmount,
    required this.originalPrice,
    required this.finalPrice,
    this.description,
    this.errorMessage,
  });

  factory PromoCodeResult.fromJson(Map<String, dynamic> json) {
    return PromoCodeResult(
      isValid: (json['isValid'] ?? json['IsValid'] ?? false) as bool,
      promoCode: (json['promoCode'] ?? json['PromoCode'] ?? '').toString(),
      discountPercent:
          ((json['discountPercent'] ?? json['DiscountPercent'] ?? 0) as num)
              .toDouble(),
      discountAmount:
          ((json['discountAmount'] ?? json['DiscountAmount'] ?? 0) as num)
              .toDouble(),
      originalPrice:
          ((json['originalPrice'] ?? json['OriginalPrice'] ?? 0) as num)
              .toDouble(),
      finalPrice:
          ((json['finalPrice'] ?? json['FinalPrice'] ?? 0) as num).toDouble(),
      description: (json['description'] ?? json['Description']) as String?,
      errorMessage: (json['errorMessage'] ?? json['ErrorMessage']) as String?,
    );
  }
}

/// Global billing service instance (uses the global apiClient)
// Intentionally non-final so tests can inject a fake (no DI container).
var billingService = BillingService(apiClient);
