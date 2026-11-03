using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The one implementation of provider-neutral transaction ingest. Moved out of the three
/// copies that lived in <see cref="PlaidService"/> (recent sync, connection sync and
/// historical import); the decision tree is unchanged.
/// </summary>
public class TransactionIngestService : ITransactionIngestService
{
  private readonly ITransactionRepository _transactionRepository;
  private readonly IAccountRepository _accountRepository;
  private readonly ITransactionIngestCategorizer _ingestCategorizer;
  private readonly ILogger<TransactionIngestService> _logger;

  public TransactionIngestService(
    ITransactionRepository transactionRepository,
    IAccountRepository accountRepository,
    ITransactionIngestCategorizer ingestCategorizer,
    ILogger<TransactionIngestService> logger)
  {
    _transactionRepository = transactionRepository;
    _accountRepository = accountRepository;
    _ingestCategorizer = ingestCategorizer;
    _logger = logger;
  }

  public async Task<TransactionIngestResult> IngestAsync(IReadOnlyList<IncomingTransaction> transactions)
  {
    var result = new TransactionIngestResult();
    if (transactions == null || transactions.Count == 0)
    {
      return result;
    }

    var transactionsToUpdate = new List<Transaction>();

    foreach (var incoming in transactions)
    {
      if (string.IsNullOrEmpty(incoming.ExternalTransactionId))
      {
        _logger.LogWarning("Encountered transaction with null or empty TransactionId, skipping.");
        continue;
      }

      var existing = await _transactionRepository.GetByPlaidTransactionIdAsync(incoming.ExternalTransactionId);
      if (existing != null)
      {
        continue;
      }

      // A posted charge that supersedes a pending hold updates that row in place, so the
      // hold and its final charge never survive as two rows.
      var merged = await TryMergePendingIntoPostedAsync(incoming);
      if (merged != null)
      {
        transactionsToUpdate.Add(merged);
        continue;
      }

      if (!string.IsNullOrEmpty(incoming.ExternalAccountId))
      {
        var account = await _accountRepository.GetByPlaidAccountIdAsync(incoming.ExternalAccountId);
        if (account != null)
        {
          result.Created.Add(CreateTransaction(incoming, account.Id));
        }
      }
    }

    if (transactionsToUpdate.Count > 0)
    {
      foreach (var txn in transactionsToUpdate)
      {
        await _transactionRepository.UpdateAsync(txn);
      }
      result.MergedPendingCount = transactionsToUpdate.Count;
      _logger.LogInformation("Updated {Count} pending transactions to posted status", transactionsToUpdate.Count);
    }

    if (result.Created.Count > 0)
    {
      await _transactionRepository.CreateManyAsync(result.Created);
      _logger.LogDebug("Created {Count} new transactions", result.Created.Count);
      result.NeedsReviewCount = result.Created.Count(t => t.NeedsReview);
      result.Categorization = await CategorizeIngestedAsync(result.Created);
    }

    return result;
  }

