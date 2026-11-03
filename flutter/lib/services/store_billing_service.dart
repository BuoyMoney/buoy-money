import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:purchases_flutter/purchases_flutter.dart';
import 'package:sentry_flutter/sentry_flutter.dart';

import '../config/app_config.dart';
import '../models/billing_models.dart';
import 'billing_service.dart';

/// A plan's price as the store quotes it, plus the RevenueCat package to buy it with.
class StorePlanPrice {
  final String productId;
  final String priceString;
  final Package package;
  const StorePlanPrice({
    required this.productId,
    required this.priceString,
    required this.package,
  });
}

/// In-app purchases through RevenueCat (App Store today; Play once the key is
/// set). Web keeps Stripe. The backend is the source of truth for the tier: a
/// purchase here is only "done" once `/billing/revenuecat/sync` (or the webhook)
/// has projected it, which is why every purchase and restore ends with a sync.
///
/// Configured once at app start with no user; identified on every auth change so
/// RevenueCat's app user id is our numeric user id, which is what the webhook
/// maps back to a BuoyAI account.
class StoreBillingService {
  StoreBillingService(this._billing);

  final BillingService _billing;
  bool _configured = false;
  String? _identifiedUserId;

  String get _apiKey {
    if (kIsWeb) return '';
    return switch (defaultTargetPlatform) {
      TargetPlatform.iOS || TargetPlatform.macOS => AppConfig.revenueCatAppleKey,
      TargetPlatform.android => AppConfig.revenueCatGoogleKey,
      _ => '',
    };
  }

  /// True when this build sells through the store instead of the web link-out.
  /// Android stays on link-out until a Play key is injected at build time.
  bool get isSupported => _apiKey.isNotEmpty;

  bool get isReady => _configured;

  /// Call once before runApp. Never throws: a broken store setup must not stop
  /// the app from opening, it just falls back to the web billing page.
  Future<void> configure() async {
    if (!isSupported || _configured) return;
    try {
      await Purchases.setLogLevel(kReleaseMode ? LogLevel.warn : LogLevel.debug);
      await Purchases.configure(PurchasesConfiguration(_apiKey));
      _configured = true;
    } catch (e, st) {
      _configured = false;
      await Sentry.captureException(e, stackTrace: st,
          withScope: (s) => s.setTag('store', 'configure'));
    }
  }

  /// Tie the store identity to the signed-in user (or drop it on sign-out).
  Future<void> identify(String? userId) async {
    if (!_configured) return;
    if (userId == _identifiedUserId) return;
    try {
      if (userId == null || userId.isEmpty) {
        if (!await Purchases.isAnonymous) await Purchases.logOut();
      } else {
        await Purchases.logIn(userId);
      }
      _identifiedUserId = userId;
    } catch (e, st) {
      await Sentry.captureException(e, stackTrace: st,
          withScope: (s) => s.setTag('store', 'identify'));
    }
  }

  /// Store prices keyed by product id, from the current offering (falling back
  /// to every offering, so a mis-set "current" in the dashboard still sells).
  Future<Map<String, StorePlanPrice>> loadPrices() async {
    if (!_configured) return const {};
    final offerings = await Purchases.getOfferings();
    final packages = <Package>[
      ...?offerings.current?.availablePackages,
      for (final o in offerings.all.values) ...o.availablePackages,
    ];
    final out = <String, StorePlanPrice>{};
    for (final p in packages) {
      final id = normalizeProductId(p.storeProduct.identifier);
      out.putIfAbsent(
        id,
        () => StorePlanPrice(
          productId: id,
          priceString: p.storeProduct.priceString,
          package: p,
        ),
      );
    }
    return out;
  }

  /// Runs the store purchase sheet, then syncs the backend. Returns the
  /// backend's view of the subscription, or null when the user cancelled.
  Future<CurrentSubscription?> purchase(StorePlanPrice price) async {
    try {
      await Purchases.purchase(PurchaseParams.package(price.package));
    } on PlatformException catch (e) {
      if (PurchasesErrorHelper.getErrorCode(e) ==
          PurchasesErrorCode.purchaseCancelledError) {
        return null;
      }
      rethrow;
    }
    return _billing.syncStorePurchase();
  }

  /// Restore purchases made with this Apple ID, then sync. Returns the backend
  /// view; the caller decides whether anything came back.
  Future<CurrentSubscription?> restore() async {
    await Purchases.restorePurchases();
    return _billing.syncStorePurchase();
  }

  /// The store's subscription-management page for this customer (App Store
  /// subscriptions on iOS, Play subscriptions on Android), or the App Store's
  /// generic subscriptions page when RevenueCat has no URL yet.
  Future<Uri> managementUrl() async {
    final info = await Purchases.getCustomerInfo();
    final url = info.managementURL;
    return Uri.parse(url == null || url.isEmpty
        ? 'https://apps.apple.com/account/subscriptions'
        : url);
  }

  /// Play product ids can carry a base-plan suffix (`product:base_plan`); the
  /// backend keys on the bare product id.
  static String normalizeProductId(String id) => id.split(':').first.trim();
}

/// Global store billing instance (configured in main, identified by AuthProvider).
final storeBilling = StoreBillingService(billingService);
