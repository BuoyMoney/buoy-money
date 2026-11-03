using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.Transaction;

public class UpdateTransactionCategoryRequest
{
  /// <summary>
  /// Unified category ID to assign: 1-99 for system categories, 1000+ for custom categories.
  /// </summary>
  public int? CategoryId { get; set; }

  /// <summary>
  /// Optional personal sub-category for allowance transactions.
  /// Only used when assigning to an allowance category AND the user is the allowance owner.
  /// </summary>
  public int? PersonalCategoryId { get; set; }

  /// <summary>
  /// Optional semantic tags for vector similarity matching (e.g. "coffee, cafe, beverage").
  /// If provided, overrides existing tags. Validated against allowed vocabulary.
  /// </summary>
  public string? SemanticTags { get; set; }
}

public class TransactionFilterRequest
{
  public DateTime? StartDate { get; set; }
  public DateTime? EndDate { get; set; }
  /// <summary>
  /// Unified category ID for filtering: 1-99 for system categories, 1000+ for custom categories.
  /// </summary>
  public int? CategoryId { get; set; }
  public int? PersonalCategoryId { get; set; }  // Filter by personal sub-category (PersonalUserCategoryId on Transaction)
  public int? AccountId { get; set; }
  public string? SearchTerm { get; set; }
  public decimal? MinAmount { get; set; }
  public decimal? MaxAmount { get; set; }

  /// <summary>
  /// Offline-sync delta fetch (#493): when set, only transactions whose <c>UpdatedAt</c> is strictly
  /// greater than this value are returned (combines with all other filters + paging). The value is
  /// converted to UTC server-side. Lets the client pull only what changed since its last sync.
  /// </summary>
  public DateTime? UpdatedAfter { get; set; }

  public int Page { get; set; } = 1;
  public int PageSize { get; set; } = 50;
  
  // Helper methods for date range validation
  public bool IsDateRangeValid()
  {
    if (StartDate.HasValue && EndDate.HasValue)
    {
      return StartDate.Value <= EndDate.Value;
    }
    return true;
  }
  
  /// <summary>
  /// Get nullable start date with UTC kind for PostgreSQL compatibility
  /// </summary>
  public DateTime? GetUtcStartDate()
  {
    if (StartDate.HasValue)
    {
      return DateTime.SpecifyKind(StartDate.Value, DateTimeKind.Utc);
    }
    return null;
  }
  
  /// <summary>
  /// Get nullable end date with UTC kind for PostgreSQL compatibility
  /// </summary>
  public DateTime? GetUtcEndDate()
  {
    if (EndDate.HasValue)
    {
      return DateTime.SpecifyKind(EndDate.Value, DateTimeKind.Utc);
    }
    return null;
  }
  
  // GetEffectiveStartDate/GetEffectiveEndDate used to live here and substituted
  // UtcNow.AddMonths(-1) / UtcNow whenever the caller supplied no dates. That default was
  // invisible at the call site and silently reduced an unfiltered request to the last month,
  // which is what made the app's "All" date chip hide most of a user's history. Use
  // GetUtcStartDate/GetUtcEndDate above: they return null for "no bound", the repository
  // already treats null as unbounded, and callers that genuinely want a window (the AI
  // context builders) set StartDate/EndDate explicitly.
}

public class TransactionSummaryDto
{
  public decimal TotalIncome { get; set; }
  public decimal TotalExpenses { get; set; }
  public decimal NetAmount { get; set; }
  public int TransactionCount { get; set; }
  public DateTime PeriodStart { get; set; }
  public DateTime PeriodEnd { get; set; }
  /// <summary>
  /// SPENDING totals keyed by category NAME (system + custom). Expenses only —
  /// income, transfers, credit-card payments, split parents and savings-earmarked
  /// transactions are excluded so this is a true "spending by category" breakdown.
  /// This is what gets sent to AI for natural language processing.
  /// </summary>
  public Dictionary<string, decimal> CategoryTotals { get; set; } = new();
  /// <summary>
  /// Spending transaction counts keyed by category NAME (expenses only; same
  /// exclusions as <see cref="CategoryTotals"/>).
  /// </summary>
  public Dictionary<string, int> CategoryCounts { get; set; } = new();
}

