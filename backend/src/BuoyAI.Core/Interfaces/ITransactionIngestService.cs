using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// One transaction as a bank-aggregation provider reports it, in provider-neutral terms. A
/// provider adapter (Plaid today) maps its own SDK types to this record and hands a batch to
/// <see cref="ITransactionIngestService"/>, which owns everything after that: dedupe, the
/// pending-to-posted merge, row creation and ingest-time categorization.
///
/// The enrichment members carry what the provider knows beyond the basics. They are generic
/// names; they currently persist into the existing <c>Plaid*</c> columns of
/// <see cref="Transaction"/>.
/// </summary>
public sealed record IncomingTransaction
{
  /// <summary>
  /// Which provider reported this transaction. Informational for now: <see cref="Transaction"/>
  /// and <see cref="Account"/> persist no provider, and the unique index on the external
  /// transaction id is global, so dedupe and account lookups are global too. A second adapter
  /// must either guarantee ids that cannot collide with another provider's, or add a persisted
  /// provider column and scope these lookups by it.
  /// </summary>
  public BankProvider Provider { get; init; } = BankProvider.Plaid;

  /// <summary>The provider's id for the transaction. Rows with a blank id are skipped.</summary>
  public string ExternalTransactionId { get; init; } = string.Empty;

  /// <summary>The provider's id for the account the transaction belongs to.</summary>
  public string? ExternalAccountId { get; init; }

  /// <summary>For a posted charge: the external id of the pending hold it replaces, when the provider links them.</summary>
  public string? PendingExternalTransactionId { get; init; }

  public bool IsPending { get; init; }

  /// <summary>BuoyAI sign convention: money in is positive, money out (an expense) is negative.</summary>
  public decimal Amount { get; init; }

  /// <summary>Whether the money went out (<see cref="TransactionType.Debit"/>) or came in.</summary>
  public TransactionType Direction { get; init; }

  /// <summary>Posted or transaction date. Null when the provider gave none.</summary>
  public DateOnly? Date { get; init; }

  public DateOnly? AuthorizedDate { get; init; }

  /// <summary>Best human-readable description (already resolved by the adapter).</summary>
  public string Description { get; init; } = string.Empty;

  /// <summary>Provider-enriched merchant name; null when the provider has none.</summary>
  public string? MerchantName { get; init; }

  public string? OriginalDescription { get; init; }
  public string? IsoCurrencyCode { get; init; }

  // Category suggestion, already decided by the adapter's trust policy.
  public int CategoryId { get; init; }
  public bool NeedsReview { get; init; }
  public string? CategorySource { get; init; }
  public string? CategoryPrimary { get; init; }
  public string? CategoryDetailed { get; init; }
  public string? CategoryConfidence { get; init; }
  public string? CategoryIconUrl { get; init; }
  public string? SemanticTags { get; init; }

  // Merchant and counterparty enrichment.
  public string? MerchantEntityId { get; init; }
  public string? MerchantLogoUrl { get; init; }
  public string? MerchantWebsite { get; init; }
  public string? CounterpartyType { get; init; }
  public string? CounterpartyConfidence { get; init; }

  // Location and payment details.
  public string? LocationCity { get; init; }
  public string? LocationRegion { get; init; }
  public string? LocationStoreNumber { get; init; }
  public string? PaymentChannel { get; init; }
  public string? CheckNumber { get; init; }
  public string? TransactionCode { get; init; }
  public string? PaymentPayee { get; init; }
  public string? PaymentPayer { get; init; }
  public string? PaymentReason { get; init; }
  public string? PaymentReferenceNumber { get; init; }

  // File imports: the user's own notes and category choice, and the batch that created the row.
  public string? UserNotes { get; init; }
  public bool IsUserCategorized { get; init; }
  public string? ImportBatchId { get; init; }
}

/// <summary>What one ingest call did.</summary>
public sealed class TransactionIngestResult
{
  /// <summary>Rows created, in input order.</summary>
  public List<Transaction> Created { get; } = new();

  /// <summary>
  /// How many of <see cref="Created"/> were flagged <c>NeedsReview</c> as created, before the
  /// categorization policy ran (a user rule clears the flag on the tracked rows afterwards).
  /// </summary>
  public int NeedsReviewCount { get; set; }

  /// <summary>Pending holds promoted in place to their posted charge.</summary>
  public int MergedPendingCount { get; set; }

  /// <summary>Outcome of the ingest categorization policy over <see cref="Created"/>.</summary>
  public IngestCategorizationResult Categorization { get; set; } = new();
}

/// <summary>
/// Provider-neutral transaction ingest. Every bank-aggregation provider feeds its transactions
/// through here so dedupe, the pending-to-posted merge, row creation and categorization are
/// written once.
/// </summary>
public interface ITransactionIngestService
{
  /// <summary>
  /// For each incoming transaction: skip it when its external id is blank or already stored;
  /// otherwise promote the pending hold it replaces, or create a new row on the matching
  /// account (dropped when the account is unknown). Updates are saved first, then new rows
  /// are created in one batch and handed to the categorization policy. A categorization
  /// failure is logged and never fails the ingest.
  /// </summary>
  Task<TransactionIngestResult> IngestAsync(IReadOnlyList<IncomingTransaction> transactions);
}
