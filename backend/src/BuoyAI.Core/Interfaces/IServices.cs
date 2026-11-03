using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.DTOs.Subscription;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

public interface IAuthService
{
  Task<LoginResponse?> LoginAsync(LoginRequest request);
  Task<LoginResponse?> RegisterAsync(RegisterRequest request);
  Task<LoginResponse?> GoogleLoginAsync(GoogleLoginRequest request);
  Task<LoginResponse?> AppleLoginAsync(AppleLoginRequest request);

  /// <summary>
  /// Single sign-on (self-host): finds the account for a verified provider identity, by its
  /// issuer + subject link first and then by verified email (which creates the link). Creates an
  /// account only for the server's admin email, an invite code, or, when
  /// <paramref name="autoRegister"/> is on, anyone the registration rules let in. Throws
  /// <see cref="BuoyAI.Core.Exceptions.OidcLoginException"/> when refused.
  /// </summary>
  Task<OidcResolvedUser> ResolveOidcUserAsync(OidcIdentity identity, bool autoRegister, string? inviteCode);

  /// <summary>Access token and profile for a user who already proved who they are (single sign-on).
  /// Null when the user is gone. The caller issues the refresh token.</summary>
  Task<LoginResponse?> IssueLoginForUserAsync(int userId, bool isNewUser);

  /// <summary>Rotates a refresh token and issues a fresh access + refresh token, or
  /// null if the refresh token is invalid/expired/revoked/reused.</summary>
  Task<LoginResponse?> RefreshTokenAsync(string refreshToken);
  Task<UserProfileDto?> GetUserProfileAsync(int userId);
  string GenerateJwtToken(int userId, string username);
  
  /// <summary>
  /// Generates JWT token with subscription tier claim for tiered rate limiting
  /// </summary>
  string GenerateJwtTokenWithClaims(int userId, string username, SubscriptionTier? subscriptionTier);

  /// <summary>The tier for a user's JWT claim: the stored tier when hosted, Premium or Admin on self-host.</summary>
  SubscriptionTier? ResolveTierClaim(BuoyAI.Core.Entities.User user);
  
  // Device Trust & Enhanced Auth Methods
  /// <summary>
  /// Validates user credentials without generating token (for device trust flow)
  /// </summary>
  Task<UserProfileDto?> ValidateCredentialsAsync(LoginRequest request);
  
  /// <summary>
  /// Gets user by email for device trust evaluation
  /// </summary>
  Task<UserProfileDto?> GetUserByEmailAsync(string email);
  
  /// <summary>
  /// Generates temporary MFA token for verification
  /// </summary>
  Task<string> GenerateMfaTokenAsync(int userId, string mfaMethod = "unknown");

  /// <summary>
  /// Fully validates a pending-MFA token — signature, issuer, audience, lifetime, and the
  /// <c>type=mfa_pending</c> claim — using the same parameters as
  /// <see cref="CompleteMfaLoginAsync"/>. Returns the token's user id, or null if the token
  /// is forged, tampered, expired, or not an MFA-pending token. Callers must use this rather
  /// than merely decoding the JWT, so an unsigned/forged token can't drive user-scoped work.
  /// </summary>
  int? ValidateMfaToken(string mfaToken);

  /// <summary>
  /// Validates MFA token and generates full auth token
  /// </summary>
  Task<LoginResponse?> CompleteMfaLoginAsync(string mfaToken, string mfaCode);
  
  // Password Reset Methods
  /// <summary>
  /// Initiates password reset by sending reset link via email
  /// </summary>
  /// <returns>Always returns true to prevent email enumeration</returns>
  Task<bool> RequestPasswordResetAsync(string email, string? ipAddress = null, string? userAgent = null);
  
  /// <summary>
  /// Validates a password reset token
  /// </summary>
  Task<bool> ValidatePasswordResetTokenAsync(string token);
  
  /// <summary>
  /// Completes password reset with new password
  /// </summary>
  Task<bool> ResetPasswordAsync(string token, string newPassword);
  
  /// <summary>
  /// Permanently deletes a user account and all associated data.
  /// Cancels subscriptions, disconnects bank accounts, and removes all personal data.
  /// </summary>
  Task<bool> DeleteAccountAsync(int userId, string? password = null);

  /// <summary>
  /// Verifies a plaintext password against the given user's stored BCrypt hash WITHOUT minting
  /// tokens, creating a session, or rotating refresh tokens. Returns false (never throws) for a
  /// wrong password, a missing/deleted user, or an account with no password (Google/Apple/MFA-only).
  /// </summary>
  Task<bool> VerifyPasswordAsync(int userId, string password);
}