  /// <summary>
  /// Promotes an existing pending hold row to its posted charge in place: swaps the external
  /// id, clears the pending flag, and refreshes amount/date/merchant from the posted data. The
  /// amount is already in the repo sign convention (expense negative). The row's type,
  /// category and user edits are left alone.
  /// </summary>
  internal static void PromotePendingToPosted(
    Transaction pending,
    string postedExternalTransactionId,
    decimal amount,
    DateOnly? date,
    string? merchantName)
  {
    pending.PlaidTransactionId = postedExternalTransactionId;
    pending.IsPending = false;
    pending.Amount = amount;
    pending.Date = date.HasValue
      ? DateTime.SpecifyKind(date.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
      : pending.Date;
    if (!string.IsNullOrEmpty(merchantName))
    {
      pending.MerchantName = merchantName;
      pending.Description = merchantName;
    }
  }

  private async Task<Transaction?> TryMergePendingIntoPostedAsync(IncomingTransaction incoming)
  {
    if (string.IsNullOrEmpty(incoming.PendingExternalTransactionId))
    {
      return null;
    }

    var pendingTransaction = await _transactionRepository.GetByPlaidTransactionIdAsync(incoming.PendingExternalTransactionId);
    if (pendingTransaction == null)
    {
      return null;
    }

    _logger.LogInformation(
      "Updating pending transaction {OldId} -> posted transaction {NewId} for {Merchant}",
      incoming.PendingExternalTransactionId,
      incoming.ExternalTransactionId,
      incoming.Description);

    PromotePendingToPosted(
      pendingTransaction,
      incoming.ExternalTransactionId,
      incoming.Amount,
      incoming.Date,
      incoming.MerchantName);

    return pendingTransaction;
  }

  private Transaction CreateTransaction(IncomingTransaction incoming, int accountId)
  {
    _logger.LogDebug(
      "INGEST - Transaction: {Name}, Amount: {Amount}, Category: {Primary}/{Detailed} " +
      "(Confidence: {Confidence}), Channel: {Channel}, NeedsReview: {NeedsReview}",
      incoming.Description,
      incoming.Amount,
      incoming.CategoryPrimary ?? "Uncategorized",
      incoming.CategoryDetailed ?? "N/A",
      incoming.CategoryConfidence,
      incoming.PaymentChannel ?? "Unknown",
      incoming.NeedsReview);

    return new Transaction
    {
      // Core fields
      AccountId = accountId,
      PlaidTransactionId = incoming.ExternalTransactionId,
      Amount = incoming.Amount,
      Type = incoming.Direction,
      Description = incoming.Description,
      MerchantName = incoming.MerchantName ?? string.Empty,
      CategoryId = incoming.CategoryId,
      Date = incoming.Date.HasValue
        ? DateTime.SpecifyKind(incoming.Date.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
        : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
      IsUserCategorized = incoming.IsUserCategorized,
      UserNotes = incoming.UserNotes,
      ImportBatchId = incoming.ImportBatchId,

      // Category enrichment
      PlaidCategoryPrimary = incoming.CategoryPrimary,
      PlaidCategoryDetailed = incoming.CategoryDetailed,
      PlaidCategoryConfidence = incoming.CategoryConfidence,
      PlaidCategoryIconUrl = incoming.CategoryIconUrl,

      // Counterparty/merchant enrichment
      PlaidMerchantEntityId = incoming.MerchantEntityId,
      PlaidMerchantLogoUrl = incoming.MerchantLogoUrl,
      PlaidMerchantWebsite = incoming.MerchantWebsite,
      PlaidCounterpartyType = incoming.CounterpartyType,
      PlaidCounterpartyConfidence = incoming.CounterpartyConfidence,

      // Location enrichment
      LocationCity = incoming.LocationCity,
      LocationRegion = incoming.LocationRegion,
      LocationStoreNumber = incoming.LocationStoreNumber,

      // Payment info
      PlaidPaymentChannel = incoming.PaymentChannel,
      OriginalDescription = incoming.OriginalDescription,

      // Checks and transfers
      CheckNumber = incoming.CheckNumber,
      TransactionCode = incoming.TransactionCode,
      PaymentPayee = incoming.PaymentPayee,
      PaymentPayer = incoming.PaymentPayer,
      PaymentReason = incoming.PaymentReason,
      PaymentReferenceNumber = incoming.PaymentReferenceNumber,

      AuthorizedDate = incoming.AuthorizedDate.HasValue
        ? DateTime.SpecifyKind(incoming.AuthorizedDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
        : null,
      IsoCurrencyCode = incoming.IsoCurrencyCode,

      IsPending = incoming.IsPending,
      SemanticTags = incoming.SemanticTags,

      // Categorization status
      NeedsReview = incoming.NeedsReview,
      CategorySource = incoming.CategorySource,
      AISuggestedCategoryId = null,
      AIConfidence = null,
      AICategorizationDate = null
    };
  }

  /// <summary>
  /// Runs the ingest categorization policy (rules, then trusted provider category, then the AI
  /// queue) on rows just persisted. Failures are logged and never fail the ingest.
  /// </summary>
  private async Task<IngestCategorizationResult> CategorizeIngestedAsync(List<Transaction> newTransactions)
  {
    try
    {
      return await _ingestCategorizer.ProcessIngestedAsync(newTransactions);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Ingest categorization threw for {Count} new transactions", newTransactions.Count);
      return new IngestCategorizationResult();
    }
  }
}