public class TransactionResponseDto
{
  public List<TransactionDto> Transactions { get; set; } = new();
  public PaginationMetadata Pagination { get; set; } = new();
  public TransactionSummaryDto Summary { get; set; } = new();
}

public class PaginationMetadata
{
  public int CurrentPage { get; set; }
  public int PageSize { get; set; }
  public int TotalPages { get; set; }
  public int TotalItems { get; set; }
  public bool HasNextPage { get; set; }
  public bool HasPreviousPage { get; set; }
}

public class CategoryStatisticsDto
{
  /// <summary>
  /// Unified CategoryId: 1-99 for system categories, 1000+ for custom categories.
  /// </summary>
  public int CategoryId { get; set; }
  public string CategoryName { get; set; } = string.Empty;
  public decimal TotalAmount { get; set; }
  public int TransactionCount { get; set; }
  public decimal AverageAmount { get; set; }
  public decimal Percentage { get; set; }
}

// Move TransactionDto here for better organization
/// <summary>
/// Represents a transaction for API responses.
/// </summary>
/// <remarks>
/// <para><b>Sign Convention:</b></para>
/// <list type="bullet">
///   <item>Positive Amount = Income (Credit) - money coming in</item>
///   <item>Negative Amount = Expense (Debit) - money going out</item>
/// </list>
/// <para>
/// Use the <see cref="Type"/> property (TransactionType enum) for authoritative 
/// income/expense determination. Avoid checking amount sign directly.
/// </para>
/// </remarks>
public class TransactionDto
{
  public int Id { get; set; }
  public int AccountId { get; set; }
  public string PlaidTransactionId { get; set; } = string.Empty;
  
  /// <summary>
  /// Transaction amount with sign convention: positive = income, negative = expense.
  /// </summary>
  /// <remarks>
  /// For authoritative income/expense determination, use <see cref="Type"/> instead of checking sign.
  /// </remarks>
  public decimal Amount { get; set; }
  
  /// <summary>
  /// Transaction type: Debit (expense) or Credit (income).
  /// This is the authoritative source for determining if a transaction is income or expense.
  /// </summary>
  public TransactionType Type { get; set; }

  /// <summary>
  /// ISO-4217 currency of <see cref="Amount"/>, as reported by the bank. Null means the
  /// bank did not report one (treat as the account's currency). Clients must not sum
  /// amounts across differing currency codes.
  /// </summary>
  public string? IsoCurrencyCode { get; set; }

  public string Description { get; set; } = string.Empty;
  public string MerchantName { get; set; } = string.Empty;

  /// <summary>
  /// The user's own name for this transaction, if they renamed it. Null when they haven't.
  /// </summary>
  public string? Nickname { get; set; }

  /// <summary>
  /// What to render as the title: the user's name, else the merchant, else the bank's
  /// description, else "Unnamed transaction". Clients should use this rather than
  /// coalescing the raw fields — both bank fields can be EMPTY STRINGS (not null), so a
  /// `??` chain silently renders a blank title.
  /// </summary>
  public string DisplayName { get; set; } = string.Empty;

  /// <summary>
  /// Category name for display
  /// </summary>
  public string CategoryName { get; set; } = string.Empty;
  
  /// <summary>
  /// Unified category ID from the Categories table.
  /// For system categories (1-99), this matches the old TransactionCategory enum value.
  /// For user-created categories, this is 1000 + original UserCategory ID.
  /// </summary>
  public int? CategoryId { get; set; }
  
  /// <summary>
  /// Category color for display (from unified Categories table)
  /// </summary>
  public string? CategoryColor { get; set; }
  
  public bool IsUserCategorized { get; set; }
  public DateTime Date { get; set; }

  /// <summary>
  /// Server-side last-modified timestamp (UTC). Exposed for offline-sync delta fetch and as the
  /// <c>baseUpdatedAt</c> the client sends back on queued edits for conflict detection (#493).
  /// </summary>
  public DateTime UpdatedAt { get; set; }

