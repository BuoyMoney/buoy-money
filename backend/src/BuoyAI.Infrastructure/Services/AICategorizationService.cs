using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.Utilities;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Ai;
using Microsoft.Extensions.Logging.Abstractions;

using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Represents a category pattern learned from user behavior.
/// Can be either a system category or a custom user category.
/// <paramref name="CategoryCount"/> is how many distinct categories the user has filed this
/// merchant under: 1 means a stable habit that can be applied without an AI call; more than
/// 1 means a split merchant (Amazon as groceries AND household) where only the amount, date
/// and description can tell the rows apart, so the deterministic shortcuts must stand aside.
/// </summary>
public record CategoryPattern(
    TransactionCategory SystemCategory,
    int? UserCategoryId,
    string DisplayName,
    int CategoryCount = 1,
    IReadOnlyList<MerchantSplit>? Splits = null)
{
    public bool IsSplit => CategoryCount > 1;

    /// <summary>Per-category breakdown of the merchant's confirmed rows; empty when unknown.</summary>
    public IReadOnlyList<MerchantSplit> Splits { get; init; } = Splits ?? Array.Empty<MerchantSplit>();
}

public class AICategorizationService : IAICategorizationService
{
    private readonly ITransactionRepository _transactionRepository;
    private readonly IUserRepository _userRepository;
    private readonly IEntitlementService _entitlements;
    private readonly ICategorizationRuleService _categorizationRuleService;
    private readonly ITransactionEmbeddingService _transactionEmbeddingService;
    private readonly ICategoryRepository _categoryRepository;
    private readonly BuoyAIDbContext _dbContext;
    private readonly ILogger<AICategorizationService> _logger;
    private readonly AIModelConfiguration _modelConfiguration;
    private readonly IAIUsageTracker? _usageTracker;
    private readonly ICostTrackingService? _costTracking;
    public const string UsageContext = "categorization";
    /// <summary>
    /// How many Gemini calls one batch runs at once. Each call takes about 2 s, so a first
    /// sync of 100 queued rows took over 3 minutes one at a time and the progress line crawled
    /// ("5, then nothing for 10 seconds"). Only the network call runs concurrently; everything
    /// that touches the DbContext stays sequential.
    /// </summary>
    public const string ModelConcurrencyKey = "AI:Categorization:ModelConcurrency";
    public const int DefaultModelConcurrency = 6;
    private readonly int _modelConcurrency;
    private readonly IAiProvider _ai;
    private readonly double _reviewThreshold;
    private readonly double _autoApplyThreshold;
    private const double VectorSimilarityThreshold = 0.82;
    
    // Category definitions for AI context
    private static readonly Dictionary<TransactionCategory, string> CategoryDescriptions = new()
    {
        [TransactionCategory.Salary] = "Regular employment income, paychecks",
        [TransactionCategory.Bonus] = "One-time bonuses, commissions, incentive pay",
        [TransactionCategory.Investment] = "Dividends, interest income, capital gains",
        [TransactionCategory.OtherIncome] = "Refunds, reimbursements, other miscellaneous income",
        [TransactionCategory.Rent] = "Rent or mortgage payments",
        [TransactionCategory.Utilities] = "Electric, gas, water, internet, phone bills",
        [TransactionCategory.Insurance] = "Health, auto, home, life insurance premiums",
        [TransactionCategory.Subscriptions] = "Streaming services, software subscriptions, memberships",
        [TransactionCategory.LoanPayments] = "Student loans, car loans, personal loans",
        [TransactionCategory.Groceries] = "Supermarket purchases, grocery stores, food shopping",
        [TransactionCategory.DiningOut] = "Restaurants, fast food, cafes, bars, food delivery",
        [TransactionCategory.Transportation] = "Gas, parking, public transit, rideshare, car maintenance",
        [TransactionCategory.Entertainment] = "Movies, concerts, games, hobbies, recreation",
        [TransactionCategory.Shopping] = "Retail purchases, online shopping, clothing, electronics",
        [TransactionCategory.Healthcare] = "Doctor visits, prescriptions, dental, vision, medical supplies",
        [TransactionCategory.Travel] = "Hotels, flights, vacation expenses, travel bookings",
        [TransactionCategory.PersonalCare] = "Haircuts, spa, cosmetics, gym memberships",
        [TransactionCategory.Education] = "Tuition, books, courses, educational materials",
        [TransactionCategory.Gifts] = "Presents, donations, charitable giving",
        [TransactionCategory.CreditPayment] = "Credit card payments, paying off debt",
        [TransactionCategory.Transfer] = "Bank transfers between accounts, internal movements"
    };

    public AICategorizationService(
        ITransactionRepository transactionRepository,
        IUserRepository userRepository,
        IEntitlementService entitlements,
        ICategorizationRuleService categorizationRuleService,
        ITransactionEmbeddingService transactionEmbeddingService,
        ICategoryRepository categoryRepository,
        BuoyAIDbContext dbContext,
        IConfiguration configuration,
        ILogger<AICategorizationService> logger,
        AIModelConfiguration modelConfiguration,
        GeminiUsageReporter geminiUsage,
        // Optional so the many direct constructions in tests keep compiling; DI supplies both.
        IAIUsageTracker? usageTracker = null,
        ICostTrackingService? costTracking = null,
        // Optional so the many direct constructions in tests keep compiling; DI supplies the
        // configured provider. Without one the service uses Gemini, as it always has.
        IAiProvider? aiProvider = null)
    {
        _transactionRepository = transactionRepository;
        _userRepository = userRepository;
        _entitlements = entitlements;
        _categorizationRuleService = categorizationRuleService;
        _transactionEmbeddingService = transactionEmbeddingService;
        _categoryRepository = categoryRepository;
        _dbContext = dbContext;
        _logger = logger;
        _modelConfiguration = modelConfiguration;
        _usageTracker = usageTracker;
        _costTracking = costTracking;
        
        var geminiSettings = configuration.GetSection("Gemini");
        _ai = aiProvider ?? new GeminiNativeProvider(configuration, NullLogger<GeminiNativeProvider>.Instance, geminiUsage);

        // Warn if AI is not set up - AI categorization will gracefully degrade
        if (!_ai.IsConfigured)
        {
            _logger.LogWarning(
                "AI provider '{Provider}' is not configured. AI-powered transaction categorization will be disabled. " +
                "For Gemini set GOOGLE_API_KEY or Gemini:ApiKey; for another provider see the Ai:* settings.", _ai.Id);
        }
        
        // Confidence thresholds from config or defaults
        _reviewThreshold = double.Parse(geminiSettings["CategorizationReviewThreshold"] ?? "0.7");
        _autoApplyThreshold = double.Parse(geminiSettings["CategorizationAutoApplyThreshold"] ?? "0.85");
        _modelConcurrency = int.TryParse(configuration[ModelConcurrencyKey], out var concurrency) && concurrency > 0
            ? Math.Min(concurrency, 16)
            : DefaultModelConcurrency;

        // Same window the ingest pipeline auto-queues: the manual button offers exactly the
        // rows the pipeline would have sent had they arrived today, never the deep backlog.
        _autoQueueWindowMonths = int.TryParse(
            configuration[TransactionIngestCategorizationService.WindowMonthsKey], out var months) && months >= 0
            ? months
            : TransactionIngestCategorizationService.DefaultWindowMonths;
    }

    private readonly int _autoQueueWindowMonths;

    /// <summary>
    /// Rows the manual Auto-Categorize button may send: pending review, never attempted by
    /// the AI, and dated inside the auto-queue window (this month and last by default).
    /// </summary>
    private bool IsAiEligible(Transaction t, DateTime now) =>
        IsPendingReview(t, now)
        && !t.AISuggestedCategoryId.HasValue
        && t.AICategorizationDate == null
        && t.Date >= TransactionIngestCategorizationService.WindowStart(now, _autoQueueWindowMonths);

