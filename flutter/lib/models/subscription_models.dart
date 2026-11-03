/// Subscription Models
/// 
/// Data models for subscription tracking feature.
library;

// ============================================================================
// ENUMS
// ============================================================================

/// How often a subscription bills
enum SubscriptionFrequency {
  weekly(0),
  biWeekly(1),
  monthly(2),
  quarterly(3),
  semiAnnual(4),
  annual(5),
  custom(99);

  final int value;
  const SubscriptionFrequency(this.value);

  static SubscriptionFrequency fromValue(dynamic value) {
    if (value == null) return SubscriptionFrequency.monthly;
    final intValue = value is int ? value : int.tryParse(value.toString()) ?? 2;
    return SubscriptionFrequency.values.firstWhere(
      (e) => e.value == intValue,
      orElse: () => SubscriptionFrequency.monthly,
    );
  }

  String get displayName {
    switch (this) {
      case SubscriptionFrequency.weekly:
        return 'Weekly';
      case SubscriptionFrequency.biWeekly:
        return 'Bi-Weekly';
      case SubscriptionFrequency.monthly:
        return 'Monthly';
      case SubscriptionFrequency.quarterly:
        return 'Quarterly';
      case SubscriptionFrequency.semiAnnual:
        return 'Semi-Annual';
      case SubscriptionFrequency.annual:
        return 'Annual';
      case SubscriptionFrequency.custom:
        return 'Custom';
    }
  }

  /// Returns multiplier to convert to monthly amount
  double get monthlyMultiplier {
    switch (this) {
      case SubscriptionFrequency.weekly:
        return 4.33; // ~4.33 weeks per month
      case SubscriptionFrequency.biWeekly:
        return 2.17;
      case SubscriptionFrequency.monthly:
        return 1.0;
      case SubscriptionFrequency.quarterly:
        return 1.0 / 3.0;
      case SubscriptionFrequency.semiAnnual:
        return 1.0 / 6.0;
      case SubscriptionFrequency.annual:
        return 1.0 / 12.0;
      case SubscriptionFrequency.custom:
        return 1.0;
    }
  }

  /// Returns the number of billing periods per year. Used for an accurate
  /// yearly total — weekly/bi-weekly do not evenly divide into months, so
  /// deriving the year from the monthly multiplier (4.33 * 12 != 52) drifts.
  double get yearlyMultiplier {
    switch (this) {
      case SubscriptionFrequency.weekly:
        return 52;
      case SubscriptionFrequency.biWeekly:
        return 26;
      case SubscriptionFrequency.monthly:
        return 12;
      case SubscriptionFrequency.quarterly:
        return 4;
      case SubscriptionFrequency.semiAnnual:
        return 2;
      case SubscriptionFrequency.annual:
        return 1;
      case SubscriptionFrequency.custom:
        return 12;
    }
  }
}

/// Current status of a subscription
enum SubscriptionStatus {
  active(0),
  paused(1),
  cancelled(2),
  expired(3);

  final int value;
  const SubscriptionStatus(this.value);

  static SubscriptionStatus fromValue(dynamic value) {
    if (value == null) return SubscriptionStatus.active;
    final intValue = value is int ? value : int.tryParse(value.toString()) ?? 0;
    return SubscriptionStatus.values.firstWhere(
      (e) => e.value == intValue,
      orElse: () => SubscriptionStatus.active,
    );
  }

  String get displayName {
    switch (this) {
      case SubscriptionStatus.active:
        return 'Active';
      case SubscriptionStatus.paused:
        return 'Paused';
      case SubscriptionStatus.cancelled:
        return 'Cancelled';
      case SubscriptionStatus.expired:
        return 'Expired';
    }
  }
}

/// Category of subscription service
enum SubscriptionCategory {
  streaming(1),
  software(2),
  utilities(3),
  insurance(4),
  fitness(5),
  news(6),
  gaming(7),
  shopping(8),
  education(9),
  professional(10),
  other(99);

  final int value;
  const SubscriptionCategory(this.value);

  static SubscriptionCategory fromValue(dynamic value) {
    if (value == null) return SubscriptionCategory.other;
    final intValue = value is int ? value : int.tryParse(value.toString()) ?? 99;
    return SubscriptionCategory.values.firstWhere(
      (e) => e.value == intValue,
      orElse: () => SubscriptionCategory.other,
    );
  }

