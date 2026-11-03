import 'dart:async';
import 'package:dio/dio.dart';
import '../offline/sync_service.dart';
import 'api_client.dart';

/// Model for notification — mirrors backend NotificationDto
class AppNotification {
  final int id;
  final String type;
  final String title;
  final String message;
  final String? actionUrl;
  final String? actionText;
  final String? relatedConnectionId;
  final String? institutionName;
  final bool isRead;
  final DateTime? readAt;
  final bool isArchived;
  final DateTime createdAt;
  final Map<String, dynamic>? metadata;

  AppNotification({
    required this.id,
    required this.type,
    required this.title,
    required this.message,
    this.actionUrl,
    this.actionText,
    this.relatedConnectionId,
    this.institutionName,
    required this.isRead,
    this.readAt,
    required this.isArchived,
    required this.createdAt,
    this.metadata,
  });

  factory AppNotification.fromJson(Map<String, dynamic> json) {
    return AppNotification(
      id: int.tryParse((json['id'] ?? json['Id'] ?? 0).toString()) ?? 0,
      type: (json['type'] ?? json['Type'] ?? 'info').toString(),
      title: (json['title'] ?? json['Title'] ?? '').toString(),
      message: (json['message'] ?? json['Message'] ?? '').toString(),
      actionUrl: (json['actionUrl'] ?? json['ActionUrl']) as String?,
      actionText: (json['actionText'] ?? json['ActionText']) as String?,
      // Backend sends RelatedConnectionId as an int? (e.g. 42). A bare `as String?` throws on
      // mobile/AOT (int is not a String); .toString() keeps the field String? for a numeric value.
      relatedConnectionId: (json['relatedConnectionId'] ?? json['RelatedConnectionId'])?.toString(),
      institutionName: (json['institutionName'] ?? json['InstitutionName']) as String?,
      isRead: (json['isRead'] ?? json['IsRead']) as bool? ?? false,
      readAt: (json['readAt'] ?? json['ReadAt']) != null
          ? DateTime.tryParse((json['readAt'] ?? json['ReadAt']) as String)
          : null,
      isArchived: (json['isArchived'] ?? json['IsArchived']) as bool? ?? false,
      createdAt: json['createdAt'] != null || json['CreatedAt'] != null
          ? DateTime.parse((json['createdAt'] ?? json['CreatedAt']).toString())
          : DateTime.now(),
      metadata: (json['metadata'] ?? json['Metadata']) as Map<String, dynamic>?,
    );
  }
}

/// Paginated notification response from /notifications/paginated
class PaginatedNotifications {
  final List<AppNotification> notifications;
  final bool hasMore;
  final int page;
  final int pageSize;
  final bool includeArchived;

  PaginatedNotifications({
    required this.notifications,
    required this.hasMore,
    required this.page,
    required this.pageSize,
    required this.includeArchived,
  });

  factory PaginatedNotifications.fromJson(Map<String, dynamic> json) {
    final list = json['notifications'] as List<dynamic>? ?? [];
    return PaginatedNotifications(
      notifications: list
          .map((j) => AppNotification.fromJson(j as Map<String, dynamic>))
          .toList(),
      hasMore: json['hasMore'] as bool? ?? false,
      page: json['page'] as int? ?? 1,
      pageSize: json['pageSize'] as int? ?? 20,
      includeArchived: json['includeArchived'] as bool? ?? false,
    );
  }
}

/// Notification summary from /notifications/summary
class NotificationSummary {
  final int totalCount;
  final int unreadCount;
  final List<AppNotification> recent;

  NotificationSummary({
    required this.totalCount,
    required this.unreadCount,
    required this.recent,
  });

  factory NotificationSummary.fromJson(Map<String, dynamic> json) {
    final list = json['recent'] as List<dynamic>? ?? [];
    return NotificationSummary(
      totalCount: json['totalCount'] as int? ?? 0,
      unreadCount: json['unreadCount'] as int? ?? 0,
      recent: list
          .map((j) => AppNotification.fromJson(j as Map<String, dynamic>))
          .toList(),
    );
  }
}

/// Service for notification-related API calls
class NotificationService {
  final Dio _dio = apiClient.dio;

