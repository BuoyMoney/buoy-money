using Going.Plaid;
using Going.Plaid.Entity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Plaid banking integration service. Split into partial classes for maintainability:
/// - PlaidService.cs (this file): Constructor, fields, common utilities
/// - PlaidService.Connection.cs: Link tokens, connection management, status updates
/// - PlaidService.Sync.cs: Account/transaction sync, historical import
/// </summary>
public partial class PlaidService : IPlaidService
{
  private readonly PlaidClient _plaidClient;
  private readonly IUserRepository _userRepository;
  private readonly IAccountRepository _accountRepository;
  private readonly ITransactionRepository _transactionRepository;
  private readonly IPlaidConnectionRepository _plaidConnectionRepository;
  private readonly INotificationService _notificationService;
  private readonly IBankConnectionAlertService _alerts;
  private readonly IEncryptionService _encryptionService;
  private readonly IPlaidUsageTracker _plaidUsageTracker;
  private readonly ITransactionIngestService _transactionIngest;
  private readonly ILogger<PlaidService> _logger;
  private readonly IConfiguration _configuration;
  private readonly BuoyAiMetrics _metrics;
  private readonly string _clientName;

  public PlaidService(
    IConfiguration configuration,
    IUserRepository userRepository,
    IAccountRepository accountRepository,
    ITransactionRepository transactionRepository,
    IPlaidConnectionRepository plaidConnectionRepository,
    INotificationService notificationService,
    IBankConnectionAlertService alerts,
    IEncryptionService encryptionService,
    IPlaidUsageTracker plaidUsageTracker,
    ITransactionIngestService transactionIngest,
    ILogger<PlaidService> logger,
    BuoyAiMetrics metrics)
  {
    var plaidSettings = configuration.GetSection("Plaid");
    var environment = plaidSettings["Environment"]?.ToLowerInvariant() switch
    {
      "sandbox" => Going.Plaid.Environment.Sandbox,
      "development" => Going.Plaid.Environment.Development,
      "production" => Going.Plaid.Environment.Production,
      _ => Going.Plaid.Environment.Sandbox
    };

    IOptions<Going.Plaid.PlaidOptions> plaidOptions = Options.Create(new Going.Plaid.PlaidOptions
    {
      ClientId = plaidSettings["ClientId"] ?? throw new InvalidOperationException("Plaid ClientId not configured"),
      Secret = plaidSettings["SecretKey"] ?? throw new InvalidOperationException("Plaid SecretKey not configured"),
      Environment = environment
    });

    _plaidClient = new PlaidClient(plaidOptions);

    _userRepository = userRepository;
    _accountRepository = accountRepository;
    _transactionRepository = transactionRepository;
    _plaidConnectionRepository = plaidConnectionRepository;
    _notificationService = notificationService;
    _alerts = alerts;
    _encryptionService = encryptionService;
    _plaidUsageTracker = plaidUsageTracker;
    _transactionIngest = transactionIngest;
    _logger = logger;
    _metrics = metrics;
    _configuration = configuration;
    _clientName = plaidSettings["ClientName"] ?? BuoyAI.Core.Configuration.Brand.Name;
  }

  #region Rate Limiting

  private async Task<T> ExecuteWithRateLimit<T>(
    string itemId,
    Func<Task<T>> apiCall,
    Func<string, Task<bool>> canMakeRequest,
    Func<string, Task> recordRequest,
    string operationType)
  {
    const int maxRetries = 3;
    var delay = TimeSpan.FromSeconds(1);

    for (int attempt = 0; attempt < maxRetries; attempt++)
    {
      if (!await canMakeRequest(itemId))
      {
        _logger.LogWarning("Rate limit exceeded for {OperationType} on item {ItemId}. Attempt {Attempt}/{MaxRetries}",
          operationType, itemId, attempt + 1, maxRetries);
        
        if (attempt < maxRetries - 1)
        {
          await Task.Delay(delay);
          delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
          continue;
        }
        throw new InvalidOperationException($"Rate limit exceeded for {operationType} on item {itemId}");
      }

      try
      {
        var result = await apiCall();
        await recordRequest(itemId);
        return result;
      }
      catch (Exception ex) when (ex.Message.Contains("RATE_LIMIT_EXCEEDED") || ex.Message.Contains("429"))
      {
        _logger.LogWarning(ex, "Plaid rate limit hit for {OperationType} on item {ItemId}. Attempt {Attempt}/{MaxRetries}",
          operationType, itemId, attempt + 1, maxRetries);
        
        if (attempt < maxRetries - 1)
        {
          await Task.Delay(delay);
          delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
          continue;
        }
        throw;
      }
    }

    throw new InvalidOperationException($"Failed to execute {operationType} after {maxRetries} attempts");
  }

