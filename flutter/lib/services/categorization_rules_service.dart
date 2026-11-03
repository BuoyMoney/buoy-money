import 'package:shared_preferences/shared_preferences.dart';
import '../models/categorization_rule.dart';
import 'api_client.dart';

/// Categorization rules: the user's explicit word on a merchant. A rule runs
/// before Plaid and before AI on every new transaction (see
/// docs/ai_categorization_pipeline.md), so creating one from a correction is
/// the fastest way to teach the app.
class CategorizationRulesService {
  CategorizationRulesService({ApiClient? client}) : _client = client ?? apiClient;

  final ApiClient _client;

  /// Merchants the user has answered "just this one" for. One decline is
  /// enough to stop asking about that merchant on this device; the learned
  /// habit and vector shortcuts still pick up the pattern over time.
  static const declinedPrefsKey = 'rule_offer_declined_merchants';

  /// Master switch for the "Always file X as Y?" prompt. Off by default: a
  /// question after every disagreement with the AI is friction on the exact
  /// action we want to be quick, and the habit shortcut learns the pattern
  /// anyway. Settings exposes it for people who want rules made for them.
  static const offersEnabledPrefsKey = 'rule_offer_enabled';
  static const offersEnabledDefault = false;

  Future<bool> areOffersEnabled() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      return prefs.getBool(offersEnabledPrefsKey) ?? offersEnabledDefault;
    } catch (_) {
      return offersEnabledDefault;
    }
  }

  Future<void> setOffersEnabled(bool enabled) async {
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setBool(offersEnabledPrefsKey, enabled);
    } catch (_) {
      // Best-effort; the default is the safe (off) state anyway.
    }
  }

  /// "Always file {merchant} as {category}": one rule, Plaid entity id when
  /// the transaction carries one (survives merchant-name drift), exact merchant
  /// name otherwise. Shared with the family by default, matching the rule
  /// page's own default.
  Future<Map<String, dynamic>> createRuleFromCategorization({
    required int transactionId,
    required int categoryId,
    bool shareWithFamily = true,
  }) async {
    return _client.post<Map<String, dynamic>>(
      '/categorizationrule/from-categorization',
      data: {
        'transactionId': transactionId,
        'newCategoryId': categoryId,
        'shareWithFamily': shareWithFamily,
        'usePlaidEntityId': true,
      },
    );
  }

  /// Every rule the user can see (their own and family-shared), active or paused.
  Future<List<CategorizationRule>> listRules() async {
    return _client.get<List<CategorizationRule>>(
      '/categorizationrule',
      queryParameters: {'includeInactive': true},
      fromJson: (data) => (data as List)
          .map((e) => CategorizationRule.fromJson(Map<String, dynamic>.from(e as Map)))
          .toList(),
    );
  }

  /// Pause or resume a rule. Returns the rule as saved.
  Future<CategorizationRule> toggleRule(int id) async {
    return _client.post<CategorizationRule>(
      '/categorizationrule/$id/toggle',
      fromJson: (data) => CategorizationRule.fromJson(Map<String, dynamic>.from(data as Map)),
    );
  }

  Future<void> deleteRule(int id) async {
    await _client.delete<void>('/categorizationrule/$id');
  }

  Future<bool> shouldOfferRule(String merchant) async {
    final key = _key(merchant);
    if (key.isEmpty) return false;
    if (!await areOffersEnabled()) return false;
    try {
      final prefs = await SharedPreferences.getInstance();
      final declined = prefs.getStringList(declinedPrefsKey) ?? const [];
      return !declined.contains(key);
    } catch (_) {
      return true;
    }
  }

  Future<void> markOfferDeclined(String merchant) async {
    final key = _key(merchant);
    if (key.isEmpty) return;
    try {
      final prefs = await SharedPreferences.getInstance();
      final declined = <String>[...?prefs.getStringList(declinedPrefsKey)];
      if (!declined.contains(key)) {
        declined.add(key);
        await prefs.setStringList(declinedPrefsKey, declined);
      }
    } catch (_) {
      // Best-effort memory; worst case the user is asked once more.
    }
  }

  static String _key(String merchant) => merchant.trim().toLowerCase();
}

final categorizationRulesService = CategorizationRulesService();
