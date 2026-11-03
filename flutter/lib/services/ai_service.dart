import 'dart:async';
import 'dart:math' as math;

import 'package:dio/dio.dart';
import '../models/transaction_models.dart';
import 'api_client.dart';

/// A structured, one-tap follow-up attached to an insight.
/// type == 'navigate'      -> send the user to [route]
/// type == 'agent_handoff' -> run [instruction] via a write-capable agent (with undo)
class SuggestedAction {
  static const String typeNavigate = 'navigate';
  static const String typeAgentHandoff = 'agent_handoff';

  final String type;
  final String label;
  final String? route;
  final String? instruction;
  final String? summary;

  SuggestedAction({
    required this.type,
    required this.label,
    this.route,
    this.instruction,
    this.summary,
  });

  bool get isNavigate => type == typeNavigate;
  bool get isAgentHandoff => type == typeAgentHandoff;

  factory SuggestedAction.fromJson(Map<String, dynamic> json) {
    // Handle both camelCase and PascalCase per project convention.
    String? s(String camel, String pascal) =>
        (json[camel] ?? json[pascal]) as String?;
    return SuggestedAction(
      type: s('type', 'Type') ?? '',
      label: s('label', 'Label') ?? '',
      route: s('route', 'Route'),
      instruction: s('instruction', 'Instruction'),
      summary: s('summary', 'Summary'),
    );
  }
}

/// AI Insight model
class AIInsight {
  final String id;
  final String
      type; // 'spending', 'budget', 'savings', 'prediction', 'recommendation', 'general'
  final String title;
  final String message;
  final String? actionSuggestion;
  final Map<String, dynamic>? data;
  final SuggestedAction? suggestedAction;
  final double confidence;
  final DateTime generatedAt;
  final String priority; // 'low', 'medium', 'high'
  final bool isDismissed;

  AIInsight({
    required this.id,
    required this.type,
    required this.title,
    required this.message,
    this.actionSuggestion,
    this.data,
    this.suggestedAction,
    required this.confidence,
    required this.generatedAt,
    required this.priority,
    this.isDismissed = false,
  });

  factory AIInsight.fromJson(Map<String, dynamic> json) {
    return AIInsight(
      id: json['id'] as String,
      type: json['type'] as String? ?? 'general',
      title: json['title'] as String? ?? '',
      message: json['message'] as String? ?? '',
      actionSuggestion: json['actionSuggestion'] as String?,
      data: json['data'] as Map<String, dynamic>?,
      suggestedAction: json['suggestedAction'] != null
          ? SuggestedAction.fromJson(
              json['suggestedAction'] as Map<String, dynamic>)
          : null,
      confidence: (json['confidence'] as num?)?.toDouble() ?? 0.0,
      generatedAt: json['generatedAt'] != null
          ? DateTime.parse(json['generatedAt'] as String)
          : DateTime.now(),
      priority: json['priority'] as String? ?? 'medium',
      isDismissed: json['isDismissed'] as bool? ?? false,
    );
  }

  /// Icon for insight type
  String get icon {
    switch (type) {
      case 'spending':
        return '💳';
      case 'budget':
        return '📊';
      case 'savings':
        return '🎯';
      case 'prediction':
        return '🔮';
      case 'recommendation':
        return '💡';
      default:
        return '✨';
    }
  }

  /// Color for priority
  String get priorityColor {
    switch (priority) {
      case 'high':
        return 'error';
      case 'medium':
        return 'warning';
      default:
        return 'info';
    }
  }
}

/// Result of running an insight's agent_handoff action.
class InsightActionResult {
  final bool success;
  final String summary;
  final List<String> changes;
  final String? undoToken;

  InsightActionResult({
    required this.success,
    required this.summary,
    required this.changes,
    this.undoToken,
  });

  bool get canUndo => undoToken != null && undoToken!.isNotEmpty;