  String get displayName {
    switch (this) {
      case SubscriptionCategory.streaming:
        return 'Streaming';
      case SubscriptionCategory.software:
        return 'Software';
      case SubscriptionCategory.utilities:
        return 'Utilities';
      case SubscriptionCategory.insurance:
        return 'Insurance';
      case SubscriptionCategory.fitness:
        return 'Fitness';
      case SubscriptionCategory.news:
        return 'News';
      case SubscriptionCategory.gaming:
        return 'Gaming';
      case SubscriptionCategory.shopping:
        return 'Shopping';
      case SubscriptionCategory.education:
        return 'Education';
      case SubscriptionCategory.professional:
        return 'Professional';
      case SubscriptionCategory.other:
        return 'Other';
    }
  }

  /// Icon for this category
  String get icon {
    switch (this) {
      case SubscriptionCategory.streaming:
        return '🎬';
      case SubscriptionCategory.software:
        return '💻';
      case SubscriptionCategory.utilities:
        return '🔌';
      case SubscriptionCategory.insurance:
        return '🛡️';
      case SubscriptionCategory.fitness:
        return '💪';
      case SubscriptionCategory.news:
        return '📰';
      case SubscriptionCategory.gaming:
        return '🎮';
      case SubscriptionCategory.shopping:
        return '🛒';
      case SubscriptionCategory.education:
        return '📚';
      case SubscriptionCategory.professional:
        return '💼';
      case SubscriptionCategory.other:
        return '📦';
    }
  }
}

/// Status of AI-suggested subscription
enum SuggestedSubscriptionStatus {
  pending(0),
  confirmed(1),
  dismissed(2),
  snoozed(3);

  final int value;
  const SuggestedSubscriptionStatus(this.value);

  static SuggestedSubscriptionStatus fromValue(dynamic value) {
    if (value == null) return SuggestedSubscriptionStatus.pending;
    final intValue = value is int ? value : int.tryParse(value.toString()) ?? 0;
    return SuggestedSubscriptionStatus.values.firstWhere(
      (e) => e.value == intValue,
      orElse: () => SuggestedSubscriptionStatus.pending,
    );
  }
}

// ============================================================================
// MODELS
// ============================================================================

/// A tracked subscription
class Subscription {
  final int id;
  final String name;
  final String? description;
  final double amount;
  final SubscriptionFrequency frequency;
  final DateTime nextDueDate;
  final DateTime? lastBilledDate;
  final SubscriptionStatus status;
  final SubscriptionCategory category;
  final bool isDetected;
  final String? merchantName;
  final bool hiddenFromReadOnly;
  final DateTime createdAt;
  final DateTime updatedAt;

