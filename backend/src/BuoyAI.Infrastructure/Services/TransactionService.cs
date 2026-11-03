using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Utilities;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.Infrastructure.Services;

public class TransactionService : ITransactionService
{
  // Bounds for manually-entered transactions (Plaid-sourced rows are trusted).
  private const int MaxDescriptionLength = 200;
  private const int MaxMerchantLength = 200;
  private const int MaxNotesLength = 1000;
  // Upper bound on split children per transaction. Without it a single request could fan out into
  // tens of thousands of INSERTs / child rows (write-amplification DoS on the transactions path).
  private const int MaxSplitCount = 50;
  private const int MaxSplitNoteLength = 500;

  private readonly ITransactionRepository _transactionRepository;
  private readonly IAccountRepository _accountRepository;
  private readonly IUserRepository _userRepository;
  private readonly IBudgetRepository _budgetRepository;
  private readonly ICategoryRepository _categoryRepository;
  private readonly INotificationService _notificationService;
  private readonly IFamilyAuthorizationService _authService;
  private readonly ISubscriptionTransactionRepository _subscriptionTransactionRepository;
  private readonly ITransactionEmbeddingService _transactionEmbeddingService;
  private readonly ILogger<TransactionService> _logger;
  private readonly IGamificationEvaluationQueue _gamificationQueue;
  private readonly IGamificationService _gamificationService;
  // Lazy to keep construction cheap and avoid any DI-graph ordering surprises; used only to reverse a
  // savings-goal earmark when an earmarked transaction is deleted or split.
  private readonly Lazy<ISavingsService> _savingsService;

  public TransactionService(
    ITransactionRepository transactionRepository,
    IAccountRepository accountRepository,
    IUserRepository userRepository,
    IBudgetRepository budgetRepository,
    ICategoryRepository categoryRepository,
    INotificationService notificationService,
    IFamilyAuthorizationService authService,
    ISubscriptionTransactionRepository subscriptionTransactionRepository,
    ITransactionEmbeddingService transactionEmbeddingService,
    ILogger<TransactionService> logger,
    IGamificationEvaluationQueue gamificationQueue,
    IGamificationService gamificationService,
    Lazy<ISavingsService> savingsService)
  {
    _transactionRepository = transactionRepository;
    _accountRepository = accountRepository;
    _userRepository = userRepository;
    _budgetRepository = budgetRepository;
    _categoryRepository = categoryRepository;
    _notificationService = notificationService;
    _authService = authService;
    _subscriptionTransactionRepository = subscriptionTransactionRepository;
    _transactionEmbeddingService = transactionEmbeddingService;
    _logger = logger;
    _gamificationQueue = gamificationQueue;
    _gamificationService = gamificationService;
    _savingsService = savingsService;
  }

  /// <summary>
  /// Applies common filtering logic to a collection of transactions based on the filter request.
  /// Extracts duplicated filtering code into a single reusable method.
  /// </summary>
  private static IQueryable<Core.Entities.Transaction> ApplyFilters(
    IEnumerable<Core.Entities.Transaction> transactions, 
    TransactionFilterRequest filter)
  {
    var query = transactions.AsQueryable();

    // Filter out parent split transactions - they should not appear in the list
    // as their amounts have been distributed to child split transactions.
    // We show the children (which have ParentTransactionId set) but hide the parents (IsSplit = true).
    query = query.Where(t => !t.IsSplit);

    // Unified CategoryId filter (1-99 system, 1000+ custom)
    if (filter.CategoryId.HasValue)
    {
      query = query.Where(t => t.CategoryId == filter.CategoryId.Value);
    }
    
    // Filter by PersonalCategoryId for personal budget sub-categories
    if (filter.PersonalCategoryId.HasValue)
    {
      query = query.Where(t => t.PersonalUserCategoryId == filter.PersonalCategoryId.Value);
    }

    if (filter.AccountId.HasValue)
    {
      query = query.Where(t => t.AccountId == filter.AccountId.Value);
    }

    if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
    {
      query = query.Where(t => 
        t.Description.Contains(filter.SearchTerm, StringComparison.OrdinalIgnoreCase) ||
        t.MerchantName.Contains(filter.SearchTerm, StringComparison.OrdinalIgnoreCase));
    }

    if (filter.MinAmount.HasValue)
    {
      query = query.Where(t => Math.Abs(t.Amount) >= filter.MinAmount.Value);
    }

    if (filter.MaxAmount.HasValue)
    {
      query = query.Where(t => Math.Abs(t.Amount) <= filter.MaxAmount.Value);
    }

    // Offline-sync delta fetch (#493): strictly-newer-than the client's last-seen UpdatedAt.
    if (filter.UpdatedAfter.HasValue)
    {
      var updatedAfterUtc = filter.UpdatedAfter.Value.ToUniversalTime();
      query = query.Where(t => t.UpdatedAt > updatedAfterUtc);
    }

    return query;
  }

