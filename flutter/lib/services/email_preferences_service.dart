import 'api_client.dart';

/// Email Preferences Model
class EmailPreferences {
  // Account Activity
  final bool plaidConnectionIssues;
  final bool plaidReconnectionSuccess;
  final bool subscriptionChanges;
  final bool trialExpirationWarnings;
  
  // Financial Insights
  final bool budgetAlerts;
  final bool savingsGoalMilestones;
  final bool largeTransactionAlerts;
  final bool monthlySummary;
  
  // Marketing & Updates
  final bool featureAnnouncements;
  final bool tipsAndBestPractices;
  final bool productUpdates;
  final bool promotionalOffers;
  
  // Achievements
  final bool badgeEarned;
  final bool milestoneReached;
  final bool streakNotifications;

  EmailPreferences({
    this.plaidConnectionIssues = true,
    this.plaidReconnectionSuccess = true,
    this.subscriptionChanges = true,
    this.trialExpirationWarnings = true,
    this.budgetAlerts = true,
    this.savingsGoalMilestones = true,
    this.largeTransactionAlerts = true,
    this.monthlySummary = true,
    this.featureAnnouncements = false,
    this.tipsAndBestPractices = false,
    this.productUpdates = true,
    this.promotionalOffers = false,
    this.badgeEarned = true,
    this.milestoneReached = true,
    this.streakNotifications = true,
  });

  factory EmailPreferences.fromJson(Map<String, dynamic> json) {
    return EmailPreferences(
      plaidConnectionIssues: json['plaidConnectionIssues'] as bool? ?? true,
      plaidReconnectionSuccess: json['plaidReconnectionSuccess'] as bool? ?? true,
      subscriptionChanges: json['subscriptionChanges'] as bool? ?? true,
      trialExpirationWarnings: json['trialExpirationWarnings'] as bool? ?? true,
      budgetAlerts: json['budgetAlerts'] as bool? ?? true,
      savingsGoalMilestones: json['savingsGoalMilestones'] as bool? ?? true,
      largeTransactionAlerts: json['largeTransactionAlerts'] as bool? ?? true,
      monthlySummary: json['monthlySummary'] as bool? ?? true,
      featureAnnouncements: json['featureAnnouncements'] as bool? ?? false,
      tipsAndBestPractices: json['tipsAndBestPractices'] as bool? ?? false,
      productUpdates: json['productUpdates'] as bool? ?? true,
      promotionalOffers: json['promotionalOffers'] as bool? ?? false,
      badgeEarned: json['badgeEarned'] as bool? ?? true,
      milestoneReached: json['milestoneReached'] as bool? ?? true,
      streakNotifications: json['streakNotifications'] as bool? ?? true,
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'plaidConnectionIssues': plaidConnectionIssues,
      'plaidReconnectionSuccess': plaidReconnectionSuccess,
      'subscriptionChanges': subscriptionChanges,
      'trialExpirationWarnings': trialExpirationWarnings,
      'budgetAlerts': budgetAlerts,
      'savingsGoalMilestones': savingsGoalMilestones,
      'largeTransactionAlerts': largeTransactionAlerts,
      'monthlySummary': monthlySummary,
      'featureAnnouncements': featureAnnouncements,
      'tipsAndBestPractices': tipsAndBestPractices,
      'productUpdates': productUpdates,
      'promotionalOffers': promotionalOffers,
      'badgeEarned': badgeEarned,
      'milestoneReached': milestoneReached,
      'streakNotifications': streakNotifications,
    };
  }

  EmailPreferences copyWith({
    bool? plaidConnectionIssues,
    bool? plaidReconnectionSuccess,
    bool? subscriptionChanges,
    bool? trialExpirationWarnings,
    bool? budgetAlerts,
    bool? savingsGoalMilestones,
    bool? largeTransactionAlerts,
    bool? monthlySummary,
    bool? featureAnnouncements,
    bool? tipsAndBestPractices,
    bool? productUpdates,
    bool? promotionalOffers,
    bool? badgeEarned,
    bool? milestoneReached,
    bool? streakNotifications,
  }) {
    return EmailPreferences(
      plaidConnectionIssues: plaidConnectionIssues ?? this.plaidConnectionIssues,
      plaidReconnectionSuccess: plaidReconnectionSuccess ?? this.plaidReconnectionSuccess,
      subscriptionChanges: subscriptionChanges ?? this.subscriptionChanges,
      trialExpirationWarnings: trialExpirationWarnings ?? this.trialExpirationWarnings,
      budgetAlerts: budgetAlerts ?? this.budgetAlerts,
      savingsGoalMilestones: savingsGoalMilestones ?? this.savingsGoalMilestones,
      largeTransactionAlerts: largeTransactionAlerts ?? this.largeTransactionAlerts,
      monthlySummary: monthlySummary ?? this.monthlySummary,
      featureAnnouncements: featureAnnouncements ?? this.featureAnnouncements,
      tipsAndBestPractices: tipsAndBestPractices ?? this.tipsAndBestPractices,
      productUpdates: productUpdates ?? this.productUpdates,
      promotionalOffers: promotionalOffers ?? this.promotionalOffers,
      badgeEarned: badgeEarned ?? this.badgeEarned,
      milestoneReached: milestoneReached ?? this.milestoneReached,
      streakNotifications: streakNotifications ?? this.streakNotifications,
    );
  }
}

/// Email Preferences Service
class EmailPreferencesService {
  /// Get current email preferences
  Future<EmailPreferences> getPreferences() async {
    try {
      final response = await apiClient.get<Map<String, dynamic>>(
        '/email-preferences',
      );
      return EmailPreferences.fromJson(response);
    } catch (e) {
      // Return defaults if not found
      return EmailPreferences();
    }
  }

  /// Update email preferences
  Future<EmailPreferences> updatePreferences(EmailPreferences prefs) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/email-preferences',
      data: prefs.toJson(),
    );
    
    if (response['preferences'] != null) {
      return EmailPreferences.fromJson(response['preferences'] as Map<String, dynamic>);
    }
    return prefs;
  }

  /// Update a single preference
  Future<EmailPreferences> updateSinglePreference(String key, bool value) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/email-preferences',
      data: {key: value},
    );
    
    if (response['preferences'] != null) {
      return EmailPreferences.fromJson(response['preferences'] as Map<String, dynamic>);
    }
    throw Exception('Failed to update preference');
  }
}

/// Singleton instance
final emailPreferencesService = EmailPreferencesService();