  Subscription({
    required this.id,
    required this.name,
    this.description,
    required this.amount,
    required this.frequency,
    required this.nextDueDate,
    this.lastBilledDate,
    required this.status,
    required this.category,
    this.isDetected = false,
    this.merchantName,
    this.hiddenFromReadOnly = false,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Subscription.fromJson(Map<String, dynamic> json) {
    return Subscription(
      id: int.tryParse((json['id'] ?? json['Id'] ?? 0).toString()) ?? 0,
      name: (json['name'] ?? json['Name'] ?? '').toString(),
      description: (json['description'] ?? json['Description'])?.toString(),
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      frequency: SubscriptionFrequency.fromValue(json['frequency'] ?? json['Frequency']),
      nextDueDate: DateTime.parse((json['nextDueDate'] ?? json['NextDueDate'] ?? DateTime.now().toIso8601String()).toString()),
      lastBilledDate: json['lastBilledDate'] != null || json['LastBilledDate'] != null
          ? DateTime.parse((json['lastBilledDate'] ?? json['LastBilledDate']).toString())
          : null,
      status: SubscriptionStatus.fromValue(json['status'] ?? json['Status']),
      category: SubscriptionCategory.fromValue(json['category'] ?? json['Category']),
      isDetected: (json['isDetected'] ?? json['IsDetected']) as bool? ?? false,
      merchantName: (json['merchantName'] ?? json['MerchantName'])?.toString(),
      hiddenFromReadOnly: (json['hiddenFromReadOnly'] ?? json['HiddenFromReadOnly']) as bool? ?? false,
      createdAt: DateTime.parse((json['createdAt'] ?? json['CreatedAt'] ?? DateTime.now().toIso8601String()).toString()),
      updatedAt: DateTime.parse((json['updatedAt'] ?? json['UpdatedAt'] ?? DateTime.now().toIso8601String()).toString()),
    );
  }

  Map<String, dynamic> toJson() => {
    'id': id,
    'name': name,
    'description': description,
    'amount': amount,
    'frequency': frequency.value,
    'nextDueDate': nextDueDate.toIso8601String(),
    'lastBilledDate': lastBilledDate?.toIso8601String(),
    'status': status.value,
    'category': category.value,
    'isDetected': isDetected,
    'merchantName': merchantName,
    'hiddenFromReadOnly': hiddenFromReadOnly,
    'createdAt': createdAt.toIso8601String(),
    'updatedAt': updatedAt.toIso8601String(),
  };

  /// Calculate monthly equivalent cost
  double get monthlyAmount => amount * frequency.monthlyMultiplier;

  /// Calculate yearly equivalent cost
  double get yearlyAmount => amount * frequency.yearlyMultiplier;

  /// Days until next billing
  int get daysUntilDue {
    final now = DateTime.now();
    return nextDueDate.difference(now).inDays;
  }

  /// Is due within the next week?
  bool get isDueSoon => daysUntilDue <= 7 && daysUntilDue >= 0;

  /// Is overdue?
  bool get isOverdue => daysUntilDue < 0;
}

/// Summary statistics for subscriptions
class SubscriptionSummary {
  final double monthlyTotal;
  final double yearlyTotal;
  final int activeCount;
  final List<CategorySummary> categoryBreakdown;

  SubscriptionSummary({
    required this.monthlyTotal,
    required this.yearlyTotal,
    required this.activeCount,
    required this.categoryBreakdown,
  });

  factory SubscriptionSummary.fromJson(Map<String, dynamic> json) {
    final breakdown = (json['categoryBreakdown'] ?? json['CategoryBreakdown'] ?? []) as List;
    return SubscriptionSummary(
      monthlyTotal: ((json['monthlyTotal'] ?? json['MonthlyTotal'] ?? 0) as num).toDouble(),
      yearlyTotal: ((json['yearlyTotal'] ?? json['YearlyTotal'] ?? 0) as num).toDouble(),
      activeCount: int.tryParse((json['activeCount'] ?? json['ActiveCount'] ?? 0).toString()) ?? 0,
      categoryBreakdown: breakdown.map((e) => CategorySummary.fromJson(e as Map<String, dynamic>)).toList(),
    );
  }

  factory SubscriptionSummary.empty() => SubscriptionSummary(
    monthlyTotal: 0,
    yearlyTotal: 0,
    activeCount: 0,
    categoryBreakdown: [],
  );
}

/// Category breakdown for summary
class CategorySummary {
  final SubscriptionCategory category;
  final double amount;
  final int count;

  CategorySummary({
    required this.category,
    required this.amount,
    required this.count,
  });

  factory CategorySummary.fromJson(Map<String, dynamic> json) {
    return CategorySummary(
      category: SubscriptionCategory.fromValue(json['category'] ?? json['Category']),
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      count: int.tryParse((json['count'] ?? json['Count'] ?? 0).toString()) ?? 0,
    );
  }
}

/// Subscription detected from transaction patterns
class DetectedSubscription {
  final String merchantName;
  final double amount;
  final SubscriptionFrequency frequency;
  final DateTime estimatedNextDue;
  final double confidence;
  final List<int> relatedTransactionIds;
  final bool isSubscriptionLikely;
  final bool? isAIEnhanced;
  final String? detectionMethod;
  final String? aiInsight;
  final SubscriptionCategory? aiSuggestedCategory;

  DetectedSubscription({
    required this.merchantName,
    required this.amount,
    required this.frequency,
    required this.estimatedNextDue,
    required this.confidence,
    required this.relatedTransactionIds,
    required this.isSubscriptionLikely,
    this.isAIEnhanced,
    this.detectionMethod,
    this.aiInsight,
    this.aiSuggestedCategory,
  });

  factory DetectedSubscription.fromJson(Map<String, dynamic> json) {
    final txIds = (json['relatedTransactionIds'] ?? json['RelatedTransactionIds'] ?? []) as List;
    return DetectedSubscription(
      merchantName: (json['merchantName'] ?? json['MerchantName'] ?? '').toString(),
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      frequency: SubscriptionFrequency.fromValue(json['frequency'] ?? json['Frequency']),
      estimatedNextDue: DateTime.parse((json['estimatedNextDue'] ?? json['EstimatedNextDue'] ?? DateTime.now().toIso8601String()).toString()),
      confidence: ((json['confidence'] ?? json['Confidence'] ?? 0) as num).toDouble(),
      relatedTransactionIds: txIds.map((e) => e as int).toList(),
      isSubscriptionLikely: (json['isSubscriptionLikely'] ?? json['IsSubscriptionLikely']) as bool? ?? false,
      isAIEnhanced: (json['isAIEnhanced'] ?? json['IsAIEnhanced']) as bool?,
      detectionMethod: (json['detectionMethod'] ?? json['DetectionMethod'])?.toString(),
      aiInsight: (json['aiInsight'] ?? json['AiInsight'])?.toString(),
      aiSuggestedCategory: json['aiSuggestedCategory'] != null || json['AiSuggestedCategory'] != null
          ? SubscriptionCategory.fromValue(json['aiSuggestedCategory'] ?? json['AiSuggestedCategory'])
          : null,
    );
  }

  Map<String, dynamic> toJson() => {
    'merchantName': merchantName,
    'amount': amount,
    'frequency': frequency.value,
    'estimatedNextDue': estimatedNextDue.toIso8601String(),
    'confidence': confidence,
    'relatedTransactionIds': relatedTransactionIds,
    'isSubscriptionLikely': isSubscriptionLikely,
    if (isAIEnhanced != null) 'isAIEnhanced': isAIEnhanced,
    if (detectionMethod != null) 'detectionMethod': detectionMethod,
    if (aiInsight != null) 'aiInsight': aiInsight,
    if (aiSuggestedCategory != null) 'aiSuggestedCategory': aiSuggestedCategory!.value,
  };

  /// Confidence level label
  String get confidenceLabel {
    if (confidence >= 0.8) return 'High';
    if (confidence >= 0.5) return 'Medium';
    return 'Low';
  }
}

/// AI-suggested subscription with crowdsource boost
class SuggestedSubscription {
  final int id;
  final String merchantName;
  final double amount;
  final SubscriptionFrequency frequency;
  final double confidence;
  final double baseConfidence;
  final double crowdsourceBoost;
  final SuggestedSubscriptionStatus status;
  final DateTime? snoozeUntil;
  final bool hiddenFromReadOnly;
  final SubscriptionCategory? aiSuggestedCategory;
  final String? aiInsight;
  final int matchedTransactionCount;
  final DateTime? predictedNextDate;
  final DateTime detectedAt;
  final DateTime? actionTakenAt;

  SuggestedSubscription({
    required this.id,
    required this.merchantName,
    required this.amount,
    required this.frequency,
    required this.confidence,
    required this.baseConfidence,
    required this.crowdsourceBoost,
    required this.status,
    this.snoozeUntil,
    this.hiddenFromReadOnly = false,
    this.aiSuggestedCategory,
    this.aiInsight,
    required this.matchedTransactionCount,
    this.predictedNextDate,
    required this.detectedAt,
    this.actionTakenAt,
  });

  factory SuggestedSubscription.fromJson(Map<String, dynamic> json) {
    return SuggestedSubscription(
      id: int.tryParse((json['id'] ?? json['Id'] ?? 0).toString()) ?? 0,
      merchantName: (json['merchantName'] ?? json['MerchantName'] ?? '').toString(),
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      frequency: SubscriptionFrequency.fromValue(json['frequency'] ?? json['Frequency']),
      confidence: ((json['confidence'] ?? json['Confidence'] ?? 0) as num).toDouble(),
      baseConfidence: ((json['baseConfidence'] ?? json['BaseConfidence'] ?? 0) as num).toDouble(),
      crowdsourceBoost: ((json['crowdsourceBoost'] ?? json['CrowdsourceBoost'] ?? 0) as num).toDouble(),
      status: SuggestedSubscriptionStatus.fromValue(json['status'] ?? json['Status']),
      snoozeUntil: json['snoozeUntil'] != null || json['SnoozeUntil'] != null
          ? DateTime.parse((json['snoozeUntil'] ?? json['SnoozeUntil']).toString())
          : null,
      hiddenFromReadOnly: (json['hiddenFromReadOnly'] ?? json['HiddenFromReadOnly']) as bool? ?? false,
      aiSuggestedCategory: json['aiSuggestedCategory'] != null || json['AiSuggestedCategory'] != null
          ? SubscriptionCategory.fromValue(json['aiSuggestedCategory'] ?? json['AiSuggestedCategory'])
          : null,
      aiInsight: (json['aiInsight'] ?? json['AiInsight'])?.toString(),
      matchedTransactionCount: int.tryParse((json['matchedTransactionCount'] ?? json['MatchedTransactionCount'] ?? 0).toString()) ?? 0,
      predictedNextDate: json['predictedNextDate'] != null || json['PredictedNextDate'] != null
          ? DateTime.parse((json['predictedNextDate'] ?? json['PredictedNextDate']).toString())
          : null,
      detectedAt: DateTime.parse((json['detectedAt'] ?? json['DetectedAt'] ?? DateTime.now().toIso8601String()).toString()),
      actionTakenAt: json['actionTakenAt'] != null || json['ActionTakenAt'] != null
          ? DateTime.parse((json['actionTakenAt'] ?? json['ActionTakenAt']).toString())
          : null,
    );
  }

  /// Has crowdsource community verification
  bool get hasCrowdsourceBoost => crowdsourceBoost > 0;

  /// Confidence level label
  String get confidenceLabel {
    if (confidence >= 0.8) return 'High';
    if (confidence >= 0.5) return 'Medium';
    return 'Low';
  }
}

/// Subscriptions grouped by date for calendar view
class CalendarSubscription {
  final DateTime date;
  final List<Subscription> subscriptions;
  final double totalAmount;

  CalendarSubscription({
    required this.date,
    required this.subscriptions,
    required this.totalAmount,
  });

  factory CalendarSubscription.fromJson(Map<String, dynamic> json) {
    final subs = (json['subscriptions'] ?? json['Subscriptions'] ?? []) as List;
    return CalendarSubscription(
      date: DateTime.parse((json['date'] ?? json['Date'] ?? DateTime.now().toIso8601String()).toString()),
      subscriptions: subs.map((e) => Subscription.fromJson(e as Map<String, dynamic>)).toList(),
      totalAmount: ((json['totalAmount'] ?? json['TotalAmount'] ?? 0) as num).toDouble(),
    );
  }
}

// ============================================================================
// REQUEST MODELS
// ============================================================================

/// Request to create a new subscription
class CreateSubscriptionRequest {
  final String name;
  final String? description;
  final double amount;
  final SubscriptionFrequency frequency;
  final DateTime nextDueDate;
  final SubscriptionCategory category;
  final String? merchantName;
  final bool hiddenFromReadOnly;

  CreateSubscriptionRequest({
    required this.name,
    this.description,
    required this.amount,
    required this.frequency,
    required this.nextDueDate,
    required this.category,
    this.merchantName,
    this.hiddenFromReadOnly = false,
  });

  Map<String, dynamic> toJson() => {
    'name': name,
    'description': description,
    'amount': amount,
    'frequency': frequency.value,
    'nextDueDate': nextDueDate.toIso8601String(),
    'category': category.value,
    'merchantName': merchantName,
    'hiddenFromReadOnly': hiddenFromReadOnly,
  };
}

/// Request to update an existing subscription
class UpdateSubscriptionRequest {
  final String? name;
  final String? description;
  final double? amount;
  final SubscriptionFrequency? frequency;
  final DateTime? nextDueDate;
  final SubscriptionStatus? status;
  final SubscriptionCategory? category;
  final bool? hiddenFromReadOnly;

  UpdateSubscriptionRequest({
    this.name,
    this.description,
    this.amount,
    this.frequency,
    this.nextDueDate,
    this.status,
    this.category,
    this.hiddenFromReadOnly,
  });

  Map<String, dynamic> toJson() {
    final json = <String, dynamic>{};
    if (name != null) json['name'] = name;
    if (description != null) json['description'] = description;
    if (amount != null) json['amount'] = amount;
    if (frequency != null) json['frequency'] = frequency!.value;
    if (nextDueDate != null) json['nextDueDate'] = nextDueDate!.toIso8601String();
    if (status != null) json['status'] = status!.value;
    if (category != null) json['category'] = category!.value;
    if (hiddenFromReadOnly != null) json['hiddenFromReadOnly'] = hiddenFromReadOnly;
    return json;
  }
}

/// Request to detect subscriptions from transactions
class SubscriptionDetectionRequest {
  final DateTime? startDate;
  final DateTime? endDate;
  final int? minOccurrences;
  final double? amountTolerance;

  SubscriptionDetectionRequest({
    this.startDate,
    this.endDate,
    this.minOccurrences,
    this.amountTolerance,
  });

  Map<String, dynamic> toJson() {
    final json = <String, dynamic>{};
    if (startDate != null) json['startDate'] = startDate!.toIso8601String();
    if (endDate != null) json['endDate'] = endDate!.toIso8601String();
    if (minOccurrences != null) json['minOccurrences'] = minOccurrences;
    if (amountTolerance != null) json['amountTolerance'] = amountTolerance;
    return json;
  }
}

/// Request to confirm a detected subscription
class ConfirmDetectedRequest {
  final String merchantName;
  final double amount;
  final SubscriptionFrequency frequency;
  final DateTime nextDueDate;
  final SubscriptionCategory category;
  final String name;
  final bool hiddenFromReadOnly;

  ConfirmDetectedRequest({
    required this.merchantName,
    required this.amount,
    required this.frequency,
    required this.nextDueDate,
    required this.category,
    required this.name,
    this.hiddenFromReadOnly = false,
  });

  Map<String, dynamic> toJson() => {
    'merchantName': merchantName,
    'amount': amount,
    'frequency': frequency.value,
    'nextDueDate': nextDueDate.toIso8601String(),
    'category': category.value,
    'name': name,
    'hiddenFromReadOnly': hiddenFromReadOnly,
  };
}

/// Request to snooze a suggestion
class SnoozeSuggestionRequest {
  final int days;

  SnoozeSuggestionRequest({required this.days});

  Map<String, dynamic> toJson() => {'days': days};
}

// ============================================================================
// BILLING EVENT MODELS (Calendar & Timeline)
// ============================================================================

/// Type of billing event
enum BillingEventType {
  past(0),
  next(1),
  future(2);

  final int value;
  const BillingEventType(this.value);

  static BillingEventType fromValue(dynamic value) {
    if (value == null) return BillingEventType.future;
    final intValue = value is int ? value : int.tryParse(value.toString()) ?? 2;
    return BillingEventType.values.firstWhere(
      (e) => e.value == intValue,
      orElse: () => BillingEventType.future,
    );
  }

  String get displayName {
    switch (this) {
      case BillingEventType.past:
        return 'Past Payment';
      case BillingEventType.next:
        return 'Next Due';
      case BillingEventType.future:
        return 'Upcoming';
    }
  }
}

/// A single billing event
class BillingEvent {
  final DateTime date;
  final double amount;
  final BillingEventType eventType;
  final int? transactionId;
  final bool isConfirmed;

  BillingEvent({
    required this.date,
    required this.amount,
    required this.eventType,
    this.transactionId,
    this.isConfirmed = false,
  });

  factory BillingEvent.fromJson(Map<String, dynamic> json) {
    return BillingEvent(
      date: DateTime.parse((json['date'] ?? json['Date'] ?? DateTime.now().toIso8601String()).toString()),
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      eventType: BillingEventType.fromValue(json['eventType'] ?? json['EventType']),
      transactionId: (json['transactionId'] ?? json['TransactionId']) as int?,
      isConfirmed: (json['isConfirmed'] ?? json['IsConfirmed']) as bool? ?? false,
    );
  }
}

/// Calendar billing event with subscription context
class CalendarBillingEvent {
  final DateTime date;
  final int subscriptionId;
  final String subscriptionName;
  final double amount;
  final BillingEventType eventType;
  final bool isConfirmed;
  final int? transactionId;

  CalendarBillingEvent({
    required this.date,
    required this.subscriptionId,
    required this.subscriptionName,
    required this.amount,
    required this.eventType,
    this.isConfirmed = false,
    this.transactionId,
  });

  factory CalendarBillingEvent.fromJson(Map<String, dynamic> json) {
    return CalendarBillingEvent(
      date: DateTime.parse((json['date'] ?? json['Date'] ?? DateTime.now().toIso8601String()).toString()),
      subscriptionId: int.tryParse((json['subscriptionId'] ?? json['SubscriptionId'] ?? 0).toString()) ?? 0,
      subscriptionName: (json['subscriptionName'] ?? json['SubscriptionName'] ?? '').toString(),
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      eventType: BillingEventType.fromValue(json['eventType'] ?? json['EventType']),
      isConfirmed: (json['isConfirmed'] ?? json['IsConfirmed']) as bool? ?? false,
      transactionId: (json['transactionId'] ?? json['TransactionId']) as int?,
    );
  }
}

/// Subscription timeline with billing history and projections
class SubscriptionTimeline {
  final int subscriptionId;
  final String name;
  final double amount;
  final SubscriptionFrequency frequency;
  final SubscriptionCategory category;
  final SubscriptionStatus status;
  final List<BillingEvent> billingEvents;
  final int missedBillingCycles;
  final bool isPotentiallyCancelled;

  SubscriptionTimeline({
    required this.subscriptionId,
    required this.name,
    required this.amount,
    required this.frequency,
    required this.category,
    required this.status,
    required this.billingEvents,
    this.missedBillingCycles = 0,
    this.isPotentiallyCancelled = false,
  });

  factory SubscriptionTimeline.fromJson(Map<String, dynamic> json) {
    final events = (json['billingEvents'] ?? json['BillingEvents'] ?? []) as List;
    return SubscriptionTimeline(
      subscriptionId: int.tryParse((json['subscriptionId'] ?? json['SubscriptionId'] ?? 0).toString()) ?? 0,
      name: (json['name'] ?? json['Name'] ?? '').toString(),
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      frequency: SubscriptionFrequency.fromValue(json['frequency'] ?? json['Frequency']),
      category: SubscriptionCategory.fromValue(json['category'] ?? json['Category']),
      status: SubscriptionStatus.fromValue(json['status'] ?? json['Status']),
      billingEvents: events.map((e) => BillingEvent.fromJson(e as Map<String, dynamic>)).toList(),
      missedBillingCycles: int.tryParse((json['missedBillingCycles'] ?? json['MissedBillingCycles'] ?? 0).toString()) ?? 0,
      isPotentiallyCancelled: (json['isPotentiallyCancelled'] ?? json['IsPotentiallyCancelled']) as bool? ?? false,
    );
  }
}

/// Full detection response with metadata
class SubscriptionDetectionResponse {
  final List<DetectedSubscription> detectedSubscriptions;
  final int totalDetected;
  final int highConfidenceCount;
  final int pendingReviewCount;
  final bool isAIEnhanced;
  final DateTime detectionDate;

  SubscriptionDetectionResponse({
    required this.detectedSubscriptions,
    required this.totalDetected,
    required this.highConfidenceCount,
    required this.pendingReviewCount,
    required this.isAIEnhanced,
    required this.detectionDate,
  });

  factory SubscriptionDetectionResponse.fromJson(Map<String, dynamic> json) {
    final subs = (json['detectedSubscriptions'] ?? json['DetectedSubscriptions'] ?? []) as List;
    return SubscriptionDetectionResponse(
      detectedSubscriptions: subs.map((e) => DetectedSubscription.fromJson(e as Map<String, dynamic>)).toList(),
      totalDetected: int.tryParse((json['totalDetected'] ?? json['TotalDetected'] ?? 0).toString()) ?? 0,
      highConfidenceCount: int.tryParse((json['highConfidenceCount'] ?? json['HighConfidenceCount'] ?? 0).toString()) ?? 0,
      pendingReviewCount: int.tryParse((json['pendingReviewCount'] ?? json['PendingReviewCount'] ?? 0).toString()) ?? 0,
      isAIEnhanced: (json['isAIEnhanced'] ?? json['IsAIEnhanced']) as bool? ?? false,
      detectionDate: DateTime.parse((json['detectionDate'] ?? json['DetectionDate'] ?? DateTime.now().toIso8601String()).toString()),
    );
  }
}