public interface IPlaidService
{
  Task<CreateLinkTokenResponse> CreateLinkTokenAsync(CreateLinkTokenRequest request);
  Task<ExchangePublicTokenResponse> ExchangePublicTokenAsync(ExchangePublicTokenRequest request);
  Task<ExchangePublicTokenResponse> ExchangePublicTokenWithQuickSyncAsync(ExchangePublicTokenRequest request);
  Task<IEnumerable<AccountDto>> GetAccountsAsync(int userId);
  Task<IEnumerable<TransactionDto>> GetTransactionsAsync(int userId, DateTime? startDate = null, DateTime? endDate = null);
  Task<TransactionSyncResult> SyncTransactionsAsync(int userId);
  Task<int> SyncRecentTransactionsAsync(string accessToken, DateTime fromDate, DateTime toDate);
  Task CleanupStalePendingTransactionsAsync(int userId, int staleDaysThreshold = 8);
  /// <summary>Deletes transactions Plaid reported as removed (TRANSACTIONS_REMOVED), preserving
  /// any rows the user has touched. Returns the number of rows deleted.</summary>
  Task<int> RemoveTransactionsAsync(IEnumerable<string> plaidTransactionIds);
  
  // New methods for accounts management
  Task<ExchangePublicTokenResponse> ExchangePublicTokenWithImportAsync(ImportHistoricalDataRequest request);
  Task ImportHistoricalDataAsync(int userId, string itemId, HistoricalDataRange range, DateTime? customStartDate = null, DateTime? customEndDate = null);
  Task ImportHistoricalDataAsync(string accessToken, DateTime startDate, DateTime endDate, string? itemId = null);
  Task DisconnectAccountAsync(int userId, int connectionId);
  /// <summary>
  /// Best-effort revoke of the Plaid Item behind <paramref name="connection"/> (/item/remove),
  /// which stops ongoing Transactions billing. Used by unattended jobs (e.g. churned-account
  /// cleanup) that have no request user to scope against. Returns true when the item is gone at
  /// Plaid (removed now, or already absent / token empty); false only on a transient failure the
  /// caller should retry. Never throws and never mutates the connection row (the caller owns that).
  /// </summary>
  Task<bool> TryRemovePlaidItemAsync(PlaidConnection connection, CancellationToken cancellationToken = default);
  /// <summary>Begins a genuine reconnect for a broken item and returns a Plaid update-mode link
  /// token. The item is only reactivated once re-auth completes via <see cref="HandleItemUpdateAsync"/>;
  /// this never optimistically marks the item Active with the stale token.</summary>
  Task<string> ReconnectAccountAsync(int userId, int connectionId, string? platform = null);
  Task<AccountConnectionStatusDto> GetConnectionStatusAsync(int userId, int connectionId);
  
  // Update Mode methods
  Task<string> CreateUpdateTokenAsync(int userId, int connectionId, bool accountSelectionEnabled = false, string? platform = null);

  /// <summary>
  /// Sets the given webhook URL on every active Plaid item via /item/webhook/update.
  /// One-time repair for items created while Plaid:WebhookUrl was unset (2026-07-18).
  /// Returns (updated, failed) counts.
  /// </summary>
  Task<(int Updated, int Failed)> BackfillWebhookUrlsAsync();
  Task<bool> HandleItemUpdateAsync(int connectionId, string publicToken);
  Task UpdateConnectionStatusAsync(int connectionId, PlaidItemStatus status, string? errorMessage = null);
  /// <summary>Classifies a background/polling sync failure and transitions the connection out of
  /// Active (LoginRequired or Error) with a reconnect notification when appropriate, so broken
  /// items stop being re-synced forever.</summary>
  Task HandleSyncFailureAsync(int connectionId, string errorMessage);
  Task<PlaidConnection?> GetConnectionByItemIdAsync(string itemId);
  Task HandlePendingExpirationAsync(int connectionId, DateTime expirationDate);
  Task HandleUserPermissionRevokedAsync(int connectionId);
}

public interface IBudgetService
{
  // Single budget operations
  Task<BudgetDto?> GetActiveBudgetAsync(int userId);
  Task<BudgetDto?> GetBudgetByIdAsync(int budgetId, int userId);
  Task<BudgetDto> CreateBudgetAsync(int userId, CreateBudgetRequest request);
  Task<BudgetDto> UpdateBudgetAsync(int budgetId, int userId, CreateBudgetRequest request);
  Task<BudgetCategoryDto> UpdateBudgetCategoryAsync(int categoryId, UpdateBudgetCategoryRequest request);
  Task DeleteBudgetAsync(int budgetId, int userId);
  
