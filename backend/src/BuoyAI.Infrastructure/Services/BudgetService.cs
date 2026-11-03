using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Utilities;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Budget management service. 
/// Transfer-related operations are delegated to IBudgetTransferService.
/// Category group operations are delegated to IBudgetCategoryGroupService.
/// </summary>
public class BudgetService : IBudgetService
{
  private readonly IBudgetRepository _budgetRepository;
  private readonly ITransactionRepository _transactionRepository;
  private readonly IUserRepository _userRepository;
  private readonly ICategoryRepository _categoryRepository;
  private readonly IBudgetPeriodService _budgetPeriodService;
  private readonly ISavingsRepository _savingsRepository;
  private readonly IBudgetSpendingService _spendingService;
  private readonly IFamilyAuthorizationService _authService;
  private readonly BuoyAIDbContext _context;
  private readonly ILogger<BudgetService> _logger;
  private readonly IHttpContextAccessor _httpContextAccessor;
  private readonly IGamificationEvaluationQueue _gamificationQueue;
  private readonly Telemetry.BuoyAiMetrics _metrics;

  public BudgetService(
    IBudgetRepository budgetRepository,
    ITransactionRepository transactionRepository,
    IUserRepository userRepository,
    ICategoryRepository categoryRepository,
    IBudgetPeriodService budgetPeriodService,
    ISavingsRepository savingsRepository,
    IBudgetSpendingService spendingService,
    IFamilyAuthorizationService authService,
    BuoyAIDbContext context,
    ILogger<BudgetService> logger,
    IHttpContextAccessor httpContextAccessor,
    IGamificationEvaluationQueue gamificationQueue,
    Telemetry.BuoyAiMetrics metrics)
  {
    _budgetRepository = budgetRepository;
    _transactionRepository = transactionRepository;
    _userRepository = userRepository;
    _categoryRepository = categoryRepository;
    _budgetPeriodService = budgetPeriodService;
    _savingsRepository = savingsRepository;
    _spendingService = spendingService;
    _authService = authService;
    _context = context;
    _logger = logger;
    _httpContextAccessor = httpContextAccessor;
    _gamificationQueue = gamificationQueue;
    _metrics = metrics;
  }

  /// <summary>
  /// Reads the X-Timezone-Offset header (minutes from UTC) sent by the Flutter client.
  /// Returns null if the header is absent or invalid, falling back to UTC-based period calculations.
  /// </summary>
  /// <summary>The user's chosen week-start day, or null for legacy behavior (#941).</summary>
  private async Task<DayOfWeek?> WeekStartAsync(int userId)
  {
    var day = await _context.Users.Where(u => u.Id == userId).Select(u => u.WeekStartDay).FirstOrDefaultAsync();
    return day is >= 0 and <= 6 ? (DayOfWeek)day : null;
  }

  private int? GetTimezoneOffsetMinutes()
  {
    var header = _httpContextAccessor.HttpContext?.Request.Headers["X-Timezone-Offset"].FirstOrDefault();
    if (int.TryParse(header, out var offset) && offset >= -840 && offset <= 840)
      return offset;
    return null;
  }

  /// <summary>
  /// Lazily advances a budget through EVERY elapsed period, not just one.
  /// <para>
  /// A single <see cref="IBudgetPeriodService.ResetBudgetAsync"/> call processes exactly one period's
  /// rollover/snapshot and advances the window once. If the user was idle for several periods (e.g. did
  /// not open the app for two months), a single reset would leave CurrentPeriodEnd in the PAST and the
  /// budget DTO would report a stale, already-elapsed period as "current" — with the intervening
  /// periods' rollover/savings processing silently deferred until later requests each triggered one more
  /// single reset. Loop until the budget is genuinely current, asserting the period strictly advances
  /// each iteration and capping iterations so a mis-set period can never spin forever.
  /// </para>
  /// </summary>
  private async Task ResetBudgetToCurrentAsync(Budget budget)
  {
    // >10 years of monthly periods; a pure safety valve, never hit in normal catch-up.
    const int maxCatchUpIterations = 160;
    var offset = GetTimezoneOffsetMinutes();
    var iterations = 0;
    while (budget.IsActive && _budgetPeriodService.ShouldResetBudget(budget, DateTime.UtcNow, offset))
    {
      var periodEndBefore = budget.CurrentPeriodEnd;
      await _budgetPeriodService.ResetBudgetAsync(budget);
      // Stop if the reset failed to move the window forward (guards against an infinite loop) or the
      // safety cap is reached.
      if (budget.CurrentPeriodEnd <= periodEndBefore || ++iterations >= maxCatchUpIterations)
        break;
    }
  }

