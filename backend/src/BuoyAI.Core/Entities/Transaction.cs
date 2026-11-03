using System.ComponentModel.DataAnnotations.Schema;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class Transaction
{
  public int Id { get; set; }
  public int AccountId { get; set; }
  public string PlaidTransactionId { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public TransactionType Type { get; set; }
  public string Description { get; set; } = string.Empty;
  public string MerchantName { get; set; } = string.Empty;

  /// <summary>
  /// User-entered name for this transaction, overriding whatever the bank called it.
  /// Kept separate from <see cref="Description"/> (the bank's own text) rather than
  /// overwriting it: we keep no other copy of that text, and a pending→posted promotion
  /// would clobber an in-place rename. Mirrors Account.Nickname.
  /// </summary>
  public string? Nickname { get; set; }

  /// <summary>
  /// What to show the user as this transaction's title. Prefer this over
  /// <see cref="Description"/>/<see cref="MerchantName"/> anywhere user-facing.
  ///
  /// Both bank fields can be blank: Plaid leaves merchant_name empty for checks and account
  /// transfers, and note they arrive as EMPTY STRINGS, not null — so a `??` chain silently
  /// yields "" and renders a nameless row. Check for blank, not null. Computed, never
  /// persisted.
  /// </summary>
  [NotMapped]
  public string DisplayName =>
    !string.IsNullOrWhiteSpace(Nickname) ? Nickname!
    : !string.IsNullOrWhiteSpace(MerchantName) ? MerchantName
    : !string.IsNullOrWhiteSpace(Description) ? Description
    : "Unnamed transaction";
  
  /// <summary>
  /// Reference to the unified Categories table.
  /// Contains both system categories (IDs 1-99) and user-created categories (IDs 1000+).
  /// </summary>
  public int? CategoryId { get; set; }
  
  public bool IsUserCategorized { get; set; } = false;
  
  // ==================== Plaid Rich Data ====================
  
  /// <summary>
  /// Plaid's primary category (e.g., "FOOD_AND_DRINK", "TRANSPORTATION")
  /// </summary>
  public string? PlaidCategoryPrimary { get; set; }
  
  /// <summary>
  /// Plaid's detailed category (e.g., "RESTAURANTS_FAST_FOOD", "TRANSPORTATION_GAS")
  /// </summary>
  public string? PlaidCategoryDetailed { get; set; }
  
  /// <summary>
  /// Plaid's confidence level for the category: VERY_HIGH, HIGH, MEDIUM, LOW, UNKNOWN
  /// </summary>
  public string? PlaidCategoryConfidence { get; set; }
  
  /// <summary>
  /// URL to the category icon from Plaid
  /// </summary>
  public string? PlaidCategoryIconUrl { get; set; }
  
  /// <summary>
  /// Primary counterparty/merchant entity ID (stable across transactions)
  /// </summary>
  public string? PlaidMerchantEntityId { get; set; }
  
  /// <summary>
  /// Merchant logo URL from Plaid
  /// </summary>
  public string? PlaidMerchantLogoUrl { get; set; }
  
  /// <summary>
  /// Merchant website
  /// </summary>
  public string? PlaidMerchantWebsite { get; set; }
  
  /// <summary>
  /// Counterparty type: merchant, financial_institution, payment_app, marketplace, etc.
  /// </summary>
  public string? PlaidCounterpartyType { get; set; }
  
  /// <summary>
  /// Plaid's confidence in the counterparty identification
  /// </summary>
  public string? PlaidCounterpartyConfidence { get; set; }
  
  /// <summary>
  /// Payment channel: in_store, online, other
  /// </summary>
  public string? PlaidPaymentChannel { get; set; }
  
  /// <summary>
  /// Transaction location - city
  /// </summary>
  public string? LocationCity { get; set; }
  
  /// <summary>
  /// Transaction location - region/state
  /// </summary>
  public string? LocationRegion { get; set; }
  
  /// <summary>
  /// Transaction location - store number
  /// </summary>
  public string? LocationStoreNumber { get; set; }
  
  /// <summary>
  /// Original raw description from the bank
  /// </summary>
  public string? OriginalDescription { get; set; }

  // ==================== Plaid: checks & transfers ====================
  // Plaid names a transaction via merchant_name, which it leaves null for exactly two kinds of
  // transaction: checks and inter-bank transfers. Those are the rows that arrive nameless. It
  // does describe them — just in these fields, which we never requested.

  /// <summary>
  /// Check number. Plaid populates this only for check transactions — the ones with no merchant.
  /// </summary>
  public string? CheckNumber { get; set; }

  /// <summary>
  /// Plaid's classifier for the transaction: "adjustment", "atm", "bank charge", "bill payment",
  /// "cash", "cashback", "cheque", "direct debit", "interest", "purchase", "standing order",
  /// "transfer", "null". Useful categorization signal, especially where there's no merchant.
  /// </summary>
  public string? TransactionCode { get; set; }

  /// <summary>
  /// Who received the money on an inter-bank transfer. Null on anything that isn't one.
  /// </summary>
  public string? PaymentPayee { get; set; }

  /// <summary>
  /// Who sent the money on an inter-bank transfer. Null on anything that isn't one.
  /// </summary>
  public string? PaymentPayer { get; set; }

  /// <summary>
  /// The transfer's stated reason, when the institution supplies one.
  /// </summary>
  public string? PaymentReason { get; set; }

  /// <summary>
  /// The institution's reference number for a transfer — what the user would quote to their bank.
  /// </summary>
  public string? PaymentReferenceNumber { get; set; }

  /// <summary>
  /// When the transaction was authorized, as opposed to <see cref="Date"/> (when it posted).
  /// A Friday purchase can post on Monday; the authorized date is when the user actually spent.
  /// Null when the institution doesn't supply it.
  /// </summary>
  public DateTime? AuthorizedDate { get; set; }

  /// <summary>
  /// ISO-4217 currency of the transaction. Everything else assumes USD; storing this at least
  /// makes a non-USD transaction detectable rather than silently mis-summed.
  /// </summary>
  public string? IsoCurrencyCode { get; set; }
  
  /// <summary>
  /// Whether this is a pending transaction
  /// </summary>
  public bool IsPending { get; set; } = false;
  
  // ==================== AI Categorization ====================
  
  /// <summary>
  /// AI-suggested category ID (references unified Categories table).
  /// Contains the category ID suggested by AI before user verification.
  /// </summary>
  public int? AISuggestedCategoryId { get; set; }
  
  /// <summary>
  /// AI confidence score (0.0 to 1.0) for the suggested category.
  /// Higher values indicate more confidence in the categorization.
  /// </summary>
  public double? AIConfidence { get; set; }
  
  /// <summary>
  /// When the AI categorization was performed.
  /// </summary>
  public DateTime? AICategorizationDate { get; set; }
  
  /// <summary>
  /// Whether this transaction needs user review (low AI confidence or uncategorized).
  /// </summary>
  public bool NeedsReview { get; set; } = false;
  
  /// <summary>
  /// If set, this transaction is temporarily skipped from review until this date.
  /// User can "skip for now" to defer categorization for a few days.
  /// </summary>
  public DateTime? SkipUntil { get; set; }
  
  /// <summary>
  /// Source of the category: Plaid, AI, User, VectorSimilarity
  /// </summary>
  public string? CategorySource { get; set; }

  /// <summary>
  /// Comma-separated semantic tags for vector similarity matching.
  /// Populated from Plaid detailed categories or Gemini AI responses.
  /// Examples: "coffee, cafe, beverage" or "grocery, supermarket, food"
  /// </summary>
  public string? SemanticTags { get; set; }
  
  public DateTime Date { get; set; }
  
  // Budget attribution
  /// <summary>
  /// Optional explicit assignment to a specific budget.
  /// If null, transaction is attributed to family budget (if in family) or personal budget.
  /// </summary>
  public int? BudgetId { get; set; }
  
  /// <summary>
  /// For family transactions, allows a user to "claim" the transaction for their personal tracking.
  /// The transaction still counts toward the family budget, but also shows in their personal view.
  /// </summary>
  public int? ClaimedByUserId { get; set; }
  
  /// <summary>
  /// Private sub-category for personal budget tracking.
  /// When a transaction is assigned to an allowance category (e.g., "Jon Money"),
  /// the allowance owner can privately sub-categorize it for their personal budget.
  /// This field is only visible to the allowance owner.
  /// </summary>
  public int? PersonalUserCategoryId { get; set; }
  
  /// <summary>
  /// Whether to hide this transaction from ReadOnly family members.
  /// Useful for hiding gift purchases or surprises from children, etc.
  /// Only BudgetAdmin+ roles can toggle this.
  /// </summary>
  public bool HiddenFromReadOnly { get; set; } = false;
  
  /// <summary>
  /// User-added notes/memo for this transaction.
  /// Allows users to add personal reminders about what the transaction was for.
  /// </summary>
  public string? UserNotes { get; set; }

  /// <summary>
  /// Set on rows created by a file import: the batch that created them, used to undo the import.
  /// Null for every other row.
  /// </summary>
  public string? ImportBatchId { get; set; }
  
  // ==================== Transaction Splitting ====================
  
  /// <summary>
  /// Whether this transaction has been split into multiple categories.
  /// When true, the transaction amount is distributed across TransactionSplits.
  /// </summary>
  public bool IsSplit { get; set; } = false;
  
  /// <summary>
  /// For split child transactions, reference to the original parent transaction.
  /// Null for non-split transactions and parent transactions.
  /// </summary>
  public int? ParentTransactionId { get; set; }
  
  /// <summary>
  /// If this transaction was marked as a withdrawal from a savings goal,
  /// references the goal it was deducted from.
  /// </summary>
  public int? SavingsGoalId { get; set; }

  /// <summary>
  /// For a purchase earmarked "paid from" a savings goal that lives in a real account: the
  /// transfer-out transaction on that account which actually moved the money. Null while the
  /// draw is still "in flight" (earmarked, but no transfer has left the account yet). The goal's
  /// envelope balance already dropped at earmark time; this link exists so the reconciliation
  /// view can tell in-flight money from drift, and so the matching transfer is recognised as
  /// settlement rather than as unexplained movement.
  /// </summary>
  public int? CoveringTransferTransactionId { get; set; }

  /// <summary>
  /// When the user dismissed this transaction as "not a transfer" from the
  /// suspected-transfer suggestions. Set independently of <see cref="IsUserCategorized"/>
  /// so dismissing a suggestion stops it being re-suggested WITHOUT pulling an
  /// otherwise-uncategorized transaction out of the normal categorization/AI-review queue.
  /// </summary>
  public DateTime? TransferReviewedAt { get; set; }

  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }

  // Navigation properties
  public Account Account { get; set; } = null!;
  
  /// <summary>
  /// Navigation property for the unified category.
  /// </summary>
  public Category? CategoryNav { get; set; }
  
  /// <summary>
  /// Navigation property for AI-suggested category.
  /// </summary>
  public Category? AISuggestedCategoryNav { get; set; }
  
  /// <summary>
  /// Navigation property for personal sub-category.
  /// Used for private sub-categorization by allowance owners.
  /// </summary>
  public Category? PersonalCategoryNav { get; set; }
  
  public Budget? Budget { get; set; }
  public User? ClaimedByUser { get; set; }
  public Transaction? ParentTransaction { get; set; }
  public ICollection<Transaction> SplitTransactions { get; set; } = new List<Transaction>();
  public SavingsGoal? SavingsGoal { get; set; }

  /// <summary>The transfer that settled this earmarked draw; see <see cref="CoveringTransferTransactionId"/>.</summary>
  public Transaction? CoveringTransfer { get; set; }
}