  /// <summary>
  /// Get a budget category by ID (used for authorization checks)
  /// </summary>
  Task<BudgetCategoryDto?> GetBudgetCategoryAsync(int categoryId);
  
  // Multi-budget operations
  Task<IEnumerable<BudgetDto>> GetBudgetsAsync(int userId);
  Task<IEnumerable<BudgetDto>> GetAccessibleBudgetsAsync(int userId);
  Task<BudgetDto?> GetPrimaryBudgetAsync(int userId);
  Task SetPrimaryBudgetAsync(int budgetId, int userId);
  
  // Family budget operations
  Task<IEnumerable<BudgetDto>> GetMyFamilyBudgetsAsync(int userId);
  Task<IEnumerable<BudgetDto>> GetFamilyBudgetsAsync(int familyId, int userId);
  Task<FamilyBudgetOverviewDto> GetFamilyBudgetOverviewAsync(int userId);
  Task<BudgetDto> CreateFamilyBudgetAsync(int userId, int familyId, CreateBudgetRequest request);
  Task<BudgetDto> CreateAllowanceBudgetAsync(int userId, int parentBudgetId, int assignedUserId, CreateBudgetRequest request);

  /// <summary>
  /// Spawn a Type=Personal child budget from an existing family expense category (RFC #793).
  /// Re-purposes the EXISTING category as the funding link (IsAllowance + LinkedBudgetId +
  /// AssignedUserId) keeping its real CategoryId so existing transactions stay put. The caller
  /// must be Owner/BudgetAdmin of the family that owns the category. Throws
  /// <see cref="InvalidOperationException"/> if the category is already linked or is not a
  /// family category.
  /// </summary>
  Task<BudgetDto> SpawnPersonalBudgetFromCategoryAsync(int userId, int categoryId, SpawnPersonalBudgetRequest request);

  Task<BudgetDto> CreateChildBudgetAsync(int userId, int parentBudgetId, CreateChildBudgetRequest request);
  Task<BudgetDto> UpdateChildBudgetAsync(int budgetId, int userId, UpdateChildBudgetRequest request);
  
  // Budget sharing
  Task<BudgetShareDto> ShareBudgetAsync(int budgetId, int userId, int shareWithUserId, bool canEdit, bool canViewTransactions);
  Task RemoveBudgetShareAsync(int budgetId, int userId, int shareWithUserId);
  Task<IEnumerable<BudgetShareDto>> GetBudgetSharesAsync(int budgetId, int userId);
  
  // Transaction claiming (for family budgets)
  Task ClaimTransactionAsync(int transactionId, int userId);
  Task UnclaimTransactionAsync(int transactionId, int userId);
  
  // Period history (multi-month navigation)
  Task<IEnumerable<BudgetPeriodListItemDto>> GetBudgetPeriodsAsync(int budgetId, int userId);
  Task<BudgetPeriodDetailDto?> GetBudgetPeriodDetailAsync(int budgetId, int snapshotId, int userId);
  Task<BudgetPeriodCategoryHistoryDto> UpdatePeriodCategoryAmountAsync(int budgetId, int snapshotId, int categorySnapshotId, decimal allocatedAmount, int userId);
}

public interface ICountUpBudgetService
{
  Task<CountUpBudgetDto> CalculateCountUpStatusAsync(int budgetCategoryId);
  Task<CountUpBudgetDto> UpdateDailyAllowanceAsync(int budgetCategoryId);
  Task<IEnumerable<CountUpBudgetDto>> GetAllCountUpCategoriesAsync(int userId);
  Task<IEnumerable<CountUpBudgetDto>> GetCountUpCategoriesForBudgetAsync(int budgetId, int userId);
  Task<CountUpBudgetDto> EnableCountUpBudgetingAsync(int categoryId, bool enable);
  Task<CountUpBudgetDto> UpdateCountUpSettingsAsync(int categoryId, CountUpSettingsRequest settings);
}

public interface ITransactionService
{
  Task<IEnumerable<TransactionDto>> GetTransactionsAsync(int userId, TransactionFilterRequest filter);
  Task<TransactionResponseDto> GetTransactionsWithPaginationAsync(int userId, TransactionFilterRequest filter);
  Task<TransactionSummaryDto> GetTransactionSummaryAsync(int userId, DateTime startDate, DateTime endDate);