  public async Task<BudgetDto?> GetActiveBudgetAsync(int userId)
  {
    try
    {
      var budget = await _budgetRepository.GetActivePersonalBudgetAsync(userId);
      if (budget == null)
        return null;

      // Advance through every elapsed period so the returned budget reflects the true current period.
      await ResetBudgetToCurrentAsync(budget);

      return await MapToBudgetDtoAsync(budget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting active budget for user {UserId}", userId);
      throw;
    }
  }

  public async Task<BudgetDto?> GetBudgetByIdAsync(int budgetId, int userId)
  {
    try
    {
      var budget = await _budgetRepository.GetByIdAsync(budgetId);
      if (budget == null)
        return null;

      // Check if user has access
      if (!await HasAccessToBudgetAsync(budget, userId))
      {
        _logger.LogWarning("User {UserId} attempted to access budget {BudgetId} without permission", userId, budgetId);
        return null;
      }

      // Advance through every elapsed period so the returned budget reflects the true current period.
      await ResetBudgetToCurrentAsync(budget);

      return await MapToBudgetDtoAsync(budget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting budget {BudgetId} for user {UserId}", budgetId, userId);
      throw;
    }
  }

  public async Task<IEnumerable<BudgetDto>> GetBudgetsAsync(int userId)
  {
    try
    {
      var budgets = await _budgetRepository.GetByUserIdAsync(userId);
      var budgetDtos = new List<BudgetDto>();

      // Share one transaction/user memo across every budget in this request so N budgets in the same
      // period don't each trigger a full (5-Include) transaction load + user lookup (was N+1).
      var transactionCache = new Dictionary<TransactionLoadKey, IReadOnlyList<Transaction>>();
      var userCache = new Dictionary<int, User?>();

      foreach (var budget in budgets)
      {
        // Advance through every elapsed period so each budget reflects the true current period.
        await ResetBudgetToCurrentAsync(budget);

        budgetDtos.Add(await MapToBudgetDtoAsync(budget, transactionCache, userCache));
      }

      return budgetDtos;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting budgets for user {UserId}", userId);
      throw;
    }
  }

  public async Task<IEnumerable<BudgetDto>> GetAccessibleBudgetsAsync(int userId)
  {
    try
    {
      var budgets = await _budgetRepository.GetAccessibleBudgetsAsync(userId);
      var budgetDtos = new List<BudgetDto>();

      // Share one transaction/user memo across every budget in this request (see GetBudgetsAsync).
      var transactionCache = new Dictionary<TransactionLoadKey, IReadOnlyList<Transaction>>();
      var userCache = new Dictionary<int, User?>();

      foreach (var budget in budgets)
      {
        // Advance through every elapsed period so each budget reflects the true current period.
        await ResetBudgetToCurrentAsync(budget);

        budgetDtos.Add(await MapToBudgetDtoAsync(budget, transactionCache, userCache));
      }

      return budgetDtos;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting accessible budgets for user {UserId}", userId);
      throw;
    }
  }

  public async Task<BudgetDto?> GetPrimaryBudgetAsync(int userId)
  {
    try
    {
      var budget = await _budgetRepository.GetPrimaryBudgetAsync(userId);
      if (budget == null)
        return null;

      // Advance through every elapsed period so the returned budget reflects the true current period.
      await ResetBudgetToCurrentAsync(budget);

      return await MapToBudgetDtoAsync(budget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting primary budget for user {UserId}", userId);
      throw;
    }
  }

  public async Task SetPrimaryBudgetAsync(int budgetId, int userId)
  {
    try
    {
      var budget = await _budgetRepository.GetByIdAsync(budgetId);
      if (budget == null || budget.UserId != userId)
        throw new ArgumentException("Budget not found or access denied");

      // Clear primary flag from all user's budgets
      var userBudgets = await _budgetRepository.GetByUserIdAsync(userId);
      foreach (var b in userBudgets.Where(b => b.IsPrimary))
      {
        b.IsPrimary = false;
        await _budgetRepository.UpdateAsync(b);
      }

      // Set new primary
      budget.IsPrimary = true;
      await _budgetRepository.UpdateAsync(budget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error setting primary budget {BudgetId} for user {UserId}", budgetId, userId);
      throw;
    }
  }

  /// <summary>
  /// Collapse duplicate category rows in a create/update request to a single row per
  /// (CategoryId, AssignedUserId) — the same key the DB unique index enforces (NULL AssignedUserId
  /// treated as 0). A duplicate CategoryId would otherwise double-count spend on the budget page
  /// and make a later edit throw on the ToDictionary keyed by CategoryId. First occurrence wins.
  /// </summary>
  private List<CreateBudgetCategoryRequest> DedupeCategories(List<CreateBudgetCategoryRequest>? categories)
  {
    if (categories == null || categories.Count == 0)
      return categories ?? new List<CreateBudgetCategoryRequest>();

    var deduped = categories
      .GroupBy(c => (c.CategoryId, c.AssignedUserId ?? 0))
      .Select(g => g.First())
      .ToList();

    if (deduped.Count != categories.Count)
      _logger.LogWarning("Dropped {Count} duplicate budget category row(s) from request",
        categories.Count - deduped.Count);

    return deduped;
  }

  public async Task<BudgetDto> CreateBudgetAsync(int userId, CreateBudgetRequest request)
  {
    try
    {
      // Collapse duplicate categories up front so a malformed request (same CategoryId twice)
      // can't double-count spend or trip the DB unique index. See DedupeCategories.
      request.Categories = DedupeCategories(request.Categories);

      // Guard category allocations (QE #615 Part E: no server-side bound).
      foreach (var c in request.Categories ?? Enumerable.Empty<CreateBudgetCategoryRequest>())
        Core.Services.MonetaryLimits.ValidateNonNegative(c.AllocatedAmount, "Allocated amount");

      // Check if this is user's first budget of this type - make it primary
      var existingBudgets = await _budgetRepository.GetByUserIdAsync(userId);
      var isPrimary = !existingBudgets.Any(b => b.Type == request.Type);
      
      // Deactivate existing active budgets of the SAME TYPE only
      // This allows having both a personal and family budget active simultaneously
      var existingActiveBudgetsOfSameType = existingBudgets
        .Where(b => b.IsActive && b.Type == request.Type)
        .ToList();
      
      foreach (var existingBudget in existingActiveBudgetsOfSameType)
      {
        existingBudget.IsActive = false;
        await _budgetRepository.UpdateAsync(existingBudget);
      }

      // Calculate current period dates
      var (periodStart, periodEnd) = _budgetPeriodService.CalculateCurrentPeriod(request.Period, utcOffsetMinutes: GetTimezoneOffsetMinutes(), weekStart: await WeekStartAsync(userId));

      // Resolve the family context. Family budgets require a family; a family member's
      // personal budget is linked under the family budget as a child so the whole family can
      // see its progress (an explicit ParentBudgetId, e.g. from the personal-budget-from-
      // allowance flow, is respected; otherwise it defaults to the active family budget).
      int? familyId = null;
      int? parentBudgetId = request.ParentBudgetId;
      if (request.Type == BudgetType.Family)
      {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user?.FamilyId == null)
        {
          throw new InvalidOperationException("Cannot create a family budget without being a member of a family.");
        }
        familyId = user.FamilyId;
      }
      else if (request.Type == BudgetType.Personal)
      {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user?.FamilyId != null)
        {
          familyId = user.FamilyId;
          if (parentBudgetId == null)
          {
            var familyBudgets = await _budgetRepository.GetByFamilyIdAsync(user.FamilyId.Value);
            parentBudgetId = familyBudgets.FirstOrDefault(b => b.Type == BudgetType.Family && b.IsActive)?.Id;
          }
        }
      }

      var budget = new Budget
      {
        UserId = userId,
        FamilyId = familyId,
        Name = request.Name,
        Period = request.Period,
        CurrentPeriodStart = DateTime.SpecifyKind(periodStart, DateTimeKind.Utc),
        CurrentPeriodEnd = DateTime.SpecifyKind(periodEnd, DateTimeKind.Utc),
        LastReset = DateTime.UtcNow,
        AutoReset = request.AutoReset,
        TotalIncome = request.TotalIncome,
        TotalExpenses = request.Categories.Sum(c => c.AllocatedAmount),
        IsActive = true,
        Type = request.Type,
        ParentBudgetId = parentBudgetId,
        AllocatedAmount = request.AllocatedAmount,
        IsPrimary = isPrimary,
        Categories = request.Categories.Select(c => new BudgetCategory
        {
          // Set unified CategoryId from request
          CategoryId = c.CategoryId,
          AllocatedAmount = c.AllocatedAmount,
          AllowRollover = c.AllowRollover,
          RolloverBalance = 0,
          IsIncome = c.IsIncome,  // Include income flag
          IsCountUpEnabled = c.IsCountUpEnabled,  // Include count-up flag
          IsAllowance = c.IsAllowance,
          AssignedUserId = c.AssignedUserId,
          SavingsGoalId = c.SavingsGoalId,
          RolloverOverflowSavingsGoalId = c.RolloverOverflowSavingsGoalId
        }).ToList()
      };

      var createdBudget = await _budgetRepository.CreateAsync(budget);
      _logger.LogInformation("Created {Type} budget {BudgetId} for user {UserId} with FamilyId {FamilyId}",
        request.Type, createdBudget.Id, userId, familyId);

      // RFC #793: a Personal budget created inside a family can be funded from a family
      // category — either an existing one (FundFromCategoryId) or a newly-created one
      // (NewFamilyCategoryName). This converges the personal child onto the linked-category
      // model. Creating/linking the allocation is admin-only; a plain member creating a
      // standalone personal budget (neither field set) is unaffected.
      if (request.Type == BudgetType.Personal && familyId != null && parentBudgetId != null
          && (request.FundFromCategoryId.HasValue || !string.IsNullOrWhiteSpace(request.NewFamilyCategoryName)))
      {
        await LinkFamilyFundingCategoryAsync(userId, createdBudget, parentBudgetId.Value, request);
      }

      _gamificationQueue.Enqueue(userId); // first-budget + budget achievements
      _metrics.RecordBudgetCreated(); // Business dashboard: budgets-created rate

      return await MapToBudgetDtoAsync(createdBudget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating budget for user {UserId}", userId);
      throw;
    }
  }

  public async Task<IEnumerable<BudgetDto>> GetMyFamilyBudgetsAsync(int userId)
  {
    try
    {
      // Look up user's family
      var user = await _userRepository.GetByIdAsync(userId);
      if (user?.FamilyId == null)
      {
        _logger.LogInformation("User {UserId} is not in a family, returning empty list", userId);
        return Enumerable.Empty<BudgetDto>();
      }

      return await GetFamilyBudgetsAsync(user.FamilyId.Value, userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting family budgets for user {UserId}", userId);
      throw;
    }
  }

  public async Task<IEnumerable<BudgetDto>> GetFamilyBudgetsAsync(int familyId, int userId)
  {
    try
    {
      // Verify user is in this family
      var user = await _userRepository.GetByIdAsync(userId);
      if (user?.FamilyId != familyId)
        throw new UnauthorizedAccessException("User is not a member of this family");

      var budgets = await _budgetRepository.GetByFamilyIdAsync(familyId);
      var budgetDtos = new List<BudgetDto>();

      // Share one transaction/user memo across every budget in this request. Family budgets each load
      // the WHOLE family's period transactions, so deduping identical (family, period) loads is the big
      // win here (was N+1, each an entire-family materialization).
      var transactionCache = new Dictionary<TransactionLoadKey, IReadOnlyList<Transaction>>();
      var userCache = new Dictionary<int, User?>();

      foreach (var budget in budgets)
      {
        // Only include budgets the user has access to
        if (await HasAccessToBudgetAsync(budget, userId))
        {
          // Advance through every elapsed period so each budget reflects the true current period.
          await ResetBudgetToCurrentAsync(budget);
          budgetDtos.Add(await MapToBudgetDtoAsync(budget, transactionCache, userCache));
        }
      }

      return budgetDtos;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting family budgets for family {FamilyId}", familyId);
      throw;
    }
  }

  public async Task<FamilyBudgetOverviewDto> GetFamilyBudgetOverviewAsync(int userId)
  {
    try
    {
      // Get the user and verify they're in a family
      var user = await _userRepository.GetByIdAsync(userId);
      if (user?.FamilyId == null)
        throw new InvalidOperationException("User is not a member of a family");

      // Get all family members
      var familyMembers = await _context.Users
        .Where(u => u.FamilyId == user.FamilyId)
        .ToListAsync();

      // Get the family info
      var family = await _context.Families.FindAsync(user.FamilyId);

      var memberSummaries = new List<FamilyMemberBudgetSummaryDto>();

      // Load each distinct (owner, family, period) transaction set once and compute spend in-memory,
      // instead of calling GetCategorySpendingAsync inside the nested member×budget loop — that reloaded
      // the WHOLE family's period transactions (5 Includes) once per budget, i.e. M*B redundant full
      // materializations of the same set for M members × B family budgets on a single page load.
      var transactionCache = new Dictionary<TransactionLoadKey, IReadOnlyList<Transaction>>();
      var userCache = new Dictionary<int, User?>();

      foreach (var member in familyMembers)
      {
        // Get this member's budgets that are shared with family (Type = Family)
        var memberBudgets = await _context.Budgets
          .Include(b => b.Categories)
            .ThenInclude(c => c.CategoryNav)
          .Include(b => b.User)
          .Where(b => b.UserId == member.Id &&
                     b.Type == BudgetType.Family)
          .ToListAsync();

        var budgetSummaries = new List<ChildBudgetSummaryDto>();
        decimal totalAllocated = 0;
        decimal totalSpent = 0;

        foreach (var budget in memberBudgets)
        {
          // "Allocated" and "spent" are EXPENSE metrics — exclude income categories so
          // these match the budget DTO's TotalExpenses (otherwise income received inflates
          // both, e.g. an $11k salary showing as $11k "spent").
          // GetCategorySpendingFromCacheAsync keys its result by BudgetCategory.Id (the row PK),
          // so filter by the row Ids of the expense categories.
          var expenseCategoryRowIds = budget.Categories
            .Where(c => !c.IsIncome)
            .Select(c => c.Id)
            .ToHashSet();
          var allocated = budget.Categories.Where(c => !c.IsIncome).Sum(c => c.AllocatedAmount);
          // Calculate actual spending from transactions (expense categories only), from the memoized
          // transaction set — a byte-for-byte in-memory equivalent of GetCategorySpendingAsync.
          var categorySpending = await GetCategorySpendingFromCacheAsync(budget, transactionCache, userCache);
          var spent = categorySpending.Where(kv => expenseCategoryRowIds.Contains(kv.Key)).Sum(kv => kv.Value);
          var remaining = allocated - spent;
          var percentUsed = allocated > 0 ? (double)(spent / allocated) * 100 : 0;

          budgetSummaries.Add(new ChildBudgetSummaryDto
          {
            Id = budget.Id,
            Name = budget.Name,
            AllocatedAmount = allocated,
            SpentAmount = spent,
            RemainingAmount = remaining,
            PercentUsed = percentUsed,
            RolloverMode = budget.RolloverMode,
            IsSharedWithFamily = budget.Type == BudgetType.Family // Derive from Type
          });

          totalAllocated += allocated;
          totalSpent += spent;
        }

        memberSummaries.Add(new FamilyMemberBudgetSummaryDto
        {
          UserId = member.Id,
          UserName = member.FirstName,
          Budgets = budgetSummaries,
          TotalAllocated = totalAllocated,
          TotalSpent = totalSpent,
          TotalRemaining = totalAllocated - totalSpent
        });
      }

      return new FamilyBudgetOverviewDto
      {
        FamilyId = user.FamilyId.Value,
        FamilyName = family?.Name ?? "My Family",
        Members = memberSummaries,
        TotalFamilyAllocated = memberSummaries.Sum(m => m.TotalAllocated),
        TotalFamilySpent = memberSummaries.Sum(m => m.TotalSpent)
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting family budget overview for user {UserId}", userId);
      throw;
    }
  }

  public async Task<BudgetDto> CreateFamilyBudgetAsync(int userId, int familyId, CreateBudgetRequest request)
  {
    try
    {
      // Guard category allocations (QE #615 Part E: no server-side bound) — mirror CreateBudgetAsync.
      foreach (var c in request.Categories ?? Enumerable.Empty<CreateBudgetCategoryRequest>())
        Core.Services.MonetaryLimits.ValidateNonNegative(c.AllocatedAmount, "Allocated amount");

      // Verify user is in this family
      var user = await _userRepository.GetByIdAsync(userId);
      if (user?.FamilyId != familyId)
        throw new UnauthorizedAccessException("User is not a member of this family");

      // Calculate current period dates
      var (periodStart, periodEnd) = _budgetPeriodService.CalculateCurrentPeriod(request.Period, utcOffsetMinutes: GetTimezoneOffsetMinutes(), weekStart: await WeekStartAsync(userId));

      var budget = new Budget
      {
        UserId = userId,
        FamilyId = familyId,
        Name = request.Name,
        Period = request.Period,
        CurrentPeriodStart = DateTime.SpecifyKind(periodStart, DateTimeKind.Utc),
        CurrentPeriodEnd = DateTime.SpecifyKind(periodEnd, DateTimeKind.Utc),
        LastReset = DateTime.UtcNow,
        AutoReset = request.AutoReset,
        TotalIncome = request.TotalIncome,
        TotalExpenses = request.Categories.Sum(c => c.AllocatedAmount),
        IsActive = true,
        Type = BudgetType.Family,
        IsPrimary = false,
        Categories = request.Categories.Select(c => new BudgetCategory
        {
          // Set unified CategoryId from request
          CategoryId = c.CategoryId,
          AllocatedAmount = c.AllocatedAmount,
          AllowRollover = c.AllowRollover,
          RolloverBalance = 0,
          IsAllowance = c.IsAllowance,
          AssignedUserId = c.AssignedUserId,
          SavingsGoalId = c.SavingsGoalId,
          RolloverOverflowSavingsGoalId = c.RolloverOverflowSavingsGoalId
        }).ToList()
      };

      var createdBudget = await _budgetRepository.CreateAsync(budget);
      _logger.LogInformation("Created family budget {BudgetId} for family {FamilyId} by user {UserId}", 
        createdBudget.Id, familyId, userId);
      
      return await MapToBudgetDtoAsync(createdBudget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating family budget for family {FamilyId}", familyId);
      throw;
    }
  }

  public async Task<BudgetDto> CreateAllowanceBudgetAsync(int userId, int parentBudgetId, int assignedUserId, CreateBudgetRequest request)
  {
    try
    {
      // Guard category allocations (QE #615 Part E: no server-side bound) — mirror CreateBudgetAsync.
      foreach (var c in request.Categories ?? Enumerable.Empty<CreateBudgetCategoryRequest>())
        Core.Services.MonetaryLimits.ValidateNonNegative(c.AllocatedAmount, "Allocated amount");

      // Verify parent budget exists and user has access
      var parentBudget = await _budgetRepository.GetByIdAsync(parentBudgetId);
      if (parentBudget == null)
        throw new ArgumentException("Parent budget not found");
      
      if (!await HasAccessToBudgetAsync(parentBudget, userId))
        throw new UnauthorizedAccessException("User does not have access to parent budget");

      // Verify assigned user is in the same family
      var assignedUser = await _userRepository.GetByIdAsync(assignedUserId);
      if (assignedUser == null)
        throw new ArgumentException("Assigned user not found");
      
      if (parentBudget.FamilyId != null && assignedUser.FamilyId != parentBudget.FamilyId)
        throw new ArgumentException("Assigned user is not in the same family");

      // Calculate current period dates
      var (periodStart, periodEnd) = _budgetPeriodService.CalculateCurrentPeriod(request.Period, utcOffsetMinutes: GetTimezoneOffsetMinutes(), weekStart: await WeekStartAsync(userId));

      var budget = new Budget
      {
        UserId = assignedUserId,
        FamilyId = parentBudget.FamilyId,
        Name = request.Name,
        Period = request.Period,
        CurrentPeriodStart = DateTime.SpecifyKind(periodStart, DateTimeKind.Utc),
        CurrentPeriodEnd = DateTime.SpecifyKind(periodEnd, DateTimeKind.Utc),
        LastReset = DateTime.UtcNow,
        AutoReset = request.AutoReset,
        TotalIncome = 0,
        TotalExpenses = request.Categories.Sum(c => c.AllocatedAmount),
        IsActive = true,
        Type = BudgetType.Allowance,
        ParentBudgetId = parentBudgetId,
        AllocatedAmount = request.AllocatedAmount ?? request.Categories.Sum(c => c.AllocatedAmount),
        IsPrimary = false,
        Categories = request.Categories.Select(c => new BudgetCategory
        {
          // Set unified CategoryId from request
          CategoryId = c.CategoryId,
          AllocatedAmount = c.AllocatedAmount,
          AllowRollover = c.AllowRollover,
          RolloverBalance = 0
        }).ToList()
      };

      var createdBudget = await _budgetRepository.CreateAsync(budget);
      
      // Create a linked category in the parent budget that references this allowance
      // Use CategoryId = 0 to represent 'Uncategorized' for allowance categories
      var allowanceCategory = new BudgetCategory
      {
        BudgetId = parentBudgetId,
        CategoryId = (int)TransactionCategory.Uncategorized, // Use Uncategorized for allowance categories
        AllocatedAmount = createdBudget.AllocatedAmount ?? 0,
        AllowRollover = false,
        RolloverBalance = 0,
        IsAllowance = true,
        LinkedBudgetId = createdBudget.Id,
        AssignedUserId = assignedUserId
      };
      
      await _budgetRepository.CreateBudgetCategoryAsync(allowanceCategory);
      
      _logger.LogInformation("Created allowance budget {BudgetId} for user {AssignedUserId} from parent {ParentBudgetId} with linked category", 
        createdBudget.Id, assignedUserId, parentBudgetId);
      
      return await MapToBudgetDtoAsync(createdBudget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating allowance budget for parent {ParentBudgetId}", parentBudgetId);
      throw;
    }
  }

  public async Task<BudgetDto> SpawnPersonalBudgetFromCategoryAsync(int userId, int categoryId, SpawnPersonalBudgetRequest request)
  {
    try
    {
      // Load the target family category (with its owning budget + navs).
      var category = await _budgetRepository.GetBudgetCategoryAsync(categoryId);
      if (category == null)
        throw new ArgumentException("Budget category not found");

      var parentBudget = category.Budget ?? await _budgetRepository.GetByIdAsync(category.BudgetId);
      if (parentBudget == null)
        throw new ArgumentException("Owning budget not found");

      // Must be a genuine family category to spawn from. 400 (not 404) — the caller referenced a
      // real category that just isn't eligible.
      if (parentBudget.Type != BudgetType.Family || parentBudget.FamilyId == null)
        throw new InvalidOperationException("Category is not a family budget category.");

      // B4 allocation gate (defense in depth; the controller also gates): spawning is an
      // allocation action, so Owner/BudgetAdmin only.
      var canAdmin = await _authService.CanCreateFamilyBudgetAsync(userId);
      if (!canAdmin.IsAuthorized)
        throw new UnauthorizedAccessException(canAdmin.Reason ?? "Only Owner and BudgetAdmin can spawn a personal budget from a family category.");

      // Must not already be a funding link (allowance / previously-spawned personal budget).
      if (category.IsAllowance || category.LinkedBudgetId != null)
        throw new InvalidOperationException("Category is already linked to a personal budget.");

      // The assigned member (defaults to the caller) must be in the same family.
      var assignedUserId = request.AssignedUserId ?? userId;
      var assignedUser = await _userRepository.GetByIdAsync(assignedUserId);
      if (assignedUser == null)
        throw new ArgumentException("Assigned user not found");
      if (assignedUser.FamilyId != parentBudget.FamilyId)
        throw new ArgumentException("Assigned user is not in the same family");

      // Default amount = the category's current allocation (RFC decision).
      var allocatedAmount = request.AllocatedAmount ?? category.AllocatedAmount;
      Core.Services.MonetaryLimits.ValidateNonNegative(allocatedAmount, "Allocated amount");

      // Default name = "{member first name}'s Budget" (RFC naming).
      var budgetName = string.IsNullOrWhiteSpace(request.BudgetName)
        ? $"{assignedUser.FirstName}'s Budget"
        : request.BudgetName!;

      // Create the Type=Personal child budget (inherits the parent's period). It starts with no
      // subcategories — the member re-subcategorizes their claimed charges later.
      var childBudget = new Budget
      {
        UserId = assignedUserId,
        FamilyId = parentBudget.FamilyId,
        Name = budgetName,
        Period = parentBudget.Period,
        CurrentPeriodStart = parentBudget.CurrentPeriodStart,
        CurrentPeriodEnd = parentBudget.CurrentPeriodEnd,
        LastReset = DateTime.UtcNow,
        AutoReset = true,
        TotalIncome = 0,
        TotalExpenses = 0,
        IsActive = true,
        Type = BudgetType.Personal,
        ParentBudgetId = parentBudget.Id,
        AllocatedAmount = allocatedAmount,
        IsPrimary = false,
        Categories = new List<BudgetCategory>()
      };

      var createdBudget = await _budgetRepository.CreateAsync(childBudget);

      // Re-purpose the EXISTING category as the funding link. Keep its real CategoryId so its
      // existing transactions stay attributed to the family budget (do NOT move it to Uncategorized).
      category.IsAllowance = true;
      category.LinkedBudgetId = createdBudget.Id;
      category.AssignedUserId = assignedUserId;
      category.AllocatedAmount = allocatedAmount;
      await _budgetRepository.UpdateBudgetCategoryAsync(category);

      _logger.LogInformation(
        "Spawned personal budget {BudgetId} for user {AssignedUserId} from family category {CategoryId} (parent {ParentBudgetId})",
        createdBudget.Id, assignedUserId, categoryId, parentBudget.Id);

      return await MapToBudgetDtoAsync(createdBudget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error spawning personal budget from category {CategoryId}", categoryId);
      throw;
    }
  }

  public async Task<BudgetDto> CreateChildBudgetAsync(int userId, int parentBudgetId, CreateChildBudgetRequest request)
  {
    try
    {
      // Guard allocations (QE #615 Part E: no server-side bound). FundingAmount drives both
      // AllocatedAmount and TotalExpenses, so bound it too.
      Core.Services.MonetaryLimits.ValidateNonNegative(request.FundingAmount, "Funding amount");
      foreach (var c in request.Categories ?? Enumerable.Empty<CreateBudgetCategoryRequest>())
        Core.Services.MonetaryLimits.ValidateNonNegative(c.AllocatedAmount, "Allocated amount");

      // Verify parent budget exists and user has access
      var parentBudget = await _budgetRepository.GetByIdAsync(parentBudgetId);
      if (parentBudget == null)
        throw new ArgumentException("Parent budget not found");
      
      if (!await HasAccessToBudgetAsync(parentBudget, userId))
        throw new UnauthorizedAccessException("User does not have access to parent budget");

      // Validate savings goal if specified
      if (request.DefaultSavingsGoalId.HasValue)
      {
        var savingsGoal = await _context.SavingsGoals.FindAsync(request.DefaultSavingsGoalId.Value);
        if (savingsGoal == null)
          throw new ArgumentException("Savings goal not found");
        if (savingsGoal.UserId != userId)
          throw new UnauthorizedAccessException("Savings goal does not belong to user");
      }

      // Calculate current period dates (inherit from parent)
      var (periodStart, periodEnd) = (parentBudget.CurrentPeriodStart, parentBudget.CurrentPeriodEnd);

      // Determine budget type based on sharing preference
      // If shared with family, it becomes a Family type budget
      var budgetType = request.IsSharedWithFamily ? BudgetType.Family : BudgetType.Personal;
      var familyId = request.IsSharedWithFamily ? parentBudget.FamilyId : null;

      var budget = new Budget
      {
        UserId = userId,
        FamilyId = familyId,
        Name = request.Name,
        Period = parentBudget.Period, // Inherit period from parent
        CurrentPeriodStart = periodStart,
        CurrentPeriodEnd = periodEnd,
        LastReset = DateTime.UtcNow,
        AutoReset = true, // Child budgets auto-reset with parent
        TotalIncome = 0,
        TotalExpenses = request.FundingAmount,
        IsActive = true,
        Type = budgetType,
        ParentBudgetId = parentBudgetId,
        AllocatedAmount = request.FundingAmount,
        IsPrimary = false,
        RolloverMode = request.RolloverMode,
        DefaultSavingsGoalId = request.DefaultSavingsGoalId,
        Categories = request.Categories.Select(c => new BudgetCategory
        {
          // Set unified CategoryId from request
          CategoryId = c.CategoryId,
          AllocatedAmount = c.AllocatedAmount,
          AllowRollover = c.AllowRollover,
          RolloverBalance = 0
        }).ToList()
      };

      var createdBudget = await _budgetRepository.CreateAsync(budget);
      
      _logger.LogInformation("Created child budget {BudgetId} for user {UserId} from parent {ParentBudgetId} with rollover mode {RolloverMode}", 
        createdBudget.Id, userId, parentBudgetId, request.RolloverMode);
      
      return await MapToBudgetDtoAsync(createdBudget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error creating child budget for parent {ParentBudgetId}", parentBudgetId);
      throw;
    }
  }

  public async Task<BudgetDto> UpdateChildBudgetAsync(int budgetId, int userId, UpdateChildBudgetRequest request)
  {
    try
    {
      var budget = await _budgetRepository.GetByIdAsync(budgetId);
      if (budget == null)
        throw new ArgumentException("Budget not found");

      // Role-aware gate: mutating a shared family budget's settings (name, funding, rollover, or
      // un-sharing it via IsSharedWithFamily=false) is an Edit. HasAccessToBudgetAsync alone grants
      // every family member access with no role check, letting a ReadOnly/Member orphan or rewrite a
      // shared family budget. CanAccessBudgetAsync enforces ReadOnly->deny and family-budget Member->deny.
      await _authService.ValidateBudgetAccessOrThrowAsync(userId, budgetId, ResourcePermission.Edit);

      // Validate savings goal if specified
      if (request.DefaultSavingsGoalId.HasValue)
      {
        var savingsGoal = await _context.SavingsGoals.FindAsync(request.DefaultSavingsGoalId.Value);
        if (savingsGoal == null)
          throw new ArgumentException("Savings goal not found");
        if (savingsGoal.UserId != userId)
          throw new UnauthorizedAccessException("Savings goal does not belong to user");
      }

      // RFC #793 B3/B4: if this child is funded by a linked family category, its allocation is the
      // family category's amount and only Owner/BudgetAdmin may change it. The assigned member
      // (the child's owner) can still edit everything else about their own budget, but changing the
      // allocation from here is an admin action and is bound back onto the family category.
      var linkedFundingCategory = await _budgetRepository.GetLinkedCategoryForChildAsync(budgetId);

      // Apply updates
      if (request.Name != null)
        budget.Name = request.Name;

      if (request.FundingAmount.HasValue)
      {
        if (linkedFundingCategory != null)
        {
          var canAdmin = await _authService.CanCreateFamilyBudgetAsync(userId);
          if (!canAdmin.IsAuthorized)
            throw new UnauthorizedAccessException("Only Owner and BudgetAdmin can change a family-funded budget's allocation.");
        }

        budget.AllocatedAmount = request.FundingAmount.Value;
        budget.TotalExpenses = request.FundingAmount.Value;

        // Bind the allocation back onto the family funding category (source of truth stays equal).
        // One-way write (child → category) from a distinct endpoint, so it cannot loop.
        if (linkedFundingCategory != null && linkedFundingCategory.AllocatedAmount != request.FundingAmount.Value)
        {
          linkedFundingCategory.AllocatedAmount = request.FundingAmount.Value;
          await _budgetRepository.UpdateBudgetCategoryAsync(linkedFundingCategory);
        }
      }
        
      if (request.IsSharedWithFamily.HasValue)
      {
        // Update Type based on sharing preference
        budget.Type = request.IsSharedWithFamily.Value ? BudgetType.Family : BudgetType.Personal;
        
        // If sharing with family, set family ID from parent budget
        if (request.IsSharedWithFamily.Value && budget.ParentBudget != null)
          budget.FamilyId = budget.ParentBudget.FamilyId;
        else if (!request.IsSharedWithFamily.Value)
          budget.FamilyId = null;
      }
        
      if (request.RolloverMode.HasValue)
        budget.RolloverMode = request.RolloverMode.Value;
        
      if (request.DefaultSavingsGoalId.HasValue || (request.RolloverMode.HasValue && request.RolloverMode != ChildBudgetRolloverMode.AllocateToSavings))
      {
        // If changing to non-savings rollover mode, clear the savings goal
        if (request.RolloverMode.HasValue && request.RolloverMode != ChildBudgetRolloverMode.AllocateToSavings)
          budget.DefaultSavingsGoalId = null;
        else
          budget.DefaultSavingsGoalId = request.DefaultSavingsGoalId;
      }

      budget.UpdatedAt = DateTime.UtcNow;
      
      await _budgetRepository.UpdateAsync(budget);
      
      _logger.LogInformation("Updated child budget {BudgetId} settings", budgetId);
      
      return await MapToBudgetDtoAsync(budget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating child budget {BudgetId}", budgetId);
      throw;
    }
  }

  public async Task<BudgetShareDto> ShareBudgetAsync(int budgetId, int userId, int shareWithUserId, bool canEdit, bool canViewTransactions)
  {
    try
    {
      var budget = await _budgetRepository.GetByIdAsync(budgetId);
      if (budget == null)
        throw new ArgumentException("Budget not found");
      
      // Only owner can share
      if (budget.UserId != userId)
        throw new UnauthorizedAccessException("Only the budget owner can share it");

      // Check if already shared
      var existingShare = await _budgetRepository.GetBudgetShareAsync(budgetId, shareWithUserId);
      if (existingShare != null)
        throw new InvalidOperationException("Budget is already shared with this user");

      var share = new BudgetShare
      {
        BudgetId = budgetId,
        SharedWithUserId = shareWithUserId,
        CanEdit = canEdit,
        CanViewTransactions = canViewTransactions
      };

      var createdShare = await _budgetRepository.CreateBudgetShareAsync(share);
      
      return new BudgetShareDto
      {
        Id = createdShare.Id,
        BudgetId = createdShare.BudgetId,
        SharedWithUserId = createdShare.SharedWithUserId,
        SharedWithUserName = createdShare.SharedWithUser?.FirstName ?? "Unknown",
        CanEdit = createdShare.CanEdit,
        CanViewTransactions = createdShare.CanViewTransactions
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error sharing budget {BudgetId} with user {ShareWithUserId}", budgetId, shareWithUserId);
      throw;
    }
  }

  public async Task RemoveBudgetShareAsync(int budgetId, int userId, int shareWithUserId)
  {
    try
    {
      var budget = await _budgetRepository.GetByIdAsync(budgetId);
      if (budget == null)
        throw new ArgumentException("Budget not found");
      
      // Only owner can remove share
      if (budget.UserId != userId)
        throw new UnauthorizedAccessException("Only the budget owner can remove shares");

      await _budgetRepository.DeleteBudgetShareAsync(budgetId, shareWithUserId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error removing budget share for budget {BudgetId}", budgetId);
      throw;
    }
  }

  public async Task<IEnumerable<BudgetShareDto>> GetBudgetSharesAsync(int budgetId, int userId)
  {
    try
    {
      var budget = await _budgetRepository.GetByIdAsync(budgetId);
      if (budget == null)
        throw new ArgumentException("Budget not found");
      
      // Only owner can view shares
      if (budget.UserId != userId)
        throw new UnauthorizedAccessException("Only the budget owner can view shares");

      var shares = await _budgetRepository.GetBudgetSharesAsync(budgetId);
      
      return shares.Select(s => new BudgetShareDto
      {
        Id = s.Id,
        BudgetId = s.BudgetId,
        SharedWithUserId = s.SharedWithUserId,
        SharedWithUserName = s.SharedWithUser?.FirstName ?? "Unknown",
        CanEdit = s.CanEdit,
        CanViewTransactions = s.CanViewTransactions
      });
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting budget shares for budget {BudgetId}", budgetId);
      throw;
    }
  }

  public async Task ClaimTransactionAsync(int transactionId, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");

      // Claiming re-attributes a transaction to the caller's budget — a write. The ad-hoc
      // account/family check below permitted ANY same-family member (including ReadOnly) to mutate
      // attribution. Route through the role-aware service, which denies ReadOnly members and any
      // user outside the transaction's family.
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);

      transaction.ClaimedByUserId = userId;
      await _transactionRepository.UpdateAsync(transaction);
      
      _logger.LogInformation("User {UserId} claimed transaction {TransactionId}", userId, transactionId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error claiming transaction {TransactionId} for user {UserId}", transactionId, userId);
      throw;
    }
  }

  public async Task UnclaimTransactionAsync(int transactionId, int userId)
  {
    try
    {
      var transaction = await _transactionRepository.GetByIdAsync(transactionId);
      if (transaction == null)
        throw new ArgumentException("Transaction not found");

      // Unclaiming is a write; deny ReadOnly members and non-family users up front (mirrors Claim).
      await _authService.ValidateTransactionAccessOrThrowAsync(userId, transactionId, ResourcePermission.Edit);

      // Only the user who claimed it can unclaim
      if (transaction.ClaimedByUserId != userId)
        throw new UnauthorizedAccessException("User did not claim this transaction");

      transaction.ClaimedByUserId = null;
      await _transactionRepository.UpdateAsync(transaction);
      
      _logger.LogInformation("User {UserId} unclaimed transaction {TransactionId}", userId, transactionId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error unclaiming transaction {TransactionId} for user {UserId}", transactionId, userId);
      throw;
    }
  }

  public async Task<BudgetDto> UpdateBudgetAsync(int budgetId, int userId, CreateBudgetRequest request)
  {
    try
    {
      // Collapse duplicate categories up front (same guard as CreateBudgetAsync) so an update
      // can't reintroduce duplicate-category rows or trip the DB unique index.
      request.Categories = DedupeCategories(request.Categories);

      // Guard category allocations (QE #615 Part E). CreateBudgetAsync enforces this but the edit path
      // did not, so a negative allocation understated TotalExpenses / corrupted remaining-deficit math,
      // and a value near decimal.MaxValue made the TotalExpenses Sum overflow into an unhandled 500.
      foreach (var c in request.Categories ?? Enumerable.Empty<CreateBudgetCategoryRequest>())
        Core.Services.MonetaryLimits.ValidateNonNegative(c.AllocatedAmount, "Allocated amount");

      var budget = await _budgetRepository.GetByIdAsync(budgetId);
      if (budget == null)
        throw new ArgumentException("Budget not found");

      // Verify the user has access to edit this budget
      if (budget.UserId != userId)
      {
        // Check if user has edit access via sharing
        var share = await _budgetRepository.GetBudgetShareAsync(budgetId, userId);
        if (share == null || !share.CanEdit)
          throw new UnauthorizedAccessException("User does not have permission to edit this budget");
      }

      // Validate assigned users for allowance categories
      var categoriesWithAssignedUsers = request.Categories.Where(c => c.AssignedUserId.HasValue).ToList();
      if (categoriesWithAssignedUsers.Any())
      {
        var requestingUser = await _userRepository.GetByIdAsync(userId);
        if (requestingUser?.FamilyId == null)
          throw new InvalidOperationException("Cannot assign users to categories without being a member of a family.");
        
        foreach (var cat in categoriesWithAssignedUsers)
        {
          var assignedUser = await _userRepository.GetByIdAsync(cat.AssignedUserId!.Value);
          if (assignedUser == null)
            throw new ArgumentException($"Assigned user {cat.AssignedUserId} not found");
          if (assignedUser.FamilyId != requestingUser.FamilyId)
            throw new ArgumentException($"Assigned user {cat.AssignedUserId} is not in the same family");
        }
      }

      // Handle type change (Personal <-> Family)
      if (request.Type != budget.Type)
      {
        if (request.Type == BudgetType.Family)
        {
          // Changing to Family: user must be in a family
          var user = await _userRepository.GetByIdAsync(userId);
          if (user?.FamilyId == null)
            throw new InvalidOperationException("Cannot change to a family budget without being a member of a family.");
          
          budget.FamilyId = user.FamilyId;
        }
        else if (request.Type == BudgetType.Personal)
        {
          // Changing to Personal: remove family association
          budget.FamilyId = null;
        }
        
        budget.Type = request.Type;
        _logger.LogInformation("Budget {BudgetId} type changed from {OldType} to {NewType}", 
          budgetId, budget.Type, request.Type);
      }

      budget.Name = request.Name;
      budget.Period = request.Period;
      budget.AutoReset = request.AutoReset;
      budget.TotalIncome = request.TotalIncome;
      budget.TotalExpenses = request.Categories.Sum(c => c.AllocatedAmount);

      // Recalculate period dates if period type changed
      var (periodStart, periodEnd) = _budgetPeriodService.CalculateCurrentPeriod(request.Period, utcOffsetMinutes: GetTimezoneOffsetMinutes(), weekStart: await WeekStartAsync(userId));
      budget.CurrentPeriodStart = DateTime.SpecifyKind(periodStart, DateTimeKind.Utc);
      budget.CurrentPeriodEnd = DateTime.SpecifyKind(periodEnd, DateTimeKind.Utc);

      // Update categories - preserve spent amounts where possible
      // Key by unified CategoryId to handle both system and custom categories
      // GroupBy (not ToDictionary) so any pre-existing duplicate CategoryId rows — which the DB
      // now prevents, but historical data may still carry — merge to the first row instead of
      // throwing "An item with the same key has already been added" (previously an unhandled 500
      // that left the budget uneditable).
      var existingCategories = budget.Categories
        .Where(c => c.CategoryId.HasValue)
        .GroupBy(c => c.CategoryId!.Value)
        .ToDictionary(
          g => g.Key,
          g => g.First());
        
      // DEBUG: Log incoming categories from request
      _logger.LogInformation("📝 UpdateBudgetAsync: Received {Count} categories in request", request.Categories.Count);
      foreach (var cat in request.Categories.Where(c => c.CategoryId >= 1000 || c.IsAllowance || c.AssignedUserId.HasValue))
      {
        _logger.LogInformation("📝   CategoryId={CategoryId}, IsAllowance={IsAllowance}, AssignedUserId={AssignedUserId}",
          cat.CategoryId, cat.IsAllowance, cat.AssignedUserId);
      }
      
      budget.Categories.Clear();
      budget.Categories = request.Categories.Select(c => {
        // Note: SpentAmount is no longer stored - it's calculated from transactions when mapping to DTO
        var rolloverBalance = existingCategories.TryGetValue(c.CategoryId, out var existingRollover) ? existingRollover.RolloverBalance : 0;
        var defaultAmount = existingCategories.TryGetValue(c.CategoryId, out var existingDefault) ? existingDefault.DefaultAllocatedAmount : null;
        
        return new BudgetCategory
        {
          BudgetId = budgetId,
          // Set unified CategoryId from request
          CategoryId = c.CategoryId,
          AllocatedAmount = c.AllocatedAmount,
          AllowRollover = c.AllowRollover,
          RolloverBalance = rolloverBalance,
          IsCountUpEnabled = c.IsCountUpEnabled,
          IsAllowance = c.IsAllowance,
          AssignedUserId = c.AssignedUserId,
          IsIncome = c.IsIncome,
          SavingsGoalId = c.SavingsGoalId,
          RolloverOverflowSavingsGoalId = c.RolloverOverflowSavingsGoalId,
          DefaultAllocatedAmount = defaultAmount
        };
      }).ToList();

      var updatedBudget = await _budgetRepository.UpdateAsync(budget);
      return await MapToBudgetDtoAsync(updatedBudget);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating budget {BudgetId}", budgetId);
      throw;
    }
  }

  /// <summary>
  /// Get a budget category by ID for authorization checks.
  /// WARNING: Returns SpentAmount = 0 always - this method is intended for authorization
  /// checks only where spending data is not needed. Do not use this method if you need
  /// accurate spending calculations.
  /// </summary>
  public async Task<BudgetCategoryDto?> GetBudgetCategoryAsync(int categoryId)
  {
    try
    {
      var category = await _budgetRepository.GetBudgetCategoryAsync(categoryId);
      if (category == null)
        return null;

      return new BudgetCategoryDto
      {
        Id = category.Id,
        UpdatedAt = category.UpdatedAt,
        BudgetId = category.BudgetId,
        // Use unified CategoryId from entity - default to 0 if null
        CategoryId = category.CategoryId ?? 0,
        // Derive category name from navigation property
        CategoryName = category.CategoryNav?.Name ?? "Unknown",
        AllocatedAmount = category.AllocatedAmount,
        SpentAmount = 0, // Intentionally not calculated - for authorization checks only
        AllowRollover = category.AllowRollover,
        RolloverBalance = category.RolloverBalance,
        MaxRolloverMultiplier = category.MaxRolloverMultiplier,
        RolloverOverflowSavingsGoalId = category.RolloverOverflowSavingsGoalId,
        IsCountUpEnabled = category.IsCountUpEnabled,
        IsIncome = category.IsIncome,
        DefaultAllocatedAmount = category.DefaultAllocatedAmount
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting budget category {CategoryId}", categoryId);
      throw;
    }
  }

  public async Task<BudgetCategoryDto> UpdateBudgetCategoryAsync(int categoryId, UpdateBudgetCategoryRequest request)
  {
    try
    {
      var budgetCategory = await _budgetRepository.GetBudgetCategoryAsync(categoryId);
      if (budgetCategory == null)
        throw new ArgumentException("Budget category not found");

      // Guard the allocation (QE #615 Part E: no server-side bound). Zero is a
      // valid $0 category; negatives and absurd amounts are not.
      Core.Services.MonetaryLimits.ValidateNonNegative(request.AllocatedAmount, "Allocated amount");

      // Update the category properties
      budgetCategory.AllocatedAmount = request.AllocatedAmount;
      budgetCategory.AllowRollover = request.AllowRollover;
      
      // Update rollover max settings
      budgetCategory.MaxRolloverMultiplier = request.MaxRolloverMultiplier;
      budgetCategory.RolloverOverflowSavingsGoalId = request.RolloverOverflowSavingsGoalId;
      
      // Update Count-Up rollover mode and category-level savings goal
      budgetCategory.CountUpRolloverMode = request.CountUpRolloverMode;
      budgetCategory.SavingsGoalId = request.SavingsGoalId;
      
      // If ApplyToFuturePeriods is true, save this as the default for new periods
      if (request.ApplyToFuturePeriods)
      {
        budgetCategory.DefaultAllocatedAmount = request.AllocatedAmount;
      }
      
      // Update the category in the repository
      var updatedCategory = await _budgetRepository.UpdateBudgetCategoryAsync(budgetCategory);
      
      // Also update the budget's total expenses to reflect the change
      var budget = await _budgetRepository.GetByIdAsync(updatedCategory.BudgetId);
      if (budget != null)
      {
        budget.TotalExpenses = budget.Categories.Where(c => !c.IsIncome).Sum(c => c.AllocatedAmount);
        await _budgetRepository.UpdateAsync(budget);
      }

      // RFC #793 B3 — amount binding: the family funding category is the source of truth for a
      // linked child's allocation. Propagate the new amount to the child's AllocatedAmount so the
      // two stay equal. This is a one-way write (category → child) invoked from a distinct endpoint
      // than the child-settings path, so it cannot loop.
      if (updatedCategory.IsAllowance && updatedCategory.LinkedBudgetId != null)
      {
        var linkedBudget = await _budgetRepository.GetByIdAsync(updatedCategory.LinkedBudgetId.Value);
        if (linkedBudget != null && linkedBudget.AllocatedAmount != updatedCategory.AllocatedAmount)
        {
          linkedBudget.AllocatedAmount = updatedCategory.AllocatedAmount;
          await _budgetRepository.UpdateAsync(linkedBudget);
        }
      }

      // Get spending/income data for this category
      var transactions = await _transactionRepository.GetByUserIdAsync(
        budget?.UserId ?? 0, budget?.CurrentPeriodStart ?? DateTime.MinValue, budget?.CurrentPeriodEndInclusive ?? DateTime.MaxValue);

      // Use extension methods for consistent income detection
      var isIncome = updatedCategory.IsIncome || (updatedCategory.CategoryId >= 1 && updatedCategory.CategoryId <= 4);
      
      // Use unified CategoryId from entity (default to 0/Uncategorized if null)
      var computedCategoryId = updatedCategory.CategoryId ?? 0;
      var catKey = new TransactionAmountExtensions.CategoryKey(computedCategoryId);
      
      // Get spent amount using CategoryKey-based extension methods
      var categorySpending = transactions.ExpensesByCategoryKey();
      var categoryIncome = transactions.IncomeByCategoryKey();
      var spentAmount = isIncome 
        ? categoryIncome.GetValueOrDefault(catKey, 0)
        : categorySpending.GetValueOrDefault(catKey, 0);

      return new BudgetCategoryDto
      {
        Id = updatedCategory.Id,
        UpdatedAt = updatedCategory.UpdatedAt,
        CategoryId = computedCategoryId,
        // Derive category name from navigation property
        CategoryName = updatedCategory.CategoryNav?.Name ?? "Unknown",
        AllocatedAmount = updatedCategory.AllocatedAmount,
        SpentAmount = spentAmount,
        AllowRollover = updatedCategory.AllowRollover,
        RolloverBalance = updatedCategory.RolloverBalance,
        MaxRolloverMultiplier = updatedCategory.MaxRolloverMultiplier,
        RolloverOverflowSavingsGoalId = updatedCategory.RolloverOverflowSavingsGoalId,
        IsCountUpEnabled = updatedCategory.IsCountUpEnabled,
        CountUpRolloverMode = updatedCategory.CountUpRolloverMode,
        SavingsGoalId = updatedCategory.SavingsGoalId,
        IsIncome = isIncome,
        DefaultAllocatedAmount = updatedCategory.DefaultAllocatedAmount
      };
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating budget category {CategoryId}", categoryId);
      throw;
    }
  }

  public async Task DeleteBudgetAsync(int budgetId, int userId)
  {
    try
    {
      var budget = await _budgetRepository.GetByIdAsync(budgetId);
      if (budget == null)
        throw new ArgumentException("Budget not found");
      
      // Only owner can delete
      if (budget.UserId != userId)
        throw new UnauthorizedAccessException("Only the budget owner can delete it");
      
      // Cannot delete primary budget
      if (budget.IsPrimary)
        throw new InvalidOperationException("Cannot delete primary budget. Set another budget as primary first.");
      
      await _budgetRepository.DeleteAsync(budgetId);
      _logger.LogInformation("User {UserId} deleted budget {BudgetId}", userId, budgetId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error deleting budget {BudgetId}", budgetId);
      throw;
    }
  }

  /// <summary>
  /// Simplified budget access control using Type + FamilyId as source of truth.
  /// Access rules:
  /// - Owner always has access
  /// - Type = Family + matching FamilyId → all family members have access
  /// - Type = Allowance → assigned user has access (checked via budget owner)
  /// - BudgetShare table → explicit custom sharing
  /// </summary>
  /// <summary>
  /// Links a family funding category to a newly-created Personal child budget (RFC #793 B2).
  /// Either links an existing family category (FundFromCategoryId) or creates a new one
  /// (NewFamilyCategoryName). Setting the allocation this way is an admin action, so it requires
  /// Owner/BudgetAdmin — a plain member cannot fund a personal budget from the family pool.
  /// </summary>
  private async Task LinkFamilyFundingCategoryAsync(int userId, Budget childBudget, int parentBudgetId, CreateBudgetRequest request)
  {
    // Allocation gate: creating/linking a family funding category is Owner/BudgetAdmin only.
    var canAdmin = await _authService.CanCreateFamilyBudgetAsync(userId);
    if (!canAdmin.IsAuthorized)
      throw new UnauthorizedAccessException(canAdmin.Reason ?? "Only Owner and BudgetAdmin can fund a personal budget from the family budget.");

    var parentBudget = await _budgetRepository.GetByIdAsync(parentBudgetId);
    if (parentBudget == null || parentBudget.Type != BudgetType.Family || parentBudget.FamilyId == null)
      throw new InvalidOperationException("Fund-from requires an active family budget as the parent.");

    if (request.FundFromCategoryId.HasValue)
    {
      // Link an EXISTING family category (keep its real CategoryId + transactions).
      var category = await _budgetRepository.GetBudgetCategoryAsync(request.FundFromCategoryId.Value);
      if (category == null)
        throw new ArgumentException("Fund-from category not found");
      if (category.BudgetId != parentBudget.Id)
        throw new InvalidOperationException("Fund-from category does not belong to the family budget.");
      if (category.IsAllowance || category.LinkedBudgetId != null)
        throw new InvalidOperationException("Fund-from category is already linked to a personal budget.");

      var amount = request.AllocatedAmount ?? category.AllocatedAmount;
      Core.Services.MonetaryLimits.ValidateNonNegative(amount, "Allocated amount");

      category.IsAllowance = true;
      category.LinkedBudgetId = childBudget.Id;
      category.AssignedUserId = childBudget.UserId;
      category.AllocatedAmount = amount;
      await _budgetRepository.UpdateBudgetCategoryAsync(category);

      childBudget.AllocatedAmount = amount;
      await _budgetRepository.UpdateAsync(childBudget);
    }
    else
    {
      // Create a NEW family funding category. BudgetCategory carries no name of its own, so the
      // provided name is stored on the child budget (fallback per the contract); the category
      // itself parks on Uncategorized like today's allowance path.
      var amount = request.AllocatedAmount ?? 0m;
      Core.Services.MonetaryLimits.ValidateNonNegative(amount, "Allocated amount");

      var fundingCategory = new BudgetCategory
      {
        BudgetId = parentBudget.Id,
        CategoryId = (int)TransactionCategory.Uncategorized,
        AllocatedAmount = amount,
        AllowRollover = false,
        RolloverBalance = 0,
        IsAllowance = true,
        LinkedBudgetId = childBudget.Id,
        AssignedUserId = childBudget.UserId
      };
      await _budgetRepository.CreateBudgetCategoryAsync(fundingCategory);

      childBudget.AllocatedAmount = amount;
      if (string.IsNullOrWhiteSpace(childBudget.Name))
        childBudget.Name = request.NewFamilyCategoryName!;
      await _budgetRepository.UpdateAsync(childBudget);
    }

    _logger.LogInformation("Linked family funding category to personal budget {BudgetId} (parent {ParentBudgetId})",
      childBudget.Id, parentBudgetId);
  }

  /// <summary>
  /// True when this is a Personal child budget funded by a linked family "allowance" category
  /// (RFC #793). Such children behave like Type=Allowance for personal-category bucketing and
  /// additive member income, while remaining a normal Personal budget everywhere else.
  /// </summary>
  private async Task<bool> IsLinkedPersonalChildAsync(Budget budget)
  {
    if (budget.Type != BudgetType.Personal || budget.ParentBudgetId == null)
      return false;
    var linked = await _budgetRepository.GetLinkedCategoryForChildAsync(budget.Id);
    return linked != null;
  }

  private async Task<bool> HasAccessToBudgetAsync(Budget budget, int userId)
  {
    // Owner always has access
    if (budget.UserId == userId)
      return true;

    // Custom sharing via BudgetShare table
    var share = await _budgetRepository.GetBudgetShareAsync(budget.Id, userId);
    if (share != null)
      return true;

    // Family budgets: all family members have access
    if (budget.Type == BudgetType.Family && budget.FamilyId != null)
    {
      var user = await _userRepository.GetByIdAsync(userId);
      if (user?.FamilyId == budget.FamilyId)
        return true;
    }

    // Child/Allowance budgets with family parent: family members have access
    if (budget.ParentBudgetId != null && budget.FamilyId != null)
    {
      var user = await _userRepository.GetByIdAsync(userId);
      if (user?.FamilyId == budget.FamilyId)
        return true;
    }

    return false;
  }

  // Key identifying an identical period-transaction load. Budgets that share the same owner, family
  // scope and period window resolve to the exact same DB result, so a single budget-list request can
  // reuse one materialized set instead of re-querying (with 5 Includes) once per budget.
  private readonly record struct TransactionLoadKey(bool IsFamily, int UserId, int? FamilyId, DateTime PeriodStart, DateTime PeriodEnd);

  /// <summary>
  /// Computes per-category-row spending for a budget (categoryRowId → spend) from a period-transaction
  /// load that is memoized per owner+family+period across a single request. This is a byte-for-byte
  /// in-memory equivalent of <see cref="IBudgetSpendingService.GetCategorySpendingAsync"/> (same
  /// expense/income keying, allowance branch, and ExcludePending handling), but it reuses one
  /// materialized transaction set across every budget in the same period instead of reloading the whole
  /// family's transactions once per budget.
  /// </summary>
  private async Task<Dictionary<int, decimal>> GetCategorySpendingFromCacheAsync(
    Budget budget,
    Dictionary<TransactionLoadKey, IReadOnlyList<Transaction>> transactionCache,
    Dictionary<int, User?> userCache)
  {
    // Range end is CurrentPeriodEndInclusive so last-calendar-day transactions are not dropped (#702),
    // matching the single-budget load path in MapToBudgetDtoAsync.
    var loadKey = new TransactionLoadKey(
      budget.FamilyId.HasValue, budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive);

    if (!transactionCache.TryGetValue(loadKey, out var loaded))
    {
      loaded = budget.FamilyId.HasValue
        ? (await _transactionRepository.GetByUserOrFamilyAsync(
            budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive)).ToList()
        : (await _transactionRepository.GetByUserIdAsync(
            budget.UserId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive)).ToList();
      transactionCache[loadKey] = loaded;
    }
    IEnumerable<Transaction> transactions = loaded;

    if (!userCache.TryGetValue(budget.UserId, out var owner))
    {
      owner = await _userRepository.GetByIdAsync(budget.UserId);
      userCache[budget.UserId] = owner;
    }
    if (owner?.ExcludePendingFromBudget == true)
      transactions = transactions.Where(t => !t.IsPending);

    var expenseByKey = budget.Type == BudgetType.Allowance
      ? transactions.ExpensesByPersonalCategoryKey()
      : transactions.ExpensesByCategoryKey();
    var incomeByKey = transactions.IncomeByCategoryKey();

    var result = new Dictionary<int, decimal>();
    foreach (var category in budget.Categories)
    {
      var categoryId = category.GetEffectiveCategoryId();
      var key = new TransactionAmountExtensions.CategoryKey(categoryId);
      var isIncome = category.IsIncome || (categoryId >= 1 && categoryId <= 4);
      result[category.Id] = isIncome
        ? incomeByKey.GetValueOrDefault(key, 0)
        : expenseByKey.GetValueOrDefault(key, 0);
    }
    return result;
  }

  private Task<BudgetDto> MapToBudgetDtoAsync(Budget budget)
    => MapToBudgetDtoAsync(budget, transactionCache: null, userCache: null);

  /// <param name="transactionCache">Optional per-request memo of period-transaction loads, shared across
  /// a budget-list map so N budgets in the same period trigger one query, not N. Null = load directly.</param>
  /// <param name="userCache">Optional per-request memo of user lookups (for the ExcludePending flag),
  /// deduped across budgets owned by the same user. Null = fetch directly.</param>
  private async Task<BudgetDto> MapToBudgetDtoAsync(
    Budget budget,
    Dictionary<TransactionLoadKey, IReadOnlyList<Transaction>>? transactionCache,
    Dictionary<int, User?>? userCache)
  {
    // Calculate spent amounts for each category
    // For family budgets, pull transactions from all family members' accounts
    // Perf: cache the per-(user/family, period) transaction set so a page of N budgets does not do
    // N full loads (#705). Correctness: the range end is CurrentPeriodEndInclusive so transactions on
    // the last calendar day of the period are not dropped (#702).
    var loadKey = new TransactionLoadKey(
      budget.FamilyId.HasValue, budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive);

    IReadOnlyList<Transaction> loaded;
    if (transactionCache == null || !transactionCache.TryGetValue(loadKey, out loaded!))
    {
      loaded = budget.FamilyId.HasValue
        ? (await _transactionRepository.GetByUserOrFamilyAsync(
            budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive)).ToList()
        : (await _transactionRepository.GetByUserIdAsync(
            budget.UserId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive)).ToList();
      transactionCache?.Add(loadKey, loaded);
    }
    // The subsequent per-budget filtering below only ever does `.Where(...).ToList()` into new lists and
    // never mutates the transaction entities, so sharing the cached set read-only across budgets is safe.
    IEnumerable<Transaction> transactions = loaded;

    // Check if user wants to exclude pending transactions from budget calculations
    User? user;
    if (userCache == null || !userCache.TryGetValue(budget.UserId, out user))
    {
      user = await _userRepository.GetByIdAsync(budget.UserId);
      if (userCache != null)
        userCache[budget.UserId] = user;
    }
    // Attribute the pool to this budget (the claim rule, and the owner's pending-charge setting):
    // shared with the count-up card, the daily allowance and the month close so they all agree.
    // ClaimedByUserId is seeded from the account's AssociatedUserId at ingest, so this is account
    // default + per-transaction claim, without one card's spend flooding everyone's budget.
    if (user?.ExcludePendingFromBudget == true)
      _logger.LogDebug("Excluding pending transactions from budget calculation for user {UserId}", budget.UserId);
    transactions = transactions.ScopeToBudget(budget, user?.ExcludePendingFromBudget == true).ToList();

    // DIAGNOSTIC LOGGING: Track budget calculation sources. Debug-level: MapToBudgetDtoAsync runs once
    // per budget on the budget-list hot path, so at Information these fired 40+ times per page load in
    // production (log-ingestion cost + noise). Gated to Debug so they compile out under the prod level.
    _logger.LogDebug(
      "📊 BUDGET CALC: BudgetId={BudgetId}, Type={BudgetType}, UserId={UserId}, FamilyId={FamilyId}, Period={Start:yyyy-MM-dd} to {End:yyyy-MM-dd}, TotalTransactions={Count}",
      budget.Id, budget.Type, budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEnd, transactions.Count());

    // Use extension methods for consistent expense/income categorization
    // These centralize the logic for excluding credit payments, transfers, etc.
    var categorySpending = transactions.ExpensesByCategory();
    var categoryIncome = transactions.IncomeByCategory();
    var categoryTransactionCounts = transactions.ExpenseCountsByCategory();
    var incomeTransactionCounts = transactions.IncomeCountsByCategory();
    
    // Use CategoryKey-based tracking to properly handle custom categories
    // This prevents all custom categories from sharing the same "Uncategorized" spending total
    // Allowance budgets use PersonalUserCategoryId (sub-categorization); all others use CategoryId
    Dictionary<TransactionAmountExtensions.CategoryKey, decimal> categoryKeySpending;
    Dictionary<TransactionAmountExtensions.CategoryKey, int> categoryKeyExpenseCounts;
    
    // RFC #793: a linked Personal child (funded by a family IsAllowance category) and a legacy
    // Type=Allowance budget are ONE concept — a "family-funded personal budget". They behave
    // identically for personal-category bucketing (B6), additive member income (B5), and the
    // uniform IsFamilyFunded signal exposed to the client.
    var isLinkedPersonalChild = await IsLinkedPersonalChildAsync(budget);
    var isFamilyFunded = isLinkedPersonalChild || budget.Type == BudgetType.Allowance;

    if (isFamilyFunded)
    {
      // Allowance budgets count the charges claimed for their owner (so a charge a family admin
      // assigns to a child shows on the child's allowance), bucketed by the owner's private
      // sub-category (PersonalUserCategoryId); not-yet-sub-categorized charges fall under
      // Uncategorized so they still count. Owner-scoped so children's spend doesn't cross over.
      categoryKeySpending = transactions.ExpensesByAllowanceCategoryKey(budget.UserId);
      categoryKeyExpenseCounts = transactions.ExpenseCountsByAllowanceCategoryKey(budget.UserId);
      _logger.LogDebug(
        "📊 ALLOWANCE BUDGET {BudgetId}: owner={OwnerId}, claimed-charge spending keys={SpendingKeys}.",
        budget.Id, budget.UserId, categoryKeySpending.Count);
    }
    else
    {
      // Personal/Family budgets track spending by CategoryId
      categoryKeySpending = transactions.ExpensesByCategoryKey();
      categoryKeyExpenseCounts = transactions.ExpenseCountsByCategoryKey();
    }
    
    var categoryKeyIncome = transactions.IncomeByCategoryKey();
    var categoryKeyIncomeCounts = transactions.IncomeCountsByCategoryKey();

    // Log expense totals (Debug-level; see note above — hot path, once per budget).
    var totalExpenses = categorySpending.Values.Sum();
    var totalIncome = categoryIncome.Values.Sum();
    _logger.LogDebug(
      "📊 BUDGET CALC: TotalExpenses={Expenses:C}, TotalIncome={Income:C}, ExpenseCategories={ExpCats}, IncomeCategories={IncCats}",
      totalExpenses, totalIncome, categorySpending.Count, categoryIncome.Count);

    // Log top expense categories. Guarded so the OrderByDescending/Take work is skipped entirely when
    // Debug logging is off (the common production case).
    if (_logger.IsEnabled(LogLevel.Debug))
    {
      foreach (var (cat, amount) in categorySpending.OrderByDescending(x => x.Value).Take(5))
      {
        _logger.LogDebug("  💸 {Category}: {Amount:C} ({Count} transactions)",
          cat, amount, categoryTransactionCounts.GetValueOrDefault(cat, 0));
      }
    }

    var categories = budget.Categories.Select(c => {
      // Use helper method for consistent CategoryId computation
      var categoryId = c.GetEffectiveCategoryId();
      // Use IsIncome flag, or check if CategoryId is in income range (1-4)
      var isIncomeCategory = c.IsIncome || (categoryId >= 1 && categoryId <= 4);
      // Use category name from navigation property
      var displayName = c.CategoryNav?.Name ?? "Unknown";
      
      var categoryKey = new TransactionAmountExtensions.CategoryKey(categoryId);
      
      // For SpentAmount and TransactionCount, use the CategoryKey-based lookups
      var spentAmount = isIncomeCategory
        ? categoryKeyIncome.GetValueOrDefault(categoryKey, 0)
        : categoryKeySpending.GetValueOrDefault(categoryKey, 0);
      var transactionCount = isIncomeCategory
        ? categoryKeyIncomeCounts.GetValueOrDefault(categoryKey, 0)
        : categoryKeyExpenseCounts.GetValueOrDefault(categoryKey, 0);
        
      return new BudgetCategoryDto
      {
        Id = c.Id,
        UpdatedAt = c.UpdatedAt,
        CategoryId = categoryId,
        CategoryName = displayName,
        AllocatedAmount = c.AllocatedAmount,
        // For income: actual income received; for expense: amount spent.
        // Allowance categories are funded sub-budgets stored under CategoryId 99
        // (Uncategorized). Matching transactions by category would wrongly attribute the
        // family's genuinely-uncategorized spending to the allowance (the >100% "used"
        // bug). The child's real spend is tracked in their linked budget, so report 0 here.
        SpentAmount = c.IsAllowance ? 0m : spentAmount,
        AllowRollover = c.AllowRollover,
        RolloverBalance = c.RolloverBalance,
        MaxRolloverMultiplier = c.MaxRolloverMultiplier,
        RolloverOverflowSavingsGoalId = c.RolloverOverflowSavingsGoalId,
        RolloverOverflowSavingsGoalName = c.RolloverOverflowSavingsGoal?.Name,
        IsCountUpEnabled = c.IsCountUpEnabled,
        CountUpRolloverMode = c.CountUpRolloverMode,
        SavingsGoalId = c.SavingsGoalId,
        SavingsGoalName = c.SavingsGoal?.Name,
        IsAllowance = c.IsAllowance,
        LinkedBudgetId = c.LinkedBudgetId,
        AssignedUserId = c.AssignedUserId,
        AssignedUserName = c.AssignedUser?.FirstName,
        IsVirtual = false,
        TransactionCount = transactionCount,
        // New income/template fields
        IsIncome = isIncomeCategory,
        DefaultAllocatedAmount = c.DefaultAllocatedAmount
      };
    }).ToList();

    // Add virtual categories for ANY category with transactions that isn't in the budget
    // This ensures all spending is visible, even if the user hasn't created a budget category for it
    // Use CategoryKey (unified CategoryId) to check for transactions
    var budgetCategoryIds = budget.Categories
      .Select(c => c.CategoryId)
      .ToHashSet();
    
    // Find all category IDs with spending that aren't in the budget
    var keysWithSpending = categoryKeySpending.Keys
      .Union(categoryKeyExpenseCounts.Keys)
      .Where(key => !budgetCategoryIds.Contains(key.CategoryId))
      .ToList();

    // Pre-load category names for virtual categories to avoid placeholder names
    var virtualCategoryIds = keysWithSpending.Select(k => k.CategoryId).ToHashSet();
    var loadedCategories = await _categoryRepository.GetByIdsAsync(virtualCategoryIds);
    var categoryNameLookup = loadedCategories.ToDictionary(c => c.Id, c => c.Name);

    foreach (var categoryKey in keysWithSpending)
    {
      var spending = categoryKeySpending.GetValueOrDefault(categoryKey, 0);
      var count = categoryKeyExpenseCounts.GetValueOrDefault(categoryKey, 0);
      
      // Only add if there's actual activity
      if (count > 0 || spending != 0)
      {
        // Derive display name: lookup from pre-loaded categories, fall back to system enum or placeholder
        string displayName;
        if (categoryNameLookup.TryGetValue(categoryKey.CategoryId, out var loadedName))
        {
          displayName = loadedName;
        }
        else if (categoryKey.CategoryId >= 1 && categoryKey.CategoryId < 1000 && 
                 Enum.IsDefined(typeof(TransactionCategory), categoryKey.CategoryId))
        {
          displayName = ((TransactionCategory)categoryKey.CategoryId).GetDisplayName();
        }
        else
        {
          displayName = $"Unknown Category {categoryKey.CategoryId}";
        }
        
        categories.Add(new BudgetCategoryDto
        {
          Id = -1, // Virtual ID - indicates this isn't a real category in the database
          CategoryId = categoryKey.CategoryId,
          CategoryName = displayName,
          AllocatedAmount = 0, // No budget allocated for this category
          SpentAmount = spending,
          AllowRollover = false,
          RolloverBalance = 0,
          IsCountUpEnabled = false,
          IsAllowance = false,
          LinkedBudgetId = null,
          AssignedUserId = null,
          AssignedUserName = null,
          IsVirtual = true, // Flag this as a virtual category
          TransactionCount = count
        });
      }
    }

    // Calculate period information
    var daysInPeriod = _budgetPeriodService.GetDaysInPeriod(budget.Period, budget.CurrentPeriodStart, budget.CurrentPeriodEnd);
    var daysElapsed = (DateTime.UtcNow.Date - budget.CurrentPeriodStart.Date).Days + 1;
    daysElapsed = Math.Max(1, Math.Min(daysElapsed, daysInPeriod)); // Clamp between 1 and total days

    // Map shares if loaded
    var shares = budget.Shares?.Select(s => new BudgetShareDto
    {
      Id = s.Id,
      BudgetId = s.BudgetId,
      SharedWithUserId = s.SharedWithUserId,
      SharedWithUserName = s.SharedWithUser?.FirstName ?? "Unknown",
      CanEdit = s.CanEdit,
      CanViewTransactions = s.CanViewTransactions
    }).ToList();

    // Map child budgets if loaded
    List<BudgetDto>? childBudgets = null;
    if (budget.ChildBudgets?.Any() == true)
    {
      childBudgets = new List<BudgetDto>();
      foreach (var child in budget.ChildBudgets)
      {
        var childDto = await MapToBudgetDtoAsync(child, transactionCache, userCache);
        // The entity's TotalExpenses is the allocated sum; report the child's ACTUAL
        // spend (sum of expense-category spend, including virtual categories from
        // transactions) so the family's Child Budgets section shows real progress.
        childDto.TotalExpenses =
          childDto.Categories?.Where(c => !c.IsIncome).Sum(c => c.SpentAmount) ?? 0m;
        childBudgets.Add(childDto);
      }
    }

    // Reflect each child's actual allowance spend on its linked allowance category so
    // the family budget's totals track real usage (not the CategoryId-99 collision,
    // which we already zeroed out above).
    if (childBudgets != null)
    {
      foreach (var allowanceCat in categories.Where(c => c.IsAllowance && c.LinkedBudgetId != null))
      {
        var linked = childBudgets.FirstOrDefault(cb => cb.Id == allowanceCat.LinkedBudgetId);
        if (linked != null) allowanceCat.SpentAmount = linked.TotalExpenses;
      }
    }

    // Calculate TotalIncome based on budget type:
    // - Personal budgets: use AllocatedAmount (the allowance from family budget)
    // - Family/Regular budgets: sum of MAX(expected, actual) for each income category
    decimal calculatedTotalIncome;
    if (isFamilyFunded)
    {
      // RFC #793 B5 — additive member income: a family-funded personal budget's spendable is the
      // family allocation PLUS the member's own income booked on this budget (e.g. their job
      // income), rather than forcing TotalIncome = allocation. Applies uniformly to a linked
      // Type=Personal child and a legacy Type=Allowance budget. Own income = MAX(expected, actual)
      // over the budget's income categories, mirroring the family/regular derivation below.
      var ownIncome = categories.Where(c => c.IsIncome).Sum(c => Math.Max(c.AllocatedAmount, c.SpentAmount));
      calculatedTotalIncome = (budget.AllocatedAmount ?? 0m) + ownIncome;
    }
    else if (budget.Type == BudgetType.Personal)
    {
      // Standalone personal budget (no family funding): "income" is the allocation amount (unchanged).
      calculatedTotalIncome = budget.AllocatedAmount ?? budget.TotalIncome;
    }
    else
    {
      // Family/Regular budgets: calculate from income categories
      var incomeFromCategories = categories
        .Where(c => c.IsIncome)
        .Sum(c => Math.Max(c.AllocatedAmount, c.SpentAmount));
      
      // Use category-based income if we have income categories, otherwise fall back to stored TotalIncome
      calculatedTotalIncome = incomeFromCategories > 0 ? incomeFromCategories : budget.TotalIncome;
    }

    // Savings transfers: transactions earmarked to a goal (and thus excluded from
    // the income/expense totals above). Surfaced as their own section for visibility.
    // Exclude split PARENTS (IsSplit) the same way IsBudgetable / ExpensesByCategory do:
    // splitting an earmarked charge copies the parent's SavingsGoalId onto each child
    // (so the earmark survives the split), so the parent AND its children all carry the
    // goal id. Counting the parent here as well would render it two/three times
    // (e.g. a $60 charge split $40+$20 → three rows totalling $120). Dropping the parent
    // leaves the children carrying the amount exactly once ($40+$20 = $60).
    var savingsTransfers = transactions
      .Where(t => !t.IsSplit && t.IsEarmarkedToGoalExcludedFromBudget())
      .OrderByDescending(t => t.Date)
      .Select(t =>
      {
        var isDeposit = t.IsIncome();
        return new BudgetSavingsTransferDto
        {
          TransactionId = t.Id,
          Description = string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName,
          Amount = Math.Abs(t.Amount),
          IsDeposit = isDeposit,
          SavingsGoalId = t.SavingsGoalId!.Value,
          SavingsGoalName = t.SavingsGoal?.Name ?? "Savings goal",
          Date = t.Date,
        };
      })
      .ToList();

    return new BudgetDto
    {
      Id = budget.Id,
      UpdatedAt = budget.UpdatedAt,
      Name = budget.Name,
      Period = budget.Period,
      CurrentPeriodStart = budget.CurrentPeriodStart,
      CurrentPeriodEnd = budget.CurrentPeriodEnd,
      LastReset = budget.LastReset,
      AutoReset = budget.AutoReset,
      DaysInCurrentPeriod = daysInPeriod,
      DaysElapsed = daysElapsed,
      TotalIncome = calculatedTotalIncome,
      TotalExpenses = budget.TotalExpenses,
      IsActive = budget.IsActive,
      Categories = categories,
      
      // Hierarchical budget properties
      UserId = budget.UserId,
      FamilyId = budget.FamilyId,
      ParentBudgetId = budget.ParentBudgetId,
      AllocatedAmount = budget.AllocatedAmount,
      Type = budget.Type,
      IsPrimary = budget.IsPrimary,

      // RFC #793 — uniform "family-funded personal budget" signal (TRUE for a linked Type=Personal
      // child AND a legacy Type=Allowance budget) so the client never branches on Type.
      IsFamilyFunded = isFamilyFunded,
      FamilyAllocationAmount = isFamilyFunded ? budget.AllocatedAmount : null,
      
      // Child budget rollover settings
      RolloverMode = budget.RolloverMode,
      DefaultSavingsGoalId = budget.DefaultSavingsGoalId,
      DefaultSavingsGoalName = budget.DefaultSavingsGoal?.Name,
      // IsSharedWithFamily is now computed from Type == BudgetType.Family in DTO
      
      // Additional context
      OwnerName = budget.User?.FirstName,
      ChildBudgets = childBudgets,
      Shares = shares,

      SavingsTransfers = savingsTransfers,
      TotalSavingsDeposits = savingsTransfers.Where(s => s.IsDeposit).Sum(s => s.Amount),
      TotalSavingsWithdrawals = savingsTransfers.Where(s => !s.IsDeposit).Sum(s => s.Amount),

      LastPeriodSummary = await GetLastPeriodSummaryAsync(budget.Id)
    };
  }

  // ============================================
  // Period Summary Retrieval
  // ============================================

  /// <summary>
  /// Get the most recent completed period summary for a budget.
  /// Returns null if no period snapshots exist yet.
  /// </summary>
  private async Task<BudgetPeriodSummaryDto?> GetLastPeriodSummaryAsync(int budgetId)
  {
    var snapshot = await _context.BudgetPeriodSnapshots
      .Include(s => s.CategorySnapshots)
      .Where(s => s.BudgetId == budgetId)
      .OrderByDescending(s => s.PeriodStart)
      .FirstOrDefaultAsync();

    if (snapshot == null)
      return null;

    // Income categories are NOT spending. A salary line allocated $8,535 and "spent" $0 is
    // income arriving as planned, but the variance maths below reads it as coming in $8,535
    // under budget — on a real household budget that single row was 77% of a reported
    // "you saved $11,105" in a month the user actually overspent.
    var spendingSnapshots = snapshot.CategorySnapshots.Where(cs => !cs.IsIncome).ToList();

    var categorySummaries = spendingSnapshots.Select(cs => new BudgetCategorySummaryDto
    {
      CategoryName = cs.CategoryName,
      Allocated = cs.AllocatedAmount,
      Spent = cs.SpentAmount,
      Saved = cs.AllocatedAmount - cs.SpentAmount,
      RolledOver = cs.RolloverOut,
      WasUnderBudget = cs.SpentAmount <= cs.AllocatedAmount
    }).ToList();

    // NET, not the sum of the positives. Summing only the categories that came in under
    // budget silently discarded every overspend, so a period could report a large "saving"
    // while the household was down overall. Negative is a real and reportable answer.
    var netSavings = categorySummaries.Sum(c => c.Saved);

    // Encouragement copy quotes amounts; write them in the budget owner's currency (#941).
    var ownerCurrency = await _context.Budgets
      .Where(b => b.Id == budgetId)
      .Join(_context.Users, b => b.UserId, u => u.Id, (b, u) => u.Currency)
      .FirstOrDefaultAsync();

    var summary = new BudgetPeriodSummaryDto
    {
      PeriodStart = snapshot.PeriodStart,
      PeriodEnd = snapshot.PeriodEnd,
      TotalSavings = netSavings,
      TotalRollover = snapshot.TotalRolloverOut,
      CategorySummaries = categorySummaries,
      EncouragementMessage = _budgetPeriodService.GenerateEncouragementMessage(new BudgetPeriodSummaryDto
      {
        TotalSavings = netSavings,
        PeriodStart = snapshot.PeriodStart,
        PeriodEnd = snapshot.PeriodEnd,
        CategorySummaries = categorySummaries
      }, ownerCurrency)
    };

    return summary;
  }

  // ============================================
  // Period History (multi-month navigation)
  // ============================================

  public async Task<IEnumerable<BudgetPeriodListItemDto>> GetBudgetPeriodsAsync(int budgetId, int userId)
  {
    var budget = await _budgetRepository.GetByIdAsync(budgetId);
    if (budget == null || !await HasAccessToBudgetAsync(budget, userId))
      throw new UnauthorizedAccessException("Budget not found or access denied.");

    var snapshots = await _context.BudgetPeriodSnapshots
      .Where(s => s.BudgetId == budgetId)
      .OrderByDescending(s => s.PeriodStart)
      .Select(s => new BudgetPeriodListItemDto
      {
        Id = s.Id,
        PeriodStart = s.PeriodStart,
        PeriodEnd = s.PeriodEnd,
        TotalAllocated = s.TotalAllocated,
        TotalSpent = s.TotalSpent,
      })
      .ToListAsync();

    return snapshots;
  }

  public async Task<BudgetPeriodDetailDto?> GetBudgetPeriodDetailAsync(int budgetId, int snapshotId, int userId)
  {
    var budget = await _budgetRepository.GetByIdAsync(budgetId);
    if (budget == null || !await HasAccessToBudgetAsync(budget, userId))
      throw new UnauthorizedAccessException("Budget not found or access denied.");

    var snapshot = await _context.BudgetPeriodSnapshots
      .Include(s => s.CategorySnapshots)
      .FirstOrDefaultAsync(s => s.Id == snapshotId && s.BudgetId == budgetId);

    if (snapshot == null)
      return null;

    return new BudgetPeriodDetailDto
    {
      Id = snapshot.Id,
      PeriodStart = snapshot.PeriodStart,
      PeriodEnd = snapshot.PeriodEnd,
      TotalAllocated = snapshot.TotalAllocated,
      TotalSpent = snapshot.TotalSpent,
      TotalIncome = snapshot.TotalIncome,
      Categories = snapshot.CategorySnapshots
        .OrderBy(cs => cs.IsIncome ? 0 : 1)
        .ThenBy(cs => cs.CategoryName)
        .Select(cs => new BudgetPeriodCategoryHistoryDto
        {
          Id = cs.Id,
          CategoryId = cs.CategoryId,
          CategoryName = cs.CategoryName,
          AllocatedAmount = cs.AllocatedAmount,
          SpentAmount = cs.SpentAmount,
          RolloverIn = cs.RolloverIn,
          RolloverOut = cs.RolloverOut,
          IsIncome = cs.IsIncome,
          AllowRollover = cs.AllowRollover,
        })
        .ToList(),
    };
  }

  public async Task<BudgetPeriodCategoryHistoryDto> UpdatePeriodCategoryAmountAsync(
    int budgetId, int snapshotId, int categorySnapshotId, decimal allocatedAmount, int userId)
  {
    var budget = await _budgetRepository.GetByIdAsync(budgetId);
    if (budget == null)
      throw new UnauthorizedAccessException("Budget not found or access denied.");

    // Rewriting a historical period allocation is an Edit. HasAccessToBudgetAsync grants every family
    // member access with no role gate, letting a ReadOnly/Member corrupt shared family period history.
    // CanAccessBudgetAsync enforces ReadOnly->deny and family-budget Member->deny.
    await _authService.ValidateBudgetAccessOrThrowAsync(userId, budgetId, ResourcePermission.Edit);

    // Include navigation property to verify the snapshot belongs to this budget (IDOR guard)
    var categorySnapshot = await _context.BudgetPeriodCategorySnapshots
      .Include(cs => cs.BudgetPeriodSnapshot)
      .FirstOrDefaultAsync(cs => cs.Id == categorySnapshotId
        && cs.BudgetPeriodSnapshotId == snapshotId
        && cs.BudgetPeriodSnapshot.BudgetId == budgetId);

    if (categorySnapshot == null)
      throw new KeyNotFoundException("Category snapshot not found.");

    categorySnapshot.AllocatedAmount = allocatedAmount;

    // Recompute parent TotalAllocated; EF change tracker reflects the updated
    // categorySnapshot.AllocatedAmount before any save, so a single SaveChangesAsync suffices.
    var parentSnapshot = await _context.BudgetPeriodSnapshots
      .Include(s => s.CategorySnapshots)
      .FirstOrDefaultAsync(s => s.Id == snapshotId);

    if (parentSnapshot != null)
    {
      parentSnapshot.TotalAllocated = parentSnapshot.CategorySnapshots
        .Where(cs => !cs.IsIncome)
        .Sum(cs => cs.AllocatedAmount);
    }

    await _context.SaveChangesAsync();

    return new BudgetPeriodCategoryHistoryDto
    {
      Id = categorySnapshot.Id,
      CategoryId = categorySnapshot.CategoryId,
      CategoryName = categorySnapshot.CategoryName,
      AllocatedAmount = categorySnapshot.AllocatedAmount,
      SpentAmount = categorySnapshot.SpentAmount,
      RolloverIn = categorySnapshot.RolloverIn,
      RolloverOut = categorySnapshot.RolloverOut,
      IsIncome = categorySnapshot.IsIncome,
      AllowRollover = categorySnapshot.AllowRollover,
    };
  }
}