  factory InsightActionResult.fromJson(Map<String, dynamic> json) {
    // Handle both camelCase and PascalCase per project convention.
    final rawChanges =
        (json['changes'] ?? json['Changes']) as List<dynamic>? ?? const [];
    return InsightActionResult(
      success: (json['success'] ?? json['Success']) as bool? ?? false,
      summary: (json['summary'] ?? json['Summary']) as String? ?? '',
      changes: rawChanges
          .map((c) {
            final m = c as Map<String, dynamic>;
            return (m['description'] ?? m['Description']) as String? ?? '';
          })
          .where((s) => s.isNotEmpty)
          .toList(),
      undoToken: (json['undoToken'] ?? json['UndoToken']) as String?,
    );
  }
}

/// AI Usage Statistics
class AIUsageStats {
  final int requestsToday;
  final int requestsThisMonth;
  final int dailyLimit;
  final int monthlyLimit;
  final double costToday;
  final double costThisMonth;
  final String subscriptionTier;
  final DateTime? lastRequestAt;
  final Map<String, RefreshStats>? refreshStats;

  AIUsageStats({
    required this.requestsToday,
    required this.requestsThisMonth,
    required this.dailyLimit,
    required this.monthlyLimit,
    required this.costToday,
    required this.costThisMonth,
    required this.subscriptionTier,
    this.lastRequestAt,
    this.refreshStats,
  });

  factory AIUsageStats.fromJson(Map<String, dynamic> json) {
    return AIUsageStats(
      requestsToday: json['requestsToday'] as int? ?? 0,
      requestsThisMonth: json['requestsThisMonth'] as int? ?? 0,
      dailyLimit: json['dailyLimit'] as int? ?? 10,
      monthlyLimit: json['monthlyLimit'] as int? ?? 100,
      costToday: (json['costToday'] as num?)?.toDouble() ?? 0.0,
      costThisMonth: (json['costThisMonth'] as num?)?.toDouble() ?? 0.0,
      subscriptionTier: json['subscriptionTier'] as String? ?? 'Free',
      lastRequestAt: json['lastRequestAt'] != null
          ? DateTime.tryParse(json['lastRequestAt'] as String)
          : null,
      refreshStats: json['refreshStats'] != null
          ? (json['refreshStats'] as Map<String, dynamic>).map((key, value) =>
              MapEntry(
                  key, RefreshStats.fromJson(value as Map<String, dynamic>)))
          : null,
    );
  }

  bool get isAtDailyLimit => requestsToday >= dailyLimit;
  bool get isAtMonthlyLimit => requestsThisMonth >= monthlyLimit;
}

/// Refresh statistics for a specific context
class RefreshStats {
  final int usedToday;
  final int dailyLimit;
  final DateTime? lastRefreshAt;

  RefreshStats({
    required this.usedToday,
    required this.dailyLimit,
    this.lastRefreshAt,
  });

  factory RefreshStats.fromJson(Map<String, dynamic> json) {
    return RefreshStats(
      usedToday: json['usedToday'] as int? ?? 0,
      dailyLimit: json['dailyLimit'] as int? ?? 3,
      lastRefreshAt: json['lastRefreshAt'] != null
          ? DateTime.tryParse(json['lastRefreshAt'] as String)
          : null,
    );
  }

  bool get canRefresh => usedToday < dailyLimit;
}

/// AI Preferences
class AIPreferences {
  final String aiInteractionMode; // 'Quick' or 'Clarifying'
  final int aiInteractionModeValue;

  AIPreferences({
    required this.aiInteractionMode,
    required this.aiInteractionModeValue,
  });

  factory AIPreferences.fromJson(Map<String, dynamic> json) {
    return AIPreferences(
      aiInteractionMode: json['aiInteractionMode'] as String? ?? 'Quick',
      aiInteractionModeValue: json['aiInteractionModeValue'] as int? ?? 0,
    );
  }

  bool get isQuickMode => aiInteractionMode == 'Quick';
  bool get isClarifyingMode => aiInteractionMode == 'Clarifying';
}