  /// <summary>
  /// Actual money in / out / net for a period, with credit-card payments and internal transfers
  /// excluded from both sides and refunds netted (see <see cref="Core.Analysis.CashFlowCalculator"/>).
  /// The honest answer to "what did I make and spend", as opposed to budget allocations.
  /// </summary>
  Task<Core.Analysis.CashFlowSummary> GetCashFlowSummaryAsync(int userId, DateTime startDate, DateTime endDateExclusive);
  Task<IEnumerable<CategoryStatisticsDto>> GetCategoryStatisticsAsync(int userId, DateTime startDate, DateTime endDate);
  Task<TransactionDto> UpdateTransactionCategoryAsync(int transactionId, UpdateTransactionCategoryRequest request, int userId);

  /// <summary>
  /// Same as <see cref="UpdateTransactionCategoryAsync"/> but queues the notification, gamification and
  /// embedding side effects into <paramref name="afterCommit"/> instead of running them, for callers
  /// that hold an open database transaction and run them once it commits.
  /// </summary>
  Task<TransactionDto> UpdateTransactionCategoryDeferringSideEffectsAsync(
    int transactionId,
    UpdateTransactionCategoryRequest request,
    int userId,
    ICollection<Func<Task>> afterCommit
  );
  Task ResetAndResyncTransactionsAsync(int userId);
  
  /// <summary>
  /// Update the user notes for a transaction
  /// </summary>
  Task<TransactionDto> UpdateTransactionNotesAsync(int transactionId, string? notes, int userId);

  /// <summary>
  /// Create a manual transaction entered by the user (not from Plaid or file import).
  /// </summary>
  Task<TransactionDto> CreateManualTransactionAsync(CreateTransactionRequest request, int userId);

  /// <summary>
  /// Split a transaction into multiple categories
  /// </summary>
  Task<TransactionDto> SplitTransactionAsync(int transactionId, SplitTransactionRequest request, int userId);

  /// <summary>
  /// Renames a transaction. Null/empty clears the user's name so the bank's own text shows again.
  /// </summary>
  Task<TransactionDto> UpdateTransactionNicknameAsync(int transactionId, string? nickname, int userId);
  
  /// <summary>
  /// Remove splits from a transaction and restore the original
  /// </summary>
  Task<TransactionDto> UnsplitTransactionAsync(int transactionId, int userId);
  
  /// <summary>
  /// Get a single transaction by ID with split details
  /// </summary>
  Task<TransactionDto?> GetTransactionByIdAsync(int transactionId, int userId);
  
  /// <summary>
  /// Delete a transaction (user-initiated, not from Plaid sync)
  /// </summary>
  Task DeleteTransactionAsync(int transactionId, int userId);
  
  /// <summary>
  /// Update the date of a transaction
  /// </summary>
  Task<TransactionDto> UpdateTransactionDateAsync(int transactionId, DateTime newDate, int userId);
  
  /// <summary>
  /// Link or unlink a transaction to/from a savings goal
  /// </summary>
  Task<TransactionDto> LinkToSavingsGoalAsync(int transactionId, int? savingsGoalId, int userId);

  /// <summary>
  /// For an expense earmarked to a goal that lives in a real account: the pending draws on that
  /// goal and the recent money-out transactions on the account that could have moved the money.
  /// Throws InvalidOperationException when the transaction is not such a draw.
  /// </summary>
  Task<CoveringTransferCandidatesDto> GetCoveringTransferCandidatesAsync(int transactionId, int userId);

  /// <summary>Tie an earmarked draw (and optionally every other pending draw on its goal) to the transfer that moved the money.</summary>
  Task<TransactionDto> LinkCoveringTransferAsync(int transactionId, LinkCoveringTransferRequest request, int userId);

  /// <summary>Put a draw back in flight.</summary>
  Task<TransactionDto> UnlinkCoveringTransferAsync(int transactionId, int userId);

  /// <summary>
  /// Mark a transaction as reviewed for transfer detection (the user dismissed it as
  /// "not a transfer"). Sets a dedicated timestamp WITHOUT touching IsUserCategorized /
  /// NeedsReview, so a dismissed suggestion stops being re-suggested but the transaction
  /// stays in the normal categorization / AI-review queue.
  /// </summary>
  Task MarkTransferReviewedAsync(int transactionId, int userId);
}

public interface IAIService
{
  /// <summary>
  /// One-shot text generation from a prompt (no user context / tools). Used by the admin
  /// broadcast draft helper (#552) to draft a subject + body for review — never auto-sent.
  /// </summary>
  Task<string> GenerateTextAsync(string prompt);

  Task<AIQueryResponse> ProcessQueryAsync(int userId, AIQueryRequest request);
  
  /// <summary>
  /// Process chat with streaming, function calling, and multi-turn conversation support.
  /// This is the unified chat endpoint that handles text, tool calls, and confirmations.
  /// </summary>
  IAsyncEnumerable<ChatStreamEvent> ProcessChatStreamWithToolsAsync(int userId, AIChatRequest request);
  