    public async Task<AICategorizationResult> CategorizeTransactionAsync(int transactionId, int userId)
    {
        var result = new AICategorizationResult { TransactionId = transactionId };
        
        try
        {
            var transaction = await _transactionRepository.GetByIdAsync(transactionId);
            if (transaction == null)
            {
                result.Error = "Transaction not found";
                return result;
            }

            // Try vector similarity first
            var vectorResult = await TryVectorSimilarityCategorizationAsync(transaction, userId);
            if (vectorResult != null)
            {
                result.SuggestedCategoryId = vectorResult.CategoryId;
                result.SuggestedCategoryName = vectorResult.CategoryName;
                result.Confidence = vectorResult.Confidence;
                result.NeedsReview = false;
            }
            else
            {
                var userPatterns = await GetUserCategorizationPatternsAsync(userId);
                var customCategories = await GetUserCustomCategoriesAsync(userId);
                var similarContext = await GetVectorSimilarityContextAsync(transaction, userId);
                var aiResult = await CallGeminiForCategorizationAsync(transaction, userPatterns, customCategories, userId, vectorContext: similarContext);

                var unifiedCategoryId = aiResult.CustomCategoryId ?? (int)aiResult.SystemCategory;

                result.SuggestedCategoryId = unifiedCategoryId;
                result.SuggestedCategoryName = aiResult.DisplayName;
                result.Confidence = aiResult.Confidence;
                result.NeedsReview = aiResult.Confidence < _reviewThreshold;

                if (!string.IsNullOrEmpty(aiResult.SemanticTags))
                {
                    transaction.SemanticTags = aiResult.SemanticTags;
                }
            }

            // Update transaction with suggestion
            transaction.AISuggestedCategoryId = result.SuggestedCategoryId;
            transaction.AIConfidence = result.Confidence;
            transaction.AICategorizationDate = DateTime.UtcNow;
            transaction.NeedsReview = result.NeedsReview;

            var isUncategorized = !transaction.CategoryId.HasValue ||
                transaction.CategoryId == (int)TransactionCategory.Uncategorized;
            if (isUncategorized && !transaction.IsUserCategorized)
            {
                transaction.CategoryId = result.SuggestedCategoryId;
                result.Applied = true;
            }

            await _transactionRepository.UpdateAsync(transaction);

            _logger.LogInformation(
                "Categorized transaction {TransactionId}: {Category} with {Confidence:P0} confidence (Applied: {Applied})",
                transactionId, result.SuggestedCategoryName, result.Confidence, result.Applied);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error categorizing transaction {TransactionId}", transactionId);
            result.Error = ex.Message;
        }
        
        return result;
    }

    public async Task<AICategorizationResponse> BatchCategorizeAsync(int userId, AICategorizationRequest request)
    {
        var response = new AICategorizationResponse();
        
        try
        {
            // Get transactions to categorize
            var transactions = await GetTransactionsToCategorizeAsync(userId, request);
            response.TotalProcessed = transactions.Count;
            
            if (transactions.Count == 0)
            {
                _logger.LogInformation("No transactions to categorize for user {UserId}", userId);
                return response;
            }
            
            // Get user patterns once for the batch
            var userPatterns = await GetUserCategorizationPatternsAsync(userId);
            
            // Process in batches to avoid overwhelming the AI API
            const int batchSize = 10;
            foreach (var batch in transactions.Chunk(batchSize))
            {
                var batchResults = await CategorizeTransactionBatchAsync(batch.ToList(), userPatterns, userId);
                
                foreach (var result in batchResults)
                {
                    response.Results.Add(result);
                    
                    if (result.Error != null)
                    {
                        response.Errors++;
                    }
                    else
                    {
                        response.SuccessfullyCategorized++;
                        
                        if (result.Confidence >= 0.8)
                            response.HighConfidence++;
                        else if (result.Confidence >= 0.5)
                            response.MediumConfidence++;
                        else
                            response.LowConfidence++;
                        
                        if (result.NeedsReview)
                            response.NeedingReview++;
                    }
                }
                
                // Small delay between batches to avoid rate limiting
                await Task.Delay(100);
            }
            
            _logger.LogInformation(
                "Batch categorization complete for user {UserId}: {Processed} processed, {Success} succeeded, {Errors} errors",
                userId, response.TotalProcessed, response.SuccessfullyCategorized, response.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in batch categorization for user {UserId}", userId);
            throw;
        }
        
        return response;
    }

    /// <summary>
    /// THE definition of "this transaction is waiting for the user to review it".
    ///
    /// Shared by the banner count, the review queue, and the bulk dismiss so they cannot
    /// disagree: the banner previously promised a count the queue had to reproduce by hand,
    /// and a bulk dismiss that drifted from either would silently clear the wrong rows.
    ///
    /// Earmarking to a savings goal IS the user's decision about the transaction: it's
    /// excluded from budget totals entirely, so there is no category call left to make.
    /// Leaving it here asked the user to re-decide, and the only action on offer — assigning
    /// a category — silently reverses the earmark (the category endpoint auto-reverses it),
    /// destroying the work. Filtered rather than flag-mutated so removing the earmark returns
    /// it to the queue if it still needs review.
    /// </summary>
    private static bool IsPendingReview(Transaction t, DateTime now) =>
        t.NeedsReview
        && !t.IsUserCategorized
        && !t.SavingsGoalId.HasValue
        // Currently snoozed via SkipUntil.
        && (!t.SkipUntil.HasValue || t.SkipUntil.Value <= now);

    /// <summary>
    /// Start of the current month in UTC — the boundary between "this month" and the
    /// backlog, used by the banner split, the review queue scope, and the bulk dismiss.
    /// </summary>
    private static DateTime CurrentMonthStart(DateTime now) =>
        new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Loads a transaction and verifies the caller may act on it.
    ///
    /// The single-transaction review actions (accept / reject / skip) fetch by id alone and
    /// only ever used userId for their log line, so ANY authenticated user could accept,
    /// recategorise, or snooze ANY transaction by guessing an id. Access mirrors
    /// TransactionService.GetTransactionByIdAsync: the owning account's user, or a member of
    /// that user's family.
    ///
    /// Throws KeyNotFoundException rather than a distinct "forbidden" so a caller cannot
    /// probe which ids exist.
    /// </summary>
    private async Task<Transaction> GetOwnedTransactionAsync(int transactionId, int userId)
    {
        var transaction = await _transactionRepository.GetByIdAsync(transactionId);
        if (transaction == null)
            throw new KeyNotFoundException($"Transaction {transactionId} not found");

        var ownerUserId = transaction.Account?.UserId;
        if (ownerUserId == userId)
            return transaction;

        var user = await _userRepository.GetByIdAsync(userId);
        if (user?.FamilyId != null && ownerUserId.HasValue)
        {
            var owner = await _userRepository.GetByIdAsync(ownerUserId.Value);
            if (owner?.FamilyId == user.FamilyId)
                return transaction;
        }

        _logger.LogWarning(
            "User {UserId} attempted to act on transaction {TransactionId} owned by user {OwnerId}",
            userId, transactionId, ownerUserId);
        throw new KeyNotFoundException($"Transaction {transactionId} not found");
    }

    public async Task<int> DismissOlderReviewsAsync(int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        var transactions = await _transactionRepository.GetByUserOrFamilyAsync(
            userId, user?.FamilyId, null, null);

        var now = DateTime.UtcNow;
        // Deliberately NOT a caller-supplied date. The cutoff is the same boundary the banner
        // already shows as "N from earlier months", so what the user dismisses is exactly what
        // they were just told the backlog was. A client-chosen cutoff could clear this month's
        // transactions too, which is unrecoverable through the UI.
        var cutoff = CurrentMonthStart(now);

        var ids = transactions
            .Where(t => IsPendingReview(t, now) && t.Date < cutoff)
            .Select(t => t.Id)
            .ToList();

        if (ids.Count == 0)
        {
            return 0;
        }

        var dismissed = await _transactionRepository.ClearNeedsReviewAsync(ids);

        _logger.LogInformation(
            "User {UserId} dismissed {Count} transaction(s) from review dated before {Cutoff}",
            userId, dismissed, cutoff);

        return dismissed;
    }

    public async Task<ReviewStatisticsDto> GetReviewStatisticsAsync(int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        var transactions = await _transactionRepository.GetByUserOrFamilyAsync(
            userId, user?.FamilyId, null, null);

        var now = DateTime.UtcNow;
        var currentMonthStart = CurrentMonthStart(now);

        var needsReview = transactions.Where(t => IsPendingReview(t, now)).ToList();

        // Uncategorized (null / Uncategorized enum) is a SUBSET of needsReview; the rest are
        // AI-categorized-but-low-confidence. Surfaced separately so the banner can be explicit.
        var uncategorizedCategoryId = (int)TransactionCategory.Uncategorized;
        var uncategorized = needsReview.Where(t =>
            !t.CategoryId.HasValue || t.CategoryId == uncategorizedCategoryId).ToList();
        var lowConfidence = needsReview.Where(t =>
            t.AISuggestedCategoryId.HasValue &&
            t.CategoryId.HasValue && t.CategoryId != uncategorizedCategoryId).ToList();
        // Never-AI-attempted review items inside the auto-queue window: the real Auto-Categorize
        // workload. Includes rows Plaid categorized with low confidence (the AI knows the user's
        // rules and patterns; Plaid does not), excludes anything the AI already tried so an
        // inconclusive pass isn't re-offered, and excludes the deep backlog (see IsAiEligible).
        var aiEligible = needsReview.Where(t => IsAiEligible(t, now)).ToList();

        // Rows the ingest pipeline has queued and the background drainer has not yet processed:
        // the page shows its "AI is analyzing" indicator while this is non-zero.
        var queuedForAi = await GetQueuedTransactionCountAsync(userId);

        return new ReviewStatisticsDto
        {
            TotalNeedingReview = needsReview.Count,
            UncategorizedCount = uncategorized.Count,
            LowConfidenceCount = lowConfidence.Count,
            AiEligibleCount = aiEligible.Count,
            QueuedForAiCount = queuedForAi,
            // This-month vs older split so the banner/queue can default to the current month and
            // offer the backlog explicitly (both are subsets summing to TotalNeedingReview).
            ThisMonthCount = needsReview.Count(t => t.Date >= currentMonthStart),
            OlderCount = needsReview.Count(t => t.Date < currentMonthStart),
            OldestPendingReview = needsReview.OrderBy(t => t.Date).FirstOrDefault()?.Date
        };
    }

    public async Task<List<TransactionDto>> GetTransactionsNeedingReviewAsync(
        int userId, int? limit = null, ReviewScope scope = ReviewScope.All)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        var transactions = await _transactionRepository.GetByUserOrFamilyAsync(
            userId, user?.FamilyId, null, null);

        var now = DateTime.UtcNow;
        var currentMonthStart = CurrentMonthStart(now);

        var needsReview = transactions
            .Where(t => IsPendingReview(t, now))
            // Scope to the requested slice. The client defaults the review queue to ThisMonth so
            // first-run isn't overwhelming, then pages into Older as an explicit "clear the
            // backlog" step. All = the full backlog (no date filter).
            .Where(t => scope switch
            {
                ReviewScope.ThisMonth => t.Date >= currentMonthStart,
                ReviewScope.Older => t.Date < currentMonthStart,
                _ => true,
            })
            .OrderByDescending(t => t.AIConfidence ?? 0) // High confidence first (user can approve quickly)
            .ThenByDescending(t => t.Date) // Then by date (newest first)
            .ToList();

        if (limit.HasValue)
            needsReview = needsReview.Take(limit.Value).ToList();

        return needsReview.Select(ToDto).ToList();
    }