/// AI Service for interacting with AI endpoints
class AIService {
  /// Get AI insights for a specific context
  Future<List<AIInsight>> getInsights({
    required String context,
    int maxInsights = 5,
    String? specificType,
  }) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/ai/insights',
        data: {
          'context': context,
          'maxInsights': maxInsights,
          if (specificType != null) 'specificType': specificType,
        },
      );

      if (response['success'] == true && response['insights'] != null) {
        final insightsList = response['insights'] as List<dynamic>;
        return insightsList
            .map((json) => AIInsight.fromJson(json as Map<String, dynamic>))
            .toList();
      }
      return [];
    } on DioException catch (e) {
      if (e.response?.statusCode == 429) {
        throw Exception('AI rate limit exceeded. Please try again later.');
      }
      throw Exception('Failed to load AI insights');
    }
  }

  /// Refresh insights for a context (bypasses cache)
  Future<List<AIInsight>> refreshInsights(String context) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/ai/insights/refresh/$context',
      );

      if (response['success'] == true && response['insights'] != null) {
        final insightsList = response['insights'] as List<dynamic>;
        return insightsList
            .map((json) => AIInsight.fromJson(json as Map<String, dynamic>))
            .toList();
      }
      return [];
    } on DioException catch (e) {
      if (e.response?.statusCode == 429) {
        throw Exception('Daily refresh limit reached. Try again tomorrow.');
      }
      throw Exception('Failed to refresh insights');
    }
  }

  /// Dismiss an insight
  Future<bool> dismissInsight(String insightId) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/ai/insights/$insightId/dismiss',
      );
      return response['success'] == true;
    } catch (e) {
      return false;
    }
  }

  /// Run an insight's agent_handoff SuggestedAction. Tapping the button is the user's
  /// confirmation, so the follow-up agent's reversible writes execute directly. The result
  /// carries an [InsightActionResult.undoToken] for the follow-up "Undo".
  Future<InsightActionResult> actOnInsight(String instruction,
      {String? insightId}) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/ai/insights/act',
      data: {
        'instruction': instruction,
        if (insightId != null) 'insightId': insightId,
      },
    );
    return InsightActionResult.fromJson(response);
  }

  /// Revert the changes made by a prior [actOnInsight], using its signed undo token.
  Future<bool> undoInsightAction(String undoToken) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/ai/insights/undo',
        data: {'undoToken': undoToken},
      );
      return response['success'] == true;
    } catch (e) {
      return false;
    }
  }

  /// Get AI usage statistics
  Future<AIUsageStats> getUsageStats() async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/ai/usage',
    );
    return AIUsageStats.fromJson(response);
  }

  /// Get AI preferences
  Future<AIPreferences> getPreferences() async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/ai/preferences',
    );
    return AIPreferences.fromJson(response);
  }

  /// Update AI preferences
  Future<bool> updatePreferences({required String aiInteractionMode}) async {
    try {
      final response = await apiClient.put<Map<String, dynamic>>(
        '/ai/preferences',
        data: {'aiInteractionMode': aiInteractionMode},
      );
      return response['success'] == true;
    } catch (e) {
      return false;
    }
  }

  /// Send a query to the AI assistant
  Future<String> sendQuery(String query) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/ai/query',
        data: {'query': query},
      );

      if (response['success'] == true) {
        return response['response'] as String? ?? 'No response';
      }
      return response['error'] as String? ?? 'An error occurred';
    } on DioException catch (e) {
      if (e.response?.statusCode == 429) {
        return 'AI rate limit exceeded. Please try again later.';
      }
      return 'Failed to process your query. Please try again.';
    }
  }

  // ── Conversation History ────────────────────────────────

  /// Get conversation list
  Future<List<AiConversation>> getConversations({
    String? context,
    int limit = 20,
  }) async {
    try {
      final params = <String, dynamic>{
        if (context != null) 'context': context,
        'limit': limit,
      };
      final response = await apiClient.get<Map<String, dynamic>>(
        '/aiconversations',
        queryParameters: params,
      );
      final list = response['conversations'] as List<dynamic>? ?? [];
      return list
          .map((j) => AiConversation.fromJson(j as Map<String, dynamic>))
          .toList();
    } catch (e) {
      return [];
    }
  }

  /// Get a specific conversation with messages
  Future<AiConversationDetail> getConversation(String sessionId) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/aiconversations/$sessionId',
    );
    return AiConversationDetail.fromJson(response);
  }

  /// Delete a conversation
  Future<void> deleteConversation(String sessionId) async {
    await apiClient.delete('/aiconversations/$sessionId');
  }

  // ── AI Memories ─────────────────────────────────────────

  /// Get all AI memories
  Future<AiMemoriesResponse> getMemories() async {
    final response = await apiClient.get<Map<String, dynamic>>('/ai/memories');
    return AiMemoriesResponse.fromJson(response);
  }

  /// Update a memory
  Future<void> updateMemory(int id,
      {required String value, required String displayText}) async {
    await apiClient.put('/ai/memories/$id', data: {
      'value': value,
      'displayText': displayText,
    });
  }

  /// Deactivate a memory (hide it)
  Future<void> deactivateMemory(int id) async {
    await apiClient.post<dynamic>('/ai/memories/$id/deactivate');
  }

  /// Delete a single memory
  Future<void> deleteMemory(int id) async {
    await apiClient.delete('/ai/memories/$id');
  }

  /// Delete all memories
  Future<void> deleteAllMemories() async {
    await apiClient.delete('/ai/memories');
  }

  // ── Tool Execution ──────────────────────────────────────

  /// Execute a confirmed tool action. [confirmationToken] comes with the
  /// preview; the server then runs exactly the previewed arguments, once.
  Future<Map<String, dynamic>> executeToolConfirmed({
    required String toolName,
    required Map<String, dynamic> arguments,
    String? confirmationToken,
  }) async {
    return await apiClient.post<Map<String, dynamic>>(
      '/ai/tools/execute',
      data: {
        'toolName': toolName,
        'arguments': {...arguments, 'confirmed': true},
        if (confirmationToken != null) 'confirmationToken': confirmationToken,
      },
    );
  }

  // ── File Upload ─────────────────────────────────────────

  /// Upload a file for AI chat analysis
  Future<ChatFileUploadResponse> uploadChatFile(
    List<int> fileBytes,
    String fileName,
  ) async {
    final formData = FormData.fromMap({
      'file': MultipartFile.fromBytes(fileBytes, filename: fileName),
    });
    final response = await apiClient.dio.post(
      '/ai/chat/upload',
      data: formData,
    );
    return ChatFileUploadResponse.fromJson(
      response.data as Map<String, dynamic>,
    );
  }

  // ── Transaction Categorization ──────────────────────────

  /// Batch categorize transactions using AI in chunks, reporting progress.
  /// [maxPerBatch] controls how many transactions per server round-trip; the server
  /// runs a round's model calls in parallel, so a round of 10 takes about as long as
  /// two calls. [onProgress] fires with (completed, total): the real count after each
  /// round, and while a round is in flight an estimate that ticks up at the pace the
  /// previous round actually ran, so the line moves instead of jumping. The estimate
  /// never reaches the round's end; the real count replaces it when the round lands.
  Future<AICategorizationResponse> batchCategorize({
    int maxPerBatch = 10,
    void Function(int completed, int total)? onProgress,
  }) async {
    final stats = await getReviewStatistics();
    final total = stats.aiEligibleCount;
    if (total == 0) {
      return AICategorizationResponse(
        successfullyCategorized: 0,
        highConfidence: 0,
        needingReview: 0,
        failed: 0,
      );
    }

    int categorized = 0, high = 0, review = 0, failed = 0;
    int remaining = total;
    bool interrupted = false;
    // ~5s per transaction is worst-case; pad generously
    final timeoutPerBatch = maxPerBatch * 8000;

    int done = 0; // real: categorized + failed so far
    int shown = 0; // what onProgress last reported (real or estimated)
    var msPerItem =
        500; // first-round guess; replaced by each round's measured pace
    Timer? ticker;
    void startTicker(int roundSize) {
      ticker?.cancel();
      if (onProgress == null || roundSize <= 1) return;
      final started = DateTime.now();
      final ceiling = done + roundSize - 1;
      ticker = Timer.periodic(const Duration(milliseconds: 150), (_) {
        final elapsed = DateTime.now().difference(started).inMilliseconds;
        final estimate = math.min(ceiling, done + elapsed ~/ msPerItem);
        if (estimate > shown) {
          shown = estimate;
          onProgress(shown, total);
        }
      });
    }

    final maxRounds = (total / maxPerBatch).ceil() + 1;
    for (var round = 0; round < maxRounds && remaining > 0; round++) {
      final roundStarted = DateTime.now();
      startTicker(math.min(maxPerBatch, remaining));
      // A single round is one small server call (maxPerBatch transactions). A
      // transient blip on one round — a dropped connection ("Software caused
      // connection abort"), a momentary 5xx during a deploy cutover, a slow model
      // response — must NOT throw away the rounds that already succeeded. Retry the
      // round a couple of times with a short backoff, then stop gracefully and keep
      // the partial progress. The user can tap Auto-Categorize again to finish; the
      // banner's remaining count already reflects what's left.
      AICategorizationResponse? batch;
      for (var attempt = 0; attempt < 3; attempt++) {
        try {
          final response =
              await apiClient.postWithExtendedTimeout<Map<String, dynamic>>(
            '/ai/categorize/batch',
            data: {'maxTransactions': maxPerBatch},
            timeoutMs: timeoutPerBatch,
          );
          batch = AICategorizationResponse.fromJson(response);
          break;
        } catch (_) {
          if (attempt < 2) {
            await Future.delayed(Duration(milliseconds: 600 * (attempt + 1)));
          }
        }
      }
      ticker?.cancel();
      if (batch == null) {
        // Round still failing after retries — keep what we have and stop.
        interrupted = true;
        if (shown != done) onProgress?.call(done, total);
        break;
      }
      categorized += batch.successfullyCategorized;
      high += batch.highConfidence;
      review += batch.needingReview;
      failed += batch.failed;

      final processed = batch.successfullyCategorized + batch.failed;
      if (processed == 0) break;
      remaining -= processed;
      msPerItem =
          (DateTime.now().difference(roundStarted).inMilliseconds ~/ processed)
              .clamp(100, 5000);
      done = categorized + failed;
      // A round that did fewer than the ticker guessed holds the line where it is
      // (it never goes backwards); the real count shows once it catches up.
      if (done >= shown) {
        shown = done;
        onProgress?.call(done, total);
      }
    }
    ticker?.cancel();
    if (shown != done) onProgress?.call(done, total); // always end on the real count

    return AICategorizationResponse(
      successfullyCategorized: categorized,
      highConfidence: high,
      needingReview: review,
      failed: failed,
      interrupted: interrupted,
    );
  }

  /// Generate AI suggestions for SPECIFIC transactions (by id) without a bulk
  /// auto-apply. Used to enrich the review queue on demand — the backend sets each
  /// transaction's AI suggestion + confidence and leaves it in the review queue.
  Future<void> categorizeByIds(List<int> transactionIds) async {
    if (transactionIds.isEmpty) return;
    await apiClient.postWithExtendedTimeout<Map<String, dynamic>>(
      '/ai/categorize/batch',
      data: {
        'transactionIds': transactionIds,
        'maxTransactions': transactionIds.length,
      },
      timeoutMs: transactionIds.length * 8000,
    );
  }

  /// Get statistics about transactions needing review
  Future<ReviewStatistics> getReviewStatistics() async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/ai/categorize/review-stats',
    );
    return ReviewStatistics.fromJson(response);
  }

  /// Get transactions needing review, sorted by AI confidence (highest first).
  /// [scope] selects the slice of the backlog (this month / older / all) — see #737.
  Future<List<Transaction>> getTransactionsNeedingReview({
    int? limit,
    ReviewScope scope = ReviewScope.all,
  }) async {
    final params = <String, dynamic>{'scope': scope.wire};
    if (limit != null) params['limit'] = limit;
    final response = await apiClient.get<List<dynamic>>(
      '/ai/categorize/needs-review',
      queryParameters: params,
    );
    return response
        .map((j) => Transaction.fromJson(j as Map<String, dynamic>))
        .toList();
  }

  /// Accept an AI-suggested category for a transaction
  Future<Transaction> acceptAISuggestion(int transactionId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/ai/categorize/$transactionId/accept',
    );
    return Transaction.fromJson(response);
  }

  /// Reject an AI-suggested category and apply a different one
  Future<Transaction> rejectAISuggestion(
      int transactionId, int correctCategoryId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/ai/categorize/$transactionId/reject',
      data: {'correctCategory': correctCategoryId},
    );
    return Transaction.fromJson(response);
  }

  /// Skip a transaction from review for a specified number of days
  Future<Transaction> skipTransaction(int transactionId, {int days = 2}) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/ai/categorize/$transactionId/skip?days=$days',
      data: {},
    );
    return Transaction.fromJson(response);
  }

  /// Permanently dismisses every transaction older than the current month that is
  /// still waiting for review. Returns how many were dismissed.
  ///
  /// The cutoff lives on the server, so this can only ever clear the set the banner
  /// reports as "from earlier months" — it can never touch this month's.
  Future<int> dismissOlderReviews() async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/ai/categorize/dismiss-older',
      data: {},
    );
    return response['dismissed'] as int? ?? 0;
  }

  /// Get the count of transactions queued for AI categorization
  Future<int> getQueuedCount() async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/ai/categorize/queue/count',
    );
    return response['count'] as int? ?? 0;
  }

  /// Process the AI categorization queue
  Future<QueueProcessResult> processQueue({int? batchSize}) async {
    final params = <String, dynamic>{};
    if (batchSize != null) params['batchSize'] = batchSize;
    final response =
        await apiClient.postWithExtendedTimeout<Map<String, dynamic>>(
      '/ai/categorize/queue/process',
      queryParameters: params,
    );
    return QueueProcessResult.fromJson(response);
  }
}

