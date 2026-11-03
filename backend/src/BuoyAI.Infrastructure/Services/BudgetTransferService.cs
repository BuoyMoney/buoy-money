using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for handling budget fund transfers, deficit resolution, and rollover management.
/// Extracted from BudgetService to follow Single Responsibility Principle.
/// </summary>
public class BudgetTransferService : IBudgetTransferService
{
    private readonly IBudgetRepository _budgetRepository;
    private readonly IUserRepository _userRepository;
    private readonly ISavingsRepository _savingsRepository;
    private readonly IBudgetSpendingService _spendingService;
    private readonly IFamilyAuthorizationService _familyAuthService;
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<BudgetTransferService> _logger;
    private readonly IGamificationService _gamificationService;

    public BudgetTransferService(
        IBudgetRepository budgetRepository,
        IUserRepository userRepository,
        ISavingsRepository savingsRepository,
        IBudgetSpendingService spendingService,
        IFamilyAuthorizationService familyAuthService,
        BuoyAIDbContext context,
        ILogger<BudgetTransferService> logger,
        IGamificationService gamificationService)
    {
        _budgetRepository = budgetRepository;
        _userRepository = userRepository;
        _savingsRepository = savingsRepository;
        _spendingService = spendingService;
        _familyAuthService = familyAuthService;
        _context = context;
        _logger = logger;
        _gamificationService = gamificationService;
    }

    public async Task<TransferFundsResponse> TransferFundsAsync(int targetCategoryId, int userId, TransferFundsRequest request)
    {
        try
        {
            // Optimistic-concurrency retry loop: the funds check and the allocation update are a
            // read-modify-write against the xmin-guarded BudgetCategory rows. If a competing transfer
            // commits between our read and save, SaveChanges throws DbUpdateConcurrencyException; we
            // reload and re-check funds so two transfers can't both spend the same surplus.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Get target category
                    var targetCategory = await _context.BudgetCategories
                        .Include(c => c.Budget)
                        .FirstOrDefaultAsync(c => c.Id == targetCategoryId);

                    if (targetCategory == null)
                        throw new KeyNotFoundException("Target category not found");

                    // Get source category
                    var sourceCategory = await _context.BudgetCategories
                        .Include(c => c.Budget)
                        .FirstOrDefaultAsync(c => c.Id == request.SourceCategoryId);

                    if (sourceCategory == null)
                        throw new KeyNotFoundException("Source category not found");

                    // Validate both categories belong to same budget
                    if (sourceCategory.BudgetId != targetCategory.BudgetId)
                        throw new DomainValidationException("Source and target categories must belong to the same budget");

                    // Validate user has access to this budget
                    var budget = targetCategory.Budget;
                    var hasAccess = await _budgetRepository.HasAccessAsync(budget.Id, userId);
                    if (!hasAccess)
                        throw new UnauthorizedAccessException("User does not have access to this budget");

                    // Validate amount
                    if (request.Amount <= 0)
                        throw new ArgumentException("Transfer amount must be positive");

                    // Calculate available funds in source (allocated - spent + rollover)
                    var sourceSpentAmount = await _spendingService.GetCategorySpentAmountAsync(sourceCategory);
                    var sourceRemaining = sourceCategory.AllocatedAmount - sourceSpentAmount + sourceCategory.RolloverBalance;
                    if (request.Amount > sourceRemaining)
                        throw new DomainValidationException($"Insufficient funds in source category. Available: {sourceRemaining:C}");

                    // Perform transfer - adjust allocated amounts
                    sourceCategory.AllocatedAmount -= request.Amount;
                    targetCategory.AllocatedAmount += request.Amount;
                    sourceCategory.UpdatedAt = DateTime.UtcNow;
                    targetCategory.UpdatedAt = DateTime.UtcNow;

                    // Record the transfer
                    var transfer = new BudgetTransfer
                    {
                        BudgetId = budget.Id,
                        SourceCategoryId = sourceCategory.Id,
                        DestinationCategoryId = targetCategory.Id,
                        Amount = request.Amount,
                        Note = request.Note,
                        Reason = TransferReason.Manual,
                        CreatedByUserId = userId,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.BudgetTransfers.Add(transfer);
                    await _context.SaveChangesAsync();

                    _logger.LogInformation(
                        "User {UserId} transferred {Amount:C} from category {SourceId} to {TargetId} in budget {BudgetId}",
                        userId, request.Amount, sourceCategory.Id, targetCategory.Id, budget.Id);

                    return new TransferFundsResponse
                    {
                        TransferId = transfer.Id,
                        SourceCategory = sourceCategory.ToDto(),
                        TargetCategory = targetCategory.ToDto(),
                        Amount = request.Amount,
                        TransferredAt = transfer.CreatedAt
                    };
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
                {
                    _logger.LogWarning(
                        "Concurrency conflict transferring funds (attempt {Attempt}/{Max}) for user {UserId}; reloading and re-checking funds",
                        attempt, MaxConcurrencyRetries, userId);
                    DetachAllTracked();
                }
            }
        }
        catch (Exception ex) when (ex is not KeyNotFoundException && ex is not UnauthorizedAccessException && ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error transferring funds for user {UserId}", userId);
            throw;
        }
    }