  #endregion

  #region Static Mappers

  private static Core.Enums.AccountType MapAccountType(Going.Plaid.Entity.AccountType plaidType)
  {
    return plaidType switch
    {
      Going.Plaid.Entity.AccountType.Depository => BuoyAI.Core.Enums.AccountType.Checking,
      Going.Plaid.Entity.AccountType.Credit => BuoyAI.Core.Enums.AccountType.CreditCard,
      Going.Plaid.Entity.AccountType.Investment => BuoyAI.Core.Enums.AccountType.Investment,
      _ => BuoyAI.Core.Enums.AccountType.Other
    };
  }

  private static TransactionCategory MapTransactionCategory(IReadOnlyList<string> categories)
  {
    if (categories?.Any() != true)
      return TransactionCategory.Uncategorized;

    var primaryCategory = categories[0].ToLowerInvariant();

    return primaryCategory switch
    {
      "food and drink" => TransactionCategory.DiningOut,
      "shops" => TransactionCategory.Shopping,
      "transportation" => TransactionCategory.Transportation,
      "recreation" => TransactionCategory.Entertainment,
      "healthcare" => TransactionCategory.Healthcare,
      "travel" => TransactionCategory.Travel,
      "service" => TransactionCategory.PersonalCare,
      "payment" => TransactionCategory.LoanPayments,
      _ => TransactionCategory.Uncategorized
    };
  }

  private static TransactionCategory MapTransactionCategoryFromPersonalFinance(Going.Plaid.Entity.PersonalFinanceCategory? personalFinanceCategory)
  {
    if (personalFinanceCategory?.Primary == null)
      return TransactionCategory.Uncategorized;

    var primaryCategory = personalFinanceCategory.Primary.ToLowerInvariant();

    return primaryCategory switch
    {
      "food_and_drink" => TransactionCategory.DiningOut,
      "general_merchandise" => TransactionCategory.Shopping,
      "transportation" => TransactionCategory.Transportation,
      "entertainment" => TransactionCategory.Entertainment,
      "medical" => TransactionCategory.Healthcare,
      "travel" => TransactionCategory.Travel,
      "personal_care" => TransactionCategory.PersonalCare,
      "loan_payments" => TransactionCategory.LoanPayments,
      "general_services" => TransactionCategory.Uncategorized,
      "government_and_non_profit" => TransactionCategory.Uncategorized,
      "rent_and_utilities" => TransactionCategory.Utilities,
      "income" => TransactionCategory.OtherIncome,
      _ => TransactionCategory.Uncategorized
    };
  }

  /// <summary>
  /// Maps Plaid confidence level string to our string representation
  /// </summary>
  private static string MapPlaidConfidenceString(string? confidenceLevel)
  {
    return confidenceLevel?.ToUpperInvariant() switch
    {
      "VERY_HIGH" => "VERY_HIGH",
      "HIGH" => "HIGH",
      "MEDIUM" => "MEDIUM",
      "LOW" => "LOW",
      _ => "UNKNOWN"
    };
  }