// ── Models ────────────────────────────────────────────────

/// Conversation list item
class AiConversation {
  final int id;
  final String sessionId;
  final String context;
  final String? title;
  final DateTime startedAt;
  final DateTime lastMessageAt;
  final bool isActive;
  final int messageCount;

  AiConversation({
    required this.id,
    required this.sessionId,
    required this.context,
    this.title,
    required this.startedAt,
    required this.lastMessageAt,
    required this.isActive,
    required this.messageCount,
  });

  factory AiConversation.fromJson(Map<String, dynamic> json) {
    return AiConversation(
      id: json['id'] as int? ?? 0,
      sessionId: json['sessionId'] as String? ?? '',
      context: json['context'] as String? ?? '',
      title: json['title'] as String?,
      startedAt: DateTime.tryParse(json['startedAt'] as String? ?? '') ??
          DateTime.now(),
      lastMessageAt:
          DateTime.tryParse(json['lastMessageAt'] as String? ?? '') ??
              DateTime.now(),
      isActive: json['isActive'] as bool? ?? true,
      messageCount: json['messageCount'] as int? ?? 0,
    );
  }
}

/// Conversation detail with messages
class AiConversationDetail {
  final int id;
  final String sessionId;
  final String? title;
  final List<AiConversationMessage> messages;