  public string AccountName { get; set; } = string.Empty;
  public bool IsRecurring { get; set; }
  
  // Budget attribution
  public int? BudgetId { get; set; }
  public int? ClaimedByUserId { get; set; }
  public string? ClaimedByUserName { get; set; }
  
  // ==================== Personal Sub-Category (Allowance) ====================
  
  /// <summary>
  /// Private sub-category ID for personal budget tracking.
  /// When a transaction is assigned to an allowance category, the allowance owner
  /// can privately sub-categorize it for their personal budget.
  /// This field is only visible to the allowance owner.
  /// </summary>
  public int? PersonalCategoryId { get; set; }
  
  /// <summary>
  /// Name of the personal sub-category
  /// </summary>
  public string? PersonalCategoryName { get; set; }
  
  /// <summary>
  /// Color of the personal sub-category
  /// </summary>
  public string? PersonalCategoryColor { get; set; }
  
  // ==================== Plaid Rich Data ====================
  
  /// <summary>
  /// Plaid's detailed category (e.g., "RESTAURANTS_FAST_FOOD")
  /// </summary>
  public string? PlaidCategoryDetailed { get; set; }
  
  /// <summary>
  /// Merchant logo URL from Plaid
  /// </summary>
  public string? MerchantLogoUrl { get; set; }
  
  /// <summary>
  /// Merchant website
  /// </summary>
  public string? MerchantWebsite { get; set; }
  
  /// <summary>
  /// Payment channel: in_store, online, other
  /// </summary>
  public string? PaymentChannel { get; set; }
  
  /// <summary>
  /// Transaction location - city
  /// </summary>
  public string? LocationCity { get; set; }
  
  /// <summary>
  /// Transaction location - region/state
  /// </summary>
  public string? LocationRegion { get; set; }
  
  /// <summary>
  /// Whether this is a pending transaction
  /// </summary>
  public bool IsPending { get; set; }
  
  // ==================== AI Categorization ====================
  
  /// <summary>
  /// Category ID suggested by AI (if AI was used for categorization)
  /// </summary>
  public int? AISuggestedCategoryId { get; set; }
  
  /// <summary>
  /// Name of the AI-suggested category (for display)
  /// </summary>
  public string? AISuggestedCategoryName { get; set; }
  
  /// <summary>
  /// AI's confidence score for the suggested category (0.0 to 1.0)
  /// </summary>
  public double? AIConfidence { get; set; }
  
  /// <summary>
  /// Whether this transaction needs user review (low AI confidence or uncategorized)
  /// </summary>
  public bool NeedsReview { get; set; }
  
  /// <summary>
  /// If set, this transaction is temporarily skipped from review until this date
  /// </summary>
  public DateTime? SkipUntil { get; set; }
  
  /// <summary>
  /// Source of the category: Plaid, AI, User, VectorSimilarity
  /// </summary>
  public string? CategorySource { get; set; }

  /// <summary>
  /// Semantic tags for vector similarity matching (e.g. "coffee, cafe, beverage")
  /// </summary>
  public string? SemanticTags { get; set; }
  
  /// <summary>
  /// Whether the category was assigned by AI
  /// </summary>
  public bool IsAICategorized => AISuggestedCategoryId.HasValue && !IsUserCategorized;
  
  // ==================== User Notes ====================
  
  /// <summary>
  /// User-added notes/memo for this transaction
  /// </summary>
  public string? UserNotes { get; set; }
  
  // ==================== Transaction Splitting ====================
  
  /// <summary>
  /// Whether this transaction has been split into multiple categories
  /// </summary>
  public bool IsSplit { get; set; }
  
  /// <summary>
  /// For split transactions, the parent transaction ID
  /// </summary>
  public int? ParentTransactionId { get; set; }
  
  /// <summary>
  /// For parent split transactions, the child split details
  /// </summary>
  public List<TransactionSplitDto>? Splits { get; set; }
  
  // ==================== Savings Goal Withdrawal ====================
  