  /// <summary>
  /// Upload a file to Gemini Files API for use in chat.
  /// Validates file type and size before uploading.
  /// </summary>
  Task<ChatFileUploadResponse> UploadChatFileAsync(int userId, Stream fileStream, string fileName, string mimeType);
  
  Task<List<AIInsightDto>> GenerateContextualInsightsAsync(int userId, AIInsightRequest request);
  Task<List<AIInsightDto>> GetCachedOrEnqueueInsightsAsync(int userId, AIInsightRequest request);
  Task<AIInsightDto> GenerateSpecificInsightAsync(int userId, AIInsightRequest request);
  Task<bool> DismissInsightAsync(int userId, string insightId);
  Task<AIUsageStats> GetUsageStatsAsync(int userId);
  Task<List<AIInsightDto>> RefreshContextualInsightsAsync(int userId, string context, int maxInsights = 5);
}

/// <summary>
/// Service for AI-powered transaction categorization
/// </summary>
public interface IAICategorizationService
{
  /// <summary>
  /// Categorize a single transaction using AI
  /// </summary>
  Task<AICategorizationResult> CategorizeTransactionAsync(int transactionId, int userId);
  
  /// <summary>
  /// Batch categorize multiple transactions using AI
  /// </summary>
  Task<AICategorizationResponse> BatchCategorizeAsync(int userId, AICategorizationRequest request);
  
  /// <summary>
  /// Get statistics about transactions needing review
  /// </summary>
  Task<ReviewStatisticsDto> GetReviewStatisticsAsync(int userId);
  
  /// <summary>
  /// Get transactions needing review, sorted by AI confidence (lowest first)
  /// </summary>
  Task<List<TransactionDto>> GetTransactionsNeedingReviewAsync(int userId, int? limit = null, ReviewScope scope = ReviewScope.All);
  
  /// <summary>
  /// Accept an AI-suggested category (marks as user-categorized)
  /// </summary>
  Task<TransactionDto> AcceptAISuggestionAsync(int transactionId, int userId);
  
  /// <summary>
  /// Reject AI suggestion and provide the correct category. Accepts a unified
  /// CategoryId (system 1-99 or custom 1000+) so the review flow can reach
  /// user-created custom categories; the id is validated before assignment.
  /// </summary>
  Task<TransactionDto> RejectAISuggestionAsync(int transactionId, int userId, int correctCategoryId);
  
  /// <summary>
  /// Skip a transaction from review for a specified number of days (default: 2)
  /// </summary>
  Task<TransactionDto> SkipTransactionReviewAsync(int transactionId, int userId, int days = 2);

  /// <summary>
  /// Permanently clears the review flag on every transaction dated before the current
  /// month that is still waiting for review ("forget the backlog"). Returns how many were
  /// dismissed. The cutoff is fixed server-side to the same boundary the banner reports as
  /// "from earlier months", so this month's transactions are never affected.
  /// </summary>
  Task<int> DismissOlderReviewsAsync(int userId);
  
  /// <summary>
  /// Add transactions to the AI categorization queue for background processing. Ingest
  /// paths go through <see cref="ITransactionIngestCategorizer"/>, which applies the
  /// rules-then-Plaid-then-AI policy before calling this.
  /// </summary>
  Task QueueTransactionsForCategorizationAsync(int userId, List<int> transactionIds);
  
  /// <summary>
  /// Process queued transactions for a user (batch of up to maxBatchSize)
  /// Returns the number of transactions processed
  /// </summary>
  Task<int> ProcessCategorizationQueueAsync(int userId, int maxBatchSize = 20);
  
  /// <summary>
  /// Get the count of pending queue items for a user
  /// </summary>
  Task<int> GetQueuedTransactionCountAsync(int userId);
}

public interface IAIUsageTracker
{
  Task<bool> CanMakeRequestAsync(int userId, SubscriptionTier tier);

  /// <summary>
  /// Records a metered AI/tool request against the user's quota and monthly cost.
  /// <paramref name="requestContext"/> tags the source ("chat" for first-party AI chat,
  /// "mcp" for BuoyAI Connect tool calls); it is persisted on the usage record so the two
  /// surfaces can be told apart. Defaults to "chat" so existing callers are unaffected.
  /// </summary>
  Task RecordUsageAsync(int userId, string modelUsed, int tokensUsed, decimal cost, string requestContext = "chat");