  AiConversationDetail({
    required this.id,
    required this.sessionId,
    this.title,
    required this.messages,
  });

  factory AiConversationDetail.fromJson(Map<String, dynamic> json) {
    final msgList = json['messages'] as List<dynamic>? ?? [];
    return AiConversationDetail(
      id: json['id'] as int? ?? 0,
      sessionId: json['sessionId'] as String? ?? '',
      title: json['title'] as String?,
      messages: msgList
          .map((m) => AiConversationMessage.fromJson(m as Map<String, dynamic>))
          .toList(),
    );
  }
}

/// Single message in a conversation
class AiConversationMessage {
  final int id;
  final String role;
  final String content;
  final DateTime createdAt;

  AiConversationMessage({
    required this.id,
    required this.role,
    required this.content,
    required this.createdAt,
  });

  factory AiConversationMessage.fromJson(Map<String, dynamic> json) {
    return AiConversationMessage(
      id: json['id'] as int? ?? 0,
      role: json['role'] as String? ?? 'assistant',
      content: json['content'] as String? ?? '',
      createdAt: DateTime.tryParse(json['createdAt'] as String? ?? '') ??
          DateTime.now(),
    );
  }
}

/// AI Memory
class AiMemory {
  final int id;
  final String memoryType;
  final String memoryKey;
  final String memoryValue;
  final String displayText;
  final double confidence;
  final DateTime learnedAt;
  final DateTime? lastUsedAt;
  final int usageCount;
  final bool isActive;
  final bool wasManuallyEdited;

