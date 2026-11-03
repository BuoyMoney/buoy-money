import 'dart:ui' show PlatformDispatcher;

import 'package:dio/dio.dart';
import 'package:intl/intl.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'api_client.dart';

/// Outcome of a redeem attempt. Only [transientFailure] is worth retrying.
enum ReferralRedeemOutcome { redeemed, rejected, transientFailure }

/// The current user's referral code, shareable link, and successful-referral count.
class ReferralSummary {
  final String code;
  final String link;

  /// Referrals whose reward has been granted.
  final int successfulReferrals;

  /// Recorded but not yet rewarded, because the referee has not connected an account.
  /// Shown separately so "my friend hasn't finished signing up" is distinguishable from
  /// "this didn't work" — a bare success count explains neither.
  final int pendingReferrals;

  /// Free days currently held from referral rewards, or 0.
  final int rewardDaysRemaining;

  ReferralSummary({
    required this.code,
    required this.link,
    required this.successfulReferrals,
    this.pendingReferrals = 0,
    this.rewardDaysRemaining = 0,
  });

  static int _int(dynamic a, dynamic b) =>
      int.tryParse((a ?? b ?? 0).toString()) ?? 0;

  factory ReferralSummary.fromJson(Map<String, dynamic> json) => ReferralSummary(
        code: (json['code'] ?? json['Code'] ?? '').toString(),
        link: (json['link'] ?? json['Link'] ?? '').toString(),
        successfulReferrals:
            _int(json['successfulReferrals'], json['SuccessfulReferrals']),
        pendingReferrals:
            _int(json['pendingReferrals'], json['PendingReferrals']),
        rewardDaysRemaining:
            _int(json['rewardDaysRemaining'], json['RewardDaysRemaining']),
      );
}

/// The advertised referral credit, localized to a currency the backend has a
/// reward coupon for (#941). Falls back to the USD offer server-side.
class ReferralRewardInfo {
  final double amount;
  final String currency;

  const ReferralRewardInfo({required this.amount, required this.currency});

  /// What the offer is today for everyone: the USD default. Used while the
  /// fetch is in flight or when it fails, so the banner never shows nothing.
  static const usdDefault = ReferralRewardInfo(amount: 15, currency: 'USD');

  factory ReferralRewardInfo.fromJson(Map<String, dynamic> json) =>
      ReferralRewardInfo(
        amount: double.tryParse(
                (json['amount'] ?? json['Amount'] ?? 15).toString()) ??
            15,
        currency:
            (json['currency'] ?? json['Currency'] ?? 'USD').toString(),
      );
}

class ReferralService {
  static const _pendingKey = 'buoyai_pending_referral_code';
  static const _capturedAtKey = 'buoyai_pending_referral_captured_at';

  /// How long a captured code stays valid. Signing up is not instant — email, OAuth
  /// redirects, app restarts — but a code sitting in storage for months would attribute a
  /// much later signup to a link the user has long forgotten clicking.
  static const _pendingTtl = Duration(days: 30);

  /// A referral code captured from the launch URL (`?ref=CODE`), pending redemption once
  /// the user is authenticated.
  ///
  /// Persisted rather than held in memory: capture happens at app launch and redemption
  /// cannot happen until after registration, and registration routinely involves leaving the
  /// app (an OAuth redirect, an email) or restarting it. An in-memory field loses the
  /// attribution in exactly the cases referrals are most likely to be used.
  String? pendingCode;

  /// Capture a `?ref=` code from the app's launch URI (call once at startup).
  Future<void> capturePending(Uri uri) async {
    final code = uri.queryParameters['ref'];
    if (code == null || code.trim().isEmpty) {
      // Nothing in this launch URL — restore anything captured on a previous run.
      await _restorePending();
      return;
    }

    pendingCode = code.trim();
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setString(_pendingKey, pendingCode!);
      await prefs.setString(_capturedAtKey, DateTime.now().toIso8601String());
    } catch (_) {
      // Storage failure just means it lives for this session only.
    }
  }

  Future<void> _restorePending() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final stored = prefs.getString(_pendingKey);
      if (stored == null || stored.isEmpty) return;

      final capturedAt =
          DateTime.tryParse(prefs.getString(_capturedAtKey) ?? '');
      if (capturedAt == null ||
          DateTime.now().difference(capturedAt) > _pendingTtl) {
        await _clearPending();
        return;
      }
      pendingCode = stored;
    } catch (_) {
      // Non-critical.
    }
  }

  Future<void> _clearPending() async {
    pendingCode = null;
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.remove(_pendingKey);
      await prefs.remove(_capturedAtKey);
    } catch (_) {
      // Non-critical.
    }
  }

  /// The advertised referral credit for the device's locale currency (#941).
  /// Anonymous endpoint — the register page calls this pre-auth. Errors fall
  /// back to the USD default rather than surfacing: the banner is decoration,
  /// not a flow.
  Future<ReferralRewardInfo> getRewardInfo() async {
    try {
      String? currency;
      try {
        currency = NumberFormat.simpleCurrency(
                locale: PlatformDispatcher.instance.locale.toString())
            .currencyName;
      } catch (_) {
        // Unknown locale: let the server default to USD.
      }
      final response = await apiClient.dio.get<Map<String, dynamic>>(
        '/referral/reward-info',
        queryParameters: {if (currency != null) 'currency': currency},
      );
      return ReferralRewardInfo.fromJson(response.data ?? const {});
    } catch (_) {
      return ReferralRewardInfo.usdDefault;
    }
  }

  /// Redeem any pending referral code.
  ///
  /// The code is cleared only when the server has actually answered — accepted, or rejected
  /// for a reason that will never change (already referred, self-referral, unknown code). A
  /// transient failure leaves it pending so the next app start retries. Clearing before the
  /// call, as this used to, threw away the attribution on any network blip and there was no
  /// second chance: a referral can only ever be redeemed once.
  Future<void> redeemPendingIfAny() async {
    if (pendingCode == null) await _restorePending();
    final code = pendingCode;
    if (code == null) return;

    final outcome = await redeem(code);
    if (outcome == ReferralRedeemOutcome.transientFailure) return;

    await _clearPending();
  }

  Future<ReferralSummary> getSummary() async {
    final response = await apiClient.get<Map<String, dynamic>>('/referral');
    return ReferralSummary.fromJson(response);
  }

  /// Redeem a referral code for the current user.
  ///
  /// Distinguishes "the server said no" from "we could not ask", because only the second is
  /// worth retrying and a referral can be redeemed at most once ever.
  Future<ReferralRedeemOutcome> redeem(String code) async {
    try {
      final response = await apiClient
          .post<Map<String, dynamic>>('/referral/redeem', data: {'code': code});
      final redeemed = (response['redeemed'] ?? response['Redeemed']) == true;
      return redeemed
          ? ReferralRedeemOutcome.redeemed
          : ReferralRedeemOutcome.rejected;
    } on DioException catch (e) {
      final status = e.response?.statusCode;
      // A 4xx is the server's considered answer and will not change on retry. Anything else
      // (offline, 5xx, timeout) is us failing to ask.
      if (status != null && status >= 400 && status < 500) {
        return ReferralRedeemOutcome.rejected;
      }
      return ReferralRedeemOutcome.transientFailure;
    } catch (_) {
      return ReferralRedeemOutcome.transientFailure;
    }
  }
}

final referralService = ReferralService();
