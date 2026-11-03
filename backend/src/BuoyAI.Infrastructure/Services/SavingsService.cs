using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.Infrastructure.Services;

public class SavingsService : ISavingsService
{
    private readonly ISavingsRepository _savingsRepository;
    private readonly IBudgetRepository _budgetRepository;
    private readonly IUserRepository _userRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly IFamilyAuthorizationService _familyAuthService;
    private readonly IAIInsightGenerationService _aiInsightService;
    private readonly ILogger<SavingsService> _logger;
    private readonly IGamificationEvaluationQueue _gamificationQueue;
    private readonly IGamificationService _gamificationService;
    private readonly ITransactionRepository _transactionRepository;

    /// <summary>Total attempts (initial + retries) before an xmin conflict is allowed to propagate.</summary>
    private const int MaxConcurrencyRetries = 3;

    public SavingsService(
        ISavingsRepository savingsRepository,
        IBudgetRepository budgetRepository,
        IUserRepository userRepository,
        IAccountRepository accountRepository,
        IFamilyAuthorizationService familyAuthService,
        IAIInsightGenerationService aiInsightService,
        ILogger<SavingsService> logger,
        IGamificationEvaluationQueue gamificationQueue,
        IGamificationService gamificationService,
        ITransactionRepository transactionRepository)
    {
        _transactionRepository = transactionRepository;
        _savingsRepository = savingsRepository;
        _budgetRepository = budgetRepository;
        _userRepository = userRepository;
        _accountRepository = accountRepository;
        _familyAuthService = familyAuthService;
        _aiInsightService = aiInsightService;
        _logger = logger;
        _gamificationQueue = gamificationQueue;
        _gamificationService = gamificationService;
    }

    public async Task<SavingsOverviewDto> GetSavingsOverviewAsync(int userId)
    {
        try
        {
            _logger.LogInformation("Getting savings overview for user {UserId}", userId);

            // Get user to check for family membership
            var user = await _userRepository.GetByIdAsync(userId);
            // Get user's family role for permission checks
            var userFamilyRole = await _familyAuthService.GetUserRoleAsync(userId);
            
            // Get goals for user and their family (if in one)
            var goals = await _savingsRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId);
            // One query for every linked account, rather than one per goal.
            var allocatedByAccount = await GetAccountAllocationsAsync(goals);
            var goalDtos = new List<SavingsGoalDto>();

            foreach (var goal in goals)
            {
                var goalDto = MapToSavingsGoalDto(goal, userId, userFamilyRole, allocatedByAccount);
                goalDtos.Add(goalDto);
            }

            var totalSaved = await _savingsRepository.GetTotalSavedAmountAsync(userId);
            var monthlyGrowth = await _savingsRepository.GetMonthlyGrowthAsync(userId);
            var activeGoalsCount = await _savingsRepository.GetActiveGoalsCountAsync(userId);
            var completedGoalsCount = await _savingsRepository.GetCompletedGoalsCountAsync(userId);

            // Calculate leftover from personal/allowance budget (for personal goals)
            var activeBudget = await _budgetRepository.GetActivePersonalBudgetAsync(userId);
            var personalLeftover = activeBudget != null 
                ? await CalculateLeftoverBudgetAsync(activeBudget.Id) 
                : 0m;

            // Calculate leftover from family budget (for family goals)
            decimal familyLeftover = 0m;
            bool hasFamilyBudget = false;
            if (user?.FamilyId != null)
            {
                var familyBudget = await _budgetRepository.GetPrimaryFamilyBudgetAsync(user.FamilyId.Value);
                if (familyBudget != null)
                {
                    hasFamilyBudget = true;
                    familyLeftover = await CalculateLeftoverBudgetAsync(familyBudget.Id);
                }
            }

            // Total leftover is both combined (for backwards compatibility)
            var currentPeriodLeftover = personalLeftover + familyLeftover;

            // Project monthly growth based on current leftover and allocation percentages
            // Personal goals use personal leftover, family goals use family leftover
            // Same divisor as ProcessAutomaticAllocationsAsync, so a pool whose shares add up to more
            // than 100% (a reopened goal) projects what will really be paid out, not more.
            var poolGoals = goalDtos.Where(g => g.IsActive && !g.IsCompleted).ToList();
            var personalDivisor = Math.Max(100m, poolGoals
                .Where(g => g.Type != SavingsGoalType.Family).Sum(g => g.AllocationPercentage));
            var familyDivisor = Math.Max(100m, poolGoals
                .Where(g => g.Type == SavingsGoalType.Family).Sum(g => g.AllocationPercentage));

            var projectedMonthlyGrowth = poolGoals
                .Sum(g => g.Type == SavingsGoalType.Family
                    ? familyLeftover * g.AllocationPercentage / familyDivisor
                    : personalLeftover * g.AllocationPercentage / personalDivisor);

            // Calculate total allocated amount per period (separate by type)
            var totalAllocatedPerPeriod = projectedMonthlyGrowth;

            // Calculate unallocated amount (leftover budget not assigned to any goal)
            var unallocatedAmount = Math.Max(0, currentPeriodLeftover - totalAllocatedPerPeriod);

            var recentAchievements = await GetRecentAchievementsAsync(userId);

            return new SavingsOverviewDto
            {
                TotalSaved = totalSaved,
                MonthlyGrowth = monthlyGrowth,
                CurrentPeriodLeftover = currentPeriodLeftover,
                FamilyLeftover = familyLeftover,
                PersonalLeftover = personalLeftover,
                HasFamilyBudget = hasFamilyBudget,
                ProjectedMonthlyGrowth = projectedMonthlyGrowth,
                TotalAllocatedPerPeriod = totalAllocatedPerPeriod,
                UnallocatedAmount = unallocatedAmount,
                ActiveGoalsCount = activeGoalsCount,
                CompletedGoalsCount = completedGoalsCount,
                Goals = goalDtos,
                RecentAchievements = recentAchievements
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting savings overview for user {UserId}", userId);
            throw;
        }
    }