    public async Task<TransactionDto> AcceptAISuggestionAsync(int transactionId, int userId)
    {
        var transaction = await GetOwnedTransactionAsync(transactionId, userId);

        if (!transaction.AISuggestedCategoryId.HasValue)
            throw new InvalidOperationException("Transaction has no AI suggestion to accept");

        // Apply the AI suggestion as user-accepted (using unified CategoryId)
        transaction.CategoryId = transaction.AISuggestedCategoryId.Value;
        transaction.IsUserCategorized = true;
        transaction.NeedsReview = false;

        var updated = await _transactionRepository.UpdateAsync(transaction);

        // Store confirmed categorization in vector DB for future similarity matching
        var categoryName = updated.CategoryNav?.Name
            ?? (updated.CategoryId.HasValue && updated.CategoryId.Value < 1000
                ? ((TransactionCategory)updated.CategoryId.Value).ToString()
                : "Unknown");
        await _transactionEmbeddingService.StoreTransactionEmbeddingAsync(
            userId, updated, updated.CategoryId!.Value, categoryName, updated.SemanticTags);

        _logger.LogInformation(
            "User {UserId} accepted AI suggestion for transaction {TransactionId}: CategoryId={CategoryId}",
            userId, transactionId, transaction.CategoryId);

        return ToDto(updated);
    }

    public async Task<TransactionDto> RejectAISuggestionAsync(int transactionId, int userId, int correctCategoryId)
    {
        var transaction = await GetOwnedTransactionAsync(transactionId, userId);

        // Validate the target category exists before assigning. Without this, the
        // reject flow could not reach the user's own custom categories (Id >= 1000),
        // and an undefined enum value (e.g. 0 when CorrectCategory is omitted) would
        // set CategoryId to a non-existent row and blow up on the FK -> unhandled 500.
        // System categories are enum-backed (1-99); custom categories (1000+) must
        // exist and be accessible to the caller/their family.
        bool categoryValid;
        if (correctCategoryId < 1000)
        {
            categoryValid = Enum.IsDefined(typeof(TransactionCategory), correctCategoryId);
        }
        else
        {
            var user = await _userRepository.GetByIdAsync(userId);
            categoryValid = await _categoryRepository.ExistsAndAccessibleAsync(correctCategoryId, userId, user?.FamilyId);
        }

        if (!categoryValid)
            throw new ArgumentException($"Category {correctCategoryId} does not exist or is not accessible.");

        // Apply the correct category (unified CategoryId)
        transaction.CategoryId = correctCategoryId;
        transaction.IsUserCategorized = true;
        transaction.NeedsReview = false;

        var updated = await _transactionRepository.UpdateAsync(transaction);

        // Resolve a display name for the vector-store embedding.
        var categoryName = updated.CategoryNav?.Name
            ?? (correctCategoryId < 1000
                ? ((TransactionCategory)correctCategoryId).ToString()
                : (await _categoryRepository.GetByIdAsync(correctCategoryId))?.Name ?? "Custom");

        // Store corrected categorization in vector DB (upserts over any previous embedding)
        await _transactionEmbeddingService.StoreTransactionEmbeddingAsync(
            userId, updated, updated.CategoryId!.Value, categoryName, updated.SemanticTags);

        _logger.LogInformation(
            "User {UserId} rejected AI suggestion for transaction {TransactionId}: AI suggested CategoryId={AISuggested}, user set CategoryId={UserCategory}",
            userId, transactionId, transaction.AISuggestedCategoryId, transaction.CategoryId);

        return ToDto(updated);
    }

    public async Task<TransactionDto> SkipTransactionReviewAsync(int transactionId, int userId, int days = 2)
    {
        var transaction = await GetOwnedTransactionAsync(transactionId, userId);
        
        // Set skip until date
        transaction.SkipUntil = DateTime.UtcNow.AddDays(days);
        
        var updated = await _transactionRepository.UpdateAsync(transaction);
        
        _logger.LogInformation(
            "User {UserId} skipped transaction {TransactionId} for {Days} days (until {SkipUntil})",
            userId, transactionId, days, transaction.SkipUntil);
        
        return ToDto(updated);
    }

    public async Task QueueTransactionsForCategorizationAsync(int userId, List<int> transactionIds)
    {
        if (transactionIds.Count == 0) return;
        
        // Check which transactions are already queued (pending or processing)
        var existingQueuedIds = await _dbContext.AICategorizationQueue
            .Where(q => transactionIds.Contains(q.TransactionId) && 
                       (q.Status == AICategorizationQueueStatus.Pending || 
                        q.Status == AICategorizationQueueStatus.Processing))
            .Select(q => q.TransactionId)
            .ToListAsync();
        
        var newTransactionIds = transactionIds.Except(existingQueuedIds).ToList();
        
        if (newTransactionIds.Count == 0)
        {
            _logger.LogDebug("All {Count} transactions already queued for user {UserId}", 
                transactionIds.Count, userId);
            return;
        }
        
        // Add new items to queue
        var queueItems = newTransactionIds.Select(txnId => new AICategorizationQueueItem
        {
            TransactionId = txnId,
            UserId = userId,
            QueuedAt = DateTime.UtcNow,
            Status = AICategorizationQueueStatus.Pending
        });
        
        _dbContext.AICategorizationQueue.AddRange(queueItems);
        await _dbContext.SaveChangesAsync();
        
        _logger.LogInformation(
            "Queued {Count} transactions for AI categorization for user {UserId}",
            newTransactionIds.Count, userId);
    }

    public async Task<int> ProcessCategorizationQueueAsync(int userId, int maxBatchSize = 20)
    {
        // Get pending queue items for this user
        var pendingItems = await _dbContext.AICategorizationQueue
            .Where(q => q.UserId == userId && q.Status == AICategorizationQueueStatus.Pending)
            .OrderBy(q => q.QueuedAt)
            .Take(maxBatchSize)
            .ToListAsync();
        
        if (pendingItems.Count == 0)
        {
            _logger.LogDebug("No pending queue items for user {UserId}", userId);
            return 0;
        }
        
        // Mark as processing
        foreach (var item in pendingItems)
        {
            item.Status = AICategorizationQueueStatus.Processing;
        }
        await _dbContext.SaveChangesAsync();
        
        var transactionIds = pendingItems.Select(q => q.TransactionId).ToList();
        
        _logger.LogInformation(
            "Processing {Count} queued transactions for AI categorization for user {UserId}",
            pendingItems.Count, userId);
        
        try
        {
            // Get the transactions
            var transactions = await _dbContext.Transactions
                .Where(t => transactionIds.Contains(t.Id))
                .ToListAsync();
            
            // Filter out transactions that no longer need categorization. Same attempt-once
            // and snooze semantics as the manual Auto-Categorize path: a row the AI already
            // tried is not re-sent (the model would not get more confident), a snoozed row is
            // left alone until its snooze ends, and an earmarked row already has the user's
            // decision.
            var now = DateTime.UtcNow;
            var validTransactions = transactions
                .Where(t => !t.IsUserCategorized
                            && !t.AISuggestedCategoryId.HasValue
                            && t.AICategorizationDate == null
                            && !t.SavingsGoalId.HasValue
                            && (!t.SkipUntil.HasValue || t.SkipUntil.Value <= now))
                .ToList();
            
            // Skip transactions that were already categorized or deleted
            var skippedIds = transactionIds.Except(validTransactions.Select(t => t.Id)).ToList();
            foreach (var item in pendingItems.Where(q => skippedIds.Contains(q.TransactionId)))
            {
                item.Status = AICategorizationQueueStatus.Skipped;
                item.ProcessedAt = DateTime.UtcNow;
            }
            
            if (validTransactions.Count == 0)
            {
                await _dbContext.SaveChangesAsync();
                return 0;
            }
            
            // Get user patterns once for the batch
            var userPatterns = await GetUserCategorizationPatternsAsync(userId);
            
            // Process in smaller batches to avoid API rate limits
            const int apiBatchSize = 10;
            var processedCount = 0;
            
            foreach (var batch in validTransactions.Chunk(apiBatchSize))
            {
                var batchResults = await CategorizeTransactionBatchAsync(batch.ToList(), userPatterns, userId);
                
                foreach (var result in batchResults)
                {
                    var queueItem = pendingItems.FirstOrDefault(q => q.TransactionId == result.TransactionId);
                    if (queueItem != null)
                    {
                        if (result.Error != null)
                        {
                            queueItem.Status = AICategorizationQueueStatus.Failed;
                            queueItem.ErrorMessage = result.Error;
                            queueItem.RetryCount++;
                        }
                        else
                        {
                            queueItem.Status = AICategorizationQueueStatus.Completed;
                            processedCount++;
                        }
                        queueItem.ProcessedAt = DateTime.UtcNow;
                    }
                }
                
                // Small delay between API batches
                if (validTransactions.Count > apiBatchSize)
                    await Task.Delay(100);
            }
            
            await _dbContext.SaveChangesAsync();
            
            _logger.LogInformation(
                "Completed processing {Processed} of {Total} queued transactions for user {UserId}",
                processedCount, pendingItems.Count, userId);
            
            return processedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing categorization queue for user {UserId}", userId);
            
            // Mark all as failed
            foreach (var item in pendingItems.Where(q => q.Status == AICategorizationQueueStatus.Processing))
            {
                item.Status = AICategorizationQueueStatus.Failed;
                item.ErrorMessage = ex.Message;
                item.RetryCount++;
                item.ProcessedAt = DateTime.UtcNow;
            }
            await _dbContext.SaveChangesAsync();
            
            throw;
        }
    }