  AiMemory({
    required this.id,
    required this.memoryType,
    required this.memoryKey,
    required this.memoryValue,
    required this.displayText,
    required this.confidence,
    required this.learnedAt,
    this.lastUsedAt,
    required this.usageCount,
    required this.isActive,
    required this.wasManuallyEdited,
  });

  factory AiMemory.fromJson(Map<String, dynamic> json) {
    return AiMemory(
      id: json['id'] as int? ?? 0,
      memoryType: json['memoryType'] as String? ?? '',
      memoryKey: json['memoryKey'] as String? ?? '',
      memoryValue: json['memoryValue'] as String? ?? '',
      displayText: json['displayText'] as String? ?? '',
      confidence: (json['confidence'] as num?)?.toDouble() ?? 0.0,
      learnedAt: DateTime.tryParse(json['learnedAt'] as String? ?? '') ??
          DateTime.now(),
      lastUsedAt: json['lastUsedAt'] != null
          ? DateTime.tryParse(json['lastUsedAt'] as String)
          : null,
      usageCount: json['usageCount'] as int? ?? 0,
      isActive: json['isActive'] as bool? ?? true,
      wasManuallyEdited: json['wasManuallyEdited'] as bool? ?? false,
    );
  }
}

/// Memories response
class AiMemoriesResponse {
  final List<AiMemory> memories;
  final bool memoryEnabled;