  /// <summary>
  /// If this transaction was marked as a withdrawal from a savings goal
  /// </summary>
  public int? SavingsGoalId { get; set; }
  
  /// <summary>
  /// Name of the savings goal this transaction was withdrawn from
  /// </summary>
  public string? SavingsGoalName { get; set; }

  /// <summary>
  /// The real account the earmarked goal lives in, when it is linked to one. Null for an
  /// unlinked goal or an un-earmarked transaction. A non-null value on an expense means the
  /// draw can be tied to the transfer that moved the money (<see cref="CoveringTransfer"/>).
  /// </summary>
  public int? SavingsGoalAccountId { get; set; }

  /// <summary>Id of the transfer that settled this draw, or null while it is still in flight.</summary>
  public int? CoveringTransferTransactionId { get; set; }

  /// <summary>Summary of the covering transfer; only populated on single-transaction reads.</summary>
  public CoveringTransferDto? CoveringTransfer { get; set; }
}

/// <summary>A transfer-out transaction that settled one or more earmarked draws.</summary>
public class CoveringTransferDto
{
  public int TransactionId { get; set; }
  public DateTime Date { get; set; }
  /// <summary>Signed as stored (negative = money left the account).</summary>
  public decimal Amount { get; set; }
  public string AccountName { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
}

/// <summary>An earmarked withdrawal on a linked goal that no transfer has covered yet.</summary>
public class PendingDrawDto
{
  public int TransactionId { get; set; }
  public DateTime Date { get; set; }
  /// <summary>Positive magnitude of the draw.</summary>
  public decimal Amount { get; set; }
  public string Title { get; set; } = string.Empty;
}

public class CoveringTransferCandidateDto
{
  public int TransactionId { get; set; }
  public DateTime Date { get; set; }
  /// <summary>Positive magnitude of money that left the account.</summary>
  public decimal Amount { get; set; }
  public string Title { get; set; } = string.Empty;
  public string? CategoryName { get; set; }
  /// <summary>Amount equals this one purchase.</summary>
  public bool MatchesThis { get; set; }
  /// <summary>Amount equals every pending draw on the goal added together.</summary>
  public bool MatchesPendingTotal { get; set; }
}

/// <summary>
/// What the client needs to let the user say "this transfer covered that purchase": the
/// goal's account, the draws still waiting, and the recent money-out transactions on that
/// account, flagged where the amounts line up.
/// </summary>
public class CoveringTransferCandidatesDto
{
  public int SavingsGoalId { get; set; }
  public string SavingsGoalName { get; set; } = string.Empty;
  public int AccountId { get; set; }
  public string AccountName { get; set; } = string.Empty;
  /// <summary>Every in-flight draw on the goal, this transaction included.</summary>
  public List<PendingDrawDto> PendingDraws { get; set; } = new();
  public decimal PendingTotal { get; set; }
  public List<CoveringTransferCandidateDto> Candidates { get; set; } = new();
}

public class LinkCoveringTransferRequest
{
  public int TransferTransactionId { get; set; }
  /// <summary>Also settle every other in-flight draw on the same goal with this transfer.</summary>
  public bool IncludeOtherPending { get; set; }
}

/// <summary>
/// Represents a single split of a transaction into a category
/// </summary>
public class TransactionSplitDto
{
  public int Id { get; set; }
  public int TransactionId { get; set; }
  public decimal Amount { get; set; }
  public int? CategoryId { get; set; }
  public string CategoryName { get; set; } = string.Empty;
  public string? CategoryColor { get; set; }