    public async Task<int> GetQueuedTransactionCountAsync(int userId)
    {
        return await _dbContext.AICategorizationQueue
            .CountAsync(q => q.UserId == userId && q.Status == AICategorizationQueueStatus.Pending);
    }

    private async Task<List<Transaction>> GetTransactionsToCategorizeAsync(int userId, AICategorizationRequest request)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        var transactions = await _transactionRepository.GetByUserOrFamilyAsync(
            userId, user?.FamilyId, request.StartDate, null);
        
        var query = transactions.AsQueryable();
        
        // Filter by specific IDs if provided
        if (request.TransactionIds?.Count > 0)
        {
            query = query.Where(t => request.TransactionIds.Contains(t.Id));
        }
        
        // Exclude transfers and credit card payments - these inflate "spending" totals
        // and confuse the AI since they're not actual purchases
        var creditPaymentId = (int)TransactionCategory.CreditPayment;
        var transferId = (int)TransactionCategory.Transfer;
        query = query.Where(t => 
            !t.CategoryId.HasValue || 
            (t.CategoryId != creditPaymentId && t.CategoryId != transferId));
        
        // Also exclude transactions that look like transfers based on description patterns
        query = query.Where(t => !IsLikelyTransferDescription(t.Description ?? ""));
        
        // Only transactions that haven't been AI-categorized yet, and (for the un-targeted
        // Auto-Categorize button) only ones actually waiting for review, so the batch works the
        // same set the banner counts (AiEligibleCount): Plaid low-confidence rows included,
        // already-attempted rows excluded.
        if (!request.RecategorizeExisting)
        {
            var now = DateTime.UtcNow;
            query = query.Where(t =>
                !t.IsUserCategorized &&
                !t.AISuggestedCategoryId.HasValue &&
                t.AICategorizationDate == null);
            if (request.TransactionIds == null || request.TransactionIds.Count == 0)
            {
                query = query.Where(t => IsAiEligible(t, now));
            }
        }