  AiMemoriesResponse({required this.memories, required this.memoryEnabled});

  factory AiMemoriesResponse.fromJson(Map<String, dynamic> json) {
    final list = json['memories'] as List<dynamic>? ?? [];
    return AiMemoriesResponse(
      memories: list
          .map((m) => AiMemory.fromJson(m as Map<String, dynamic>))
          .toList(),
      memoryEnabled: json['memoryEnabled'] as bool? ?? false,
    );
  }
}

/// Chat file upload response
class ChatFileUploadResponse {
  final bool success;
  final String? fileUri;
  final String? mimeType;
  final String? displayName;
  final int? sizeBytes;
  final String? error;

  ChatFileUploadResponse({
    required this.success,
    this.fileUri,
    this.mimeType,
    this.displayName,
    this.sizeBytes,
    this.error,
  });

  factory ChatFileUploadResponse.fromJson(Map<String, dynamic> json) {
    return ChatFileUploadResponse(
      success: json['success'] as bool? ?? false,
      fileUri: json['fileUri'] as String?,
      mimeType: json['mimeType'] as String?,
      displayName: json['displayName'] as String?,
      sizeBytes: json['sizeBytes'] as int?,
      error: json['error'] as String?,
    );
  }
}

/// Tool call result from streaming
class ToolCallResult {
  final String toolName;
  final Map<String, dynamic> arguments;
  final String result;
  final bool requiresConfirmation;

  /// One-time token for a write preview; sent back when the user confirms.
  final String? confirmationToken;

  ToolCallResult({
    required this.toolName,
    required this.arguments,
    required this.result,
    required this.requiresConfirmation,
    this.confirmationToken,
  });

  factory ToolCallResult.fromJson(Map<String, dynamic> json) {
    return ToolCallResult(
      toolName: json['toolName'] as String? ?? '',
      arguments: json['arguments'] as Map<String, dynamic>? ?? {},
      result:
          json['result'] as String? ?? json['functionResult'] as String? ?? '',
      requiresConfirmation: json['requiresConfirmation'] as bool? ?? false,
      confirmationToken: json['confirmationToken'] as String?,
    );
  }
}

/// Chat file reference (attached file ready to send)
class ChatFileReference {
  final String fileUri;
  final String mimeType;
  final String displayName;
  final int sizeBytes;

  ChatFileReference({
    required this.fileUri,
    required this.mimeType,
    required this.displayName,
    required this.sizeBytes,
  });
}

