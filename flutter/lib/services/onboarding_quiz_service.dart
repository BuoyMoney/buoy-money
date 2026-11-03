import 'package:dio/dio.dart';

import 'api_client.dart';

// ── Models ──────────────────────────────────────────────

/// Submission DTO for the onboarding quiz.
class OnboardingQuizSubmission {
  final String primaryFinancialGoal;
  final String biggestChallenge;
  final String spendingPersonality;
  final String subscriptionAwareness;
  final String? savingsBarriers;
  final String preferredInsightType;
  final String financialExperience;
  final bool? hasUsedBudgetingApps;
  final String? additionalNotes;

  OnboardingQuizSubmission({
    required this.primaryFinancialGoal,
    required this.biggestChallenge,
    required this.spendingPersonality,
    required this.subscriptionAwareness,
    this.savingsBarriers,
    required this.preferredInsightType,
    required this.financialExperience,
    this.hasUsedBudgetingApps,
    this.additionalNotes,
  });

  Map<String, dynamic> toJson() => {
        'primaryFinancialGoal': primaryFinancialGoal,
        'biggestChallenge': biggestChallenge,
        'spendingPersonality': spendingPersonality,
        'subscriptionAwareness': subscriptionAwareness,
        if (savingsBarriers != null) 'savingsBarriers': savingsBarriers,
        'preferredInsightType': preferredInsightType,
        'financialExperience': financialExperience,
        if (hasUsedBudgetingApps != null)
          'hasUsedBudgetingApps': hasUsedBudgetingApps,
        if (additionalNotes != null && additionalNotes!.isNotEmpty)
          'additionalNotes': additionalNotes,
      };
}

/// Response DTO returned by the backend after submission or when
/// fetching quiz history.
class OnboardingQuizResponse {
  final int id;
  final int userId;
  final String primaryFinancialGoal;
  final String biggestChallenge;
  final String spendingPersonality;
  final String subscriptionAwareness;
  final String? savingsBarriers;
  final String preferredInsightType;
  final String financialExperience;
  final bool? hasUsedBudgetingApps;
  final String? additionalNotes;
  final DateTime createdAt;
  final DateTime updatedAt;
  final int version;

  OnboardingQuizResponse({
    required this.id,
    required this.userId,
    required this.primaryFinancialGoal,
    required this.biggestChallenge,
    required this.spendingPersonality,
    required this.subscriptionAwareness,
    this.savingsBarriers,
    required this.preferredInsightType,
    required this.financialExperience,
    this.hasUsedBudgetingApps,
    this.additionalNotes,
    required this.createdAt,
    required this.updatedAt,
    required this.version,
  });

  factory OnboardingQuizResponse.fromJson(Map<String, dynamic> json) {
    return OnboardingQuizResponse(
      id: int.tryParse((json['id'] ?? json['Id'] ?? 0).toString()) ?? 0,
      userId:
          int.tryParse((json['userId'] ?? json['UserId'] ?? 0).toString()) ?? 0,
      primaryFinancialGoal: (json['primaryFinancialGoal'] ??
              json['PrimaryFinancialGoal'] ??
              '')
          .toString(),
      biggestChallenge:
          (json['biggestChallenge'] ?? json['BiggestChallenge'] ?? '')
              .toString(),
      spendingPersonality:
          (json['spendingPersonality'] ?? json['SpendingPersonality'] ?? '')
              .toString(),
      subscriptionAwareness:
          (json['subscriptionAwareness'] ?? json['SubscriptionAwareness'] ?? '')
              .toString(),
      savingsBarriers:
          (json['savingsBarriers'] ?? json['SavingsBarriers']) as String?,
      preferredInsightType:
          (json['preferredInsightType'] ?? json['PreferredInsightType'] ?? '')
              .toString(),
      financialExperience:
          (json['financialExperience'] ?? json['FinancialExperience'] ?? '')
              .toString(),
      hasUsedBudgetingApps:
          (json['hasUsedBudgetingApps'] ?? json['HasUsedBudgetingApps'])
              as bool?,
      additionalNotes:
          (json['additionalNotes'] ?? json['AdditionalNotes']) as String?,
      createdAt: DateTime.tryParse(
              (json['createdAt'] ?? json['CreatedAt'] ?? '').toString()) ??
          DateTime.now(),
      updatedAt: DateTime.tryParse(
              (json['updatedAt'] ?? json['UpdatedAt'] ?? '').toString()) ??
          DateTime.now(),
      version:
          int.tryParse((json['version'] ?? json['Version'] ?? 1).toString()) ??
              1,
    );
  }