  /// <summary>
  /// Records one call with the provider's own input and output token counts (no 70/30 guess), the provider that
  /// answered, and whether the counts are a guess. Best-effort: a failure is logged, never thrown.
  /// </summary>
  Task RecordCallAsync(BuoyAI.Core.DTOs.AI.AiUsageEntry entry);
  Task<AIUsageStats> GetDailyUsageAsync(int userId);
  Task<AIUsageStats> GetMonthlyUsageAsync(int userId);
  
  /// <summary>
  /// Check if user is approaching or at their monthly cost limit.
  /// Returns: (canProceed, warningLevel) where warningLevel is null, "warning" (80%), or "blocked" (100%)
  /// </summary>
  Task<(bool CanProceed, string? WarningLevel, decimal UsagePercent)> CheckMonthlyLimitAsync(int userId, SubscriptionTier tier);
}

public interface ICostTrackingService
{
  Task<decimal> GetUserDailyCostAsync(int userId);
  Task<bool> CanUserMakeRequestAsync(int userId, int estimatedTokens);
  Task RecordAPIUsageAsync(int userId, string model, int inputTokens, int outputTokens, decimal cost);
  Task<AIUsageStats> GetUsageStatsAsync(int userId);

  /// <summary>
  /// Dollar cost of one call from the maintained per-model pricing table, given the
  /// provider's own token counts. Unknown models fall back to the default row.
  /// </summary>
  decimal CalculateCost(string model, int inputTokens, int outputTokens);
}

public interface IEncryptionService
{
  /// <summary>Writes the current authenticated format (version prefix "v2:").</summary>
  string Encrypt(string plainText);

  /// <summary>Reads the current format and the legacy (unprefixed AES-CBC) format.</summary>
  string Decrypt(string cipherText);

  /// <summary>True when the value carries the current version prefix. False for empty or legacy values.</summary>
  bool IsCurrentFormat(string? cipherText);

  /// <summary>True when Encrypt writes the current format (Encryption:WriteV2). Columns that were plaintext before v2 only get encrypted once this is true.</summary>
  bool WritesCurrentFormat { get; }
}

public interface ISubscriptionService
{
  // CRUD Operations
  Task<IEnumerable<SubscriptionDto>> GetSubscriptionsAsync(int userId);
  Task<SubscriptionDto?> GetSubscriptionByIdAsync(int userId, int subscriptionId);
  Task<SubscriptionDto> CreateSubscriptionAsync(int userId, CreateSubscriptionRequest request);
  Task<SubscriptionDto> UpdateSubscriptionAsync(int userId, int subscriptionId, UpdateSubscriptionRequest request);
  Task DeleteSubscriptionAsync(int userId, int subscriptionId);
  
  // Auto-Detection (with AI enhancement for Premium users)
  /// <param name="isOnDemand">
  /// True only for USER-INITIATED runs (the /detect endpoint and the Plaid-sync on-demand
  /// trigger). When true, the per-user on-demand cooldown clock is stamped. The weekly
  /// background job leaves this false so it never puts users into on-demand cooldown.
  /// </param>
  Task<SubscriptionDetectionResponse> DetectSubscriptionsAsync(int userId, SubscriptionDetectionRequest? request = null, bool isOnDemand = false);
  Task<SubscriptionDto> ConfirmDetectedSubscriptionAsync(int userId, DetectedSubscriptionDto detectedSubscription);
  Task DismissDetectedSubscriptionAsync(int userId, string merchantName, decimal? amount = null, string? reason = null);
  Task<IEnumerable<DetectedSubscriptionDto>> GetPendingDetectedSubscriptionsAsync(int userId);
  
  /// <summary>
  /// Trigger on-demand subscription detection for a user after Plaid sync.
  /// Subject to 7-day cooldown per user to avoid excessive processing.
  /// </summary>
  /// <returns>True if detection ran, false if cooldown is active</returns>
  Task<bool> TriggerOnDemandDetectionAsync(int userId);
  
  /// <summary>
  /// Check if on-demand detection is available for a user (not in cooldown period).
  /// </summary>
  Task<bool> CanRunOnDemandDetectionAsync(int userId);
  
  // Calendar and Analysis
  Task<IEnumerable<CalendarSubscriptionDto>> GetCalendarDataAsync(int userId, DateTime startDate, DateTime endDate);
  Task<SubscriptionSummaryDto> GetSubscriptionSummaryAsync(int userId);
  
  /// <summary>
  /// Get timeline data for subscriptions including past transactions and future projections.
  /// </summary>
  Task<IEnumerable<SubscriptionTimelineDto>> GetTimelineDataAsync(int userId, SubscriptionTimelineRequest? request = null);
  