  public async Task<IEnumerable<TransactionDto>> GetTransactionsAsync(int userId, TransactionFilterRequest filter)
  {
    try
    {
      // Get user to check if they're in a family
      var user = await _userRepository.GetByIdAsync(userId);
      var transactions = await _transactionRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId, filter.GetUtcStartDate(), filter.GetUtcEndDate());
      
      var filteredTransactions = ApplyFilters(transactions, filter);

      // Apply pagination
      var pagedTransactions = filteredTransactions
        .Skip((filter.Page - 1) * filter.PageSize)
        .Take(filter.PageSize)
        .ToList();

      // Batch lookup recurring transactions
      var transactionIds = pagedTransactions.Select(t => t.Id);
      var recurringIds = await _subscriptionTransactionRepository.GetRecurringTransactionIdsAsync(transactionIds);

      return pagedTransactions.Select(t => MapToTransactionDto(t, recurringIds));
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting transactions for user {UserId}", userId);
      throw;
    }
  }

  public async Task<TransactionResponseDto> GetTransactionsWithPaginationAsync(int userId, TransactionFilterRequest filter)
  {
    try
    {
      // Get user to check if they're in a family
      var user = await _userRepository.GetByIdAsync(userId);

      // A request with NO dates means "everything", not "the last month".
      //
      // These used to go through GetEffectiveStartDate/GetEffectiveEndDate, which substituted
      // UtcNow.AddMonths(-1) whenever the caller omitted a start date. Nothing surfaced that
      // substitution: the app's "All" date chip sends no dates at all, so "All" silently meant
      // "last 30 days" and everything older simply did not exist as far as the UI was concerned.
      // The list stopped scrolling at the cutoff because hasNextPage was, correctly, false for a
      // window the user never chose. On production that hid 75-84% of real users' history.
      //
      // The repository already guards each bound with HasValue, so passing the nullable values
      // straight through leaves the range genuinely open when none was requested.
      var startDate = filter.GetUtcStartDate();
      var endDate = filter.GetUtcEndDate();

      // Materialize the in-range set ONCE and reuse it for BOTH the page slice and the period
      // summary. Previously the summary re-invoked GetTransactionSummaryAsync, which ran a SECOND
      // full-range GetByUserOrFamilyAsync (all 5 joins) — doubling DB work and memory on every
      // request. The summary must aggregate the whole range (netted refunds, income, savings
      // exclusion), so one full fetch is inherent; the redundant second fetch is not.
      var allTransactions = (await _transactionRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId, startDate, endDate)).ToList();

      _logger.LogDebug(
        "GetTransactions user={UserId} familyId={FamilyId} range={Start}..{End} raw={RawCount}",
        userId, user?.FamilyId,
        startDate?.ToString("yyyy-MM-dd") ?? "open", endDate?.ToString("yyyy-MM-dd") ?? "open",
        allTransactions.Count);

      var filteredTransactions = ApplyFilters(allTransactions, filter);

      var totalItems = filteredTransactions.Count();
      var totalPages = (int)Math.Ceiling((double)totalItems / filter.PageSize);

      // Apply pagination
      var pagedTransactions = filteredTransactions
        .OrderByDescending(t => t.Date)
        .Skip((filter.Page - 1) * filter.PageSize)
        .Take(filter.PageSize)
        .ToList();

      // Batch lookup recurring transactions
      var transactionIds = pagedTransactions.Select(t => t.Id);
      var recurringIds = await _subscriptionTransactionRepository.GetRecurringTransactionIdsAsync(transactionIds);

      var transactionDtos = pagedTransactions.Select(t => MapToTransactionDto(t, recurringIds)).ToList();

      // Calculate summary from the SAME materialized set — no second round-trip to the DB.
      // With an open range the summary's period is whatever the data actually spans, so the
      // header reports the window it really covers rather than a bound nobody asked for.
      var summaryStart = startDate ?? (allTransactions.Count > 0 ? allTransactions.Min(t => t.Date) : DateTime.UtcNow);
      var summaryEnd = endDate ?? (allTransactions.Count > 0 ? allTransactions.Max(t => t.Date) : DateTime.UtcNow);
      var summary = await BuildTransactionSummaryAsync(allTransactions, summaryStart, summaryEnd);

      return new TransactionResponseDto
      {
        Transactions = transactionDtos,
        Pagination = new PaginationMetadata
        {
          CurrentPage = filter.Page,
          PageSize = filter.PageSize,
          TotalPages = totalPages,
          TotalItems = totalItems,
          HasNextPage = filter.Page < totalPages,
          HasPreviousPage = filter.Page > 1
        },
        Summary = summary
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting transactions with pagination for user {UserId}", userId);
      throw;
    }
  }

  public async Task<TransactionSummaryDto> GetTransactionSummaryAsync(int userId, DateTime startDate, DateTime endDate)
  {
    try
    {
      // Get user to check if they're in a family
      var user = await _userRepository.GetByIdAsync(userId);
      var allTransactions = await _transactionRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId, startDate, endDate);

      return await BuildTransactionSummaryAsync(allTransactions, startDate, endDate);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting transaction summary for user {UserId}", userId);
      throw;
    }
  }

  public async Task<Core.Analysis.CashFlowSummary> GetCashFlowSummaryAsync(int userId, DateTime startDate, DateTime endDateExclusive)
  {
    try
    {
      var user = await _userRepository.GetByIdAsync(userId);
      var transactions = await _transactionRepository.GetByUserOrFamilyAsync(
        userId, user?.FamilyId, startDate, endDateExclusive);

      return Core.Analysis.CashFlowCalculator.Summarize(transactions, startDate, endDateExclusive);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting cash flow for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// Builds the period summary (income/expense totals + netted per-category breakdown) from an
  /// ALREADY-FETCHED set of in-range transactions. Extracted so the paginated list endpoint can
  /// reuse the single fetch it already made instead of re-querying the whole date range.
  /// </summary>
  private async Task<TransactionSummaryDto> BuildTransactionSummaryAsync(
    IEnumerable<Core.Entities.Transaction> allTransactions, DateTime startDate, DateTime endDate)
  {
    try
    {
      // Filter out parent split transactions - only children should be counted
      var transactions = allTransactions.Where(t => !t.IsSplit).ToList();

      var totalIncome = transactions.TotalIncome();
      var transactionCount = transactions.Count();

      // CategoryTotals/Counts are a SPENDING breakdown. Use the shared ExpensesByCategory()
      // helper — the SAME one the budget page uses — so per-category spend NETS refunds
      // (a $500 grocery debit minus a $200 refund = $300, not $500) and excludes income,
      // transfers, credit-card payments, splits and savings-earmarked. Grouping with raw
      // sums here made the AI "top spending categories" disagree with the budget page for
      // any category that had a refund. Net-refund categories (net <= 0) aren't spending.
      var netByCategory = transactions.ExpensesByCategory();
      var countByCategory = transactions.ExpenseCountsByCategory();

      var categoryIds = netByCategory.Keys.ToHashSet();
      var categoryNames = await _categoryRepository.GetByIdsAsync(categoryIds);
      var categoryNameMap = categoryNames.ToDictionary(c => c.Id, c => c.Name);

      var categoryTotals = new Dictionary<string, decimal>();
      var categoryCounts = new Dictionary<string, int>();

      foreach (var (categoryId, netSpent) in netByCategory)
      {
        if (netSpent <= 0)
        {
          continue; // net refund — not a spending category
        }

        var categoryName = ResolveCategoryName(categoryId, categoryNameMap);
        categoryTotals[categoryName] = netSpent;
        categoryCounts[categoryName] = countByCategory.TryGetValue(categoryId, out var c) ? c : 0;
      }

      // Derive the total from the netted categories so the summary is self-consistent
      // (TotalExpenses == sum of CategoryTotals) and matches the budget page. With no refunds
      // this equals the previous gross TotalExpenses(); with refunds it correctly nets them.
      var totalExpenses = categoryTotals.Values.Sum();
      var netAmount = totalIncome - totalExpenses;

      return new TransactionSummaryDto
      {
        TotalIncome = totalIncome,
        TotalExpenses = totalExpenses,
        NetAmount = netAmount,
        TransactionCount = transactionCount,
        PeriodStart = startDate,
        PeriodEnd = endDate,
        CategoryTotals = categoryTotals,
        CategoryCounts = categoryCounts
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error building transaction summary for range {Start:yyyy-MM-dd}..{End:yyyy-MM-dd}", startDate, endDate);
      throw;
    }
  }

  /// <summary>
  /// Resolves a unified CategoryId to a display name: the Categories table name, else the
  /// system-enum display name for 1-99, else "Uncategorized".
  /// </summary>
  private static string ResolveCategoryName(int categoryId, IReadOnlyDictionary<int, string> categoryNameMap)
  {
    if (categoryNameMap.TryGetValue(categoryId, out var name))
    {
      return name;
    }

    return categoryId is > 0 and < 1000
      ? ((TransactionCategory)categoryId).GetDisplayName()
      : "Uncategorized";
  }

  public async Task<IEnumerable<CategoryStatisticsDto>> GetCategoryStatisticsAsync(int userId, DateTime startDate, DateTime endDate)
  {
    try
    {
      // Get user to check if they're in a family
      var user = await _userRepository.GetByIdAsync(userId);
      var allTransactions = await _transactionRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId, startDate, endDate);
      
      // Spending patterns = EXPENSES only, netted against refunds. ExpensesByCategory() (the
      // budget page's helper) nets debits minus credit refunds per category and excludes income,
      // transfers, credit-card payments, splits and savings-earmarked. Keeping only net-positive
      // categories makes "% of total spending" reflect actual net spend and agree with the budget
      // breakdown, rather than the raw debit sums that ignored refunds.
      var netByCategory = allTransactions.ExpensesByCategory();
      var countByCategory = allTransactions.ExpenseCountsByCategory();

      var spendingByCategory = netByCategory.Where(kv => kv.Value > 0).ToList();
      var totalAmount = spendingByCategory.Sum(kv => kv.Value);

      var categoryIds = spendingByCategory.Select(kv => kv.Key).ToHashSet();
      var categoryNames = await _categoryRepository.GetByIdsAsync(categoryIds);
      var categoryNameMap = categoryNames.ToDictionary(c => c.Id, c => c.Name);

      var categoryStats = spendingByCategory
        .Select(kv =>
        {
          var count = countByCategory.TryGetValue(kv.Key, out var c) ? c : 0;
          return new CategoryStatisticsDto
          {
            CategoryId = kv.Key,
            CategoryName = ResolveCategoryName(kv.Key, categoryNameMap),
            TotalAmount = kv.Value,
            TransactionCount = count,
            AverageAmount = count > 0 ? kv.Value / count : 0,
            Percentage = totalAmount > 0 ? (kv.Value / totalAmount) * 100 : 0
          };
        })
        .OrderByDescending(c => c.TotalAmount)
        .ToList();

      return categoryStats;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting category statistics for user {UserId}", userId);
      throw;
    }
  }

  public Task<TransactionDto> UpdateTransactionCategoryAsync(int transactionId, UpdateTransactionCategoryRequest request, int userId) =>
    UpdateTransactionCategoryCoreAsync(transactionId, request, userId, null);

  public Task<TransactionDto> UpdateTransactionCategoryDeferringSideEffectsAsync(
    int transactionId,
    UpdateTransactionCategoryRequest request,
    int userId,
    ICollection<Func<Task>> afterCommit
  ) => UpdateTransactionCategoryCoreAsync(transactionId, request, userId, afterCommit);

  /// <summary>
  /// When <paramref name="afterCommit"/> is non-null, the owner notification, gamification and
  /// embedding steps are queued there instead of run, so a caller holding a database transaction
  /// can run them once it commits (they make network calls and swallow failures that would
  /// otherwise abort a Postgres transaction).
  /// </summary>
  private async Task<TransactionDto> UpdateTransactionCategoryCoreAsync(
    int transactionId,
    UpdateTransactionCategoryRequest request,
    int userId,
    ICollection<Func<Task>>? afterCommit
  )
  {
    async Task RunOrDefer(Func<Task> step)
    {
      if (afterCommit != null)
        afterCommit.Add(step);
      else
        await step();
    }

    try
    {
      // Use centralized RBAC for authorization
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);

      var transaction = await _transactionRepository.GetByIdAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");

      // Validate the target category exists (QE #615: an unknown custom-category id
      // hit the FK constraint and 500'd). System categories are 1-99 (enum-backed);
      // custom categories are 1000+ and must exist in the Categories table.
      if (request.CategoryId is int cid)
      {
        var categoryExists = cid < 1000
          ? Enum.IsDefined(typeof(TransactionCategory), cid)
          : await _categoryRepository.GetByIdAsync(cid) != null;
        if (!categoryExists)
          throw new ArgumentException($"Category {cid} does not exist.");
      }

      // Get user info for allowance processing
      var user = await _userRepository.GetByIdAsync(userId);

      transaction.CategoryId = request.CategoryId;
      transaction.IsUserCategorized = true;
      transaction.NeedsReview = false;

      if (request.SemanticTags != null)
      {
        transaction.SemanticTags = SemanticTagVocabulary.ValidateAndFilter(request.SemanticTags);
      }

      // Handle personal sub-category for allowance transactions
      if (request.CategoryId.HasValue)
      {
        // Check if this category is an allowance and who the assigned user is
        var allowanceInfo = await GetAllowanceInfoAsync(request.CategoryId.Value, user?.FamilyId);
        
        if (allowanceInfo != null && allowanceInfo.IsAllowance)
        {
          if (allowanceInfo.AssignedUserId == userId)
          {
            // User IS the allowance owner - they can set personal sub-category.
            // Claim it for them too so it counts on their (owner-scoped) allowance budget.
            transaction.PersonalUserCategoryId = request.PersonalCategoryId;
            transaction.ClaimedByUserId = userId;
            _logger.LogInformation("User {UserId} set personal sub-category {PersonalCategoryId} for their allowance transaction {TransactionId}",
              userId, request.PersonalCategoryId, transactionId);
          }
          else if (allowanceInfo.AssignedUserId.HasValue)
          {
            // User is NOT the owner (e.g. a family admin assigning a charge to a child's
            // allowance). Claim it for the owner so it lands on their allowance budget — under
            // Uncategorized until they sub-categorize — and notify them to split it.
            transaction.ClaimedByUserId = allowanceInfo.AssignedUserId.Value;
            _logger.LogInformation("Non-owner {UserId} categorized transaction {TransactionId} to allowance. Notifying owner {OwnerId}",
              userId, transactionId, allowanceInfo.AssignedUserId.Value);
            
            var categorizedByUser = user;
            var categorizedByUserName = categorizedByUser != null 
              ? $"{categorizedByUser.FirstName} {categorizedByUser.LastName}".Trim() 
              : "Someone";
            
            var ownerId = allowanceInfo.AssignedUserId.Value;
            var merchantLabel = transaction.MerchantName ?? transaction.Description ?? "Transaction";
            var allowanceName = allowanceInfo.CategoryName ?? "Your Allowance";
            await RunOrDefer(() =>
              _notificationService.CreateTransactionSubCategoryNotificationAsync(
                ownerId,
                transactionId,
                merchantLabel,
                allowanceName,
                categorizedByUserName
              )
            );
          }
        }
      }
      
      var updatedTransaction = await _transactionRepository.UpdateAsync(transaction);

      await RunOrDefer(() =>
      {
        _gamificationQueue.Enqueue(userId); // categorizing changes budget spend → streaks/achievements
        return Task.CompletedTask;
      });

      // Manual categorization is a daily activity streak (🏷️). Server-owned here — the
      // client separately advances the categorize_transactions challenge via
      // POST /gamification/challenges/{type}/progress on this same user action.
      await RunOrDefer(() => _gamificationService.RecordDailyActivityAsync(userId, "categorization"));

      // Refresh the category navigation. Only the FK (CategoryId) changed; EF does not
      // reload CategoryNav on an FK change, so it still references the PREVIOUS category
      // (or null). Without this refresh, both the embedding label below and the returned
      // DTO show "Uncategorized"/"Custom" for custom categories (id >= 1000) even though
      // the change persisted correctly — the bug seen in the transaction review modal.
      updatedTransaction.CategoryNav = request.CategoryId.HasValue
          ? await _categoryRepository.GetByIdAsync(request.CategoryId.Value)
          : null;

      // Store confirmed categorization in vector DB for similarity learning
      if (request.CategoryId.HasValue)
      {
        var categoryName = updatedTransaction.CategoryNav?.Name
            ?? (request.CategoryId.Value < 1000
                ? ((TransactionCategory)request.CategoryId.Value).ToString()
                : "Custom");
        var embeddedCategoryId = request.CategoryId.Value;
        await RunOrDefer(() =>
          _transactionEmbeddingService.StoreTransactionEmbeddingAsync(
            userId, updatedTransaction, embeddedCategoryId, categoryName, updatedTransaction.SemanticTags));
      }

      return MapToTransactionDto(updatedTransaction);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating transaction category for transaction {TransactionId}", transactionId);
      throw;
    }
  }

  /// <summary>
  /// Gets allowance info for a category - whether it's an allowance and who it's assigned to
  /// </summary>
  private async Task<AllowanceInfo?> GetAllowanceInfoAsync(int categoryId, int? familyId)
  {
    if (!familyId.HasValue)
      return null;
    
    // Find the family budget with this category as an allowance
    var familyBudgets = await _budgetRepository.GetByFamilyIdAsync(familyId.Value);
    
    foreach (var budget in familyBudgets)
    {
      var category = budget.Categories.FirstOrDefault(c => 
        c.CategoryId == categoryId && c.IsAllowance);
      
      if (category != null)
      {
        return new AllowanceInfo
        {
          IsAllowance = true,
          AssignedUserId = category.AssignedUserId,
          CategoryName = category.CategoryNav?.Name
        };
      }
    }
    
    return null;
  }
  
  private class AllowanceInfo
  {
    public bool IsAllowance { get; set; }
    public int? AssignedUserId { get; set; }
    public string? CategoryName { get; set; }
  }

  /// <summary>
  /// Maps a Transaction entity to a TransactionDto with all fields including Plaid enrichment and AI data
  /// </summary>
  private static TransactionDto MapToTransactionDto(Core.Entities.Transaction t, HashSet<int>? recurringTransactionIds = null)
  {
    // Get category info from unified navigation property
    var categoryName = t.CategoryNav?.Name 
      ?? (t.CategoryId.HasValue && t.CategoryId.Value < 1000 && t.CategoryId.Value > 0 
        ? ((TransactionCategory)t.CategoryId.Value).GetDisplayName() 
        : "Uncategorized");
    
    return new TransactionDto
    {
      Id = t.Id,
      AccountId = t.AccountId,
      PlaidTransactionId = t.PlaidTransactionId,
      Amount = t.Amount,
      Type = t.Type,
      IsoCurrencyCode = t.IsoCurrencyCode,
      Description = t.Description,
      MerchantName = t.MerchantName,
      Nickname = t.Nickname,
      DisplayName = t.DisplayName,
      CategoryId = t.CategoryId,
      CategoryName = categoryName,
      CategoryColor = t.CategoryNav?.Color,
      IsUserCategorized = t.IsUserCategorized,
      Date = t.Date,
      UpdatedAt = t.UpdatedAt,
      AccountName = t.Account?.DisplayName ?? string.Empty,
      IsRecurring = recurringTransactionIds?.Contains(t.Id) ?? false,
      
      // Budget attribution
      BudgetId = t.BudgetId,
      ClaimedByUserId = t.ClaimedByUserId,
      ClaimedByUserName = t.ClaimedByUser != null ? $"{t.ClaimedByUser.FirstName} {t.ClaimedByUser.LastName}".Trim() : null,
      
      // Personal sub-category for allowance transactions
      PersonalCategoryId = t.PersonalUserCategoryId,
      PersonalCategoryName = t.PersonalCategoryNav?.Name,
      PersonalCategoryColor = t.PersonalCategoryNav?.Color,
      
      // Plaid rich data
      PlaidCategoryDetailed = t.PlaidCategoryDetailed,
      MerchantLogoUrl = t.PlaidMerchantLogoUrl,
      MerchantWebsite = t.PlaidMerchantWebsite,
      PaymentChannel = t.PlaidPaymentChannel,
      LocationCity = t.LocationCity,
      LocationRegion = t.LocationRegion,
      IsPending = t.IsPending,
      
      // AI Categorization fields
      AISuggestedCategoryId = t.AISuggestedCategoryId,
      AISuggestedCategoryName = t.AISuggestedCategoryNav?.Name,
      AIConfidence = t.AIConfidence,
      NeedsReview = t.NeedsReview,
      CategorySource = t.CategorySource,
      SemanticTags = t.SemanticTags,

      // User notes
      UserNotes = t.UserNotes,
      
      // Transaction splitting
      IsSplit = t.IsSplit,
      ParentTransactionId = t.ParentTransactionId,
      Splits = t.SplitTransactions?.Select(s => new TransactionSplitDto
      {
        Id = s.Id,
        TransactionId = t.Id,
        Amount = s.Amount,
        CategoryId = s.CategoryId,
        CategoryName = s.CategoryNav?.Name ?? (s.CategoryId.HasValue && s.CategoryId.Value < 1000 && s.CategoryId.Value > 0 
          ? ((TransactionCategory)s.CategoryId.Value).GetDisplayName() 
          : "Uncategorized"),
        CategoryColor = s.CategoryNav?.Color,
        Notes = s.UserNotes
      }).ToList(),
      
      // Savings goal withdrawal
      SavingsGoalId = t.SavingsGoalId,
      SavingsGoalName = t.SavingsGoal?.Name,
      SavingsGoalAccountId = t.SavingsGoal?.AccountId,

      // Draw settlement (0039)
      CoveringTransferTransactionId = t.CoveringTransferTransactionId,
      CoveringTransfer = t.CoveringTransfer == null ? null : new CoveringTransferDto
      {
        TransactionId = t.CoveringTransfer.Id,
        Date = t.CoveringTransfer.Date,
        Amount = t.CoveringTransfer.Amount,
        AccountName = t.CoveringTransfer.Account?.Name ?? string.Empty,
        Title = t.CoveringTransfer.DisplayName,
      }
    };
  }

  public async Task ResetAndResyncTransactionsAsync(int userId)
  {
    try
    {
      _logger.LogInformation("Starting transaction reset and resync for user {UserId}", userId);

      // Reverse every savings-goal earmark BEFORE the bulk delete. Earmarks moved money on the goal
      // via a string-tagged SavingsAllocation, NOT an FK cascade — so DeleteAllByUserIdAsync would drop
      // the earmarked transactions without restoring the goal balances, stranding the drift with no
      // transaction left to un-earmark (the same leak the per-transaction delete path guards against).
      var earmarkedTransactions = (await _transactionRepository.GetByUserIdAsync(userId))
        .Where(t => t.SavingsGoalId.HasValue)
        .ToList();
      foreach (var earmarked in earmarkedTransactions)
      {
        await ReverseSavingsEarmarkAsync(earmarked);
      }
      if (earmarkedTransactions.Count > 0)
      {
        _logger.LogInformation(
          "Reversed {Count} savings-goal earmark(s) before reset for user {UserId}",
          earmarkedTransactions.Count, userId);
      }

      // Clear all existing transactions for the user
      await _transactionRepository.DeleteAllByUserIdAsync(userId);
      _logger.LogInformation("Cleared all existing transactions for user {UserId}", userId);

      _logger.LogInformation("Transaction reset completed for user {UserId}. Background sync will repopulate transactions.", userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error resetting and resyncing transactions for user {UserId}", userId);
      throw;
    }
  }

  public async Task<TransactionDto?> GetTransactionByIdAsync(int transactionId, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdWithSplitsAsync(transactionId);
      if (transaction == null)
        return null;
      
      // Get user to check family context
      var user = await _userRepository.GetByIdAsync(userId);
      
      // Verify access
      if (transaction.Account.UserId != userId)
      {
        if (user?.FamilyId == null)
          return null;
        
        var transactionOwner = await _userRepository.GetByIdAsync(transaction.Account.UserId);
        if (transactionOwner?.FamilyId != user.FamilyId)
          return null;
      }
      
      return MapToTransactionDto(transaction);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting transaction {TransactionId} for user {UserId}", transactionId, userId);
      throw;
    }
  }

  private const int MaxNicknameLength = 200;

  public async Task<TransactionDto> UpdateTransactionNicknameAsync(int transactionId, string? nickname, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");

      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);

      var trimmed = nickname?.Trim();
      // InvalidOperationException, not ArgumentException — the controller maps the latter to 404
      // ("not found"), which would be a lie for a too-long name. Mirrors ApplyToSavingsGoal.
      if (trimmed?.Length > MaxNicknameLength)
        throw new InvalidOperationException($"A transaction name can be at most {MaxNicknameLength} characters.");

      // Empty clears it, so the bank's own name shows again. Never write "" — DisplayName
      // treats blank as absent, but null is the honest "the user set nothing".
      transaction.Nickname = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
      transaction.UpdatedAt = DateTime.UtcNow;

      var updatedTransaction = await _transactionRepository.UpdateAsync(transaction);
      _logger.LogInformation("Renamed transaction {TransactionId} by user {UserId}", transactionId, userId);

      return MapToTransactionDto(updatedTransaction);
    }
    catch (Exception ex) when (ex is not ArgumentException && ex is not InvalidOperationException)
    {
      _logger.LogError(ex, "Error renaming transaction {TransactionId}", transactionId);
      throw;
    }
  }

  public async Task<TransactionDto> UpdateTransactionNotesAsync(int transactionId, string? notes, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");
      
      // Use centralized RBAC for authorization
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);
      
      transaction.UserNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
      transaction.UpdatedAt = DateTime.UtcNow;
      
      var updatedTransaction = await _transactionRepository.UpdateAsync(transaction);
      _logger.LogInformation("Updated notes for transaction {TransactionId} by user {UserId}", transactionId, userId);
      
      return MapToTransactionDto(updatedTransaction);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating notes for transaction {TransactionId}", transactionId);
      throw;
    }
  }

  public async Task<TransactionDto> CreateManualTransactionAsync(CreateTransactionRequest request, int userId)
  {
    try
    {
      // Validate inputs. Non-zero + within the $1B magnitude cap (a fat-fingered
      // huge amount would corrupt budget math and summaries).
      Core.Services.MonetaryLimits.ValidateSignedNonZero(request.Amount, "Amount");
      if (string.IsNullOrWhiteSpace(request.Description))
        throw new ArgumentException("Description is required.");
      if (request.Description.Trim().Length > MaxDescriptionLength)
        throw new ArgumentException($"Description is too long (max {MaxDescriptionLength} characters).");
      if (request.MerchantName != null && request.MerchantName.Trim().Length > MaxMerchantLength)
        throw new ArgumentException($"Merchant name is too long (max {MaxMerchantLength} characters).");
      if (request.UserNotes != null && request.UserNotes.Trim().Length > MaxNotesLength)
        throw new ArgumentException($"Notes are too long (max {MaxNotesLength} characters).");

      // RBAC: read-only family members cannot create transactions.
      if (!await _authService.CanModifyFinancialDataAsync(userId))
        throw new UnauthorizedAccessException("You have view-only access and cannot create transactions.");

      // Resolve the target account. When AccountId is provided it must belong to the user or their
      // family. When omitted, fall back to the user's synthetic "Cash" account (created on demand)
      // so a manual transaction can be entered without a linked bank account.
      var user = await _userRepository.GetByIdAsync(userId);
      var accounts = (await _accountRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId)).ToList();
      Core.Entities.Account account;
      if (request.AccountId.HasValue)
      {
        account = accounts.FirstOrDefault(a => a.Id == request.AccountId.Value)
          ?? throw new ArgumentException("Account not found or you don't have access to it.");
      }
      else
      {
        account = await GetOrCreateCashAccountAsync(accounts, user, userId);
      }

      // Sign convention (mirrors Plaid/split): positive = income (Credit), negative = expense (Debit).
      var hasCategory = request.CategoryId.HasValue;

      // Treat the provided calendar date as UTC (transactions are stored in UTC);
      // default to now if unset.
      var date = request.Date == default
        ? DateTime.UtcNow
        : DateTime.SpecifyKind(request.Date, DateTimeKind.Utc);

      var transaction = new Core.Entities.Transaction
      {
        AccountId = account.Id,
        // "manual_" marks a user-entered transaction (vs Plaid syncs / internal_split_).
        PlaidTransactionId = $"manual_{Guid.NewGuid():N}",
        Amount = request.Amount,
        Type = request.Amount >= 0 ? TransactionType.Credit : TransactionType.Debit,
        Description = request.Description.Trim(),
        MerchantName = string.IsNullOrWhiteSpace(request.MerchantName) ? string.Empty : request.MerchantName.Trim(),
        CategoryId = request.CategoryId ?? (int)TransactionCategory.Uncategorized,
        IsUserCategorized = hasCategory,
        CategorySource = "User",
        NeedsReview = !hasCategory,
        Date = date,
        PersonalUserCategoryId = request.PersonalCategoryId,
        UserNotes = string.IsNullOrWhiteSpace(request.UserNotes) ? null : request.UserNotes.Trim(),
        SemanticTags = string.IsNullOrWhiteSpace(request.SemanticTags) ? null : request.SemanticTags.Trim(),
        IsPending = false,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
      };

      var created = await _transactionRepository.AddAsync(transaction);

      _logger.LogInformation(
        "Created manual transaction {TransactionId} (amount {Amount}) for user {UserId} on account {AccountId}",
        created.Id, created.Amount, userId, account.Id);

      // Reload with navigation props (Account, CategoryNav) so the DTO is fully populated.
      var full = await _transactionRepository.GetByIdWithSplitsAsync(created.Id);
      return MapToTransactionDto(full ?? created);
    }
    catch (Exception ex) when (ex is not ArgumentException and not UnauthorizedAccessException)
    {
      _logger.LogError(ex, "Error creating manual transaction for user {UserId}", userId);
      throw;
    }
  }

  /// <summary>
  /// Returns the user's synthetic "Cash" account, creating it on first use. This lets a manual
  /// transaction be entered without a linked bank account while keeping the ownership chain intact
  /// (a transaction is always owned via its account). One per user; never sourced from Plaid.
  /// </summary>
  private async Task<Core.Entities.Account> GetOrCreateCashAccountAsync(
    IReadOnlyList<Core.Entities.Account> ownedAccounts, Core.Entities.User? user, int userId)
  {
    var existing = ownedAccounts.FirstOrDefault(a => a.UserId == userId && a.Type == AccountType.Cash);
    if (existing != null)
      return existing;

    var now = DateTime.UtcNow;
    return await _accountRepository.CreateAsync(new Core.Entities.Account
    {
      UserId = userId,
      FamilyId = user?.FamilyId,
      PlaidConnectionId = null,
      // Per-user reserved id so it never collides with a real (globally-unique) Plaid account id.
      PlaidAccountId = $"manual-cash-{userId}",
      Name = "Cash",
      OfficialName = "Cash",
      Type = AccountType.Cash,
      Subtype = "cash",
      CurrentBalance = 0m,
      AvailableBalance = 0m,
      Currency = "USD",
      IsActive = true,
      CreatedAt = now,
      UpdatedAt = now,
    });
  }

  public async Task<TransactionDto> SplitTransactionAsync(int transactionId, SplitTransactionRequest request, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdWithSplitsAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");
      
      // Cannot split a split child transaction
      if (transaction.ParentTransactionId.HasValue)
        throw new ArgumentException("Cannot split a transaction that is already part of a split. Edit the parent transaction instead.");
      
      // Use centralized RBAC for authorization
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);
      
      // Get user for allowance processing
      var user = await _userRepository.GetByIdAsync(userId);
      
      // Validate splits
      if (request.Splits == null || request.Splits.Count < 2)
        throw new ArgumentException("At least 2 splits are required");

      if (request.Splits.Count > MaxSplitCount)
        throw new ArgumentException($"A transaction can be split into at most {MaxSplitCount} items");

      // Validate each split has a valid CategoryId (default to Uncategorized if null) and a positive
      // amount — a zero/negative split is meaningless and, en masse, would let a caller pass the
      // conservation check with junk rows.
      foreach (var split in request.Splits)
      {
        split.CategoryId ??= (int)TransactionCategory.Uncategorized;
        if (split.Amount <= 0)
          throw new ArgumentException("Each split amount must be greater than zero");
      }

      var totalSplitAmount = request.Splits.Sum(s => Math.Abs(s.Amount));
      var originalAmount = Math.Abs(transaction.Amount);
      
      // Allow small variance (0.01) for rounding
      if (Math.Abs(totalSplitAmount - originalAmount) > 0.01m)
        throw new ArgumentException($"Split amounts ({totalSplitAmount:C}) must equal the original transaction amount ({originalAmount:C})");
      
      // Determine if this is an expense (negative) or income (positive)
      var isExpense = transaction.Amount < 0;

      // The whole re-split (reverse+delete existing children, reverse parent earmark, insert new
      // children, flag the parent) must be atomic: a mid-op failure previously left the parent
      // IsSplit=false alongside committed children (charge double-counted) or the children deleted
      // with no replacement (charge vanished). Wrap it in one DB transaction so it all-or-nothing
      // commits.
      await _transactionRepository.ExecuteInTransactionAsync(async () =>
      {
        // If transaction was already split, remove existing splits first. Reverse each child's
        // savings-goal earmark BEFORE deleting it — the allocation is a string-tagged row (not an FK
        // cascade), so a plain delete would strand the goal balance with no transaction to un-earmark.
        if (transaction.IsSplit && transaction.SplitTransactions?.Any() == true)
        {
          foreach (var existingSplit in transaction.SplitTransactions.ToList())
          {
            await ReverseSavingsEarmarkAsync(existingSplit);
            await _transactionRepository.DeleteAsync(existingSplit.Id);
          }
        }

        // If the parent was earmarked to a savings goal, reverse and clear that earmark before
        // splitting. The single allocation row is keyed to the PARENT id; children can't inherit a
        // reversible earmark (no per-child allocation), which stranded the goal balance and left a
        // phantom savings transfer that couldn't be undone. Undoing here returns the money to the
        // budget as normal spend; the user can re-earmark individual children afterward.
        if (transaction.SavingsGoalId.HasValue)
        {
          await ReverseSavingsEarmarkAsync(transaction);
          transaction.SavingsGoalId = null;
        }

        // Mark parent as split
        transaction.IsSplit = true;
        transaction.UpdatedAt = DateTime.UtcNow;

        // Create split transactions (batched into a single INSERT round-trip).
        var splitChildren = new List<Core.Entities.Transaction>();
        foreach (var split in request.Splits)
        {
          splitChildren.Add(new Core.Entities.Transaction
          {
          AccountId = transaction.AccountId,
          // Use internal prefix to clearly indicate this is an internal split, not from Plaid
          PlaidTransactionId = $"internal_split_{Guid.NewGuid():N}",
          Amount = isExpense ? -Math.Abs(split.Amount) : Math.Abs(split.Amount),
          Type = transaction.Type,
          Description = transaction.Description,
          MerchantName = transaction.MerchantName,
          // Inherit the rename too — splitting a transaction the user named must not throw that
          // name away and leave the children looking nameless.
          Nickname = transaction.Nickname,
          CategoryId = split.CategoryId,
          UserNotes = string.IsNullOrWhiteSpace(split.Note) ? null : SafeText.Truncate(split.Note.Trim(), MaxSplitNoteLength),
          IsUserCategorized = true,
          CategorySource = "User",
          Date = transaction.Date,
          IsSplit = false,
          ParentTransactionId = transaction.Id,
          // Inherit the parent's budget attribution so the split amounts stay bucketed the
          // same way the original charge was. Without this, splitting a charge that was
          // claimed to an allowance (ClaimedByUserId) and privately sub-categorized
          // (PersonalUserCategoryId) — or earmarked to a savings goal / assigned to a budget —
          // silently drops it from that allowance/goal's spend, since the children carried null.
          ClaimedByUserId = transaction.ClaimedByUserId,
          PersonalUserCategoryId = transaction.PersonalUserCategoryId,
          SavingsGoalId = transaction.SavingsGoalId,
          BudgetId = transaction.BudgetId,
          CreatedAt = DateTime.UtcNow,
          UpdatedAt = DateTime.UtcNow,
          
          // Copy Plaid data
          PlaidCategoryPrimary = transaction.PlaidCategoryPrimary,
          PlaidCategoryDetailed = transaction.PlaidCategoryDetailed,
          PlaidMerchantEntityId = transaction.PlaidMerchantEntityId,
          PlaidMerchantLogoUrl = transaction.PlaidMerchantLogoUrl,
          PlaidMerchantWebsite = transaction.PlaidMerchantWebsite,
          PlaidPaymentChannel = transaction.PlaidPaymentChannel,
          CheckNumber = transaction.CheckNumber,
          TransactionCode = transaction.TransactionCode,
          PaymentPayee = transaction.PaymentPayee,
          PaymentPayer = transaction.PaymentPayer,
          PaymentReason = transaction.PaymentReason,
          PaymentReferenceNumber = transaction.PaymentReferenceNumber,
          AuthorizedDate = transaction.AuthorizedDate,
          IsoCurrencyCode = transaction.IsoCurrencyCode,
          OriginalDescription = transaction.OriginalDescription,
          LocationCity = transaction.LocationCity,
          LocationRegion = transaction.LocationRegion,
          IsPending = transaction.IsPending,
          SemanticTags = transaction.SemanticTags
          });
        }

        await _transactionRepository.CreateManyAsync(splitChildren);
        await _transactionRepository.UpdateAsync(transaction);
      });

      _logger.LogInformation("Split transaction {TransactionId} into {SplitCount} parts by user {UserId}",
        transactionId, request.Splits.Count, userId);
      
      // Reload with splits
      var updatedTransaction = await _transactionRepository.GetByIdWithSplitsAsync(transactionId);
      return MapToTransactionDto(updatedTransaction!);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error splitting transaction {TransactionId}", transactionId);
      throw;
    }
  }

  public async Task<TransactionDto> UnsplitTransactionAsync(int transactionId, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdWithSplitsAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");
      
      if (!transaction.IsSplit)
        throw new ArgumentException("Transaction is not split");
      
      // Use centralized RBAC for authorization
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);
      
      // Delete children and un-flag the parent atomically so a mid-op failure can't leave the parent
      // hidden (IsSplit=true) with no children — which would make the whole charge vanish from totals.
      await _transactionRepository.ExecuteInTransactionAsync(async () =>
      {
        if (transaction.SplitTransactions?.Any() == true)
        {
          foreach (var split in transaction.SplitTransactions.ToList())
          {
            // Reverse each child's savings-goal earmark BEFORE deleting it. The allocation is a
            // string-tagged row, not an FK cascade, so a plain delete strands the goal balance with
            // no transaction left to un-earmark. ReverseSavingsEarmarkAsync no-ops when the child
            // carries no SavingsGoalId.
            await ReverseSavingsEarmarkAsync(split);
            await _transactionRepository.DeleteAsync(split.Id);
          }
        }

        // Mark parent as no longer split
        transaction.IsSplit = false;
        transaction.UpdatedAt = DateTime.UtcNow;

        await _transactionRepository.UpdateAsync(transaction);
      });

      _logger.LogInformation("Unsplit transaction {TransactionId} by user {UserId}", transactionId, userId);

      var updatedTransaction = await _transactionRepository.GetByIdWithSplitsAsync(transactionId);
      return MapToTransactionDto(updatedTransaction!);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error unsplitting transaction {TransactionId}", transactionId);
      throw;
    }
  }

  public async Task DeleteTransactionAsync(int transactionId, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdWithSplitsAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");
      
      // Use centralized RBAC for authorization (Delete permission requires Owner/BudgetAdmin for family transactions)
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Delete);

      // Reject deleting an individual split CHILD directly. Children are surfaced in the transaction
      // list (IsSplit=false, ParentTransactionId set), so a DELETE could target one — but removing a
      // single child leaves the parent still flagged split with children that no longer sum to the
      // parent amount, silently dropping that child's spend from every total with no way to
      // reconcile. The user must edit or unsplit the parent instead.
      if (transaction.ParentTransactionId.HasValue)
        throw new ArgumentException("Cannot delete an individual split item. Unsplit or edit the parent transaction instead.");

      // Reverse any savings-goal earmark BEFORE deleting. The earmark moved money on the goal
      // (WithdrawFromGoalAsync / DepositFromTransactionAsync) via a string-tagged allocation, NOT an
      // FK cascade — so deleting the transaction without reversing left the goal permanently drifted
      // by the earmarked amount with no transaction left to un-earmark. Reverse the parent (which
      // holds the allocation) and each split child (defensive; child reversals no-op when they carry
      // no allocation of their own).
      await ReverseSavingsEarmarkAsync(transaction);

      // If transaction has splits, delete them first
      if (transaction.IsSplit && transaction.SplitTransactions?.Any() == true)
      {
        foreach (var split in transaction.SplitTransactions.ToList())
        {
          await ReverseSavingsEarmarkAsync(split);
          await _transactionRepository.DeleteAsync(split.Id);
        }
      }

      // Delete the transaction
      await _transactionRepository.DeleteAsync(transactionId);
      
      _logger.LogInformation("Deleted transaction {TransactionId} by user {UserId}", transactionId, userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error deleting transaction {TransactionId}", transactionId);
      throw;
    }
  }

  /// <summary>
  /// Reverses a transaction's savings-goal earmark so the goal balance is restored before the
  /// transaction is deleted or split. Reverses by the PERSISTED allocation source tag, not by
  /// re-deriving the direction from the transaction: the apply path
  /// (TransactionsController.IsIncomeTransaction) books a deposit vs. a withdrawal using ONLY
  /// Type==Credit or a SYSTEM income-category id (1-4), whereas the entity IsIncome() ALSO honors
  /// custom IsIncome-flagged categories. So a Debit in a custom income category is WITHDRAWN on apply
  /// but IsIncome()==true here — re-deriving would look for a non-existent 'transaction_deposit:{id}'
  /// allocation, throw, and silently strand the goal drift (the exact bug this reversal exists to
  /// prevent). Probing BOTH undo sources reverses whichever direction was actually applied; the source
  /// tags ('transaction_withdrawal:{id}' vs 'transaction_deposit:{id}') are distinct, so at most one
  /// matches. No-op when the transaction isn't earmarked, carries no allocation of its own (e.g. an
  /// inherited split-child link), or was already reversed.
  /// </summary>
  private async Task ReverseSavingsEarmarkAsync(Core.Entities.Transaction transaction)
  {
    if (!transaction.SavingsGoalId.HasValue)
      return;

    var goalId = transaction.SavingsGoalId.Value;

    // Direction-agnostic: try the withdrawal undo, then the deposit undo. Each is a no-op (throws
    // ArgumentException, swallowed) when no allocation of that direction exists for the transaction.
    var reversed = await TryUndoEarmarkAsync(
      () => _savingsService.Value.UndoWithdrawalAsync(goalId, transaction.Id));
    reversed |= await TryUndoEarmarkAsync(
      () => _savingsService.Value.UndoDepositAsync(goalId, transaction.Id));

    if (!reversed)
    {
      _logger.LogWarning(
        "No savings allocation to reverse for transaction {TransactionId} on goal {GoalId}; continuing",
        transaction.Id, goalId);
    }
  }

  /// <summary>
  /// Runs one directional earmark undo, returning true if it reversed an allocation and false when
  /// there was none of that direction (the ArgumentException the Undo* helpers throw for a missing
  /// allocation). Lets <see cref="ReverseSavingsEarmarkAsync"/> probe both directions without either
  /// probe's "not found" aborting the delete/split.
  /// </summary>
  private static async Task<bool> TryUndoEarmarkAsync(Func<Task> undo)
  {
    try
    {
      await undo();
      return true;
    }
    catch (ArgumentException)
    {
      return false;
    }
  }

  public async Task<TransactionDto> UpdateTransactionDateAsync(int transactionId, DateTime newDate, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdWithSplitsAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");
      
      // Use centralized RBAC for authorization
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);
      
      // Update the date (ensure it's stored as UTC)
      var utcDate = newDate.Kind == DateTimeKind.Utc ? newDate : DateTime.SpecifyKind(newDate, DateTimeKind.Utc);
      transaction.Date = utcDate;
      transaction.UpdatedAt = DateTime.UtcNow;
      
      var updatedTransaction = await _transactionRepository.UpdateAsync(transaction);
      
      _logger.LogInformation("Updated transaction {TransactionId} date to {NewDate} by user {UserId}", 
        transactionId, utcDate, userId);
      
      return MapToTransactionDto(updatedTransaction);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating transaction date for {TransactionId}", transactionId);
      throw;
    }
  }

  public async Task<TransactionDto> LinkToSavingsGoalAsync(int transactionId, int? savingsGoalId, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdWithSplitsAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");

      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);

      // A draw only exists while the earmark does: un-earmarking or moving to another goal
      // puts nothing "in flight", so the settlement link goes with it.
      if (transaction.SavingsGoalId != savingsGoalId)
        transaction.CoveringTransferTransactionId = null;
      transaction.SavingsGoalId = savingsGoalId;
      transaction.UpdatedAt = DateTime.UtcNow;

      var updatedTransaction = await _transactionRepository.UpdateAsync(transaction);

      _logger.LogInformation("Linked transaction {TransactionId} to savings goal {SavingsGoalId} by user {UserId}",
        transactionId, savingsGoalId, userId);

      return MapToTransactionDto(updatedTransaction);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error linking transaction {TransactionId} to savings goal", transactionId);
      throw;
    }
  }

  /// <summary>Window in which a transfer out of the goal's account can plausibly have moved the money.</summary>
  private const int CoveringTransferLookbackDays = 14;

  /// <summary>
  /// Loads an earmarked expense together with the linked goal it draws from, or throws the
  /// InvalidOperationException the controller turns into a 400 explaining why this row can't
  /// have a covering transfer.
  /// </summary>
  private async Task<(Core.Entities.Transaction Draw, SavingsGoalDto Goal)> LoadDrawAsync(int transactionId, int userId)
  {
    var draw = await _transactionRepository.GetByIdWithSplitsAsync(transactionId)
      ?? throw new ArgumentException("Transaction not found");
    await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);

    if (!draw.SavingsGoalId.HasValue)
      throw new InvalidOperationException("This transaction isn't paid from a savings goal.");
    if (draw.Amount >= 0)
      throw new InvalidOperationException("Only a purchase paid from a goal can be covered by a transfer.");

    var goal = await _savingsService.Value.GetSavingsGoalAsync(draw.SavingsGoalId.Value, userId)
      ?? throw new ArgumentException("Savings goal not found");
    if (!goal.AccountId.HasValue)
      throw new InvalidOperationException($"{goal.Name} isn't linked to an account, so there is no transfer to match.");

    return (draw, goal);
  }

  public async Task<CoveringTransferCandidatesDto> GetCoveringTransferCandidatesAsync(int transactionId, int userId)
  {
    var (draw, goal) = await LoadDrawAsync(transactionId, userId);
    var pending = await _savingsService.Value.GetPendingDrawsAsync(goal.Id);
    if (pending.All(p => p.TransactionId != draw.Id))
    {
      // Earmarked before the last sync: the bank already absorbed it, but the user may still
      // want to record which transfer it was. Treat it as one more pending draw for matching.
      pending = pending.Concat(new[] { new PendingDrawDto
      {
        TransactionId = draw.Id, Date = draw.Date, Amount = Math.Abs(draw.Amount), Title = draw.DisplayName,
      } }).ToList();
    }
    var pendingTotal = pending.Sum(p => p.Amount);
    var thisAmount = Math.Abs(draw.Amount);

    var from = draw.Date.AddDays(-CoveringTransferLookbackDays);
    var candidates = (await _transactionRepository.GetByAccountIdAsync(goal.AccountId.Value))
      .Where(t => t.Amount < 0 && !t.IsPending && t.ParentTransactionId == null && t.Id != draw.Id && t.Date >= from)
      .OrderByDescending(t => t.Date)
      .Take(40)
      .Select(t => new CoveringTransferCandidateDto
      {
        TransactionId = t.Id,
        Date = t.Date,
        Amount = Math.Abs(t.Amount),
        Title = t.DisplayName,
        CategoryName = t.CategoryNav?.Name,
        MatchesThis = Math.Abs(t.Amount) == thisAmount,
        MatchesPendingTotal = pending.Count > 1 && Math.Abs(t.Amount) == pendingTotal,
      })
      // Exact matches first, then most recent.
      .OrderByDescending(c => c.MatchesPendingTotal || c.MatchesThis)
      .ThenByDescending(c => c.Date)
      .ToList();

    return new CoveringTransferCandidatesDto
    {
      SavingsGoalId = goal.Id,
      SavingsGoalName = goal.Name,
      AccountId = goal.AccountId.Value,
      AccountName = goal.AccountName ?? string.Empty,
      PendingDraws = pending.ToList(),
      PendingTotal = pendingTotal,
      Candidates = candidates,
    };
  }

  public async Task<TransactionDto> LinkCoveringTransferAsync(int transactionId, LinkCoveringTransferRequest request, int userId)
  {
    var (draw, goal) = await LoadDrawAsync(transactionId, userId);
    if (request.TransferTransactionId == draw.Id)
      throw new InvalidOperationException("A purchase can't cover itself.");

    var transfer = await _transactionRepository.GetByIdAsync(request.TransferTransactionId)
      ?? throw new ArgumentException("Transfer transaction not found");
    await _authService.ValidateTransactionAccessOrThrowAsync(userId, transfer.Id, ResourcePermission.Edit);
    if (transfer.AccountId != goal.AccountId!.Value)
      throw new InvalidOperationException($"That transaction isn't on {goal.AccountName}, where {goal.Name} lives.");
    if (transfer.Amount >= 0)
      throw new InvalidOperationException("The covering transaction must be money leaving the account.");

    var now = DateTime.UtcNow;
    draw.CoveringTransferTransactionId = transfer.Id;
    draw.UpdatedAt = now;
    await _transactionRepository.UpdateAsync(draw);
    var covered = 1;

    if (request.IncludeOtherPending)
    {
      foreach (var other in await _savingsService.Value.GetPendingDrawsAsync(goal.Id))
      {
        if (other.TransactionId == draw.Id) continue;
        var row = await _transactionRepository.GetByIdAsync(other.TransactionId);
        if (row == null || row.CoveringTransferTransactionId.HasValue) continue;
        row.CoveringTransferTransactionId = transfer.Id;
        row.UpdatedAt = now;
        await _transactionRepository.UpdateAsync(row);
        covered++;
      }
    }

    // The transfer is explained now; it must not resurface as a suspected transfer.
    if (!transfer.TransferReviewedAt.HasValue)
    {
      transfer.TransferReviewedAt = now;
      await _transactionRepository.UpdateAsync(transfer);
    }

    _logger.LogInformation(
      "User {UserId} tied {Count} draw(s) on goal {GoalId} to transfer {TransferId}", userId, covered, goal.Id, transfer.Id);

    var refreshed = await _transactionRepository.GetByIdWithSplitsAsync(draw.Id);
    return MapToTransactionDto(refreshed!);
  }

  public async Task<TransactionDto> UnlinkCoveringTransferAsync(int transactionId, int userId)
  {
    var draw = await _transactionRepository.GetByIdWithSplitsAsync(transactionId)
      ?? throw new ArgumentException("Transaction not found");
    await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);

    if (draw.CoveringTransferTransactionId.HasValue)
    {
      draw.CoveringTransferTransactionId = null;
      draw.CoveringTransfer = null;
      draw.UpdatedAt = DateTime.UtcNow;
      await _transactionRepository.UpdateAsync(draw);
    }
    return MapToTransactionDto(draw);
  }

  public async Task MarkTransferReviewedAsync(int transactionId, int userId)
  {
    try
    {
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);

      var transaction = await _transactionRepository.GetByIdAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");

      // Dedicated marker only — deliberately does NOT set IsUserCategorized / NeedsReview,
      // so dismissing a transfer suggestion doesn't remove an uncategorized transaction from
      // the categorization / AI-review queue (it only stops the transfer detector re-suggesting it).
      transaction.TransferReviewedAt = DateTime.UtcNow;
      transaction.UpdatedAt = DateTime.UtcNow;

      await _transactionRepository.UpdateAsync(transaction);

      _logger.LogInformation("Marked transaction {TransactionId} reviewed for transfer detection by user {UserId}",
        transactionId, userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error marking transaction {TransactionId} transfer-reviewed", transactionId);
      throw;
    }
  }
}