  /// <summary>The part's own notes (a receipt split lists its items here).</summary>
  public string? Notes { get; set; }
}

/// <summary>
/// Request to update transaction notes
/// </summary>
public class UpdateTransactionNotesRequest
{
  /// <summary>
  /// The notes to set for the transaction. Null or empty clears the notes.
  /// </summary>
  public string? Notes { get; set; }
}

/// <summary>
/// Request to rename a transaction
/// </summary>
public class UpdateTransactionNicknameRequest
{
  /// <summary>
  /// The user's name for the transaction. Null or empty clears it, falling back to whatever
  /// the bank called it.
  /// </summary>
  public string? Nickname { get; set; }
}

/// <summary>
/// Request to update transaction date
/// </summary>
public class UpdateTransactionDateRequest
{
  /// <summary>
  /// The new date for the transaction
  /// </summary>
  public DateTime Date { get; set; }
}

/// <summary>
/// Request to create a manual transaction (user-entered, not from Plaid/import).
/// </summary>
public class CreateTransactionRequest
{
  /// <summary>
  /// Account the transaction belongs to. Optional: when omitted (null), the transaction lands in
  /// the user's synthetic "Cash" account (created on demand) so a manual transaction can be entered
  /// without a linked bank account. When provided, it must be owned by the user or their family.
  /// </summary>
  public int? AccountId { get; set; }

  /// <summary>
  /// Signed amount (required, non-zero). Positive = income (Credit); negative = expense (Debit).
  /// </summary>
  public decimal Amount { get; set; }

  /// <summary>
  /// Short description (required), e.g. "Farmers market", "Freelance payment".
  /// </summary>
  public string Description { get; set; } = string.Empty;

  /// <summary>
  /// Merchant/vendor name (optional).
  /// </summary>
  public string? MerchantName { get; set; }

  /// <summary>
  /// Transaction date (required). Treated as UTC.
  /// </summary>
  public DateTime Date { get; set; }

  /// <summary>
  /// Unified category ID (optional): 1-99 system, 1000+ custom. Defaults to Uncategorized.
  /// </summary>
  public int? CategoryId { get; set; }

  /// <summary>
  /// Personal sub-category for allowance transactions (optional).
  /// </summary>
  public int? PersonalCategoryId { get; set; }

  /// <summary>
  /// User notes/memo (optional).
  /// </summary>
  public string? UserNotes { get; set; }

  /// <summary>
  /// Comma-separated semantic tags for later matching (optional).
  /// </summary>
  public string? SemanticTags { get; set; }
}

/// <summary>
/// Request to split a transaction into multiple categories
/// </summary>
public class SplitTransactionRequest
{
  /// <summary>
  /// The splits to apply. Sum of amounts must equal the original transaction amount.
  /// </summary>
  public List<TransactionSplitItem> Splits { get; set; } = new();
}

/// <summary>
/// A single split item for transaction splitting
/// </summary>
public class TransactionSplitItem
{
  /// <summary>
  /// Amount for this split (positive value)
  /// </summary>
  public decimal Amount { get; set; }
  
  /// <summary>
  /// Unified category ID for this split: 1-99 for system categories, 1000+ for custom categories.
  /// </summary>
  public int? CategoryId { get; set; }

  /// <summary>
  /// Optional note for this part, saved as its notes (receipt splits list the part's items here).
  /// </summary>
  public string? Note { get; set; }
}

/// <summary>
/// Request to trigger AI categorization on transactions
/// </summary>
public class AICategorizationRequest
{
  /// <summary>
  /// Specific transaction IDs to categorize. If empty, categorizes all uncategorized transactions.
  /// </summary>
  public List<int>? TransactionIds { get; set; }
  
  /// <summary>
  /// Only categorize transactions from this date forward
  /// </summary>
  public DateTime? StartDate { get; set; }
  
  /// <summary>
  /// Maximum number of transactions to process in one batch
  /// </summary>
  public int MaxTransactions { get; set; } = 25;
  
  /// <summary>
  /// Re-categorize transactions that already have AI categorization
  /// </summary>
  public bool RecategorizeExisting { get; set; } = false;
}

/// <summary>
/// Response from AI categorization operation
/// </summary>
public class AICategorizationResponse
{
  public int TotalProcessed { get; set; }
  public int SuccessfullyCategorized { get; set; }
  public int HighConfidence { get; set; }  // >= 0.8
  public int MediumConfidence { get; set; }  // 0.5 - 0.8
  public int LowConfidence { get; set; }  // < 0.5
  public int NeedingReview { get; set; }
  public int Errors { get; set; }
  public List<AICategorizationResult> Results { get; set; } = new();
}

/// <summary>
/// Individual transaction categorization result
/// </summary>
public class AICategorizationResult
{
  public int TransactionId { get; set; }
  