  /// <summary>
  /// Get calendar billing events showing past, next, and future charges for all subscriptions.
  /// </summary>
  Task<IEnumerable<CalendarBillingEventDto>> GetCalendarBillingEventsAsync(int userId, DateTime startDate, DateTime endDate);
  
  /// <summary>
  /// Get subscriptions that appear to be cancelled (no charges in 2+ billing cycles).
  /// </summary>
  Task<IEnumerable<SubscriptionTimelineDto>> GetPotentiallyCancelledSubscriptionsAsync(int userId);
}

public interface IFamilyService
{
  // Family Management
  Task<FamilyDto> CreateFamilyAsync(int userId, CreateFamilyRequest request);
  Task<FamilyDto?> GetFamilyAsync(int userId);
  Task<FamilyDto?> GetFamilyByIdAsync(int familyId);
  Task<FamilyDto> UpdateFamilyAsync(int userId, UpdateFamilyRequest request);
  Task DeleteFamilyAsync(int userId);
  Task LeaveFamilyAsync(int userId);

  // Member Management
  Task<IEnumerable<FamilyMemberDto>> GetFamilyMembersAsync(int userId);
  Task<FamilyMemberDto> UpdateMemberRoleAsync(int userId, int targetUserId, FamilyMemberRole newRole);
  Task RemoveMemberAsync(int userId, int targetUserId);

  // Invitation Management
  Task<FamilyInvitationDto> InviteMemberAsync(int userId, InviteFamilyMemberRequest request);
  Task<IEnumerable<FamilyInvitationDto>> GetPendingInvitationsAsync(int userId);
  Task<FamilyInvitationDto?> GetInvitationByTokenAsync(string token);
  Task<FamilyMemberDto> AcceptInvitationAsync(int userId, string token);
  Task CancelInvitationAsync(int userId, int invitationId);
  Task DeclineInvitationAsync(int userId, string token);
  Task<IEnumerable<FamilyInvitationDto>> GetPendingInvitationsForUserAsync(int userId);

  // Permission Checking
  Task<bool> CanUserModifyFinancialDataAsync(int userId);
  Task<bool> CanUserManageFamilyAsync(int userId);
  Task<bool> CanUserManageBillingAsync(int userId);
  Task<FamilyMemberRole?> GetUserRoleInFamilyAsync(int userId);
  Task<bool> IsUserInFamilyAsync(int userId, int familyId);
  
  /// <summary>
  /// Updates subscription tiers for all family members when the owner's subscription changes.
  /// If owner upgrades to Premium, members get PremiumInherited.
  /// If owner downgrades or cancels, members lose PremiumInherited.
  /// </summary>
  Task UpdateFamilyMembersTierAsync(int ownerUserId, SubscriptionTier? newOwnerTier);
}

/// <summary>
/// Service for managing AI conversation history and feedback.
/// Provides conversation persistence, tool execution logging, and user feedback collection.
/// All operations are strictly user-isolated for privacy.
/// </summary>
public interface IAiConversationService
{
  /// <summary>
  /// Start a new conversation or get existing one by session ID.
  /// </summary>
  Task<AiConversationDto> StartOrGetConversationAsync(int userId, Guid sessionId, string context = "chat");
  
  /// <summary>
  /// Get a conversation by session ID for a specific user.
  /// Returns null if not found or user doesn't have access.
  /// </summary>
  Task<AiConversationDetailDto?> GetConversationAsync(int userId, Guid sessionId);
  
  /// <summary>
  /// Get recent conversations for a user, ordered by last message time.
  /// </summary>
  Task<List<AiConversationDto>> GetRecentConversationsAsync(int userId, int limit = 20);
  
  /// <summary>
  /// Add a message to a conversation.
  /// </summary>
  Task<int> AddMessageAsync(int conversationId, int userId, string role, string content, string? model = null, int? tokenCount = null);
  
  /// <summary>
  /// Log an MCP tool execution for auditing and feedback.
  /// </summary>
  Task<int> LogToolExecutionAsync(
    int messageId,
    int userId,
    Guid sessionId,
    string toolName,
    string? arguments,
    string? result,
    bool wasConfirmed,
    bool? success,
    int? durationMs);
  
  /// <summary>
  /// Submit feedback on a tool execution.
  /// </summary>
  Task<AiActionFeedbackDto> SubmitFeedbackAsync(int toolExecutionId, int userId, bool thumbsUp, string? comment = null);
  
  /// <summary>
  /// Update existing feedback on a tool execution.
  /// </summary>
  Task<AiActionFeedbackDto?> UpdateFeedbackAsync(int feedbackId, int userId, bool? thumbsUp, string? comment);
  