/// Which slice of the review backlog to request. Wire values match the backend
/// `ReviewScope` enum names (case-insensitive on the server).
enum ReviewScope { all, thisMonth, older }

extension ReviewScopeWire on ReviewScope {
  String get wire => switch (this) {
        ReviewScope.all => 'All',
        ReviewScope.thisMonth => 'ThisMonth',
        ReviewScope.older => 'Older',
      };
}

/// Review statistics for transactions needing categorization
class ReviewStatistics {
  final int totalNeedingReview;
  final int uncategorizedCount;
  final int lowConfidenceCount;

  /// Review-pending rows the AI has never attempted, from this month and last;
  /// drives Auto-Categorize. Older backlog is deliberately excluded (the ingest
  /// pipeline never auto-sends history either).
  final int aiEligibleCount;

  /// Rows the ingest pipeline queued for background AI categorization that are
  /// still waiting. Non-zero = the AI is working right now; the page shows its
  /// analyzing indicator and polls until this reaches zero.
  final int queuedForAiCount;

  /// Subsets of [totalNeedingReview], split by date. thisMonth + older == total.
  final int thisMonthCount;
  final int olderCount;

  final String? oldestPendingReview;

  ReviewStatistics({
    required this.totalNeedingReview,
    required this.uncategorizedCount,
    required this.lowConfidenceCount,
    this.aiEligibleCount = 0,
    this.queuedForAiCount = 0,
    this.thisMonthCount = 0,
    this.olderCount = 0,
    this.oldestPendingReview,
  });

  factory ReviewStatistics.fromJson(Map<String, dynamic> json) {
    int readInt(String camel, String pascal) =>
        (json[camel] as int?) ?? (json[pascal] as int?) ?? 0;
    return ReviewStatistics(
      totalNeedingReview: readInt('totalNeedingReview', 'TotalNeedingReview'),
      uncategorizedCount: readInt('uncategorizedCount', 'UncategorizedCount'),
      lowConfidenceCount: readInt('lowConfidenceCount', 'LowConfidenceCount'),
      aiEligibleCount: readInt('aiEligibleCount', 'AiEligibleCount'),
      queuedForAiCount: readInt('queuedForAiCount', 'QueuedForAiCount'),
      thisMonthCount: readInt('thisMonthCount', 'ThisMonthCount'),
      olderCount: readInt('olderCount', 'OlderCount'),
      oldestPendingReview: (json['oldestPendingReview'] ??
          json['OldestPendingReview']) as String?,
    );
  }
}

/// Response from batch categorization
class AICategorizationResponse {
  final int successfullyCategorized;
  final int highConfidence;
  final int needingReview;
  final int failed;

  /// True when a multi-round [AiService.batchCategorize] run stopped early
  /// because a round kept failing after retries. The counts are still valid
  /// (partial progress); the caller should tell the user more remains. Always
  /// false for a single server response parsed via [fromJson].
  final bool interrupted;

  AICategorizationResponse({
    required this.successfullyCategorized,
    required this.highConfidence,
    required this.needingReview,
    required this.failed,
    this.interrupted = false,
  });

  factory AICategorizationResponse.fromJson(Map<String, dynamic> json) {
    return AICategorizationResponse(
      successfullyCategorized: json['successfullyCategorized'] as int? ?? 0,
      highConfidence: json['highConfidence'] as int? ?? 0,
      needingReview: json['needingReview'] as int? ?? 0,
      failed: json['failed'] as int? ?? 0,
    );
  }
}

/// Result from processing the categorization queue
class QueueProcessResult {
  final int processed;
  final int remaining;
  final bool hasMore;

  QueueProcessResult({
    required this.processed,
    required this.remaining,
    required this.hasMore,
  });

  factory QueueProcessResult.fromJson(Map<String, dynamic> json) {
    return QueueProcessResult(
      processed: json['processed'] as int? ?? 0,
      remaining: json['remaining'] as int? ?? 0,
      hasMore: json['hasMore'] as bool? ?? false,
    );
  }
}

/// Singleton instance
final aiService = AIService();