  /// <summary>
  /// Determines if Plaid confidence is high enough to auto-apply without review
  /// </summary>
  /// <summary>
  /// Determines if Plaid confidence is high enough to auto-apply without review.
  /// Only VERY_HIGH confidence skips review. HIGH confidence still requires user review.
  /// </summary>
  private static bool IsVeryHighConfidence(string? confidenceLevel)
  {
    var level = confidenceLevel?.ToUpperInvariant();
    return level == "VERY_HIGH";
  }

  /// <summary>
  /// Creates a transaction entity from Plaid transaction data, capturing ALL available enrichment data.
  /// Implements Plaid-first, AI-second categorization:
  /// - VERY_HIGH confidence + mapped category: Auto-apply, NeedsReview = false
  /// - HIGH confidence: Apply Plaid guess, NeedsReview = true (user should verify)
  /// - MEDIUM/LOW confidence: Apply Plaid guess, NeedsReview = true (AI will process later)
  /// - Any Uncategorized result: Always NeedsReview = true (user must categorize)
  /// </summary>
  /// <summary>
  /// The best description Plaid gives us for a transaction.
  ///
  /// Order matters, and so does including <c>Name</c>:
  /// <list type="bullet">
  ///   <item><c>MerchantName</c> — Plaid's enriched, human-readable merchant. Best when present,
  ///     but Plaid leaves it null for checks and account transfers ("no meaningful merchant").</item>
  ///   <item><c>Name</c> — "the merchant name or transaction description". Always present. This is
  ///     the one that rescues the rows above.</item>
  ///   <item><c>OriginalDescription</c> — only sent when the request sets
  ///     <c>include_original_description</c>, which we never do, so it is always null. Kept last
  ///     purely in case that option is enabled later.</item>
  /// </list>
  ///
  /// The old chain was <c>MerchantName ?? OriginalDescription ?? ""</c>, which skipped the only
  /// always-populated field and collapsed to empty for every check and transfer — ~19% of one
  /// real user's transactions arrived with no name at all.
  /// </summary>
  internal static string PlaidDescriptionOf(Going.Plaid.Entity.Transaction t)
  {
    if (!string.IsNullOrWhiteSpace(t.MerchantName)) return t.MerchantName;
    if (!string.IsNullOrWhiteSpace(t.Name)) return t.Name;
    if (!string.IsNullOrWhiteSpace(t.OriginalDescription)) return t.OriginalDescription;

    // Last resort, for the two kinds Plaid gives no merchant: a check, or an inter-bank
    // transfer. Anything here beats a nameless row.
    if (!string.IsNullOrWhiteSpace(t.CheckNumber)) return $"Check #{t.CheckNumber.Trim()}";

    var payee = t.PaymentMeta?.Payee;
    if (!string.IsNullOrWhiteSpace(payee)) return $"Transfer to {payee.Trim()}";

    var payer = t.PaymentMeta?.Payer;
    if (!string.IsNullOrWhiteSpace(payer)) return $"Transfer from {payer.Trim()}";

    return string.Empty;
  }