        return query.Take(request.MaxTransactions).ToList();
    }
    
    /// <summary>
    /// Detects if a transaction description looks like a transfer or credit card payment.
    /// These should be excluded from AI categorization.
    /// </summary>
    private static bool IsLikelyTransferDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        
        var lower = description.ToLowerInvariant();
        
        // Common transfer patterns
        return lower.Contains("payment thank you") ||      // Credit card payment confirmations
               lower.Contains("autopay payment") ||        // Auto credit card payments
               lower.Contains("online payment") ||         // Online bill pay transfers
               lower.Contains("transfer to") ||            // Explicit transfers
               lower.Contains("transfer from") ||
               lower.Contains("xfer to") ||
               lower.Contains("xfer from") ||
               lower.Contains("internal transfer") ||
               lower.Contains("ach transfer") ||
               lower.Contains("wire transfer") ||
               lower.Contains("zelle") ||                  // P2P transfers
               lower.Contains("venmo") ||
               lower.Contains("paypal transfer") ||
               lower.Contains("mobile deposit") ||         // Check deposits (income, not expense)
               lower.Contains("atm deposit") ||
               lower.Contains("direct dep") ||             // Direct deposits
               lower.Contains("overdraft transfer") ||
               lower.Contains("balance transfer") ||
               lower.Contains("credit card payment");
    }

    /// <summary>
    /// Gets user categorization patterns including custom categories.
    /// Returns merchant -> CategoryPattern mappings based on user's past categorization behavior.
    /// </summary>
    private async Task<Dictionary<string, CategoryPattern>> GetUserCategorizationPatternsAsync(int userId)
    {
        var patterns = new Dictionary<string, CategoryPattern>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            var transactions = await _transactionRepository.GetByUserOrFamilyAsync(
                userId, user?.FamilyId, DateTime.UtcNow.AddMonths(-6), null);
            
            // Merchant -> category patterns from user-categorized transactions, with the
            // per-category splits (count, average amount, weekday/weekend) the prompt uses to
            // tell a split merchant's rows apart. Unified CategoryId (1-99 system, 1000+ custom).
            patterns = CategorizationPromptSignals.BuildPatterns(transactions);

            _logger.LogDebug("Loaded {Count} categorization patterns for user {UserId} ({CustomCount} custom)", 
                patterns.Count, userId, patterns.Count(p => p.Value.UserCategoryId.HasValue));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load user categorization patterns for user {UserId}", userId);
        }
        
        return patterns;
    }
    
    /// <summary>
    /// Gets user's custom categories for inclusion in AI prompts.
    /// Reads from the unified Categories table (custom categories have Id >= 1000)
    /// so that categories created via the live app (POST /categories, which writes
    /// ONLY to Categories, never the legacy UserCategories table) are actually
    /// offered to the model and can be parsed back. The real unified Id is the
    /// token used in the prompt / parser, so no +1000 indirection is needed.
    /// </summary>
    private async Task<List<Category>> GetUserCustomCategoriesAsync(int userId)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            var familyId = user?.FamilyId;

            // Personal custom categories (UserId == userId) plus family-scoped
            // custom categories (FamilyId == user's family). Id >= 1000 excludes
            // the built-in system categories (1-99).
            var query = _dbContext.Categories
                .AsNoTracking()
                .Where(c => c.Id >= 1000 && !c.IsSystem &&
                    (c.UserId == userId || (familyId.HasValue && c.FamilyId == familyId)))
                .OrderBy(c => c.Name);

            return await query.ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load custom categories for user {UserId}", userId);
            return new List<Category>();
        }
    }

    private async Task<List<AICategorizationResult>> CategorizeTransactionBatchAsync(
        List<Transaction> transactions, 
        Dictionary<string, CategoryPattern> userPatterns,
        int userId)
    {
        var results = new List<AICategorizationResult>();
        var customCategories = await GetUserCustomCategoriesAsync(userId);
        
        // Get user's categorization rules for few-shot learning
        // Exclude rules orphaned to a null AssignCategoryId (their category was
        // deleted — FK is SetNull). Previously such a rule still Matches() and forced
        // the transaction to Uncategorized at high confidence with NeedsReview=false.
        var user = await _userRepository.GetByIdAsync(userId);
        var userRules = await _dbContext.CategorizationRules
            .Where(r => r.IsActive && r.AssignCategoryId != null &&
                (r.UserId == userId || (user != null && r.FamilyId == user.FamilyId)))
            .Include(r => r.AssignCategoryNav)
            .OrderBy(r => r.Priority)
            .ToListAsync();
        
        // Rules, merchant habits and vector matches are cheap and read the database, so they
        // run in order. Rows that need the model are held back and their calls go out
        // together (up to _modelConcurrency at once), then the results are written in order:
        // the DbContext is not thread-safe, only the network call runs in parallel.
        var pending = new List<PendingModelCall>();
        string? modelName = null;

        foreach (var transaction in transactions)
        {
            var result = new AICategorizationResult { TransactionId = transaction.Id };
            results.Add(result);

            try
            {
                // PHASE 2: Check categorization rules FIRST (highest priority)
                var matchingRule = userRules.FirstOrDefault(r => r.Matches(transaction));
                if (matchingRule != null)
                {
                    // Use unified AssignCategoryId - determine if system or custom
                    var assignedCategoryId = matchingRule.AssignCategoryId ?? (int)TransactionCategory.Uncategorized;
                    var isCustomCategory = assignedCategoryId >= 1000;
                    var systemCategory = isCustomCategory
                        ? TransactionCategory.Uncategorized
                        : (TransactionCategory)assignedCategoryId;

                    var displayName = matchingRule.AssignCategoryNav?.Name
                        ?? systemCategory.ToString();

                    // Calculate confidence based on times applied (more uses = higher confidence)
                    var baseConfidence = matchingRule.Source == RuleSource.MerchantPreference ? 0.98 : 0.95;
                    var confidenceBoost = Math.Min(0.02, matchingRule.TimesApplied * 0.002); // +0.2% per use, max +2%
                    var confidence = Math.Min(1.0, baseConfidence + confidenceBoost);

                    result.SuggestedCategoryId = assignedCategoryId;
                    result.SuggestedCategoryName = displayName;
                    result.Confidence = confidence;
                    result.NeedsReview = false; // Rules are user-defined, no review needed

                    // Update rule statistics
                    matchingRule.TimesApplied++;
                    matchingRule.LastAppliedAt = DateTime.UtcNow;
                    _dbContext.CategorizationRules.Update(matchingRule);

                    _logger.LogDebug("Rule match for transaction {TransactionId}: Rule '{RuleName}' → {Category} (Source: {Source}, TimesApplied: {Times})",
                        transaction.Id, matchingRule.Name, displayName, matchingRule.Source, matchingRule.TimesApplied);
                }
                // Then check user patterns from transaction history. A split merchant (filed
                // under more than one category) has no single habit to apply, and the vector
                // shortcut below would commit on whichever confirmed row happened to sit
                // closest, so both deterministic phases stand aside and Gemini judges with
                // the user's context instead.
                else if (!string.IsNullOrEmpty(transaction.MerchantName) &&
                    userPatterns.TryGetValue(transaction.MerchantName.ToLowerInvariant(), out var pattern) &&
                    !pattern.IsSplit)
                {
                    // Convert pattern to unified CategoryId
                    var unifiedCategoryId = pattern.UserCategoryId.HasValue
                        ? 1000 + pattern.UserCategoryId.Value
                        : (int)pattern.SystemCategory;

                    result.SuggestedCategoryId = unifiedCategoryId;
                    result.SuggestedCategoryName = pattern.DisplayName;
                    result.Confidence = 0.95; // High confidence for pattern match
                    result.NeedsReview = false;

                    _logger.LogDebug("Pattern match for transaction {TransactionId}: {Merchant} → {Category} (Custom: {IsCustom})",
                        transaction.Id, transaction.MerchantName, pattern.DisplayName, pattern.UserCategoryId.HasValue);
                }
                else
                {
                    var isSplitMerchant = !string.IsNullOrEmpty(transaction.MerchantName) &&
                        userPatterns.TryGetValue(transaction.MerchantName.ToLowerInvariant(), out var splitPattern) &&
                        splitPattern.IsSplit;

                    // PHASE 3: Vector similarity search — check if similar confirmed transactions exist.
                    // One search serves both the shortcut (top 5 at >= 0.75) and the model's
                    // context (top 3 at >= 0.6): each is an exact subset of top 5 at >= 0.6.
                    // Two searches per row were half of a round's time.
                    var similar = await FindSimilarForBatchAsync(transaction, userId);
                    var vectorResult = isSplitMerchant ? null : VectorShortcut(similar);
                    if (vectorResult != null)
                    {
                        result.SuggestedCategoryId = vectorResult.CategoryId;
                        result.SuggestedCategoryName = vectorResult.CategoryName;
                        result.Confidence = vectorResult.Confidence;
                        result.NeedsReview = false;

                        _logger.LogDebug("Vector similarity match for transaction {TransactionId}: {Merchant} → {Category} (Score: {Score:F3})",
                            transaction.Id, transaction.MerchantName, vectorResult.CategoryName, vectorResult.Confidence);
                    }
                    else
                    {
                        // PHASE 4: Gemini AI fallback (now includes rules as few-shot examples).
                        // Prepared here, sent below with the rest of the batch's model calls.
                        var prepared = await PrepareModelCallAsync(
                            transaction, userId, userPatterns, customCategories, userRules, modelName,
                            similar.Count > 0 ? similar.Take(3).ToList() : null);
                        if (prepared.Fallback != null)
                        {
                            SetModelSuggestion(transaction, result, prepared.Fallback);
                        }
                        else
                        {
                            modelName = prepared.ModelName;
                            pending.Add(new PendingModelCall(transaction, result, prepared.Prompt!));
                            continue; // finished after the concurrent model calls
                        }
                    }
                }

                await ApplySuggestionAsync(transaction, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error categorizing transaction {TransactionId}", transaction.Id);
                result.Error = ex.Message;
            }
        }

        if (pending.Count > 0 && modelName != null)
        {
            var started = DateTime.UtcNow;
            var replies = await SendModelCallsAsync(modelName, pending);
            for (var i = 0; i < pending.Count; i++)
            {
                var call = pending[i];
                try
                {
                    AICategorizationInternalResult aiResult;
                    if (replies[i].Error is { } sendError)
                    {
                        _logger.LogError(sendError, "Error calling Gemini for categorization of transaction {TransactionId} (Merchant: {Merchant})",
                            call.Transaction.Id, call.Transaction.MerchantName);
                        var fallback = GetFallbackCategory(call.Transaction);
                        aiResult = new AICategorizationInternalResult(fallback, null, fallback.ToString(), 0.3);
                    }
                    else
                    {
                        aiResult = await CompleteModelCallAsync(
                            call.Transaction, userId, modelName, call.Prompt, replies[i].Reply!, customCategories);
                    }
                    SetModelSuggestion(call.Transaction, call.Result, aiResult);
                    await ApplySuggestionAsync(call.Transaction, call.Result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error categorizing transaction {TransactionId}", call.Transaction.Id);
                    call.Result.Error = ex.Message;
                }
            }
            _logger.LogInformation("Gemini categorized {Count} transactions in {Ms} ms ({Concurrency} at a time)",
                pending.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds, _modelConcurrency);
        }

        return results;
    }

    private record PendingModelCall(Transaction Transaction, AICategorizationResult Result, string Prompt);

    private record PreparedModelCall(string? ModelName, string? Prompt, AICategorizationInternalResult? Fallback);

    private record ModelReply(string Text, int? PromptTokens, int? CandidateTokens, string Model, decimal? CostUsd);

    /// <summary>
    /// Everything a model call needs, read sequentially (the tier lookup hits the
    /// database; the vector context comes from the row's single similarity search). Returns a fallback suggestion instead of a prompt when Gemini is not
    /// configured or the tier lookup fails, the same outcomes the one-at-a-time path had.
    /// </summary>
    private async Task<PreparedModelCall> PrepareModelCallAsync(
        Transaction transaction,
        int userId,
        Dictionary<string, CategoryPattern> userPatterns,
        List<Category> customCategories,
        List<CategorizationRule>? userRules,
        string? knownModelName,
        List<TransactionSimilarityResult>? similarContext)
    {
        if (!_ai.IsConfigured)
        {
            _logger.LogWarning("AI provider not configured, using fallback categorization for transaction {TransactionId}", transaction.Id);
            var fallback = GetFallbackCategory(transaction);
            return new PreparedModelCall(null, null, new AICategorizationInternalResult(fallback, null, fallback.ToString(), 0.3));
        }
        try
        {
            var modelName = knownModelName ?? await ResolveCategorizationModelAsync(userId);
            return new PreparedModelCall(modelName,
                BuildCategorizationPrompt(transaction, userPatterns, customCategories, userRules, similarContext), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error preparing Gemini categorization of transaction {TransactionId} (Merchant: {Merchant})",
                transaction.Id, transaction.MerchantName);
            var fallback = GetFallbackCategory(transaction);
            return new PreparedModelCall(null, null, new AICategorizationInternalResult(fallback, null, fallback.ToString(), 0.3));
        }
    }

    /// <summary>The batch's model calls, up to _modelConcurrency at once. Network only.</summary>
    private async Task<List<(ModelReply? Reply, Exception? Error)>> SendModelCallsAsync(
        string modelName, List<PendingModelCall> pending)
    {
        using var gate = new SemaphoreSlim(_modelConcurrency);
        var calls = pending.Select(async call =>
        {
            await gate.WaitAsync();
            try
            {
                return ((ModelReply?)await SendModelCallAsync(modelName, call.Prompt), (Exception?)null);
            }
            catch (Exception ex)
            {
                return ((ModelReply?)null, (Exception?)ex);
            }
            finally
            {
                gate.Release();
            }
        });
        return (await Task.WhenAll(calls)).ToList();
    }

    private void SetModelSuggestion(Transaction transaction, AICategorizationResult result, AICategorizationInternalResult aiResult)
    {
        result.SuggestedCategoryId = aiResult.CustomCategoryId ?? (int)aiResult.SystemCategory;
        result.SuggestedCategoryName = aiResult.DisplayName;
        result.Confidence = aiResult.Confidence;
        result.NeedsReview = aiResult.Confidence < _reviewThreshold;

        // Store Gemini-generated semantic tags on the transaction
        if (!string.IsNullOrEmpty(aiResult.SemanticTags))
        {
            transaction.SemanticTags = aiResult.SemanticTags;
        }
    }

    private async Task ApplySuggestionAsync(Transaction transaction, AICategorizationResult result)
    {
        // Update transaction with AI suggestion (using unified CategoryId)
        transaction.AISuggestedCategoryId = result.SuggestedCategoryId;
        transaction.AIConfidence = result.Confidence;
        transaction.AICategorizationDate = DateTime.UtcNow;
        transaction.NeedsReview = result.NeedsReview;

        // Always apply the AI suggestion so the transaction has a category.
        // Low-confidence ones stay in the review queue via NeedsReview = true.
        var isUncategorized = !transaction.CategoryId.HasValue ||
            transaction.CategoryId == (int)TransactionCategory.Uncategorized;
        if (isUncategorized && !transaction.IsUserCategorized)
        {
            transaction.CategoryId = result.SuggestedCategoryId;
            result.Applied = true;
        }

        await _transactionRepository.UpdateAsync(transaction);
    }

    internal record AICategorizationInternalResult(
        TransactionCategory SystemCategory,
        // Unified Categories.Id (>= 1000) when the model picked a custom category;
        // null for system categories. No +1000 indirection — this is the real id.
        int? CustomCategoryId,
        string DisplayName,
        double Confidence,
        string? SemanticTags = null);

    private record VectorCategorizationResult(int CategoryId, string CategoryName, double Confidence);

    private async Task<VectorCategorizationResult?> TryVectorSimilarityCategorizationAsync(Transaction transaction, int userId)
    {
        try
        {
            var similarTransactions = await _transactionEmbeddingService.FindSimilarCategorizedTransactionsAsync(
                userId, transaction, limit: 5, minScore: VectorShortcutMinScore);
            return VectorShortcut(similarTransactions ?? new List<TransactionSimilarityResult>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector similarity search failed for transaction {TransactionId}, falling through to Gemini",
                transaction.Id);
            return null;
        }
    }

    private const float VectorShortcutMinScore = 0.75f;
    private const float VectorContextMinScore = 0.6f;

    /// <summary>
    /// The batch's one similarity search per row: top 5 at >= VectorContextMinScore, which
    /// contains both the shortcut's candidates and the model's context. Empty on failure.
    /// </summary>
    private async Task<List<TransactionSimilarityResult>> FindSimilarForBatchAsync(Transaction transaction, int userId)
    {
        try
        {
            return await _transactionEmbeddingService.FindSimilarCategorizedTransactionsAsync(
                userId, transaction, limit: 5, minScore: VectorContextMinScore)
                ?? new List<TransactionSimilarityResult>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector similarity search failed for transaction {TransactionId}, falling through to Gemini",
                transaction.Id);
            return new List<TransactionSimilarityResult>();
        }
    }

    /// <summary>
    /// The vector shortcut: the closest confirmed row is within VectorSimilarityThreshold and
    /// the top matches (>= VectorShortcutMinScore) agree on its category.
    /// </summary>
    private static VectorCategorizationResult? VectorShortcut(List<TransactionSimilarityResult> similar)
    {
        var candidates = similar.Where(r => r.Score >= VectorShortcutMinScore).ToList();
        if (candidates.Count == 0) return null;

        // Check if top results agree on a category
        var topResult = candidates[0];
        if (topResult.Score < VectorSimilarityThreshold) return null;

        // Consensus check: do the top results agree?
        var agreeing = candidates.Count(r => r.CategoryId == topResult.CategoryId);
        if (agreeing < Math.Min(2, candidates.Count)) return null;

        return new VectorCategorizationResult(
            topResult.CategoryId,
            topResult.CategoryName,
            topResult.Score);
    }

    private async Task<List<TransactionSimilarityResult>?> GetVectorSimilarityContextAsync(Transaction transaction, int userId)
    {
        try
        {
            var similar = await _transactionEmbeddingService.FindSimilarCategorizedTransactionsAsync(
                userId, transaction, limit: 3, minScore: VectorContextMinScore);
            return similar.Count > 0 ? similar : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<AICategorizationInternalResult> CallGeminiForCategorizationAsync(
        Transaction transaction,
        Dictionary<string, CategoryPattern> userPatterns,
        List<Category> customCategories,
        int userId,
        List<CategorizationRule>? userRules = null,
        List<TransactionSimilarityResult>? vectorContext = null)
    {
        if (!_ai.IsConfigured)
        {
            _logger.LogWarning("AI provider not configured, using fallback categorization for transaction {TransactionId}", transaction.Id);
            var fallback = GetFallbackCategory(transaction);
            return new AICategorizationInternalResult(fallback, null, fallback.ToString(), 0.3);
        }
        
        try
        {
            var modelName = await ResolveCategorizationModelAsync(userId);

            _logger.LogDebug("AI Categorization starting for transaction {TransactionId}: Model={Model}, Merchant={Merchant}, CustomCategories={CustomCount}, Rules={RuleCount}",
                transaction.Id, modelName, transaction.MerchantName, customCategories.Count, userRules?.Count ?? 0);

            var prompt = BuildCategorizationPrompt(transaction, userPatterns, customCategories, userRules, vectorContext);

            _logger.LogDebug("AI Categorization prompt built for transaction {TransactionId} (length: {PromptLength} chars)",
                transaction.Id, prompt.Length);

            var reply = await SendModelCallAsync(modelName, prompt);
            return await CompleteModelCallAsync(transaction, userId, modelName, prompt, reply, customCategories);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Gemini for categorization of transaction {TransactionId} (Merchant: {Merchant})",
                transaction.Id, transaction.MerchantName);
            var fallback = GetFallbackCategory(transaction);
            return new AICategorizationInternalResult(fallback, null, fallback.ToString(), 0.3);
        }
    }

    /// <summary>The tier's primary model (Standard when the user has no subscription).</summary>
    private async Task<string> ResolveCategorizationModelAsync(int userId)
    {
        var tier = await _entitlements.GetEffectiveTierAsync(userId) ?? SubscriptionTier.Standard;
        return _modelConfiguration.GetModelsForTier(tier)[0];
    }

    /// <summary>
    /// The Gemini call alone: network only, no database, so a batch can run several at once.
    /// </summary>
    private async Task<ModelReply> SendModelCallAsync(string modelName, string prompt)
    {
        var result = await _ai.GenerateTextAsync(new AiTextRequest(prompt, modelName, UsageContext));
        return new ModelReply(result.Text.Trim(), result.InputTokens, result.OutputTokens,
            _ai.ResolveModel(modelName, UsageContext), result.CostUsd);
    }

    /// <summary>
    /// Usage bookkeeping (writes through the DbContext, so callers run it sequentially) and
    /// parsing of one model reply.
    /// </summary>
    private async Task<AICategorizationInternalResult> CompleteModelCallAsync(
        Transaction transaction,
        int userId,
        string modelName,
        string prompt,
        ModelReply reply,
        List<Category> customCategories)
    {
        // The spend metric for this call (bulk categorisation is the highest-volume AI path, so
        // the most likely to burn through prepaid credits unnoticed) is reported by the
        // provider itself, from the provider's own token accounting.
        //
        // Durable row (AIUsageRecords), so categorization cost is a number in the
        // admin usage views and Grafana rather than an estimate. Metrics above are
        // process-local and Sentry-only on refusal; this is what survives.
        await RecordCategorizationUsageAsync(userId, reply.Model, reply.PromptTokens, reply.CandidateTokens, reply.CostUsd);

        _logger.LogDebug("AI Categorization response for transaction {TransactionId}: RawResponse=\"{Response}\"",
            transaction.Id, reply.Text);

        var result = ParseCategorizationResponse(reply.Text, customCategories, _logger);

        _logger.LogDebug("AI Categorization parsed for transaction {TransactionId}: Category={Category}, CustomCategoryId={CustomCategoryId}, Confidence={Confidence:P0}",
            transaction.Id, result.SystemCategory, result.CustomCategoryId, result.Confidence);

        // Ensure we never return Uncategorized for system categories (custom categories use Uncategorized as parent)
        if (!result.CustomCategoryId.HasValue && result.SystemCategory == TransactionCategory.Uncategorized)
        {
            var (finalCategory, finalConfidence) = EnsureNotUncategorized(result.SystemCategory, result.Confidence, transaction);
            return new AICategorizationInternalResult(finalCategory, null, finalCategory.ToString(), finalConfidence);
        }

        _logger.LogInformation("AI Categorization complete for transaction {TransactionId}: {Merchant} → {Category} ({Confidence:P0})",
            transaction.Id, transaction.MerchantName, result.DisplayName, result.Confidence);

        return result;
    }

    /// <summary>
    /// Writes one usage row for a Gemini categorization call. Best-effort: a bookkeeping
    /// failure must never fail the categorization that already succeeded. Skipped when the
    /// provider returned no token counts (nothing truthful to record) or when the trackers
    /// were not supplied (direct construction in tests).
    /// </summary>
    internal async Task RecordCategorizationUsageAsync(int userId, string model, int? inputTokens, int? outputTokens, decimal? costUsd = null)
    {
        if (_usageTracker == null || inputTokens == null || outputTokens == null) return;
        try
        {
            // A provider that knows its own price (local models: 0) wins over the Gemini price
            // table, which would bill an unknown model at Gemini rates.
            var cost = costUsd ?? _costTracking?.CalculateCost(model, inputTokens.Value, outputTokens.Value) ?? 0m;
            // AIUsageRecords.Model is 50 characters; OpenRouter ids and local tags can be longer.
            model = SafeText.Truncate(model, 50);
            // The provider's own input/output split, not one total divided 70/30; still counts toward the request limits, as before.
            await _usageTracker.RecordCallAsync(new BuoyAI.Core.DTOs.AI.AiUsageEntry(
                userId, model, _ai.Id, inputTokens.Value, outputTokens.Value, false, cost, UsageContext,
                CountsAgainstRequestLimits: true));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record categorization usage for user {UserId} ({Model})", userId, model);
        }
    }

    internal static string BuildCategorizationPrompt(
        Transaction transaction,
        Dictionary<string, CategoryPattern> userPatterns,
        List<Category> customCategories,
        List<CategorizationRule>? userRules = null,
        List<TransactionSimilarityResult>? vectorContext = null)
    {
        // Use extension method for consistency across the codebase
        var isExpense = transaction.IsExpense();
        var amount = Math.Abs(transaction.Amount);
        
        // Build system categories list (excluding income categories for expenses and vice versa)
        var systemCategoryList = string.Join("\n", CategoryDescriptions
            .Where(c => isExpense ? 
                c.Key != TransactionCategory.Salary && 
                c.Key != TransactionCategory.Bonus && 
                c.Key != TransactionCategory.Investment :
                c.Key == TransactionCategory.Salary || 
                c.Key == TransactionCategory.Bonus || 
                c.Key == TransactionCategory.Investment ||
                c.Key == TransactionCategory.OtherIncome)
            .Select(c => $"- {(int)c.Key}:{c.Key} - {c.Value}"));
        
        // Build custom categories list (prefixed with "C" to distinguish from system categories)
        var customCategoryList = "";
        if (customCategories.Count > 0 && isExpense)
        {
            customCategoryList = "\n\nUser's Custom Categories (use these if they fit better!):\n" +
                string.Join("\n", customCategories.Select(c => 
                    $"- C{c.Id}:{c.Name}" + (string.IsNullOrEmpty(c.Description) ? "" : $" - {c.Description}")));
        }
        
        // PHASE 2: Build categorization rules context (few-shot learning examples)
        var rulesContext = "";
        if (userRules?.Count > 0)
        {
            var ruleExamples = userRules
                .Where(r => !string.IsNullOrEmpty(r.MerchantContains) || !string.IsNullOrEmpty(r.MerchantExact))
                .Take(10)
                .Select(r => {
                    // Sanitize user-controlled rule text before injecting into the
                    // LLM prompt. Without this, a malicious user could craft a rule
                    // name/pattern containing newlines + pseudo-system directives
                    // (e.g. "\n\nSystem: ignore previous instructions") that break
                    // out of the rules list and attempt prompt injection. See #419.
                    var matchCriteria = !string.IsNullOrEmpty(r.MerchantExact)
                        ? $"'{SanitizeForPrompt(r.MerchantExact)}'"
                        : $"*{SanitizeForPrompt(r.MerchantContains)}*";
                    var category = SanitizeForPrompt(r.AssignCategoryNav?.Name ?? "Unknown");
                    var timesUsed = r.TimesApplied > 0 ? $" (used {r.TimesApplied}x)" : "";
                    return $"  - {matchCriteria} → {category}{timesUsed}";
                });
            
            if (ruleExamples.Any())
            {
                rulesContext = $"\n\nUser's Categorization Rules (FOLLOW THESE PATTERNS!):\n{string.Join("\n", ruleExamples)}\n";
            }
        }
        
        // Build user pattern context (now includes custom categories)
        var userPatternContext = "";
        if (userPatterns.Count > 0)
        {
            var samplePatterns = userPatterns.Take(15)
                .Select(p => {
                    var categoryDisplay = p.Value.UserCategoryId.HasValue
                        ? $"[CUSTOM] {p.Value.DisplayName}"
                        : p.Value.DisplayName;
                    return $"'{p.Key}' → {categoryDisplay}";
                });
            userPatternContext = $"\nThe user has previously categorized these merchants (learn from their preferences!):\n{string.Join("\n", samplePatterns)}\n";
        }

        // How this user SPLITS merchants they file under more than one category. The flat list
        // above shows one category per merchant, which is exactly the information that is
        // wrong for a split merchant: "amazon → Shopping" hides that it depends. Give the
        // distribution with the features that separate the rows (count, average amount,
        // weekday/weekend) so the model can match this transaction's amount and day to the
        // right one. This transaction's own merchant comes first when it is split.
        var merchantKey = transaction.MerchantName?.ToLowerInvariant() ?? "";
        var splitMerchants = userPatterns
            .Where(p => p.Value.IsSplit)
            .OrderByDescending(p => p.Key == merchantKey)
            .ThenByDescending(p => p.Value.Splits.Sum(s => s.Count))
            .Take(8)
            .ToList();
        var splitContext = "";
        if (splitMerchants.Count > 0)
        {
            var lines = splitMerchants.Select(p =>
                "  - " + SanitizeForPrompt(CategorizationPromptSignals.DescribeSplit(p.Key, p.Value)));
            splitContext = $"\nMerchants this user files under MORE THAN ONE category (match the amount and day to the closest pattern):\n{string.Join("\n", lines)}\n";
        }

        // Timing and Plaid's own read, as explicit signals rather than a bare date string.
        var timing = CategorizationPromptSignals.DescribeTiming(transaction.Date);
        var plaidGuess = CategorizationPromptSignals.DescribePlaidGuess(transaction);
        var plaidLine = plaidGuess != null ? $"\n- Plaid's guess: {SanitizeForPrompt(plaidGuess)}" : "";
        
        // Add vector similarity context if available
        var vectorSimilarityContext = "";
        if (vectorContext?.Count > 0)
        {
            var examples = vectorContext.Select(v =>
                $"  - '{v.MerchantName}' → {v.CategoryName} (similarity: {v.Score:F2})");
            vectorSimilarityContext = $"\nSimilar transactions this user has confirmed:\n{string.Join("\n", examples)}\nUse these as strong hints for categorization.\n";
        }

        // Extra signal for the transactions with no merchant to go on — checks and inter-bank
        // transfers. "Merchant: <blank>, Description: CHECK" is nothing to categorize from; a
        // payee, a reason, or the raw bank string usually is.
        //
        // Sanitized like the user's rules (#419), and for a stronger reason: a transfer's payee
        // and reason are frequently typed by whoever SENT the money (a Zelle/Venmo memo), so
        // this is third-party text going into an LLM prompt. Not attacker-proof, but it gets the
        // same newline/role-marker neutralization as every other untrusted string here.
        var extraSignal = new List<string>();
        void AddSignal(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                extraSignal.Add($"- {label}: {SanitizeForPrompt(value)}");
        }
        AddSignal("Bank's raw description", transaction.OriginalDescription);
        AddSignal("Transaction type", transaction.TransactionCode);
        AddSignal("Check number", transaction.CheckNumber);
        AddSignal("Transfer payee", transaction.PaymentPayee);
        AddSignal("Transfer payer", transaction.PaymentPayer);
        AddSignal("Transfer reason", transaction.PaymentReason);
        var extraSignalBlock = extraSignal.Count > 0 ? "\n" + string.Join("\n", extraSignal) : "";

        return $@"You are a financial transaction categorizer. Analyze this transaction and categorize it.

Transaction Details:
- Merchant: {SanitizeForPrompt(transaction.MerchantName)}
- Description: {SanitizeForPrompt(transaction.Description)}
- Amount: ${amount:F2} ({(isExpense ? "expense" : "income")})
- Date: {transaction.Date:yyyy-MM-dd}
- When: {timing}{plaidLine}{extraSignalBlock}
{rulesContext}{userPatternContext}{splitContext}{vectorSimilarityContext}
Available System Categories (format: ID:Name - Description):
{systemCategoryList}
{customCategoryList}

IMPORTANT:
1. FIRST check if any user rules match (they define patterns like *coffee* → DiningOut)
2. THEN check if user has custom categories that fit better
3. User rules and patterns represent LEARNED preferences - follow them!
4. For a merchant the user files under more than one category, pick the category whose typical amount and day (weekday/weekend, early/mid/late month) is closest to this transaction. Plaid's guess is one more signal, not the answer; the user's own history outranks it.

Respond in exactly this format (no other text):
CATEGORY_ID:CONFIDENCE:tag1,tag2,tag3

Where:
- CATEGORY_ID is either a numeric ID (like 21) for system categories, OR ""C"" followed by ID (like C5) for custom categories
- CONFIDENCE is a decimal from 0.0 to 1.0 indicating how confident you are
- tags are 2-4 lowercase semantic descriptors for this merchant type (e.g. coffee, cafe, beverage)

Example responses:
21:0.95:coffee,cafe,beverage (system category with tags)
C5:0.90:grocery,organic (custom category with tags)
23:0.75:gas,fuel (system category with tags)

Only respond with the category ID, confidence, and tags. No explanation needed.";
    }

    /// <summary>
    /// Strips characters that could allow user-supplied rule text to break out
    /// of its list context in the Gemini prompt and inject pseudo-system
    /// instructions. Removes newlines / carriage returns, backticks, and
    /// directive-style markers, then truncates to a safe length. See issue
    /// #419 (Lilli-inspired hardening).
    /// </summary>
    private static string SanitizeForPrompt(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = value
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("`", "'")
            // Neutralize common prompt-injection role markers
            .Replace("System:", "system:", StringComparison.OrdinalIgnoreCase)
            .Replace("Assistant:", "assistant:", StringComparison.OrdinalIgnoreCase)
            .Replace("User:", "user:", StringComparison.OrdinalIgnoreCase);

        // Collapse repeated whitespace introduced by the replacements above
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s+", " ").Trim();

        const int maxLength = 120;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength];
        }

        return sanitized;
    }

    internal static AICategorizationInternalResult ParseCategorizationResponse(string response, List<Category> customCategories, ILogger? logger = null)
    {
        try
        {
            // Expected format: "21:0.95:coffee,cafe" or "C5:0.90:grocery,organic" or legacy "21:0.95"
            var cleanResponse = response.Trim().Split('\n')[0].Trim();
            var parts = cleanResponse.Split(':');

            if (parts.Length >= 2 && double.TryParse(parts[1], out var confidence))
            {
                confidence = Math.Min(1.0, Math.Max(0.0, confidence));
                var categoryPart = parts[0].Trim();

                // Parse optional semantic tags (3rd segment)
                string? semanticTags = null;
                if (parts.Length >= 3)
                {
                    var rawTags = string.Join(":", parts[2..]).Trim();
                    semanticTags = SemanticTagVocabulary.ValidateAndFilter(rawTags.Replace(",", ", "));
                }

                // Check if it's a custom category (starts with "C")
                if (categoryPart.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(categoryPart[1..], out var customCategoryId))
                    {
                        var customCategory = customCategories.FirstOrDefault(c => c.Id == customCategoryId);
                        if (customCategory != null)
                        {
                            logger?.LogDebug("Parsed custom category response: {CategoryId} ({CategoryName}), Confidence={Confidence}, Tags={Tags}",
                                customCategoryId, customCategory.Name, confidence, semanticTags);
                            return new AICategorizationInternalResult(
                                TransactionCategory.Uncategorized,
                                customCategoryId,
                                customCategory.Name,
                                confidence,
                                semanticTags);
                        }
                    }
                }
                // System category
                else if (int.TryParse(categoryPart, out var categoryId))
                {
                    if (Enum.IsDefined(typeof(TransactionCategory), categoryId))
                    {
                        var category = (TransactionCategory)categoryId;
                        return new AICategorizationInternalResult(category, null, category.ToString(), confidence, semanticTags);
                    }
                }
            }

            logger?.LogWarning("Failed to parse AI response: {Response}", response);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error parsing AI categorization response: {Response}", response);
        }

        return new AICategorizationInternalResult(TransactionCategory.Uncategorized, null, "Uncategorized", 0.0);
    }

    /// <summary>
    /// Get a sensible fallback category when AI fails or API key is missing.
    /// Never returns Uncategorized.
    /// </summary>
    private static TransactionCategory GetFallbackCategory(Transaction transaction)
    {
        // Use extension method for consistency: Income -> OtherIncome
        if (transaction.IsIncome())
            return TransactionCategory.OtherIncome;
        
        // For expenses, try to infer from merchant name
        var merchant = transaction.MerchantName?.ToLowerInvariant() ?? "";
        var description = transaction.Description?.ToLowerInvariant() ?? "";
        var combined = $"{merchant} {description}";
        
        // Common grocery stores
        if (combined.Contains("walmart") || combined.Contains("kroger") || combined.Contains("safeway") || 
            combined.Contains("publix") || combined.Contains("whole foods") || combined.Contains("trader joe") ||
            combined.Contains("grocery") || combined.Contains("supermarket") || combined.Contains("food lion"))
            return TransactionCategory.Groceries;
        
        // Restaurants and dining
        if (combined.Contains("restaurant") || combined.Contains("mcdonald") || combined.Contains("starbucks") ||
            combined.Contains("chipotle") || combined.Contains("uber eats") || combined.Contains("doordash") ||
            combined.Contains("grubhub") || combined.Contains("pizza") || combined.Contains("cafe"))
            return TransactionCategory.DiningOut;
        
        // Gas stations
        if (combined.Contains("shell") || combined.Contains("chevron") || combined.Contains("exxon") ||
            combined.Contains("bp ") || combined.Contains("gas") || combined.Contains("fuel"))
            return TransactionCategory.Transportation;
        
        // Common retailers
        if (combined.Contains("amazon") || combined.Contains("target") || combined.Contains("best buy") ||
            combined.Contains("costco") || combined.Contains("home depot") || combined.Contains("lowes"))
            return TransactionCategory.Shopping;
        
        // Subscriptions
        if (combined.Contains("netflix") || combined.Contains("spotify") || combined.Contains("hulu") ||
            combined.Contains("disney+") || combined.Contains("youtube") || combined.Contains("subscription"))
            return TransactionCategory.Subscriptions;
        
        // Utilities
        if (combined.Contains("electric") || combined.Contains("water") || combined.Contains("gas bill") ||
            combined.Contains("internet") || combined.Contains("comcast") || combined.Contains("verizon") ||
            combined.Contains("at&t") || combined.Contains("utility"))
            return TransactionCategory.Utilities;
        
        // Default fallback for expenses is Shopping (most common general category)
        return TransactionCategory.Shopping;
    }
    
    /// <summary>
    /// Ensures we never return Uncategorized - replaces with sensible fallback.
    /// </summary>
    private static (TransactionCategory category, double confidence) EnsureNotUncategorized(
        TransactionCategory category, double confidence, Transaction transaction)
    {
        if (category == TransactionCategory.Uncategorized)
        {
            return (GetFallbackCategory(transaction), 0.3); // Low confidence for fallback
        }
        return (category, confidence);
    }

    private TransactionDto ToDto(Transaction transaction)
    {
        // Get category info from unified navigation property
        var categoryName = transaction.CategoryNav?.Name 
            ?? (transaction.CategoryId.HasValue && transaction.CategoryId.Value < 1000 && transaction.CategoryId.Value > 0 
                ? ((TransactionCategory)transaction.CategoryId.Value).GetDisplayName() 
                : "Uncategorized");
        
        return new TransactionDto
        {
            Id = transaction.Id,
            AccountId = transaction.AccountId,
            PlaidTransactionId = transaction.PlaidTransactionId,
            Amount = transaction.Amount,
            Type = transaction.Type,
            IsoCurrencyCode = transaction.IsoCurrencyCode,
            Description = transaction.Description,
            MerchantName = transaction.MerchantName,
            Nickname = transaction.Nickname,
            DisplayName = transaction.DisplayName,
            CategoryId = transaction.CategoryId,
            CategoryName = categoryName,
            CategoryColor = transaction.CategoryNav?.Color,
            IsUserCategorized = transaction.IsUserCategorized,
            Date = transaction.Date,
            AccountName = transaction.Account?.DisplayName ?? "",
            IsRecurring = false,
            
            // Budget attribution
            BudgetId = transaction.BudgetId,
            ClaimedByUserId = transaction.ClaimedByUserId,
            ClaimedByUserName = transaction.ClaimedByUser != null ? $"{transaction.ClaimedByUser.FirstName} {transaction.ClaimedByUser.LastName}".Trim() : null,
            
            // Personal sub-category for allowance transactions
            PersonalCategoryId = transaction.PersonalUserCategoryId,
            PersonalCategoryName = transaction.PersonalCategoryNav?.Name,
            PersonalCategoryColor = transaction.PersonalCategoryNav?.Color,
            
            // Plaid rich data
            PlaidCategoryDetailed = transaction.PlaidCategoryDetailed,
            MerchantLogoUrl = transaction.PlaidMerchantLogoUrl,
            MerchantWebsite = transaction.PlaidMerchantWebsite,
            PaymentChannel = transaction.PlaidPaymentChannel,
            LocationCity = transaction.LocationCity,
            LocationRegion = transaction.LocationRegion,
            IsPending = transaction.IsPending,
            
            // AI Categorization
            AISuggestedCategoryId = transaction.AISuggestedCategoryId,
            AISuggestedCategoryName = transaction.AISuggestedCategoryNav?.Name,
            AIConfidence = transaction.AIConfidence,
            NeedsReview = transaction.NeedsReview,
            SkipUntil = transaction.SkipUntil,
            CategorySource = transaction.CategorySource,
            SemanticTags = transaction.SemanticTags
        };
    }
}