    public async Task<IEnumerable<SavingsGoalDto>> GetSavingsGoalsAsync(int userId)
    {
        try
        {
            // Get user to check for family membership
            var user = await _userRepository.GetByIdAsync(userId);
            // Get user's family role for permission checks
            var userFamilyRole = await _familyAuthService.GetUserRoleAsync(userId);
            
            // Get goals for user and their family (if in one)
            var goals = await _savingsRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId);
            // One query for every linked account, rather than one per goal.
            var allocatedByAccount = await GetAccountAllocationsAsync(goals);
            var pendingByGoal = await GetPendingDrawTotalsAsync(goals);
            var goalDtos = new List<SavingsGoalDto>();

            foreach (var goal in goals)
            {
                var goalDto = MapToSavingsGoalDto(goal, userId, userFamilyRole, allocatedByAccount, pendingByGoal);
                goalDtos.Add(goalDto);
            }

            return goalDtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting savings goals for user {UserId}", userId);
            throw;
        }
    }

    public async Task<SavingsGoalDto?> GetSavingsGoalAsync(int goalId, int? currentUserId = null)
    {
        try
        {
            var goal = await _savingsRepository.GetByIdAsync(goalId);
            if (goal == null)
                return null;
            var role = await GetUserRoleIfNeededAsync(currentUserId);
            var allocatedByAccount = await GetAccountAllocationsAsync(new[] { goal });
            return MapToSavingsGoalDto(goal, currentUserId, role, allocatedByAccount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> CreateSavingsGoalAsync(int userId, CreateSavingsGoalRequest request)
    {
        try
        {
            _logger.LogInformation("Creating {GoalType} savings goal for user {UserId}: {GoalName}", 
                request.Type, userId, request.Name);

            // Get user to check for family membership
            var user = await _userRepository.GetByIdAsync(userId);

            // For family goals, validate user is in a family
            int? familyId = null;
            if (request.Type == SavingsGoalType.Family)
            {
                if (user?.FamilyId == null)
                {
                    throw new InvalidOperationException("Cannot create a family savings goal without being a member of a family.");
                }
                familyId = user.FamilyId;
            }

            // Validate that total allocation percentages don't exceed 100% for this goal type
            await ValidateAllocationPercentageAsync(userId, request.AllocationPercentage, request.Type, familyId);

            // Target must be positive and within the $1B cap (QE #615 Part E: no upper bound).
            Core.Services.MonetaryLimits.ValidatePositive(request.TargetAmount, "Target amount");

            if (request.InitialAmount < 0)
            {
                throw new ArgumentException("Initial amount cannot be negative.", nameof(request.InitialAmount));
            }

            if (request.InitialAmount > request.TargetAmount)
            {
                throw new ArgumentException("Initial amount cannot exceed target amount.", nameof(request.InitialAmount));
            }

            var initialAmount = request.InitialAmount;

            if (request.AccountId.HasValue)
            {
                await ValidateAccountLinkAsync(request.AccountId.Value, userId);
            }

            var goal = new SavingsGoal
            {
                UserId = userId,
                FamilyId = familyId,
                AccountId = request.AccountId,
                Type = request.Type,
                Name = request.Name,
                Description = request.Description,
                TargetAmount = request.TargetAmount,
                CurrentAmount = initialAmount,
                AllocationPercentage = request.AllocationPercentage,
                TargetDate = request.TargetDate.HasValue 
                    ? DateTime.SpecifyKind(request.TargetDate.Value, DateTimeKind.Utc)
                    : null,
                IconName = request.IconName,
                ColorTheme = request.ColorTheme,
                IsActive = true,
                IsCompleted = initialAmount >= request.TargetAmount,
                CountWithdrawalsTowardBudget = request.CountWithdrawalsTowardBudget
            };

            var createdGoal = await _savingsRepository.CreateAsync(goal);

            // Record initial amount as an allocation for audit trail
            if (initialAmount > 0)
            {
                var allocation = new SavingsAllocation
                {
                    SavingsGoalId = createdGoal.Id,
                    AllocatedAmount = initialAmount,
                    LeftoverBudget = initialAmount,
                    AllocationDate = DateTime.UtcNow,
                    Source = "initial_amount"
                };
                await _savingsRepository.CreateAllocationAsync(allocation);
            }
            
            // Gamification (achievements + streaks) is evaluated off-thread.
            _gamificationQueue.Enqueue(userId);

            return MapToSavingsGoalDto(createdGoal, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating savings goal for user {UserId}", userId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> UpdateSavingsGoalAsync(int goalId, UpdateSavingsGoalRequest request, int? currentUserId = null)
    {
        try
        {
            var goal = await _savingsRepository.GetByIdAsync(goalId);
            if (goal == null)
                throw new ArgumentException($"Savings goal with ID {goalId} not found.");
            var originalType = goal.Type;
            var originalShare = goal.AllocationPercentage;
            // Only a goal already in the pool (active, not finished) may keep an unchanged share in
            // an over-100% pool. One rejoining it (reactivated, or a raised target that reopens it)
            // is checked like a new share, or the edit itself would push the pool past 100%.
            var wasInPool = goal.IsActive && !goal.IsCompleted;

            // Handle type change if requested
            if (request.Type.HasValue && request.Type.Value != goal.Type)
            {
                var user = await _userRepository.GetByIdAsync(goal.UserId);
                
                // Validate type change
                if (request.Type.Value == SavingsGoalType.Family)
                {
                    // Changing to Family: user must be in a family
                    if (user?.FamilyId == null)
                        throw new InvalidOperationException("Cannot change to a family goal without being a member of a family.");
                    
                    // Check family budget leftover can support this
                    var familyBudget = await _budgetRepository.GetPrimaryFamilyBudgetAsync(user.FamilyId.Value);
                    if (familyBudget == null)
                        throw new InvalidOperationException("No family budget found. Create a family budget first.");
                    
                    var familyLeftover = await CalculateLeftoverBudgetAsync(familyBudget.Id);
                    _logger.LogInformation("Changing goal {GoalId} to Family type. Family leftover: {Leftover}, Allocation: {Allocation}%", 
                        goalId, familyLeftover, request.AllocationPercentage);
                    
                    goal.FamilyId = user.FamilyId;
                }
                else
                {
                    // Changing to Personal: remove family association
                    goal.FamilyId = null;
                    _logger.LogInformation("Changing goal {GoalId} to Personal type", goalId);
                }
                
                goal.Type = request.Type.Value;
            }

            // Validate allocation percentage against the SAME-TYPE pool only (excluding this goal),
            // mirroring create-time validation. Personal and family goals draw from separate leftover
            // pools, so summing both types here wrongly blocked a user who legitimately holds e.g.
            // 60% personal + 60% family from editing either goal. Family goals validate family-wide.
            await ValidateAllocationPercentageAsync(
                goal.UserId,
                request.AllocationPercentage,
                goal.Type,
                goal.FamilyId,
                excludeGoalId: goalId,
                currentPercentage: goal.Type == originalType && wasInPool ? originalShare : null);

            Core.Services.MonetaryLimits.ValidatePositive(request.TargetAmount, "Target amount");

            goal.Name = request.Name;
            goal.Description = request.Description;
            goal.TargetAmount = request.TargetAmount;
            goal.AllocationPercentage = request.AllocationPercentage;
            goal.TargetDate = request.TargetDate.HasValue 
                ? DateTime.SpecifyKind(request.TargetDate.Value, DateTimeKind.Utc)
                : null;
            goal.IconName = request.IconName;
            goal.ColorTheme = request.ColorTheme;
            // Only apply IsActive when the client actually supplied it (mirrors the guard below).
            // Omitted -> preserve the goal's current active state instead of defaulting it off.
            if (request.IsActive.HasValue)
                goal.IsActive = request.IsActive.Value;
            if (request.CountWithdrawalsTowardBudget.HasValue)
                goal.CountWithdrawalsTowardBudget = request.CountWithdrawalsTowardBudget.Value;

            // Unlink is explicit; an absent AccountId preserves the existing link rather than
            // clearing it, so editing only the name can never silently drop the account.
            if (request.ClearAccountLink)
            {
                goal.AccountId = null;
            }
            else if (request.AccountId.HasValue)
            {
                await ValidateAccountLinkAsync(request.AccountId.Value, goal.UserId);
                goal.AccountId = request.AccountId;
            }

            // Recompute completion SYMMETRICALLY. The old one-directional check left a goal stuck
            // "completed" when its target was raised above the current balance (100/100 -> target 200):
            // it then rendered as complete at 50%, was skipped by auto-allocation, and could not accept
            // manual contributions — only a balance-zeroing reset cleared it. Raising the target must
            // un-complete; lowering it at/under the balance must re-complete. Gamification only fires on
            // a false->true transition (matches Withdraw/Deposit).
            var wasCompleted = goal.IsCompleted;
            goal.IsCompleted = goal.CurrentAmount >= goal.TargetAmount;
            if (goal.IsCompleted && !wasCompleted)
                _gamificationQueue.Enqueue(goal.UserId);

            var updatedGoal = await _savingsRepository.UpdateAsync(goal);
            var role = await GetUserRoleIfNeededAsync(currentUserId);
            return MapToSavingsGoalDto(updatedGoal, currentUserId, role);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> ResetSavingsGoalAsync(int goalId)
    {
        try
        {
            var goal = await _savingsRepository.GetByIdAsync(goalId);
            if (goal == null)
                throw new ArgumentException($"Savings goal with ID {goalId} not found.");

            // Reset clears the current balance only. Allocation history
            // (SavingsAllocation rows) is preserved as an immutable audit
            // trail; the zeroed CurrentAmount makes those rows informational
            // only. Achievements are user-scoped, not goal-scoped, so they
            // stay earned. The goal stays IsActive so it can continue to
            // receive future contributions.
            goal.CurrentAmount = 0;
            if (goal.IsCompleted)
            {
                goal.IsCompleted = false;

                // A completed goal is outside the 100% auto-allocation pool, so its share may have been
                // handed to other goals since. Rejoining with the old share would push the pool past
                // 100%: the period-end allocation would then pay out more than the leftover, and every
                // later edit in the pool would be refused by the 100% check. Keep only what still fits.
                if (goal.IsActive)
                {
                    var othersInPool = await SumAllocationPoolAsync(
                        goal.UserId, goal.Type, goal.FamilyId, excludeGoalId: goal.Id);
                    goal.AllocationPercentage = Math.Min(
                        goal.AllocationPercentage, Math.Max(0m, 100m - othersInPool));
                }
            }

            var updatedGoal = await _savingsRepository.UpdateAsync(goal);
            _logger.LogInformation("Savings goal {GoalId} reset to zero for owner {OwnerUserId}",
                goalId, goal.UserId);
            return MapToSavingsGoalDto(updatedGoal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task DeleteSavingsGoalAsync(int goalId)
    {
        try
        {
            await _savingsRepository.DeleteAsync(goalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> AllocateToGoalAsync(int goalId, ManualAllocationRequest request, int? currentUserId = null)
    {
        try
        {
            // Bound the contribution (QE #615: a ~$1T add-funds 500'd on the decimal→DB write).
            Core.Services.MonetaryLimits.ValidatePositive(request.Amount, "Amount");

            var updatedGoal = await MutateGoalAtomicAsync(goalId, goal =>
            {
                // Manual allocation → no source budget. BudgetId is a nullable FK, so this MUST be
                // null; 0 violated FK_SavingsAllocations_Budgets_BudgetId and 500'd manual add-funds.
                var allocation = new SavingsAllocation
                {
                    SavingsGoalId = goalId,
                    BudgetId = null,
                    AllocatedAmount = request.Amount,
                    LeftoverBudget = request.Amount,
                    AllocationDate = DateTime.UtcNow,
                    Source = request.Source
                };

                goal.CurrentAmount += request.Amount;
                if (goal.CurrentAmount >= goal.TargetAmount)
                    goal.IsCompleted = true;

                return allocation;
            });

            // Check for new achievements
            _gamificationQueue.Enqueue(updatedGoal.UserId);

            // Adding money to a goal is a savings-streak day (💰) for the contributor
            // (fall back to the goal owner for legacy callers that pass no actor).
            await _gamificationService.RecordDailyActivityAsync(
                currentUserId ?? updatedGoal.UserId, "savings_goal");

            var role = await GetUserRoleIfNeededAsync(currentUserId);
            return MapToSavingsGoalDto(updatedGoal, currentUserId, role);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error allocating to savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> WithdrawFromGoalAsync(int goalId, decimal amount, int transactionId)
    {
        try
        {
            if (amount <= 0)
                throw new ArgumentException("Withdrawal amount must be greater than zero.");

            await AdoptUnassignedAccountMoneyIfShortAsync(goalId, amount);

            var updatedGoal = await MutateGoalAtomicAsync(goalId, goal =>
            {
                // Bounds are re-checked against the FRESHLY reloaded goal on every retry attempt, so a
                // concurrent writer that drained the balance can't be over-withdrawn.
                if (amount > goal.CurrentAmount)
                    throw new InvalidOperationException(InsufficientBalanceMessage(goal, amount));

                var allocation = new SavingsAllocation
                {
                    SavingsGoalId = goalId,
                    BudgetId = null,
                    AllocatedAmount = -amount,
                    LeftoverBudget = 0,
                    AllocationDate = DateTime.UtcNow,
                    Source = $"transaction_withdrawal:{transactionId}"
                };

                goal.CurrentAmount -= amount;
                if (goal.CurrentAmount < goal.TargetAmount)
                    goal.IsCompleted = false;

                return allocation;
            });

            _logger.LogInformation("Withdrew {Amount} from savings goal {GoalId} for transaction {TransactionId}",
                amount, goalId, transactionId);

            return MapToSavingsGoalDto(updatedGoal);
        }
        catch (Exception ex) when (ex is not ArgumentException and not InvalidOperationException)
        {
            _logger.LogError(ex, "Error withdrawing from savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> UndoWithdrawalAsync(int goalId, int transactionId)
    {
        try
        {
            decimal restoredAmount = 0;
            var updatedGoal = await MutateGoalRemovalAtomicAsync(goalId, goal =>
            {
                // Find the withdrawal allocation for this transaction
                var allocations = goal.Allocations?
                    .Where(a => a.Source == $"transaction_withdrawal:{transactionId}")
                    .ToList() ?? new List<SavingsAllocation>();

                if (allocations.Count == 0)
                    throw new ArgumentException($"No withdrawal found for transaction {transactionId} on goal {goalId}.");

                // Restore the withdrawn amount
                restoredAmount = allocations.Sum(a => Math.Abs(a.AllocatedAmount));
                goal.CurrentAmount += restoredAmount;

                if (goal.CurrentAmount >= goal.TargetAmount)
                    goal.IsCompleted = true;

                return allocations.Select(a => a.Id).ToList();
            });

            _logger.LogInformation("Undid withdrawal of {Amount} from savings goal {GoalId} for transaction {TransactionId}",
                restoredAmount, goalId, transactionId);

            return MapToSavingsGoalDto(updatedGoal);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error undoing withdrawal from savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> DepositFromTransactionAsync(int goalId, decimal amount, int transactionId)
    {
        try
        {
            if (amount <= 0)
                throw new ArgumentException("Deposit amount must be greater than zero.");

            var updatedGoal = await MutateGoalAtomicAsync(goalId, goal =>
            {
                // Positive allocation record for the audit trail. Distinct source tag so
                // UndoDepositAsync/UndoWithdrawalAsync never match each other's rows.
                var allocation = new SavingsAllocation
                {
                    SavingsGoalId = goalId,
                    BudgetId = null,
                    AllocatedAmount = amount,
                    LeftoverBudget = 0,
                    AllocationDate = DateTime.UtcNow,
                    Source = $"transaction_deposit:{transactionId}"
                };

                goal.CurrentAmount += amount;
                if (goal.CurrentAmount >= goal.TargetAmount)
                    goal.IsCompleted = true;

                return allocation;
            });

            // Reaching a target via a deposit can unlock achievements, same as a manual allocation.
            _gamificationQueue.Enqueue(updatedGoal.UserId);

            // Earmarking a transaction into a goal is money added → savings-streak day (💰).
            // This path carries no actor id, so credit the goal owner.
            await _gamificationService.RecordDailyActivityAsync(updatedGoal.UserId, "savings_goal");

            _logger.LogInformation("Deposited {Amount} into savings goal {GoalId} from transaction {TransactionId}",
                amount, goalId, transactionId);

            return MapToSavingsGoalDto(updatedGoal);
        }
        catch (Exception ex) when (ex is not ArgumentException and not InvalidOperationException)
        {
            _logger.LogError(ex, "Error depositing into savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> UndoDepositAsync(int goalId, int transactionId)
    {
        try
        {
            decimal removedAmount = 0;
            var updatedGoal = await MutateGoalRemovalAtomicAsync(goalId, goal =>
            {
                // Find the deposit allocation(s) for this transaction
                var allocations = goal.Allocations?
                    .Where(a => a.Source == $"transaction_deposit:{transactionId}")
                    .ToList() ?? new List<SavingsAllocation>();

                if (allocations.Count == 0)
                    throw new ArgumentException($"No deposit found for transaction {transactionId} on goal {goalId}.");

                // Remove the deposited amount, clamping at zero so a goal can't go negative
                // if the balance was already spent down by other withdrawals.
                removedAmount = allocations.Sum(a => Math.Abs(a.AllocatedAmount));
                goal.CurrentAmount = Math.Max(0, goal.CurrentAmount - removedAmount);

                if (goal.CurrentAmount < goal.TargetAmount)
                    goal.IsCompleted = false;

                return allocations.Select(a => a.Id).ToList();
            });

            _logger.LogInformation("Undid deposit of {Amount} into savings goal {GoalId} for transaction {TransactionId}",
                removedAmount, goalId, transactionId);

            return MapToSavingsGoalDto(updatedGoal);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error undoing deposit into savings goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task ProcessAutomaticAllocationsAsync(int budgetId)
    {
        try
        {
            _logger.LogInformation("Processing automatic allocations for budget {BudgetId}", budgetId);

            var budget = await _budgetRepository.GetByIdAsync(budgetId);
            if (budget == null) return;

            var leftoverAmount = await CalculateLeftoverBudgetAsync(budgetId);
            if (leftoverAmount <= 0) return;

            // Scope this budget's leftover to matching-type goals only: a Family budget's
            // leftover funds only Family goals, a Personal budget's leftover funds only
            // Personal goals. This mirrors GetSavingsOverviewAsync's projection model
            // (personalLeftover vs familyLeftover) and matches how budgets are classified
            // in GetActivePersonalBudgetAsync/GetPrimaryFamilyBudgetAsync (budget.Type).
            // Without it, EVERY active goal's AllocationPercentage is applied to this one
            // budget's leftover; since personal- and family-goal percentages each sum to
            // their own 100% pool, a single budget could be allocated at up to ~200% and
            // cross-fund the other scope's goals.
            var budgetScope = budget.Type == BudgetType.Family
                ? SavingsGoalType.Family
                : SavingsGoalType.Personal;

            // For a Family budget the leftover must fund EVERY family member's family goals, not just
            // the budget owner's. GetByUserIdAsync returns only owner-owned goals, so other members'
            // family goals (whose percentages count toward the same family-wide 100% pool and toward
            // the overview's projected growth) were silently never funded. Use the family-aware query
            // for family scope; personal budgets stay owner-scoped.
            var userGoals = budgetScope == SavingsGoalType.Family
                ? await _savingsRepository.GetByUserOrFamilyAsync(budget.UserId, budget.FamilyId)
                : await _savingsRepository.GetByUserIdAsync(budget.UserId);
            var activeGoals = userGoals
                .Where(g => g.IsActive && !g.IsCompleted && g.Type == budgetScope)
                .ToList();

            // Per-period idempotency: a goal that already received a budget_surplus allocation from
            // THIS budget in the current period is skipped. The public /allocations/process endpoint
            // has no snapshot gate (unlike BudgetPeriodService.ResetBudgetAsync), so a double-submit
            // or client retry would otherwise stack duplicate allocations and fabricate savings balance.
            bool AllocatedThisPeriod(SavingsGoal g) => g.Allocations != null && g.Allocations.Any(a =>
                a.Source == "budget_surplus"
                && a.BudgetId == budgetId
                && a.AllocationDate >= budget.CurrentPeriodStart);

            // The 100% cap is enforced on create/edit, but a completed goal that reopens (a withdrawal
            // or undone deposit pulls it back under target) rejoins the pool with its old share, so
            // the shares can add up to more than 100%. Divide by the real total in that case: each
            // goal keeps its proportion and the payout never exceeds the leftover. Goals already paid
            // this period count too: one that the payment finished drops out of activeGoals, and a
            // retry after a partial run must not divide the rest by a smaller total.
            var poolDivisor = Math.Max(100m, userGoals
                .Where(g => activeGoals.Contains(g) || AllocatedThisPeriod(g))
                .Sum(g => g.AllocationPercentage));

            foreach (var goal in activeGoals)
            {
                if (AllocatedThisPeriod(goal)) continue;

                // Round down to whole cents: balances and the ledger are stored to the cent, and
                // rounding each share up could pay out a cent or two more than the leftover.
                var allocationAmount = Math.Round(
                    leftoverAmount * goal.AllocationPercentage / poolDivisor, 2, MidpointRounding.ToZero);
                if (allocationAmount <= 0) continue;

                // Atomic ledger+balance write per goal (single SaveChanges, xmin retry) so a
                // concurrent writer can't orphan the allocation row from CurrentAmount.
                await MutateGoalAtomicAsync(goal.Id, g =>
                {
                    var allocation = new SavingsAllocation
                    {
                        SavingsGoalId = g.Id,
                        BudgetId = budgetId,
                        AllocatedAmount = allocationAmount,
                        LeftoverBudget = leftoverAmount,
                        AllocationDate = DateTime.UtcNow,
                        Source = "budget_surplus"
                    };

                    g.CurrentAmount += allocationAmount;
                    if (g.CurrentAmount >= g.TargetAmount)
                        g.IsCompleted = true;

                    return allocation;
                });
            }

            // Gamification (achievements + streaks) is evaluated off-thread.
            _gamificationQueue.Enqueue(budget.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing automatic allocations for budget {BudgetId}", budgetId);
            throw;
        }
    }

    public async Task<SavingsGoalDto> CalculateGoalProgressAsync(int goalId)
    {
        try
        {
            var goal = await _savingsRepository.GetByIdAsync(goalId);
            if (goal == null)
                throw new ArgumentException($"Savings goal with ID {goalId} not found.");

            return MapToSavingsGoalDto(goal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating progress for goal {GoalId}", goalId);
            throw;
        }
    }

    public async Task<decimal> CalculateLeftoverBudgetAsync(int budgetId)
    {
        try
        {
            var budget = await _budgetRepository.GetByIdAsync(budgetId);
            if (budget == null) return 0;

            // Only sum EXPENSE categories (not income categories)
            // Income categories are those marked as IsIncome or that have CategoryId in the income range (1-4)
            var totalExpenseAllocated = budget.Categories
                .Where(c => !c.IsIncome && !(c.CategoryId >= 1 && c.CategoryId <= 4))
                .Sum(c => c.AllocatedAmount);
            
            // Leftover budget is the income that wasn't allocated to any EXPENSE budget category
            // This is the money available for savings goals
            var leftoverFromIncome = budget.TotalIncome - totalExpenseAllocated;

            return Math.Max(0, leftoverFromIncome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating leftover budget for budget {BudgetId}", budgetId);
            throw;
        }
    }

    // Savings achievements are now sourced from the unified gamification system (single
    // source of truth). These methods adapt the unified savings-category achievements to
    // the savings-overview view shape; they no longer maintain a separate store.
    public async Task<IEnumerable<SavingsAchievementDto>> GetUserAchievementsAsync(int userId)
    {
        var achievements = await _gamificationService.GetAchievementsAsync(userId);
        return (achievements ?? new List<UserAchievementDto>())
            .Where(a => a.Category == "savings")
            .OrderByDescending(a => a.EarnedAt)
            .Select(MapToSavingsAchievementDto)
            .ToList();
    }

    public async Task<IEnumerable<SavingsAchievementDto>> CheckForNewAchievementsAsync(int userId)
    {
        // Evaluation runs through the unified gamification pipeline; awards surface via the
        // dashboard's new-achievement flow, so nothing is returned here to celebrate twice.
        await _gamificationService.CheckAndAwardAchievementsAsync(userId);
        return Array.Empty<SavingsAchievementDto>();
    }

    // Private helper methods

    /// <summary>
    /// Runs a savings-goal balance mutation atomically: reloads the goal, lets <paramref name="mutate"/>
    /// adjust it and build the SavingsAllocation ledger row, then persists BOTH in one transaction.
    /// On an xmin optimistic-concurrency conflict it reloads fresh rows and re-applies (so bounds
    /// checks in <paramref name="mutate"/> re-run against the winning writer's state), mirroring
    /// BudgetTransferService's retry loop. This is what keeps CurrentAmount and the allocation ledger
    /// from diverging under concurrent add-funds / transfer / deficit-pull writes.
    /// </summary>
    private async Task<SavingsGoal> MutateGoalAtomicAsync(int goalId, Func<SavingsGoal, SavingsAllocation> mutate)
    {
        for (var attempt = 1; ; attempt++)
        {
            var goal = await _savingsRepository.GetByIdAsync(goalId)
                ?? throw new ArgumentException($"Savings goal with ID {goalId} not found.");

            var allocation = mutate(goal);
            try
            {
                await _savingsRepository.SaveGoalWithAllocationAsync(goal, allocation);
                return goal;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                _logger.LogWarning(
                    "Concurrency conflict mutating savings goal {GoalId} (attempt {Attempt}/{Max}); reloading",
                    goalId, attempt, MaxConcurrencyRetries);
                _savingsRepository.DetachAll();
            }
        }
    }

    /// <summary>
    /// Same as <see cref="MutateGoalAtomicAsync"/> but for the undo paths: <paramref name="mutate"/>
    /// adjusts the balance and returns the allocation ids to delete; the deletes and the balance update
    /// commit together (one transaction) with the same xmin retry.
    /// </summary>
    private async Task<SavingsGoal> MutateGoalRemovalAtomicAsync(int goalId, Func<SavingsGoal, List<int>> mutate)
    {
        for (var attempt = 1; ; attempt++)
        {
            var goal = await _savingsRepository.GetByIdAsync(goalId)
                ?? throw new ArgumentException($"Savings goal with ID {goalId} not found.");

            var allocationIds = mutate(goal);
            try
            {
                await _savingsRepository.SaveGoalRemovingAllocationsAsync(goal, allocationIds);
                return goal;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                _logger.LogWarning(
                    "Concurrency conflict reversing savings goal {GoalId} (attempt {Attempt}/{Max}); reloading",
                    goalId, attempt, MaxConcurrencyRetries);
                _savingsRepository.DetachAll();
            }
        }
    }

    /// <summary>Resolves the caller's family role for ownership/contribution flags, only when a user id is supplied.</summary>
    private async Task<FamilyMemberRole?> GetUserRoleIfNeededAsync(int? currentUserId)
    {
        if (!currentUserId.HasValue)
            return null;
        return await _familyAuthService.GetUserRoleAsync(currentUserId.Value);
    }

    /// <summary>Ledger source for a one-tap sync of a sole goal to its account balance.</summary>
    public const string SyncAllocationSource = "account_sync";

    /// <summary>Ledger source for a redistribution across goals sharing one account.</summary>
    public const string ReconcileAllocationSource = "account_reconcile";

    public async Task<SavingsGoalDto> SyncGoalToAccountAsync(int goalId, int currentUserId)
    {
        var goal = await _savingsRepository.GetByIdAsync(goalId)
            ?? throw new ArgumentException($"Savings goal with ID {goalId} not found.");

        if (!goal.AccountId.HasValue)
            throw new InvalidOperationException("This goal is not linked to an account.");

        var account = await ValidateAccountLinkAsync(goal.AccountId.Value, currentUserId);
        await EnsureCanContributeAsync(goal, currentUserId);

        // The single-goal shortcut is only honest when the goal really is alone on the account.
        // With siblings, setting this one to the whole balance would claim their money for it.
        // Re-checked here rather than trusted from the client, which cannot see other members' goals.
        var siblings = await _savingsRepository.GetGoalCountByAccountAsync(goal.AccountId.Value);
        if (siblings > 1)
        {
            throw new InvalidOperationException(
                "More than one goal is held in this account — reconcile them together instead.");
        }

        var delta = account.CurrentBalance - goal.CurrentAmount;
        if (delta == 0) return await BuildGoalDtoAsync(goal, currentUserId);

        var updated = await MutateGoalAtomicAsync(goalId, g =>
        {
            var allocation = BuildReconcileAllocation(g.Id, account.CurrentBalance - g.CurrentAmount, SyncAllocationSource);
            g.CurrentAmount = account.CurrentBalance;
            // Symmetric: a sync DOWN must be able to un-complete a goal, or a goal whose money was
            // spent stays badged as achieved forever.
            g.IsCompleted = g.CurrentAmount >= g.TargetAmount;
            return allocation;
        });

        // Deliberately no gamification. This is a correction, not a deposit — awarding "you saved
        // $2,000!" for money that was already sitting there is a lie, and sync/unsync would farm it.
        _logger.LogInformation(
            "Synced goal {GoalId} to account {AccountId} balance ({Delta:+0.00;-0.00})",
            goalId, goal.AccountId, delta);

        return await BuildGoalDtoAsync(updated, currentUserId);
    }

    public async Task<ReconcileResultDto> ReconcileAccountAsync(
        int accountId, ReconcileAccountRequest request, int currentUserId)
    {
        var account = await ValidateAccountLinkAsync(accountId, currentUserId);

        var user = await _userRepository.GetByIdAsync(currentUserId);
        var allGoals = (await _savingsRepository.GetByUserOrFamilyAsync(currentUserId, user?.FamilyId))
            .Where(g => g.AccountId == accountId)
            .ToList();

        var changes = new List<(SavingsGoal, SavingsAllocation)>();
        foreach (var adjustment in request.Adjustments)
        {
            var goal = allGoals.FirstOrDefault(g => g.Id == adjustment.GoalId)
                ?? throw new ArgumentException(
                    $"Goal {adjustment.GoalId} is not held in account {accountId}.");

            await EnsureCanContributeAsync(goal, currentUserId);

            if (adjustment.NewAmount < 0)
                throw new ArgumentException("A goal balance cannot be negative.");
            Core.Services.MonetaryLimits.ValidatePositive(adjustment.NewAmount + 1, "Amount");

            var delta = adjustment.NewAmount - goal.CurrentAmount;
            if (delta == 0) continue;

            goal.CurrentAmount = adjustment.NewAmount;
            goal.IsCompleted = goal.CurrentAmount >= goal.TargetAmount;
            changes.Add((goal, BuildReconcileAllocation(goal.Id, delta, ReconcileAllocationSource)));
        }

        if (changes.Count > 0)
        {
            try
            {
                // One transaction across every goal: a reconcile that lands halfway would leave
                // the account MORE out of step than before the user tried to fix it.
                await _savingsRepository.SaveGoalsWithAllocationsAsync(changes);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Deliberately NOT retried, unlike the single-goal paths. Those apply a delta, which
                // is still correct against a moved balance. These are absolute amounts the user
                // approved after seeing a specific set of numbers — replaying them onto a baseline
                // that changed underneath would commit a distribution they never agreed to.
                _savingsRepository.DetachAll();
                _logger.LogWarning("Concurrency conflict reconciling account {AccountId}", accountId);
                throw new InvalidOperationException(
                    "These goals changed while you were reconciling. Reopen and try again.");
            }
        }

        // Re-read so the response reflects committed state rather than our in-memory guess.
        var refreshed = (await _savingsRepository.GetByUserOrFamilyAsync(currentUserId, user?.FamilyId))
            .Where(g => g.AccountId == accountId)
            .ToList();
        var allocatedByAccount = await GetAccountAllocationsAsync(refreshed);
        var pendingByGoal = await GetPendingDrawTotalsAsync(refreshed);
        var role = await GetUserRoleIfNeededAsync(currentUserId);
        var allocated = allocatedByAccount.TryGetValue(accountId, out var t) ? t : 0m;

        return new ReconcileResultDto
        {
            AccountBalance = account.CurrentBalance,
            AllocatedToGoals = allocated,
            Unallocated = account.CurrentBalance - allocated,
            Goals = refreshed
                .Select(g => MapToSavingsGoalDto(g, currentUserId, role, allocatedByAccount, pendingByGoal))
                .ToList(),
        };
    }

    /// <summary>
    /// Ledger row for a correction. <paramref name="delta"/> is signed: a reconcile can move a
    /// goal DOWN, and recording that as a negative keeps the history a faithful account of why a
    /// balance changed rather than only ever showing deposits.
    /// </summary>
    /// <summary>
    /// A linked goal's envelope drifts below the real balance between manual syncs (interest,
    /// deposits, a sync nobody tapped). When a withdrawal is bigger than the envelope but the
    /// account holds money no goal has claimed, adopt that money into this goal first, exactly
    /// as the user would by tapping Sync, instead of refusing with "insufficient" while the bank
    /// plainly has it. Alone on the account the goal adopts everything unassigned (a sync); with
    /// siblings it adopts only the shortfall, because the rest may be theirs to claim. Money
    /// that another goal already claims is never touched.
    /// </summary>
    private async Task AdoptUnassignedAccountMoneyIfShortAsync(int goalId, decimal amount)
    {
        var goal = await _savingsRepository.GetByIdAsync(goalId);
        if (goal == null || !goal.AccountId.HasValue || amount <= goal.CurrentAmount)
            return;

        var account = await _accountRepository.GetByIdAsync(goal.AccountId.Value);
        if (account == null) return;

        var totals = await _savingsRepository.GetGoalTotalsByAccountAsync(new[] { goal.AccountId.Value });
        var allocated = totals.TryGetValue(goal.AccountId.Value, out var t) ? t : goal.CurrentAmount;
        var unassigned = account.CurrentBalance - allocated;
        var shortfall = amount - goal.CurrentAmount;
        if (unassigned < shortfall) return;

        var siblings = await _savingsRepository.GetGoalCountByAccountAsync(goal.AccountId.Value);
        var adopt = siblings <= 1 ? unassigned : shortfall;
        if (adopt <= 0) return;

        await MutateGoalAtomicAsync(goalId, g =>
        {
            var allocation = BuildReconcileAllocation(g.Id, adopt, SyncAllocationSource);
            g.CurrentAmount += adopt;
            g.IsCompleted = g.CurrentAmount >= g.TargetAmount;
            return allocation;
        });

        _logger.LogInformation(
            "Adopted {Adopt} unassigned from account {AccountId} into goal {GoalId} before a {Amount} withdrawal",
            adopt, goal.AccountId, goalId, amount);
    }

    private static string InsufficientBalanceMessage(SavingsGoal goal, decimal amount)
    {
        var basic = $"Insufficient savings balance. Current: {goal.CurrentAmount:C}, Requested: {amount:C}";
        if (goal.Account == null) return basic;
        return basic + $" {goal.Account.Name} holds {goal.Account.CurrentBalance:C}, but other goals claim the rest; reconcile the account to move money into this goal.";
    }

    /// <summary>
    /// Earmarks dated on or after the goal's last sync/reconcile. A sync asserts that the bank
    /// and the envelope agreed at that moment, so anything earmarked before it has already been
    /// absorbed into the balance and is no longer "in flight".
    /// </summary>
    private static DateTime PendingDrawsSince(SavingsGoal goal)
    {
        var lastSync = goal.Allocations?
            .Where(a => a.Source == SyncAllocationSource || a.Source == ReconcileAllocationSource)
            .Select(a => (DateTime?)a.AllocationDate)
            .Max();
        return lastSync?.Date ?? DateTime.MinValue;
    }

    private static PendingDrawDto ToPendingDraw(Transaction t) => new()
    {
        TransactionId = t.Id,
        Date = t.Date,
        Amount = Math.Abs(t.Amount),
        Title = t.DisplayName,
    };

    /// <summary>Per-goal in-flight totals for the linked goals in <paramref name="goals"/>; unlinked goals are absent.</summary>
    private async Task<Dictionary<int, decimal>> GetPendingDrawTotalsAsync(IEnumerable<SavingsGoal> goals)
    {
        var linked = goals.Where(g => g.AccountId.HasValue).ToList();
        if (linked.Count == 0) return new Dictionary<int, decimal>();

        var uncovered = await _transactionRepository.GetUncoveredGoalWithdrawalsAsync(linked.Select(g => g.Id));
        var since = linked.ToDictionary(g => g.Id, PendingDrawsSince);
        var totals = linked.ToDictionary(g => g.Id, _ => 0m);
        foreach (var t in uncovered)
        {
            if (t.SavingsGoalId is int gid && since.TryGetValue(gid, out var s) && t.Date >= s)
                totals[gid] += Math.Abs(t.Amount);
        }
        return totals;
    }

    public async Task<IReadOnlyList<PendingDrawDto>> GetPendingDrawsAsync(int goalId)
    {
        var goal = await _savingsRepository.GetByIdAsync(goalId);
        if (goal == null || !goal.AccountId.HasValue) return Array.Empty<PendingDrawDto>();

        var since = PendingDrawsSince(goal);
        return (await _transactionRepository.GetUncoveredGoalWithdrawalsAsync(new[] { goalId }))
            .Where(t => t.Date >= since)
            .OrderBy(t => t.Date)
            .Select(ToPendingDraw)
            .ToList();
    }

    public async Task<int> AutoCoverPendingDrawsAsync(IReadOnlyCollection<Transaction> newTransactions)
    {
        // Only money leaving an account can settle a draw; pending holds still change amount.
        var outflows = newTransactions
            .Where(t => t.Amount < 0 && !t.IsPending && t.ParentTransactionId == null)
            .ToList();
        if (outflows.Count == 0) return 0;

        var settled = 0;
        foreach (var byAccount in outflows.GroupBy(t => t.AccountId))
        {
            var goals = (await _savingsRepository.GetByAccountIdAsync(byAccount.Key)).ToList();
            if (goals.Count == 0) continue;

            foreach (var goal in goals)
            {
                var since = PendingDrawsSince(goal);
                var pending = (await _transactionRepository.GetUncoveredGoalWithdrawalsAsync(new[] { goal.Id }))
                    .Where(t => t.Date >= since)
                    .OrderBy(t => t.Date)
                    .ToList();
                if (pending.Count == 0) continue;

                foreach (var transfer in byAccount.OrderBy(t => t.Date))
                {
                    var moved = Math.Abs(transfer.Amount);
                    var pendingTotal = pending.Sum(p => Math.Abs(p.Amount));
                    List<Transaction> matched;
                    if (pendingTotal == moved)
                    {
                        matched = pending.ToList();
                    }
                    else
                    {
                        var single = pending.FirstOrDefault(p => Math.Abs(p.Amount) == moved);
                        if (single == null) continue;
                        matched = new List<Transaction> { single };
                    }

                    foreach (var draw in matched)
                    {
                        draw.CoveringTransferTransactionId = transfer.Id;
                        draw.UpdatedAt = DateTime.UtcNow;
                        await _transactionRepository.UpdateAsync(draw);
                        pending.Remove(draw);
                        settled++;
                    }
                    // The transfer is now explained; keep it out of the suspected-transfer queue.
                    transfer.TransferReviewedAt ??= DateTime.UtcNow;
                    await _transactionRepository.UpdateAsync(transfer);

                    _logger.LogInformation(
                        "Transfer {TransferId} ({Moved}) on account {AccountId} auto-covered {Count} draw(s) on goal {GoalId}",
                        transfer.Id, moved, byAccount.Key, matched.Count, goal.Id);
                    if (pending.Count == 0) break;
                }
            }
        }
        return settled;
    }

    private static SavingsAllocation BuildReconcileAllocation(int goalId, decimal delta, string source) =>
        new()
        {
            SavingsGoalId = goalId,
            BudgetId = null,
            AllocatedAmount = delta,
            LeftoverBudget = 0m,
            AllocationDate = DateTime.UtcNow,
            Source = source,
        };

    /// <summary>Reuses the contribution permission rule: if you may not add to a goal, you may not correct it.</summary>
    private async Task EnsureCanContributeAsync(SavingsGoal goal, int currentUserId)
    {
        var role = await _familyAuthService.GetUserRoleAsync(currentUserId);
        var isOwner = goal.UserId == currentUserId;
        var canContribute = goal.Type == SavingsGoalType.Personal
            ? isOwner
            : isOwner || (role.HasValue && role.Value != FamilyMemberRole.ReadOnly);

        if (!canContribute)
        {
            throw new UnauthorizedAccessException(
                $"User {currentUserId} cannot modify savings goal {goal.Id}.");
        }
    }

    private async Task<SavingsGoalDto> BuildGoalDtoAsync(SavingsGoal goal, int currentUserId)
    {
        var allocatedByAccount = await GetAccountAllocationsAsync(new[] { goal });
        var pendingByGoal = await GetPendingDrawTotalsAsync(new[] { goal });
        var role = await GetUserRoleIfNeededAsync(currentUserId);
        return MapToSavingsGoalDto(goal, currentUserId, role, allocatedByAccount, pendingByGoal);
    }

    /// <summary>
    /// Confirms the caller may link this account, and returns it. Throws if they may not.
    /// </summary>
    /// <remarks>
    /// Scoped by user OR family, matching every other data read: without this an attacker could
    /// point a goal at someone else's account id and the reconciliation fields would hand back
    /// that account's name and balance — an IDOR that leaks real balances through a savings goal.
    /// </remarks>
    private async Task<Account> ValidateAccountLinkAsync(int accountId, int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        var accounts = await _accountRepository.GetByUserOrFamilyAsync(userId, user?.FamilyId);
        var account = accounts.FirstOrDefault(a => a.Id == accountId);
        if (account == null)
        {
            throw new UnauthorizedAccessException(
                $"Account {accountId} does not belong to user {userId}.");
        }

        if (!LinkableAccountTypes.Contains(account.Type))
        {
            throw new InvalidOperationException(
                $"A savings goal cannot be held in a {account.Type} account.");
        }

        return account;
    }

    /// <summary>
    /// Account types that can hold savings. Deliberately excludes CreditCard (its balance is debt,
    /// so syncing a goal to it would read money owed as money saved) and Investment (a fluctuating
    /// market value isn't a balance you reconcile an envelope against).
    /// </summary>
    private static readonly HashSet<AccountType> LinkableAccountTypes = new()
    {
        AccountType.Checking,
        AccountType.Savings,
        AccountType.Cash,
    };

    /// <summary>
    /// Per-account totals for every linked account in <paramref name="goals"/>, so a list of goals
    /// costs one extra query rather than one per goal.
    /// </summary>
    private async Task<Dictionary<int, decimal>> GetAccountAllocationsAsync(IEnumerable<SavingsGoal> goals)
    {
        var accountIds = goals.Where(g => g.AccountId.HasValue)
                              .Select(g => g.AccountId!.Value)
                              .Distinct()
                              .ToList();
        return accountIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _savingsRepository.GetGoalTotalsByAccountAsync(accountIds);
    }

    private SavingsGoalDto MapToSavingsGoalDto(
        SavingsGoal goal,
        int? currentUserId = null,
        FamilyMemberRole? userFamilyRole = null,
        IReadOnlyDictionary<int, decimal>? allocatedByAccount = null,
        IReadOnlyDictionary<int, decimal>? pendingByGoal = null)
    {
        var progressPercentage = goal.TargetAmount > 0 
            ? Math.Min((goal.CurrentAmount / goal.TargetAmount) * 100, 100) 
            : 0;
        
        var remainingAmount = Math.Max(goal.TargetAmount - goal.CurrentAmount, 0);
        var daysRemaining = goal.TargetDate.HasValue 
            ? Math.Max((goal.TargetDate.Value - DateTime.UtcNow).Days, 0) 
            : (int?)null;
        
        var dailyRequiredSavings = daysRemaining.HasValue && daysRemaining > 0 
            ? remainingAmount / daysRemaining.Value 
            : 0;
        
        // On-track is a PACE comparison, not a flat 25%/30-day short-circuit (which reported a
        // $0-of-$10k goal due in 60 days as "on_track"). Compare the current balance against the
        // linear expectation over [CreatedAt, TargetDate]. No target date => can't be "behind".
        bool isOnTrack;
        if (goal.IsCompleted || progressPercentage >= 100)
        {
            isOnTrack = true;
        }
        else if (!goal.TargetDate.HasValue)
        {
            isOnTrack = true;
        }
        else
        {
            var totalDuration = (goal.TargetDate.Value - goal.CreatedAt).TotalDays;
            if (totalDuration <= 0)
            {
                // Degenerate window (target date at/before creation): only "on track" if already funded.
                isOnTrack = goal.CurrentAmount >= goal.TargetAmount;
            }
            else
            {
                var elapsed = (DateTime.UtcNow - goal.CreatedAt).TotalDays;
                var expectedFraction = (decimal)Math.Clamp(elapsed / totalDuration, 0, 1);
                var expectedAmount = goal.TargetAmount * expectedFraction;
                isOnTrack = goal.CurrentAmount >= expectedAmount;
            }
        }

        var progressStatus = goal.IsCompleted ? "completed" :
                           progressPercentage >= 100 ? "completed" :
                           isOnTrack ? "on_track" : "behind";

        // Determine ownership - user is owner if they created the goal
        var isOwner = currentUserId.HasValue && goal.UserId == currentUserId.Value;
        var ownerName = goal.User?.FirstName ?? "Unknown";
        
        // Determine if user can contribute to this goal
        // - Personal goals: only owner can contribute
        // - Family goals: any family member with Member role or higher can contribute (not ReadOnly)
        // - Completed goals: no one can contribute
        var canContribute = false;
        if (!goal.IsCompleted && currentUserId.HasValue)
        {
            if (goal.Type == SavingsGoalType.Personal)
            {
                // Personal goal - only owner can contribute
                canContribute = isOwner;
            }
            else if (goal.Type == SavingsGoalType.Family)
            {
                // Family goal - owner always can, non-ReadOnly family members can contribute
                canContribute = isOwner || (userFamilyRole.HasValue && userFamilyRole.Value != FamilyMemberRole.ReadOnly);
            }
        }

        // Reconciliation is only meaningful when the goal is linked AND the account was loaded.
        // Allocated falls back to this goal's own amount rather than 0 when the totals lookup is
        // absent, so a missing dictionary understates nothing — it can only report LESS drift,
        // never invent it.
        decimal? accountBalance = null, accountAllocated = null, accountUnallocated = null, pendingTransfer = null;
        if (goal.AccountId.HasValue && goal.Account != null)
        {
            accountBalance = goal.Account.CurrentBalance;
            accountAllocated =
                allocatedByAccount != null && allocatedByAccount.TryGetValue(goal.AccountId.Value, out var total)
                    ? total
                    : goal.CurrentAmount;
            accountUnallocated = accountBalance - accountAllocated;
            pendingTransfer = pendingByGoal != null && pendingByGoal.TryGetValue(goal.Id, out var pending)
                ? pending
                : null;
        }

        return new SavingsGoalDto
        {
            Id = goal.Id,
            UpdatedAt = goal.UpdatedAt,
            AccountId = goal.AccountId,
            AccountName = goal.Account?.Name,
            AccountBalance = accountBalance,
            AccountAllocatedToGoals = accountAllocated,
            AccountUnallocated = accountUnallocated,
            PendingTransferAmount = pendingTransfer,
            Type = goal.Type,
            UserId = goal.UserId,
            FamilyId = goal.FamilyId,
            OwnerName = ownerName,
            IsOwner = isOwner,
            CanContribute = canContribute,
            Name = goal.Name,
            Description = goal.Description,
            TargetAmount = goal.TargetAmount,
            CurrentAmount = goal.CurrentAmount,
            AllocationPercentage = goal.AllocationPercentage,
            TargetDate = goal.TargetDate,
            IconName = goal.IconName,
            ColorTheme = goal.ColorTheme,
            IsActive = goal.IsActive,
            IsCompleted = goal.IsCompleted,
            CountWithdrawalsTowardBudget = goal.CountWithdrawalsTowardBudget,
            ProgressPercentage = progressPercentage,
            RemainingAmount = remainingAmount,
            DaysRemaining = daysRemaining,
            DailyRequiredSavings = dailyRequiredSavings,
            IsOnTrack = isOnTrack,
            ProgressStatus = progressStatus,
            CreatedAt = goal.CreatedAt
        };
    }

    // Adapts a unified gamification achievement to the savings-overview view shape.
    private static SavingsAchievementDto MapToSavingsAchievementDto(UserAchievementDto a)
    {
        return new SavingsAchievementDto
        {
            Id = a.Id,
            AchievementType = a.AchievementKey,
            Title = a.Title,
            Description = a.Description,
            BadgeIcon = a.Icon,
            BadgeColor = string.Empty,
            EarnedDate = a.EarnedAt,
            IsNotified = !a.IsNew
        };
    }

    // Recent savings-related achievements, sourced from the unified gamification system
    // (single source of truth) rather than a separate savings-only store.
    private async Task<List<SavingsAchievementDto>> GetRecentAchievementsAsync(int userId)
    {
        var achievements = await _gamificationService.GetAchievementsAsync(userId);
        return (achievements ?? new List<UserAchievementDto>())
            .Where(a => a.Category == "savings" && a.EarnedAt >= DateTime.UtcNow.AddDays(-30))
            .OrderByDescending(a => a.EarnedAt)
            .Take(5)
            .Select(MapToSavingsAchievementDto)
            .ToList();
    }

    private async Task ValidateAllocationPercentageAsync(
        int userId, 
        decimal newPercentage, 
        SavingsGoalType goalType = SavingsGoalType.Personal, 
        int? familyId = null,
        int? excludeGoalId = null,
        decimal? currentPercentage = null)
    {
        // A negative percentage passes the "sum <= 100" test (it only shrinks the total) yet corrupts
        // the savings-overview projections (leftover * pct/100 subtracts from projected growth). Reject
        // the whole invalid [0,100] range server-side for both create and update.
        if (newPercentage < 0 || newPercentage > 100)
            throw new InvalidOperationException("Allocation percentage must be between 0 and 100.");

        var totalExisting = await SumAllocationPoolAsync(userId, goalType, familyId, excludeGoalId);

        // A pool can already be over 100% when a finished goal reopens (a purchase or an undone
        // deposit takes it back under target) with its old share. Payouts scale that down, so it's
        // safe, but refusing every edit would lock the user out: only refuse an edit that raises
        // this goal's share (or a new goal's).
        var raises = currentPercentage is null || newPercentage > currentPercentage.Value;
        if (totalExisting + newPercentage > 100 && raises)
        {
            var goalTypeLabel = goalType == SavingsGoalType.Family ? "family" : "personal";
            throw new InvalidOperationException($"Total allocation percentage for {goalTypeLabel} goals cannot exceed 100%.");
        }
    }

    /// <summary>
    /// Sum of AllocationPercentage over the goals that share one auto-allocation pool: the user's
    /// personal goals, or the whole family's family goals. Only active, uncompleted goals count,
    /// because those are the only ones auto-allocation funds.
    /// </summary>
    private async Task<decimal> SumAllocationPoolAsync(
        int userId,
        SavingsGoalType goalType,
        int? familyId,
        int? excludeGoalId = null)
    {
        IEnumerable<SavingsGoal> existingGoals;

        if (goalType == SavingsGoalType.Family && familyId.HasValue)
        {
            var allGoals = await _savingsRepository.GetByUserOrFamilyAsync(userId, familyId);
            existingGoals = allGoals.Where(g => g.Type == SavingsGoalType.Family);
        }
        else
        {
            var allGoals = await _savingsRepository.GetByUserIdAsync(userId);
            existingGoals = allGoals.Where(g => g.Type == SavingsGoalType.Personal);
        }

        return existingGoals
            .Where(g => g.IsActive && !g.IsCompleted && g.Id != excludeGoalId)
            .Sum(g => g.AllocationPercentage);
    }

}
