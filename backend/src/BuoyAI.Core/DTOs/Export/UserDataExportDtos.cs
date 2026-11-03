namespace BuoyAI.Core.DTOs.Export;

// Allow-list DTOs for the full data export. Entities are never serialized directly: a field
// only leaves the system if it is listed here. Secrets (password hash, TOTP secret, recovery
// codes, Apple refresh token, Stripe customer id, Plaid access token / item id, tokens, codes,
// passkeys, trusted devices, device push tokens) have no DTO field on purpose.

public record ExportUserDto(
    int Id, string Username, string Email, string FirstName, string LastName, string Currency,
    string? SignupCountry, string? PreferredLanguage, int? WeekStartDay,
    DateTime CreatedAt, DateTime UpdatedAt, bool IsEmailVerified, bool HasMfaEnabled,
    string? SubscriptionTier, DateTime? SubscriptionStartDate, DateTime? SubscriptionEndDate,
    DateTime? TrialStartDate, DateTime? TrialEndDate, int? FamilyId,
    string AiInteractionMode, string AiAutonomyLevel, bool AiMemoryEnabled,
    DateTime? AiConsentGrantedAt, DateTime? AiConsentWithdrawnAt, int? AiConsentVersion,
    bool ExcludePendingFromBudget, bool ReattributeOnAccountAssociationChange);

public record ExportAccountDto(
    int Id, int OwnerUserId, bool Shared, int? FamilyId, int? AssociatedUserId, string Name, string OfficialName, string? Nickname,
    string? Mask, string Type, string Subtype, decimal CurrentBalance, decimal AvailableBalance,
    string Currency, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);

public record ExportTransactionDto(
    int Id, int AccountId, bool Shared, DateOnly Date, DateOnly? AuthorizedDate, decimal Amount, string Type,
    string Description, string MerchantName, string? Nickname, int? CategoryId, bool IsUserCategorized,
    string? CategorySource, string? PlaidCategoryPrimary, string? PlaidCategoryDetailed,
    string? PaymentChannel, string? LocationCity, string? LocationRegion, string? OriginalDescription,
    string? IsoCurrencyCode, bool IsPending, int? BudgetId, int? SavingsGoalId, string? UserNotes,
    bool IsSplit, int? ParentTransactionId, DateTime CreatedAt, DateTime UpdatedAt,
    int? ClaimedByUserId, int? PersonalUserCategoryId, int? AiSuggestedCategoryId, double? AiConfidence,
    DateTime? AiCategorizationDate, bool NeedsReview, DateTime? SkipUntil, string? SemanticTags,
    int? CoveringTransferTransactionId, DateTime? TransferReviewedAt);

public record ExportBudgetDto(
    int Id, int OwnerUserId, bool Shared, int? FamilyId, string Name, string Period,
    DateTime CurrentPeriodStart, DateTime CurrentPeriodEnd, bool AutoReset, decimal TotalIncome,
    decimal TotalExpenses, bool IsActive, int? ParentBudgetId, decimal? AllocatedAmount, string Type,
    bool IsPrimary, string RolloverMode, int? DefaultSavingsGoalId, DateTime CreatedAt, DateTime UpdatedAt);

public record ExportBudgetCategoryDto(
    int Id, int BudgetId, int? CategoryId, int? GroupId, int SortOrder, bool IsIncome,
    decimal AllocatedAmount, decimal? DefaultAllocatedAmount, bool AllowRollover, decimal RolloverBalance,
    bool IsCountUpEnabled, decimal DailyAllowance, decimal EarnedToDate, int? SavingsGoalId,
    bool IsAllowance, int? LinkedBudgetId, int? AssignedUserId, decimal? MaxRolloverMultiplier,
    int? RolloverOverflowSavingsGoalId, DateTime LastAllowanceCalculation, string? CountUpRolloverMode,
    DateTime CreatedAt, DateTime UpdatedAt);

public record ExportBudgetTransferDto(
    int Id, int BudgetId, int CreatedByUserId, int? SourceCategoryId, int? SourceSavingsGoalId,
    int? DestinationCategoryId, int? DestinationSavingsGoalId, decimal Amount, string? Note,
    string Reason, DateTime CreatedAt);

public record ExportCategoryDto(
    int Id, string Name, string Color, string? Icon, bool IsIncome, bool IsSystem, int SortOrder,
    string? Description, int? OwnerUserId, bool Shared, int? FamilyId);

public record ExportCategoryGroupDto(
    int Id, string Name, string? Icon, int SortOrder, int? OwnerUserId, bool Shared, int? FamilyId);

public record ExportCategorizationRuleDto(
    int Id, int OwnerUserId, bool Shared, int? FamilyId, string Name, string Source, bool IsActive,
    int Priority, string? MerchantContains, string? MerchantExact, string? DescriptionContains,
    decimal? MinAmount, decimal? MaxAmount, int? AssignCategoryId, int TimesApplied,
    DateTime? LastAppliedAt, DateTime CreatedAt);