  Map<String, dynamic> toJson() => {
        'id': id,
        'userId': userId,
        'primaryFinancialGoal': primaryFinancialGoal,
        'biggestChallenge': biggestChallenge,
        'spendingPersonality': spendingPersonality,
        'subscriptionAwareness': subscriptionAwareness,
        if (savingsBarriers != null) 'savingsBarriers': savingsBarriers,
        'preferredInsightType': preferredInsightType,
        'financialExperience': financialExperience,
        if (hasUsedBudgetingApps != null)
          'hasUsedBudgetingApps': hasUsedBudgetingApps,
        if (additionalNotes != null) 'additionalNotes': additionalNotes,
        'createdAt': createdAt.toIso8601String(),
        'updatedAt': updatedAt.toIso8601String(),
        'version': version,
      };
}

/// Personalized insight returned by the insights endpoint.
class PersonalizedInsight {
  final String insightType;
  final String title;
  final String message;
  final List<String> recommendedActions;
  final int priority;

  PersonalizedInsight({
    required this.insightType,
    required this.title,
    required this.message,
    required this.recommendedActions,
    required this.priority,
  });

  factory PersonalizedInsight.fromJson(Map<String, dynamic> json) {
    return PersonalizedInsight(
      insightType:
          (json['insightType'] ?? json['InsightType'] ?? '').toString(),
      title: (json['title'] ?? json['Title'] ?? '').toString(),
      message: (json['message'] ?? json['Message'] ?? '').toString(),
      recommendedActions: List<String>.from(
          (json['recommendedActions'] ?? json['RecommendedActions'] ?? [])
              as List<dynamic>),
      priority:
          int.tryParse((json['priority'] ?? json['Priority'] ?? 0).toString()) ??
              0,
    );
  }

  Map<String, dynamic> toJson() => {
        'insightType': insightType,
        'title': title,
        'message': message,
        'recommendedActions': recommendedActions,
        'priority': priority,
      };
}

// ── Service ─────────────────────────────────────────────

/// Onboarding Quiz Service — handles quiz submission, retrieval, and
/// personalized insights.
///
/// Backend endpoints:
///   OnboardingQuizController → /api/OnboardingQuiz/*
class OnboardingQuizService {
  /// Submit quiz answers. Returns the persisted response.
  Future<OnboardingQuizResponse> submitQuiz(
      OnboardingQuizSubmission submission) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/OnboardingQuiz/submit',
      data: submission.toJson(),
    );
    return OnboardingQuizResponse.fromJson(response);
  }

  /// Get the most recent quiz response for the current user.
  /// Returns `null` if the user has never completed the quiz (404).
  Future<OnboardingQuizResponse?> getLatestQuiz() async {
    try {
      final response = await apiClient.get<Map<String, dynamic>>(
        '/OnboardingQuiz/latest',
      );
      return OnboardingQuizResponse.fromJson(response);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Check whether the current user has completed the onboarding quiz.
  /// Returns `null` on 404 (endpoint not found / user not found).
  Future<bool?> hasCompletedQuiz() async {
    try {
      final response = await apiClient.get<Map<String, dynamic>>(
        '/OnboardingQuiz/completed',
      );
      return (response['hasCompleted'] ?? response['HasCompleted'] ?? false)
          as bool?;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Get the full quiz response history for the current user.
  Future<List<OnboardingQuizResponse>> getQuizHistory() async {
    final response = await apiClient.get<List<dynamic>>(
      '/OnboardingQuiz/history',
    );
    return response
        .map((e) => OnboardingQuizResponse.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  /// Get personalized insights based on quiz answers.
  Future<List<PersonalizedInsight>> getInsights() async {
    final response = await apiClient.get<List<dynamic>>(
      '/OnboardingQuiz/insights',
    );
    return response
        .map((e) => PersonalizedInsight.fromJson(e as Map<String, dynamic>))
        .toList();
  }
}

/// Singleton instance
final onboardingQuizService = OnboardingQuizService();
