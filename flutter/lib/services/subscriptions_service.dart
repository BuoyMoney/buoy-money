import '../models/subscription_models.dart';
import 'api_client.dart';

/// Subscriptions Service — all subscription-related API calls.
///
/// Matches backend endpoints:
///   SubscriptionsController       → /api/subscriptions/*
///   SuggestedSubscriptionsController → /api/suggestedsubscriptions/*
class SubscriptionsService {
  // ── CRUD ────────────────────────────────────────────────

  Future<List<Subscription>> getSubscriptions() async {
    final response = await apiClient.get<List<dynamic>>('/subscriptions');
    return response.map((e) => Subscription.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<Subscription> getSubscription(int id) async {
    final response = await apiClient.get<Map<String, dynamic>>('/subscriptions/$id');
    return Subscription.fromJson(response);
  }

  Future<Subscription> createSubscription(CreateSubscriptionRequest request) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/subscriptions',
      data: request.toJson(),
    );
    return Subscription.fromJson(response);
  }

  Future<Subscription> updateSubscription(int id, UpdateSubscriptionRequest request) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/subscriptions/$id',
      data: request.toJson(),
    );
    return Subscription.fromJson(response);
  }

  Future<void> deleteSubscription(int id) async {
    await apiClient.delete('/subscriptions/$id');
  }

  // ── Detection ───────────────────────────────────────────

  /// Run detection — returns full response with metadata.
  Future<SubscriptionDetectionResponse> detectSubscriptionsFull([
    SubscriptionDetectionRequest? request,
  ]) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/subscriptions/detect',
      data: request?.toJson() ?? {},
    );
    return SubscriptionDetectionResponse.fromJson(response);
  }

  /// Run detection — returns only the detected list (convenience).
  Future<List<DetectedSubscription>> detectSubscriptions([
    SubscriptionDetectionRequest? request,
  ]) async {
    final full = await detectSubscriptionsFull(request);
    return full.detectedSubscriptions;
  }

  /// Confirm a detected subscription → creates real subscription.
  /// Backend expects a [DetectedSubscriptionDto] body.
  Future<Subscription> confirmDetected(DetectedSubscription detected) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/subscriptions/confirm',
      data: detected.toJson(),
    );
    return Subscription.fromJson(response);
  }

  /// Dismiss a detected subscription by merchant name.
  Future<void> dismissDetected(String merchantName, {double? amount, String? reason}) async {
    await apiClient.post(
      '/subscriptions/dismiss',
      data: {
        'merchantName': merchantName,
        if (amount != null) 'amount': amount,
        if (reason != null) 'reason': reason,
      },
    );
  }

  /// Get pending detected subscriptions.
  Future<List<DetectedSubscription>> getPendingDetected() async {
    final response = await apiClient.get<List<dynamic>>('/subscriptions/pending');
    return response.map((e) => DetectedSubscription.fromJson(e as Map<String, dynamic>)).toList();
  }

  // ── Calendar & Summary ──────────────────────────────────

  /// Get calendar data for a specific month.
  Future<List<CalendarSubscription>> getCalendarData({
    required int year,
    required int month,
  }) async {
    final start = DateTime(year, month, 1);
    final end = DateTime(year, month + 1, 0); // last day of month
    final response = await apiClient.get<List<dynamic>>(
      '/subscriptions/calendar',
      queryParameters: {
        'startDate': start.toIso8601String().split('T')[0],
        'endDate': end.toIso8601String().split('T')[0],
      },
    );
    return response.map((e) => CalendarSubscription.fromJson(e as Map<String, dynamic>)).toList();
  }

  /// Get calendar billing events (Past/Next/Future) for a date range.
  Future<List<CalendarBillingEvent>> getCalendarBillingEvents({
    required int year,
    required int month,
  }) async {
    final start = DateTime(year, month, 1);
    final end = DateTime(year, month + 1, 0);
    final response = await apiClient.get<List<dynamic>>(
      '/subscriptions/calendar/events',
      queryParameters: {
        'startDate': start.toIso8601String().split('T')[0],
        'endDate': end.toIso8601String().split('T')[0],
      },
    );
    return response.map((e) => CalendarBillingEvent.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<SubscriptionSummary> getSubscriptionSummary() async {
    final response = await apiClient.get<Map<String, dynamic>>('/subscriptions/summary');
    return SubscriptionSummary.fromJson(response);
  }

  // ── Timeline ────────────────────────────────────────────

  /// Get subscriptions with 2+ missed billing cycles.
  Future<List<SubscriptionTimeline>> getPotentiallyCancelled() async {
    final response = await apiClient.get<List<dynamic>>(
      '/subscriptions/potentially-cancelled',
    );
    return response.map((e) => SubscriptionTimeline.fromJson(e as Map<String, dynamic>)).toList();
  }

  // ── Family Visibility ───────────────────────────────────

  Future<void> toggleFamilyVisibility(int id, bool hiddenFromReadOnly) async {
    await apiClient.patch(
      '/subscriptions/$id/visibility',
      data: {'hiddenFromReadOnly': hiddenFromReadOnly},
    );
  }

  Future<void> batchUpdateVisibility(List<int> ids, bool hiddenFromReadOnly) async {
    await apiClient.patch(
      '/subscriptions/visibility/batch',
      data: {
        'subscriptionIds': ids,
        'hiddenFromReadOnly': hiddenFromReadOnly,
      },
    );
  }

  // ── AI Suggestions ──────────────────────────────────────

  Future<List<SuggestedSubscription>> getSuggestions() async {
    final response = await apiClient.get<List<dynamic>>('/suggestedsubscriptions');
    return response.map((e) => SuggestedSubscription.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<List<SuggestedSubscription>> getAllSuggestions() async {
    final response = await apiClient.get<List<dynamic>>('/suggestedsubscriptions/all');
    return response.map((e) => SuggestedSubscription.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<Subscription> confirmSuggestion(int id, {SubscriptionCategory? category}) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/suggestedsubscriptions/$id/confirm',
      data: {if (category != null) 'category': category.value},
    );
    return Subscription.fromJson(response);
  }

  Future<void> dismissSuggestion(int id, {String? reason, bool permanent = false}) async {
    await apiClient.post(
      '/suggestedsubscriptions/$id/dismiss',
      data: {
        if (reason != null) 'reason': reason,
        if (permanent) 'permanent': true,
      },
    );
  }

  Future<void> snoozeSuggestion(int id, int days) async {
    await apiClient.post(
      '/suggestedsubscriptions/$id/snooze',
      data: {'days': days},
    );
  }

  Future<void> undoSuggestionAction(int id) async {
    await apiClient.post('/suggestedsubscriptions/$id/undo');
  }

  Future<void> updateSuggestionVisibility(int id, bool hiddenFromReadOnly) async {
    await apiClient.patch(
      '/suggestedsubscriptions/$id/visibility',
      data: {'hiddenFromReadOnly': hiddenFromReadOnly},
    );
  }

  // ── Detection Cooldown ──────────────────────────────────

  /// Check if on-demand detection is available (7-day cooldown).
  Future<bool> canRunDetection() async {
    final response = await apiClient.get<Map<String, dynamic>>('/subscriptions/detect/cooldown');
    return response['canRun'] as bool? ?? true;
  }
}

/// Singleton instance
final subscriptionsService = SubscriptionsService();