  /// <summary>
  /// Unified category ID suggested by AI: 1-99 for system categories, 1000+ for custom categories.
  /// </summary>
  public int SuggestedCategoryId { get; set; }
  public string SuggestedCategoryName { get; set; } = string.Empty;
  
  public double Confidence { get; set; }
  public bool NeedsReview { get; set; }
  public bool Applied { get; set; }
  public string? Error { get; set; }
}

/// <summary>
/// Statistics about review-needed transactions
/// </summary>
public class ReviewStatisticsDto
{
  public int TotalNeedingReview { get; set; }
  public int UncategorizedCount { get; set; }

  /// <summary>
  /// Review-pending transactions the AI has never looked at (no AI suggestion, no attempt
  /// date), dated inside the auto-queue window (this month and last by default). This, not
  /// UncategorizedCount, drives the Auto-Categorize button: it includes Plaid-categorized
  /// low-confidence rows (the AI knows the user's rules; Plaid doesn't), excludes rows the AI
  /// already tried so an inconclusive AI pass is never re-offered, and excludes the deep
  /// backlog from a first import (see docs/ai_categorization_pipeline.md).
  /// </summary>
  public int AiEligibleCount { get; set; }

  /// <summary>
  /// Transactions the ingest pipeline queued for background AI categorization that have not
  /// been processed yet. Non-zero means the AI is working on this user's new transactions
  /// right now; the client shows its analyzing indicator and polls until it reaches zero.
  /// </summary>
  public int QueuedForAiCount { get; set; }
  public int LowConfidenceCount { get; set; }

  /// <summary>Needs-review transactions dated in the current calendar month (subset of the total).</summary>
  public int ThisMonthCount { get; set; }

  /// <summary>Needs-review transactions dated before the current month — the historical backlog (subset of the total).</summary>
  public int OlderCount { get; set; }

  public DateTime? OldestPendingReview { get; set; }
}

// ==================== Transaction Import DTOs ====================

/// <summary>
/// Result of a transaction file import operation
/// </summary>
public class TransactionImportResult
{
  public bool Success { get; set; }
  public int TotalRowsInFile { get; set; }
  public int ImportedCount { get; set; }
  public int SkippedDuplicates { get; set; }
  public int SkippedInvalid { get; set; }
  public List<string> Errors { get; set; } = new();
  public List<string> Warnings { get; set; } = new();
  public string? Message { get; set; }
}

/// <summary>
/// Parsed transaction from file import, before mapping to Transaction entity
/// </summary>
public class ParsedTransactionDto
{
  public DateTime Date { get; set; }
  public decimal Amount { get; set; }
  public string Description { get; set; } = string.Empty;
  public string? MerchantName { get; set; }
  public string? TransactionId { get; set; } // For OFX/QFX FITID
  public TransactionType Type { get; set; }
  public string? Memo { get; set; }
  public string? CheckNumber { get; set; }
}

/// <summary>
/// Allowed file types for transaction import
/// </summary>
public static class AllowedTransactionImportTypes
{
  public static readonly Dictionary<string, string> MimeTypes = new()
  {
    { ".ofx", "application/x-ofx" },
    { ".qfx", "application/vnd.intu.qfx" },
    { ".csv", "text/csv" }
  };
  
  public const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5MB
  
  public static bool IsAllowedExtension(string fileName)
  {
    var ext = Path.GetExtension(fileName).ToLowerInvariant();
    return MimeTypes.ContainsKey(ext);
  }
  
  public static string GetMimeType(string fileName)
  {
    var ext = Path.GetExtension(fileName).ToLowerInvariant();
    return MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
  }
}

/// <summary>
/// Request to mark a transaction as a savings goal withdrawal
/// </summary>
public class MarkSavingsWithdrawalRequest
{
  public int SavingsGoalId { get; set; }
}
