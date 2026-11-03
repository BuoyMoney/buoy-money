using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Ingest-time categorization policy. See <see cref="ITransactionIngestCategorizer"/> for the
/// contract; this is the one implementation, called by <see cref="PlaidService"/> right after
/// it persists new rows on any sync or import path.
///
/// Why it lives here and not in the sync controller: the controller was the only one of five
/// ingest paths that applied rules and queued for AI, and it applied rules only to the rows
/// Plaid was unsure about, so a rule written for a VERY_HIGH merchant could never fire on the
/// transaction it was written for.
/// </summary>
public class TransactionIngestCategorizationService : ITransactionIngestCategorizer
{
  /// <summary>
  /// How many months before the current one are auto-queued for AI. 1 (the default) means
  /// "this month and last". Rows older than that keep Plaid's category; the user can still
  /// send them from the Transactions page.
  /// </summary>
  public const string WindowMonthsKey = "AI:Categorization:AutoQueueWindowMonths";
  public const int DefaultWindowMonths = 1;

  private const string VeryHigh = "VERY_HIGH";

  private readonly BuoyAIDbContext _db;
  private readonly ICategorizationRuleService _rules;
  private readonly IAICategorizationService _categorization;
  private readonly IAICategorizationQueueSignal _signal;
  private readonly ILogger<TransactionIngestCategorizationService> _logger;
  /// <summary>Optional so the categorization tests need not wire savings; DI always supplies it.</summary>
  private readonly ISavingsService? _savingsService;
  private readonly int _windowMonths;

  // Null in tests that build the service directly: treated as AI on, the old behaviour.
  private readonly IAiProvider? _ai;

  public TransactionIngestCategorizationService(
    BuoyAIDbContext db,
    ICategorizationRuleService rules,
    IAICategorizationService categorization,
    IAICategorizationQueueSignal signal,
    IConfiguration configuration,
    ILogger<TransactionIngestCategorizationService> logger,
    ISavingsService? savingsService = null,
    IAiProvider? ai = null)
  {
    _ai = ai;
    _savingsService = savingsService;
    _db = db;
    _rules = rules;
    _categorization = categorization;
    _signal = signal;
    _logger = logger;
    _windowMonths = int.TryParse(configuration[WindowMonthsKey], out var months) && months >= 0
      ? months
      : DefaultWindowMonths;
  }