public record ExportSavingsGoalDto(
    int Id, int OwnerUserId, bool Shared, int? FamilyId, string Type, string Name, string Description,
    decimal TargetAmount, decimal CurrentAmount, DateOnly? TargetDate, bool IsActive, bool IsCompleted,
    int? AccountId, DateTime CreatedAt, DateTime UpdatedAt);

public record ExportSavingsAllocationDto(
    int Id, int SavingsGoalId, int? BudgetId, decimal AllocatedAmount, decimal LeftoverBudget,
    DateOnly AllocationDate, string Source, DateTime CreatedAt);

public record ExportSubscriptionDto(
    int Id, int OwnerUserId, bool Shared, int? FamilyId, string Name, string Description, decimal Amount,
    string Frequency, DateOnly NextDueDate, DateOnly? LastBilledDate, string Status, string Category,
    bool IsDetected, string? MerchantName, DateTime CreatedAt, DateTime UpdatedAt);

public record ExportAiMessageDto(
    int Id, int ConversationId, string Role, string Content, string? Model, DateTime CreatedAt);

// Messages are written separately (nested under each conversation in JSON, ai_messages.csv in the zip).
public record ExportAiConversationDto(
    int Id, Guid SessionId, string Context, string? Title, DateTime StartedAt, DateTime LastMessageAt,
    bool IsActive);

public record ExportAiMemoryDto(
    int Id, string MemoryType, string MemoryKey, string MemoryValue, string DisplayText,
    decimal Confidence, DateTime LearnedAt, bool IsActive, DateTime CreatedAt);

public record ExportInsightDto(
    int Id, int OwnerUserId, bool Shared, int? FamilyId, string Context, string Type, string Title,
    string Message, string? ActionSuggestion, double Confidence, string Priority, bool IsDismissed,
    bool IsArchived, DateTime GeneratedAt);

public record ExportNotificationDto(
    int Id, string Type, string Title, string Message, bool IsRead, DateTime? ReadAt, DateTime CreatedAt);

public record ExportAchievementDto(
    int Id, string AchievementKey, string Title, string Description, string Category, int Points,
    DateTime EarnedAt, int TimesEarned, DateTime? RevokedAt);

public record ExportStreakDto(
    int Id, string StreakType, int CurrentCount, int BestCount, DateTime LastIncrementDate,
    DateTime StreakStartDate);

public record ExportChallengeDto(
    int Id, string ChallengeType, string Title, string Description, decimal TargetValue,
    decimal CurrentValue, int RewardPoints, DateTime StartDate, DateTime EndDate, string Status,
    DateTime? CompletedAt);

public record ExportConsentDto(
    int Id, string ConsentType, string ConsentVersion, bool HasConsented, DateTime ConsentedAt);

public record ExportEmailPreferencesDto(
    bool PlaidConnectionIssues, bool PlaidReconnectionSuccess, bool SubscriptionChanges,
    bool TrialExpirationWarnings, bool BudgetAlerts, bool SavingsGoalMilestones,
    bool LargeTransactionAlerts, bool MonthlySummary, bool FeatureAnnouncements,
    bool TipsAndBestPractices, bool ProductUpdates, bool PromotionalOffers, bool ReferralInvites,
    bool ProductAnnouncements, bool BadgeEarned, bool MilestoneReached, bool StreakNotifications);

public record ExportPushPreferencesDto(
    bool PushEnabled, bool ConnectionIssues, bool BudgetAlerts, bool BillReminders,
    bool SubscriptionDetection, bool TransactionReview, bool QuietHoursEnabled,
    int QuietHoursStartMinutes, int QuietHoursEndMinutes, string? TimeZoneId);

// Sign-in security is exported as enabled yes/no, the authenticator method name and whether a passkey
// is registered, never a secret. A passkey-only account reads Enabled true, Method None, PasskeyRegistered true.
public record ExportMfaDto(bool Enabled, string Method, bool PasskeyRegistered);

public record ExportPreferencesDto(
    ExportEmailPreferencesDto? Email, ExportPushPreferencesDto? Push, ExportMfaDto Mfa);

public record ExportFamilyMemberDto(int UserId, string Role, DateTime JoinedAt, bool IsActive);

public record ExportFamilyDto(
    int Id, string Name, DateTime CreatedAt, List<ExportFamilyMemberDto> Members);

public record ExportBudgetShareDto(
    int Id, int BudgetId, int SharedWithUserId, bool CanEdit, bool CanViewTransactions, DateTime CreatedAt);