    public async Task<TransferToSavingsResponse> TransferToSavingsAsync(int categoryId, int userId, TransferToSavingsRequest request)
    {
        try
        {
            // Optimistic-concurrency retry loop: both the source category (AllocatedAmount) and the
            // destination goal (CurrentAmount) are xmin-guarded. A concurrent writer to either row
            // triggers DbUpdateConcurrencyException; we reload and re-check funds before retrying so
            // the same surplus can't be moved twice.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Get source category
                    var sourceCategory = await _context.BudgetCategories
                        .Include(c => c.Budget)
                        .FirstOrDefaultAsync(c => c.Id == categoryId);

                    if (sourceCategory == null)
                        throw new KeyNotFoundException("Category not found");

                    // Validate user has access to this budget
                    var budget = sourceCategory.Budget;
                    var hasAccess = await _budgetRepository.HasAccessAsync(budget.Id, userId);
                    if (!hasAccess)
                        throw new UnauthorizedAccessException("User does not have access to this budget");

                    // Get target savings goal
                    var savingsGoal = await _context.SavingsGoals.FindAsync(request.SavingsGoalId);
                    if (savingsGoal == null)
                        throw new KeyNotFoundException("Savings goal not found");

                    // Authorization rules:
                    // 1. Personal budget → Personal goals (user owns goal) - OK
                    // 2. Personal budget → Family goals (user can allocate to goal) - OK
                    // 3. Family budget → Family goals (same family) - OK
                    // 4. Family budget → Personal goals - NOT ALLOWED

                    var isPersonalBudget = budget.FamilyId == null;
                    var isPersonalGoal = savingsGoal.Type == SavingsGoalType.Personal;
                    var isFamilyGoal = savingsGoal.Type == SavingsGoalType.Family;

                    if (!isPersonalBudget && isPersonalGoal)
                    {
                        // Family budget cannot contribute to personal goals
                        throw new UnauthorizedAccessException("Family budget funds can only be contributed to family goals");
                    }

                    if (isPersonalGoal)
                    {
                        // Personal goal - must be owned by the user
                        if (savingsGoal.UserId != userId)
                            throw new UnauthorizedAccessException("You can only contribute to your own personal savings goals");
                    }
                    else if (isFamilyGoal)
                    {
                        // Family goal - check user has Allocate permission
                        var authResult = await _familyAuthService.CanAccessGoalAsync(userId, savingsGoal.Id, ResourcePermission.Allocate);
                        if (!authResult.IsAuthorized)
                            throw new UnauthorizedAccessException(authResult.Reason ?? "You don't have permission to contribute to this family goal");
                    }

                    // Validate amount
                    if (request.Amount <= 0)
                        throw new ArgumentException("Transfer amount must be positive");

                    // Calculate available funds in source (allocated - spent + rollover)
                    var sourceSpentAmount = await _spendingService.GetCategorySpentAmountAsync(sourceCategory);
                    var sourceRemaining = sourceCategory.AllocatedAmount - sourceSpentAmount + sourceCategory.RolloverBalance;
                    if (request.Amount > sourceRemaining)
                        throw new DomainValidationException($"Insufficient funds in category. Available: {sourceRemaining:C}");

                    // Perform transfer - reduce category allocation and increase savings goal
                    sourceCategory.AllocatedAmount -= request.Amount;
                    sourceCategory.UpdatedAt = DateTime.UtcNow;

                    savingsGoal.CurrentAmount += request.Amount;
                    savingsGoal.UpdatedAt = DateTime.UtcNow;

                    // Record the transfer
                    var transfer = new BudgetTransfer
                    {
                        BudgetId = budget.Id,
                        SourceCategoryId = sourceCategory.Id,
                        DestinationSavingsGoalId = savingsGoal.Id,
                        Amount = request.Amount,
                        Note = request.Note ?? $"Transfer to {savingsGoal.Name}",
                        Reason = request.Reason,
                        CreatedByUserId = userId,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.BudgetTransfers.Add(transfer);
                    await _context.SaveChangesAsync();

                    _logger.LogInformation(
                        "User {UserId} transferred {Amount:C} from category {CategoryId} to savings goal {GoalId} ({GoalName}) - Reason: {Reason}",
                        userId, request.Amount, categoryId, savingsGoal.Id, savingsGoal.Name, request.Reason);

                    // Moving budget money into a savings goal is a savings-streak day (💰)
                    // for the actor.
                    await _gamificationService.RecordDailyActivityAsync(userId, "savings_goal");

                    return new TransferToSavingsResponse
                    {
                        TransferId = transfer.Id,
                        SourceCategory = sourceCategory.ToDto(),
                        SavingsGoalId = savingsGoal.Id,
                        SavingsGoalName = savingsGoal.Name,
                        Amount = request.Amount,
                        NewGoalCurrentAmount = savingsGoal.CurrentAmount,
                        TransferredAt = transfer.CreatedAt
                    };
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
                {
                    _logger.LogWarning(
                        "Concurrency conflict transferring to savings (attempt {Attempt}/{Max}) for user {UserId}; reloading and re-checking funds",
                        attempt, MaxConcurrencyRetries, userId);
                    DetachAllTracked();
                }
            }
        }
        catch (Exception ex) when (ex is not KeyNotFoundException && ex is not UnauthorizedAccessException && ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error transferring funds to savings for user {UserId}", userId);
            throw;
        }
    }