  /// <summary>
  /// Delete a conversation and all related data.
  /// </summary>
  Task<bool> DeleteConversationAsync(int userId, Guid sessionId);
  
  /// <summary>
  /// Update conversation title.
  /// </summary>
  Task<bool> UpdateConversationTitleAsync(int userId, Guid sessionId, string title);
}

/// <summary>
/// Service for gamification features (achievements, streaks, challenges)
/// </summary>
/// <summary>Double-sided referral loop: shareable codes, attribution, and reward tracking.</summary>
public interface IReferralService
{
  /// <summary>The caller's referral code + link + successful-referral count (code generated lazily).</summary>
  Task<BuoyAI.Core.DTOs.ReferralSummaryDto> GetSummaryAsync(int userId);

  /// <summary>Record that <paramref name="refereeUserId"/> joined via <paramref name="code"/>.
  /// Returns false if invalid, self-referral, or the referee was already referred.</summary>
  Task<bool> RedeemAsync(int refereeUserId, string code);
}

public interface IGamificationService
{
  /// <summary>
  /// Get gamification dashboard data for a user
  /// </summary>
  Task<GamificationDashboardDto> GetDashboardDataAsync(int userId);
  
  /// <summary>
  /// Get all achievements for a user
  /// </summary>
  Task<List<UserAchievementDto>> GetAchievementsAsync(int userId);

  /// <summary>
  /// Get the full achievement catalog (every definition) merged with the user's earned
  /// state. Locked "good" achievements are returned as masked silhouettes; cautionary
  /// achievements are always fully described.
  /// </summary>
  Task<List<AchievementCatalogItemDto>> GetAchievementCatalogAsync(int userId);

  /// <summary>
  /// Idempotently award a specific achievement to a user for an event-driven one-off that
  /// can't be re-derived from state (e.g. resetting rollover debt). No-op if already earned.
  /// </summary>
  Task AwardAsync(int userId, string achievementKey);

  /// <summary>
  /// Cautionary ("oops") badge history including revoked rows — repeat counts survive
  /// revocation and are the behavioral signal the AI advisor reasons about.
  /// </summary>
  Task<List<OopsBadgeHistoryDto>> GetOopsBadgeHistoryAsync(int userId);

  /// <summary>
  /// Advance the user's active challenge of the given type by <paramref name="amount"/>,
  /// marking it completed when the target is reached. No-op if there's no matching active
  /// challenge.
  /// </summary>
  Task IncrementChallengeProgressAsync(int userId, string challengeType, decimal amount = 1);

  /// <summary>
  /// Mark achievements as seen (no longer new)
  /// </summary>
  Task MarkAchievementsSeenAsync(int userId, List<string> achievementKeys);
  
  /// <summary>
  /// Get all streaks for a user
  /// </summary>
  Task<List<UserStreakDto>> GetStreaksAsync(int userId);
  
  /// <summary>
  /// Get active challenges for a user
  /// </summary>
  Task<List<UserChallengeDto>> GetActiveChallengesAsync(int userId);
  
  /// <summary>
  /// Dismiss (delete) a challenge by ID
  /// </summary>
  Task DismissChallengeAsync(int userId, int challengeId);
  
  /// <summary>
  /// Check and award achievements based on user activity
  /// </summary>
  Task CheckAndAwardAchievementsAsync(int userId);
  
  /// <summary>
  /// Update streaks based on daily activity
  /// </summary>
  Task UpdateStreaksAsync(int userId);

  /// <summary>
  /// Advance a personal activity streak ("login", "categorization", "savings_goal") for
  /// today. At most one increment per UTC day; uses the same freeze/best-count semantics
  /// as the under_budget streak. Unknown streak types are ignored.
  /// </summary>
  Task RecordDailyActivityAsync(int userId, string streakType);
  
  /// <summary>
  /// Generate personalized daily challenge
  /// </summary>
  Task<UserChallengeDto?> GenerateDailyChallengeAsync(int userId);
}

/// <summary>
/// Service for importing transactions from financial files (OFX, QFX, CSV)
/// </summary>
public interface ITransactionImportService
{
  /// <summary>
  /// Import transactions from a file (OFX, QFX, or CSV) into a user's account.
  /// Handles file parsing, duplicate detection, and bulk creation.
  /// </summary>
  /// <param name="userId">The user performing the import</param>
  /// <param name="accountId">The target account for the transactions</param>
  /// <param name="fileStream">The file contents</param>
  /// <param name="fileName">Original filename (used to determine file type)</param>
  /// <returns>Import result with counts and any errors</returns>
  Task<TransactionImportResult> ImportFromFileAsync(int userId, int accountId, Stream fileStream, string fileName);
}