  /// <summary>
  /// Maps a Going.Plaid transaction to the provider-neutral record the ingest consumes,
  /// capturing ALL available enrichment data.
  /// Implements Plaid-first, AI-second categorization:
  /// - VERY_HIGH confidence + mapped category: Auto-apply, NeedsReview = false
  /// - HIGH confidence: Apply Plaid guess, NeedsReview = true (user should verify)
  /// - MEDIUM/LOW confidence: Apply Plaid guess, NeedsReview = true (AI will process later)
  /// - Any Uncategorized result: Always NeedsReview = true (user must categorize)
  /// </summary>
  internal static IncomingTransaction ToIncomingTransaction(Going.Plaid.Entity.Transaction plaidTransaction)
  {
    var originalAmount = (decimal)(plaidTransaction.Amount ?? 0);
    // Sign convention transformation:
    // - Plaid: positive = debit (money out), negative = credit (money in)
    // - BuoyAI: positive = income (money in), negative = expense (money out)
    var transformedAmount = -originalAmount;

    var primaryCounterparty = plaidTransaction.Counterparties?.FirstOrDefault();

    var plaidConfidenceString = MapPlaidConfidenceString(plaidTransaction.PersonalFinanceCategory?.ConfidenceLevel?.ToString());
    var counterpartyConfidenceString = MapPlaidConfidenceString(primaryCounterparty?.ConfidenceLevel?.ToString());

    // Map category first to check if it's Uncategorized
    var mappedCategory = MapTransactionCategoryFromPersonalFinance(plaidTransaction.PersonalFinanceCategory);

    // Determine if review is needed:
    // 1. Only VERY_HIGH confidence can skip review (not HIGH)
    // 2. Any transaction that maps to Uncategorized ALWAYS needs review
    var isVeryHighConfidence = IsVeryHighConfidence(plaidTransaction.PersonalFinanceCategory?.ConfidenceLevel?.ToString());
    var needsReview = !isVeryHighConfidence || mappedCategory == TransactionCategory.Uncategorized;
    var categorySource = isVeryHighConfidence && mappedCategory != TransactionCategory.Uncategorized
      ? "Plaid"
      : "Plaid-NeedsReview";

    return new IncomingTransaction
    {
      Provider = BankProvider.Plaid,
      ExternalTransactionId = plaidTransaction.TransactionId ?? string.Empty,
      ExternalAccountId = plaidTransaction.AccountId,
      PendingExternalTransactionId = plaidTransaction.PendingTransactionId,
      IsPending = plaidTransaction.Pending ?? false,
      Amount = transformedAmount,
      Direction = plaidTransaction.Amount > 0 ? TransactionType.Debit : TransactionType.Credit,
      Date = plaidTransaction.Date,
      AuthorizedDate = plaidTransaction.AuthorizedDate,
      Description = PlaidDescriptionOf(plaidTransaction),
      MerchantName = plaidTransaction.MerchantName,
      OriginalDescription = plaidTransaction.OriginalDescription,
      IsoCurrencyCode = plaidTransaction.IsoCurrencyCode,

      // Use unified CategoryId - system category values map directly
      CategoryId = (int)mappedCategory,
      NeedsReview = needsReview,
      CategorySource = categorySource,
      CategoryPrimary = plaidTransaction.PersonalFinanceCategory?.Primary,
      CategoryDetailed = plaidTransaction.PersonalFinanceCategory?.Detailed,
      CategoryConfidence = plaidConfidenceString,
      CategoryIconUrl = plaidTransaction.PersonalFinanceCategoryIconUrl,
      // Semantic tags from Plaid detailed category (for vector similarity matching)
      SemanticTags = Core.Utilities.SemanticTagVocabulary.ParsePlaidCategoryToTags(
        plaidTransaction.PersonalFinanceCategory?.Detailed),

      MerchantEntityId = plaidTransaction.MerchantEntityId ?? primaryCounterparty?.EntityId,
      MerchantLogoUrl = plaidTransaction.LogoUrl ?? primaryCounterparty?.LogoUrl,
      MerchantWebsite = primaryCounterparty?.Website,
      CounterpartyType = primaryCounterparty?.Type.ToString(),
      CounterpartyConfidence = counterpartyConfidenceString,

      LocationCity = plaidTransaction.Location?.City,
      LocationRegion = plaidTransaction.Location?.Region,
      LocationStoreNumber = plaidTransaction.Location?.StoreNumber,

      PaymentChannel = plaidTransaction.PaymentChannel?.ToString(),

      // Checks & transfers: the transactions Plaid gives no merchant for.
      CheckNumber = plaidTransaction.CheckNumber,
      TransactionCode = plaidTransaction.TransactionCode?.ToString(),
      PaymentPayee = plaidTransaction.PaymentMeta?.Payee,
      PaymentPayer = plaidTransaction.PaymentMeta?.Payer,
      PaymentReason = plaidTransaction.PaymentMeta?.Reason,
      PaymentReferenceNumber = plaidTransaction.PaymentMeta?.ReferenceNumber
    };
  }

  #endregion
}