  /// Get all notifications (simple list).
  /// Reads go through [apiClient.get] so responses land in the offline cache
  /// (#493) — the offline mark-read/archive patch needs a cached copy to patch.
  Future<List<AppNotification>> getNotifications({int page = 1, int pageSize = 20}) async {
    final data = await apiClient.get<List<dynamic>>(
      '/notifications',
      queryParameters: {'page': page, 'pageSize': pageSize},
    );
    return data
        .map((json) => AppNotification.fromJson(json as Map<String, dynamic>))
        .toList();
  }

  /// Get paginated notifications (with hasMore + includeArchived)
  Future<PaginatedNotifications> getNotificationsPaginated({
    int page = 1,
    int pageSize = 20,
    bool includeArchived = false,
  }) async {
    final data = await apiClient.get<Map<String, dynamic>>(
      '/notifications/paginated',
      queryParameters: {
        'page': page,
        'pageSize': pageSize,
        'includeArchived': includeArchived,
      },
    );
    return PaginatedNotifications.fromJson(data);
  }

  /// Get notification summary (totalCount, unreadCount, recent)
  Future<NotificationSummary> getSummary() async {
    final data = await apiClient.get<Map<String, dynamic>>(
      '/notifications/summary',
    );
    return NotificationSummary.fromJson(data);
  }

  /// Get unread notification count (backend returns bare int)
  Future<int> getUnreadCount() async {
    try {
      final data = await apiClient.get<dynamic>('/notifications/unread-count');
      // Backend returns bare int
      if (data is int) return data;
      // Fallback: try map shape
      if (data is Map) {
        return int.tryParse((data['count'] ?? 0).toString()) ?? 0;
      }
      return 0;
    } catch (_) {
      return 0;
    }
  }

  /// Mark a single notification as read.
  ///
  /// Offline-first (#493): if the network is down, queue the op and optimistically
  /// patch any cached notification payloads so the UI reflects the read state on
  /// re-render. Non-network errors still throw.
  Future<void> markAsRead(int notificationId) async {
    try {
      await _dio.post('/notifications/$notificationId/mark-read');
      unawaited(syncService.drainOutbox());
    } on DioException catch (e) {
      if (!SyncService.isNetworkError(e)) rethrow;
      await syncService.enqueue(
        operationType: SyncService.opNotificationMarkRead,
        entityId: '$notificationId',
        payload: const {},
      );
      await syncService.patchNotification(notificationId, isRead: true);
    }
  }

  /// Mark all notifications as read.
  ///
  /// Offline-first (#493): the sync/batch registry has no mark-all-read op, so on
  /// a network failure we optimistically flip every cached notification to read
  /// and enqueue an individual [SyncService.opNotificationMarkRead] op per
  /// previously-unread id (those DO have a batch op). Keeps the UI consistent
  /// offline instead of throwing, matching [markAsRead]. Non-network errors throw.
  Future<void> markAllAsRead() async {
    try {
      await _dio.post('/notifications/mark-all-read');
      unawaited(syncService.drainOutbox());
    } on DioException catch (e) {
      if (!SyncService.isNetworkError(e)) rethrow;
      final unreadIds = await syncService.markAllNotificationsReadInCache();
      for (final id in unreadIds) {
        await syncService.enqueue(
          operationType: SyncService.opNotificationMarkRead,
          entityId: '$id',
          payload: const {},
        );
      }
    }
  }

  /// Archive a notification. Offline-first: queues + optimistically patches the
  /// cache on a network failure (see [markAsRead]).
  Future<void> archive(int notificationId) async {
    try {
      await _dio.post('/notifications/$notificationId/archive');
      unawaited(syncService.drainOutbox());
    } on DioException catch (e) {
      if (!SyncService.isNetworkError(e)) rethrow;
      await syncService.enqueue(
        operationType: SyncService.opNotificationArchive,
        entityId: '$notificationId',
        payload: const {},
      );
      await syncService.patchNotification(notificationId, isArchived: true);
    }
  }

  /// Delete a notification
  Future<void> delete(int notificationId) async {
    await _dio.delete('/notifications/$notificationId');
  }
}

// Singleton instance
final notificationService = NotificationService();