public record ExportBudgetPeriodSnapshotDto(
    int Id, int BudgetId, DateOnly PeriodStart, DateOnly PeriodEnd, decimal TotalAllocated, decimal TotalSpent,
    decimal TotalIncome, decimal TotalRolloverIn, decimal TotalRolloverOut, DateTime CreatedAt);

public record ExportBudgetPeriodCategorySnapshotDto(
    int Id, int BudgetPeriodSnapshotId, int? BudgetCategoryId, int CategoryId, string CategoryName,
    decimal AllocatedAmount, decimal SpentAmount, decimal RolloverIn, decimal RolloverOut,
    bool AllowRollover, bool IsIncome);

public record ExportUserCategoryDto(
    int Id, string Name, string Color, string? Icon, bool IsIncome, bool IsPersonal, string? Description,
    int SortOrder, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);

public record ExportSubscriptionTransactionDto(int Id, int SubscriptionId, int TransactionId, DateTime CreatedAt);

public record ExportSuggestedSubscriptionDto(
    int Id, int OwnerUserId, bool Shared, int? FamilyId, string NormalizedMerchantName,
    string DisplayMerchantName, decimal Amount, string Frequency, double Confidence, string Status,
    DateTime? SnoozeUntil, string? AiSuggestedCategory, string? AiInsight, int MatchedTransactionCount,
    DateOnly? LastTransactionDate, DateOnly? PredictedNextDate, DateTime DetectedAt, DateTime? ActionTakenAt,
    string? DismissReason, int? ConfirmedSubscriptionId, DateTime CreatedAt);

public record ExportDismissedSubscriptionPatternDto(
    int Id, string MerchantName, decimal? AmountMin, decimal? AmountMax, DateTime DismissedAt,
    string? DismissReason, bool IsPermanent);

public record ExportOnboardingQuizDto(
    int Id, string? PrimaryFinancialGoal, string? BiggestChallenge, string? SpendingPersonality,
    string? SubscriptionAwareness, string? SavingsBarriers, string? PreferredInsightType,
    string? FinancialExperience, bool HasUsedBudgetingApps, string? AdditionalNotes, int Version,
    DateTime CreatedAt, DateTime UpdatedAt);

public record ExportVisualizationDto(
    int Id, int OwnerUserId, bool Shared, int? FamilyId, string ChartType, string Title, string Message,
    string? MotivationalMessage, string ChartData, string? ChartConfig, string Category, string Priority,
    double Confidence, bool IsStarred, bool IsDismissed, DateTime GeneratedAt, DateTime CreatedAt);

public record ExportAiToolExecutionDto(
    int Id, int MessageId, Guid SessionId, string ToolName, string? Arguments, string? Result,
    bool WasConfirmed, bool? Success, int? DurationMs, DateTime ExecutedAt);

public record ExportAiActionFeedbackDto(
    int Id, int ToolExecutionId, bool? ThumbsUp, string? Comment, DateTime CreatedAt, DateTime? UpdatedAt);

// Bank connection metadata only, for the connection owner only. Access token, item id and the reconnect link are never exported.
public record ExportPlaidConnectionDto(
    int Id, int OwnerUserId, int? FamilyId, string Provider, string InstitutionId,
    string InstitutionName, bool IsActive, string Status, DateTime LastSyncAt, DateTime CreatedAt,
    DateTime? LastTransactionSyncAt, DateTime? DisconnectDate, bool RequiresUserAction);

// Referrals the caller made. The referred person's user id is deliberately left out.
public record ExportReferralDto(int Id, string ReferralCode, string RewardStatus, DateTime CreatedAt);

// BuoyAI plan and payment history (own only). Stripe and store ids are never exported.
public record ExportUserSubscriptionDto(
    int Id, string Tier, string BillingCycle, decimal Amount, DateTime StartDate, DateTime? EndDate,
    DateTime NextBillingDate, string Status, string? PromoCode, string Provider, decimal? DiscountAmount,
    DateTime CreatedAt);

// The products the plan includes (own only). Provider ids are never exported.
public record ExportUserEntitlementDto(
    int Id, string Entitlement, int Quantity, string Provider, string Status, DateTime? PaidThrough,
    DateTime? EndsAt, bool Founding, DateTime CreatedAt);

public record ExportSubscriptionPaymentDto(
    int Id, int UserSubscriptionId, decimal Amount, string Status, DateTime PaymentDate,
    DateTime? FailureDate, string? FailureReason, DateTime CreatedAt);

/// <summary>A receipt the user scanned: what was read from it, what it matched, and the split offered or applied.</summary>
public record ExportReceiptScanDto(
    int Id, string Status, string? Retailer, string? OrderNumber, DateTime? OrderDate, decimal? Total,
    string? ParsedJson, string? ProposalJson, int? TransactionId, string? Error,
    DateTime ReceivedAt, DateTime? MatchedAt, DateTime? AppliedAt);