    public async Task<IEnumerable<BudgetTransferDto>> GetTransferHistoryAsync(int budgetId, int userId)
    {
        var hasAccess = await _budgetRepository.HasAccessAsync(budgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        var transfers = await _context.BudgetTransfers
            .Include(t => t.SourceCategory)
            .Include(t => t.DestinationCategory)
            .Include(t => t.SourceSavingsGoal)
            .Include(t => t.DestinationSavingsGoal)
            .Include(t => t.CreatedByUser)
            .Where(t => t.BudgetId == budgetId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .ToListAsync();

        return transfers.Select(t => new BudgetTransferDto
        {
            Id = t.Id,
            SourceCategoryId = t.SourceCategoryId,
            SourceCategoryName = t.SourceCategory?.CategoryNav?.Name ?? "Unknown",
            SourceSavingsGoalId = t.SourceSavingsGoalId,
            SourceSavingsGoalName = t.SourceSavingsGoal?.Name,
            TargetCategoryId = t.DestinationCategoryId,
            TargetCategoryName = t.DestinationCategory?.CategoryNav?.Name ?? "Unknown",
            TargetSavingsGoalId = t.DestinationSavingsGoalId,
            TargetSavingsGoalName = t.DestinationSavingsGoal?.Name,
            Amount = t.Amount,
            Note = t.Note,
            Reason = t.Reason.ToString(),
            CreatedByUserName = t.CreatedByUser?.FirstName ?? "Unknown",
            CreatedAt = t.CreatedAt
        });
    }

    public async Task<ResolveDeficitResponse> ResolveDeficitAsync(int budgetId, int userId, ResolveDeficitRequest request)
    {
        try
        {
            // Optimistic-concurrency retry loop: deficit resolution moves money between xmin-guarded
            // categories and savings goals. If a competing writer commits mid-flight, SaveChanges
            // throws DbUpdateConcurrencyException; we reload the budget graph and re-run the actions
            // (which re-read surplus/savings balances) so we can't over-draw a source that a
            // concurrent transfer already emptied.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Get the budget with categories and verify access
                    var budget = await _context.Budgets
                        .Include(b => b.Categories)
                            .ThenInclude(c => c.CategoryNav)
                        .Include(b => b.DefaultSavingsGoal)
                        .FirstOrDefaultAsync(b => b.Id == budgetId);

                    if (budget == null)
                        throw new KeyNotFoundException("Budget not found");

                    var hasAccess = await _budgetRepository.HasAccessAsync(budgetId, userId);
                    if (!hasAccess)
                        throw new UnauthorizedAccessException("User does not have access to this budget");

                    // Get the target category
                    var targetCategory = budget.Categories.FirstOrDefault(c => c.Id == request.CategoryId);
                    if (targetCategory == null)
                        throw new KeyNotFoundException("Category not found in this budget");

                    // Verify the category has a deficit - calculate actual spending from transactions
                    var targetSpentAmount = await _spendingService.GetCategorySpentAmountAsync(targetCategory);
                    var actualDeficit = targetSpentAmount - targetCategory.AllocatedAmount - targetCategory.RolloverBalance;
                    if (actualDeficit < 0)
                        actualDeficit = 0; // No deficit

                    if (actualDeficit == 0)
                        throw new DomainValidationException("Category does not have a deficit to resolve");

                    // Guard every action amount: it MUST be a sane positive value. The Process* helpers
                    // subtract action.Amount from the source (savings goal / category / rollover), so a
                    // negative amount inflates the source out of nothing (fabricated savings progress,
                    // unbounded budget money) and its guards (e.g. `CurrentAmount < action.Amount`) pass
                    // trivially. Also require the total requested resolution not exceed the SERVER-computed
                    // deficit rather than trusting the client-supplied request.DeficitAmount, so a caller
                    // can't drain a savings goal into a category far beyond the real overspend.
                    decimal requestedResolution = 0;
                    foreach (var action in request.Actions)
                    {
                        Core.Services.MonetaryLimits.ValidatePositive(action.Amount, "Resolution amount");
                        requestedResolution += action.Amount;
                    }
                    if (requestedResolution > actualDeficit)
                        throw new DomainValidationException(
                            $"Total resolution amount ({requestedResolution:C}) exceeds the category's deficit ({actualDeficit:C}).");

                    var response = new ResolveDeficitResponse
                    {
                        ActionResults = new List<ResolutionActionResult>(),
                        TotalResolved = 0,
                        RemainingDeficit = 0
                    };

                    foreach (var action in request.Actions)
                    {
                        var result = new ResolutionActionResult
                        {
                            Strategy = action.Strategy,
                            Amount = action.Amount,
                            Success = false
                        };

                        switch (action.Strategy)
                        {
                            case DeficitResolutionStrategy.PullFromSavings:
                                result = await ProcessPullFromSavingsAsync(budget, targetCategory, action, userId);
                                break;

                            case DeficitResolutionStrategy.TransferFromCategory:
                                result = await ProcessTransferFromCategoryAsync(budget, targetCategory, action, userId);
                                break;

                            case DeficitResolutionStrategy.CarryAsNegativeRollover:
                                result = ProcessCarryAsNegativeRollover(targetCategory, action);
                                break;
                        }

                        response.ActionResults.Add(result);
                        if (result.Success)
                        {
                            response.TotalResolved += result.Amount;
                        }
                        else
                        {
                            response.RemainingDeficit += result.Amount;
                        }
                    }

                    // Save all changes
                    targetCategory.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();

                    // Return updated category
                    response.UpdatedCategory = targetCategory.ToDto();

                    _logger.LogInformation(
                        "User {UserId} resolved deficit for category {CategoryId}: Resolved {Resolved:C}, Remaining {Remaining:C}",
                        userId, request.CategoryId, response.TotalResolved, response.RemainingDeficit);

                    return response;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
                {
                    _logger.LogWarning(
                        "Concurrency conflict resolving deficit (attempt {Attempt}/{Max}) for budget {BudgetId}, category {CategoryId}; reloading and retrying",
                        attempt, MaxConcurrencyRetries, budgetId, request.CategoryId);
                    DetachAllTracked();
                }
            }
        }
        catch (Exception ex) when (ex is not KeyNotFoundException && ex is not UnauthorizedAccessException && ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error resolving deficit for budget {BudgetId}, category {CategoryId}", budgetId, request.CategoryId);
            throw;
        }
    }

    public async Task<ResetRolloversResponse> ResetCategoryRolloverAsync(int categoryId, int userId)
    {
        var category = await _context.BudgetCategories
            .Include(c => c.Budget)
            .FirstOrDefaultAsync(c => c.Id == categoryId);

        if (category == null)
            throw new KeyNotFoundException("Category not found");

        var hasAccess = await _budgetRepository.HasAccessAsync(category.BudgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        var previousRollover = category.RolloverBalance;
        var wasNegative = previousRollover < 0;

        category.RolloverBalance = 0;
        category.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "User {UserId} reset rollover for category {CategoryId} (cleared {Amount:C})",
            userId, categoryId, previousRollover);

        // Award "Selective Amnesia" achievement if clearing a negative rollover
        if (wasNegative)
        {
            await AwardSelectiveAmnesiaAchievementAsync(userId, Math.Abs(previousRollover));
        }

        return new ResetRolloversResponse
        {
            CategoriesReset = 1,
            TotalRolloverCleared = previousRollover,
            ResetCategories = new List<CategoryRolloverResetDto>
            {
                new()
                {
                    CategoryId = category.Id,
                    CategoryName = category.CategoryNav?.Name ?? "Unknown",
                    PreviousRollover = previousRollover
                }
            }
        };
    }

    public async Task<ResetRolloversResponse> ResetAllRolloversAsync(int budgetId, int userId)
    {
        var hasAccess = await _budgetRepository.HasAccessAsync(budgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        var categories = await _context.BudgetCategories
            .Where(c => c.BudgetId == budgetId && c.RolloverBalance != 0)
            .ToListAsync();

        var resetCategories = new List<CategoryRolloverResetDto>();
        decimal totalCleared = 0;
        decimal totalNegativeCleared = 0;

        foreach (var category in categories)
        {
            resetCategories.Add(new CategoryRolloverResetDto
            {
                CategoryId = category.Id,
                CategoryName = category.CategoryNav?.Name ?? "Unknown",
                PreviousRollover = category.RolloverBalance
            });
            totalCleared += category.RolloverBalance;
            if (category.RolloverBalance < 0)
                totalNegativeCleared += Math.Abs(category.RolloverBalance);
            category.RolloverBalance = 0;
            category.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "User {UserId} reset all rollovers for budget {BudgetId}: {Count} categories, {Amount:C} cleared",
            userId, budgetId, categories.Count, totalCleared);

        // Award "Selective Amnesia" achievement if any negative rollovers were cleared
        if (totalNegativeCleared > 0)
        {
            await AwardSelectiveAmnesiaAchievementAsync(userId, totalNegativeCleared);
        }

        return new ResetRolloversResponse
        {
            CategoriesReset = categories.Count,
            TotalRolloverCleared = totalCleared,
            ResetCategories = resetCategories
        };
    }

    #region Private Helper Methods

    /// <summary>
    /// Total attempts (initial + retries) for an optimistic-concurrency read-modify-write before the
    /// DbUpdateConcurrencyException is allowed to propagate.
    /// </summary>
    private const int MaxConcurrencyRetries = 3;

    /// <summary>
    /// Detaches every entity the context is tracking so the next retry attempt re-reads fresh rows
    /// (with current xmin values) from the database. Called after a DbUpdateConcurrencyException so the
    /// retry re-evaluates the funds check against the winning writer's committed state instead of the
    /// stale in-memory snapshot, and so the previously-Added BudgetTransfer is dropped rather than
    /// re-saved on the next attempt.
    /// </summary>
    private void DetachAllTracked()
    {
        foreach (var entry in _context.ChangeTracker.Entries().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<ResolutionActionResult> ProcessPullFromSavingsAsync(Budget budget, BudgetCategory targetCategory, DeficitResolutionAction action, int userId)
    {
        var result = new ResolutionActionResult
        {
            Strategy = action.Strategy,
            Amount = action.Amount,
            Success = false
        };

        var savingsGoalId = action.SavingsGoalId ?? budget.DefaultSavingsGoalId;
        if (savingsGoalId == null)
        {
            result.Message = "No savings goal specified and no default savings goal set";
            return result;
        }

        var savingsGoal = await _context.SavingsGoals.FindAsync(savingsGoalId.Value);
        if (savingsGoal == null)
        {
            result.Message = "Savings goal not found";
            return result;
        }

        // Authorize the caller against the SOURCE goal — never trust the client-supplied
        // SavingsGoalId (or the budget's DefaultSavingsGoalId) purely by existence. Without this a
        // user who owns budget B could drain ANY other user's savings goal into B's category by id
        // (cross-tenant IDOR). Mirror the ownership rules used by TransferToSavingsAsync:
        //   personal budget -> personal goal (caller owns it) / family goal (Allocate permission)
        //   family budget   -> family goal only (Allocate permission); never personal goals.
        var isPersonalBudget = budget.FamilyId == null;
        if (!isPersonalBudget && savingsGoal.Type == SavingsGoalType.Personal)
        {
            result.Message = "Family budget deficits can only pull from family savings goals";
            return result;
        }
        if (savingsGoal.Type == SavingsGoalType.Personal)
        {
            if (savingsGoal.UserId != userId)
            {
                result.Message = "You can only pull from your own personal savings goals";
                return result;
            }
        }
        else
        {
            var authResult = await _familyAuthService.CanAccessGoalAsync(userId, savingsGoal.Id, ResourcePermission.Allocate);
            if (!authResult.IsAuthorized)
            {
                result.Message = authResult.Reason ?? "You don't have permission to pull from this family savings goal";
                return result;
            }
        }

        if (savingsGoal.CurrentAmount < action.Amount)
        {
            result.Message = $"Insufficient funds in savings goal. Available: {savingsGoal.CurrentAmount:C}";
            return result;
        }

        // Pull from savings
        savingsGoal.CurrentAmount -= action.Amount;
        savingsGoal.UpdatedAt = DateTime.UtcNow;

        // Keep completion in sync with the balance (mirrors WithdrawFromGoalAsync). Without this a
        // fully-funded goal that gets pulled below its target stays flagged IsCompleted and is then
        // permanently skipped by leftover auto-allocation and shown as "complete" while underfunded.
        if (savingsGoal.CurrentAmount < savingsGoal.TargetAmount)
            savingsGoal.IsCompleted = false;

        // Add to category's allocation
        targetCategory.AllocatedAmount += action.Amount;

        // Record the transfer
        var transfer = new BudgetTransfer
        {
            BudgetId = budget.Id,
            SourceSavingsGoalId = savingsGoal.Id,
            DestinationCategoryId = targetCategory.Id,
            Amount = action.Amount,
            Note = $"Deficit resolution: pulled from {savingsGoal.Name}",
            Reason = TransferReason.DeficitResolution,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        _context.BudgetTransfers.Add(transfer);

        result.Success = true;
        result.SavingsGoalName = savingsGoal.Name;
        result.Message = $"Successfully pulled {action.Amount:C} from {savingsGoal.Name}";

        return result;
    }

    private async Task<ResolutionActionResult> ProcessTransferFromCategoryAsync(Budget budget, BudgetCategory targetCategory, DeficitResolutionAction action, int userId)
    {
        var result = new ResolutionActionResult
        {
            Strategy = action.Strategy,
            Amount = action.Amount,
            Success = false
        };

        if (action.SourceCategoryId == null)
        {
            result.Message = "Source category ID is required for transfer";
            return result;
        }

        var sourceCategory = budget.Categories.FirstOrDefault(c => c.Id == action.SourceCategoryId.Value);
        if (sourceCategory == null)
        {
            result.Message = "Source category not found in this budget";
            return result;
        }

        // Calculate available surplus from actual transaction spending
        var sourceSpentAmount = await _spendingService.GetCategorySpentAmountAsync(sourceCategory);
        var sourceSurplus = sourceCategory.AllocatedAmount + sourceCategory.RolloverBalance - sourceSpentAmount;
        if (sourceSurplus < action.Amount)
        {
            result.Message = $"Insufficient surplus in source category. Available: {sourceSurplus:C}";
            return result;
        }

        // Transfer from source to target
        sourceCategory.AllocatedAmount -= action.Amount;
        sourceCategory.UpdatedAt = DateTime.UtcNow;
        targetCategory.AllocatedAmount += action.Amount;

        // Record the transfer
        var transfer = new BudgetTransfer
        {
            BudgetId = budget.Id,
            SourceCategoryId = sourceCategory.Id,
            DestinationCategoryId = targetCategory.Id,
            Amount = action.Amount,
            Note = $"Deficit resolution: transferred from {sourceCategory.CategoryNav?.Name ?? "Unknown"}",
            Reason = TransferReason.DeficitResolution,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        _context.BudgetTransfers.Add(transfer);

        result.Success = true;
        result.SourceName = sourceCategory.CategoryNav?.Name ?? "Unknown";
        result.Message = $"Successfully transferred {action.Amount:C} from {sourceCategory.CategoryNav?.Name ?? "Unknown"}";

        return result;
    }

    private static ResolutionActionResult ProcessCarryAsNegativeRollover(BudgetCategory targetCategory, DeficitResolutionAction action)
    {
        // Apply negative rollover to the category
        targetCategory.RolloverBalance -= action.Amount;

        return new ResolutionActionResult
        {
            Strategy = action.Strategy,
            Amount = action.Amount,
            Success = true,
            Message = $"Carrying {action.Amount:C} as negative rollover to next period"
        };
    }

    private async Task AwardSelectiveAmnesiaAchievementAsync(int userId, decimal amountForgotten)
    {
        // Awarded through the unified gamification system (idempotent).
        await _gamificationService.AwardAsync(userId, AchievementDefinitions.SelectiveAmnesia);

        _logger.LogInformation(
            "Awarded 'Selective Amnesia' achievement to user {UserId} for resetting {Amount:C} in negative rollover",
            userId, amountForgotten);
    }

    #endregion
}