  /// <summary>First instant (UTC) of the auto-queue window: the 1st of the month, minus N months.</summary>
  public static DateTime WindowStart(DateTime nowUtc, int previousMonths) =>
    new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-previousMonths);

  internal static bool IsPlaidVeryHigh(Transaction t) =>
    string.Equals(t.PlaidCategoryConfidence, VeryHigh, StringComparison.OrdinalIgnoreCase);

  public async Task<IngestCategorizationResult> ProcessIngestedAsync(
    IReadOnlyCollection<Transaction> newTransactions,
    CancellationToken cancellationToken = default)
  {
    var result = new IngestCategorizationResult();
    if (newTransactions.Count == 0) return result;

    // The rows come straight from CreateManyAsync and carry only AccountId; resolve owners
    // once for the batch so a multi-account sync still runs one rules pass per user.
    var accountIds = newTransactions.Select(t => t.AccountId).Distinct().ToList();
    var owners = await _db.Accounts
      .AsNoTracking()
      .Where(a => accountIds.Contains(a.Id))
      .Select(a => new { a.Id, a.UserId })
      .ToDictionaryAsync(a => a.Id, a => a.UserId, cancellationToken);

    var byUser = newTransactions
      .Where(t => owners.ContainsKey(t.AccountId))
      .GroupBy(t => owners[t.AccountId]);

    foreach (var group in byUser)
    {
      try
      {
        await ProcessUserAsync(group.Key, group.ToList(), result, cancellationToken);
      }
      catch (Exception ex)
      {
        // Categorization bookkeeping must never fail the sync that produced the rows.
        _logger.LogError(ex, "Ingest categorization failed for user {UserId}; rows keep Plaid's category", group.Key);
      }
    }

    // Savings draws: a transfer out of a goal's account that equals what was earmarked settles
    // those draws without the user opening the app (0039). Same never-fail-the-sync rule.
    if (_savingsService != null)
    {
      try
      {
        await _savingsService.AutoCoverPendingDrawsAsync(newTransactions);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Auto-covering savings draws failed for {Count} new rows", newTransactions.Count);
      }
    }

    return result;
  }

  private async Task ProcessUserAsync(
    int userId,
    List<Transaction> transactions,
    IngestCategorizationResult result,
    CancellationToken cancellationToken)
  {
    var ids = transactions.Select(t => t.Id).ToList();

    // 1. Rules on EVERY new row, VERY_HIGH included. Rules are the user's explicit word.
    var (applied, unmatchedIds) = await _rules.ApplyRulesToTransactionIdsAsync(userId, ids);
    result.RulesApplied += applied;
    if (unmatchedIds.Count == 0) return;

    var unmatched = unmatchedIds.ToHashSet();
    var now = DateTime.UtcNow;
    var windowStart = WindowStart(now, _windowMonths);

    var candidates = transactions
      .Where(t => unmatched.Contains(t.Id) && !t.IsUserCategorized)
      .ToList();

    // 2. History stays cheap: nothing before the window is sent to AI automatically.
    var inWindow = candidates.Where(t => t.Date >= windowStart).ToList();
    result.OutsideWindow += candidates.Count - inWindow.Count;

    var toQueue = new List<int>();

    // 3. Plaid was unsure (anything below VERY_HIGH, or an unmapped category): AI decides.
    toQueue.AddRange(inWindow.Where(t => t.NeedsReview).Select(t => t.Id));

    // 4. Plaid was VERY_HIGH: trusted until this user has proven Plaid wrong for the merchant.
    var trusted = inWindow.Where(t => !t.NeedsReview).ToList();
    if (trusted.Count > 0)
    {
      var provenWrong = await ProvenWrongMerchantsAsync(
        userId, trusted.Select(t => t.MerchantName), cancellationToken);

      foreach (var t in trusted)
      {
        if (provenWrong.Contains(MerchantKey(t.MerchantName)))
          toQueue.Add(t.Id);
        else
          result.AcceptedFromPlaid++;
      }
    }

    if (toQueue.Count == 0) return;

    // AI off still queues: the drainer leaves the queue alone until a provider is configured,
    // so these rows get categorized once one is turned on.
    await _categorization.QueueTransactionsForCategorizationAsync(userId, toQueue);
    result.QueuedForAi += toQueue.Count;
    result.QueuedTransactionIds.AddRange(toQueue);
    _signal.Notify(userId);

    _logger.LogInformation(
      "Ingest categorization for user {UserId}: {Rules} by rule, {Queued} queued for AI, {Plaid} kept from Plaid, {Old} outside window",
      userId, applied, toQueue.Count, result.AcceptedFromPlaid, result.OutsideWindow);
  }

  /// <summary>
  /// Merchants (lower-cased) where this user's own confirmed history contradicts Plaid: either
  /// they recategorized a VERY_HIGH row of that merchant before, or they file the merchant
  /// under more than one category (Amazon as groceries AND household AND gifts). For those,
  /// Plaid's confidence is not evidence and the AI gets the row with the user's context.
  /// </summary>
  private async Task<HashSet<string>> ProvenWrongMerchantsAsync(
    int userId,
    IEnumerable<string?> merchantNames,
    CancellationToken cancellationToken)
  {
    var keys = merchantNames
      .Select(MerchantKey)
      .Where(k => k.Length > 0)
      .Distinct()
      .ToList();
    if (keys.Count == 0) return new HashSet<string>();

    var history = await _db.Transactions
      .AsNoTracking()
      .Where(t => t.Account.UserId == userId
                  && t.IsUserCategorized
                  && t.MerchantName != null
                  && keys.Contains(t.MerchantName.ToLower()))
      .Select(t => new { t.MerchantName, t.CategoryId, t.PlaidCategoryConfidence })
      .ToListAsync(cancellationToken);

    return history
      .GroupBy(h => MerchantKey(h.MerchantName))
      .Where(g => g.Any(h => string.Equals(h.PlaidCategoryConfidence, VeryHigh, StringComparison.OrdinalIgnoreCase))
                  || g.Select(h => h.CategoryId).Distinct().Count() > 1)
      .Select(g => g.Key)
      .ToHashSet();
  }

  private static string MerchantKey(string? merchantName) =>
    string.IsNullOrWhiteSpace(merchantName) ? string.Empty : merchantName.Trim().ToLowerInvariant();
}
