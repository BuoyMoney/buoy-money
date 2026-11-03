using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

public interface ISavingsService
{
    // Overview and dashboard
    Task<SavingsOverviewDto> GetSavingsOverviewAsync(int userId);
    
    // Goal management
    Task<IEnumerable<SavingsGoalDto>> GetSavingsGoalsAsync(int userId);
    // currentUserId (optional) populates IsOwner/CanContribute on the returned DTO so the caller's
    // own add-funds/edit responses don't hide the owner's controls until a full overview reload.
    Task<SavingsGoalDto?> GetSavingsGoalAsync(int goalId, int? currentUserId = null);
    Task<SavingsGoalDto> CreateSavingsGoalAsync(int userId, CreateSavingsGoalRequest request);
    Task<SavingsGoalDto> UpdateSavingsGoalAsync(int goalId, UpdateSavingsGoalRequest request, int? currentUserId = null);
    Task<SavingsGoalDto> ResetSavingsGoalAsync(int goalId);
    Task DeleteSavingsGoalAsync(int goalId);

    // Allocation management
    Task<SavingsGoalDto> AllocateToGoalAsync(int goalId, ManualAllocationRequest request, int? currentUserId = null);

    /// <summary>
    /// Sets a goal's balance to its linked account's balance. Only valid when the goal is the ONLY
    /// one held in that account — with siblings this would claim their money, so it throws.
    /// </summary>
    Task<SavingsGoalDto> SyncGoalToAccountAsync(int goalId, int currentUserId);

    /// <summary>
    /// Redistributes balances across the goals sharing one account, in a single transaction.
    /// Does not require the result to match the balance exactly: money no goal claims is a
    /// legitimate end state, so a leftover surplus is allowed to stand.
    /// </summary>
    Task<ReconcileResultDto> ReconcileAccountAsync(int accountId, ReconcileAccountRequest request, int currentUserId);
    Task ProcessAutomaticAllocationsAsync(int budgetId);
    
    // Withdrawal management (expense earmark: money OUT of the goal)
    Task<SavingsGoalDto> WithdrawFromGoalAsync(int goalId, decimal amount, int transactionId);

    /// <summary>
    /// Earmarked withdrawals on a linked goal, made since the last account sync/reconcile, that
    /// no transfer out of the account has covered yet. Empty for an unlinked goal.
    /// </summary>
    Task<IReadOnlyList<PendingDrawDto>> GetPendingDrawsAsync(int goalId);

    /// <summary>
    /// Ingest hook: when a money-out transaction lands on an account that holds linked goals and
    /// its amount equals one pending draw (or all of them together), tie the draws to it. Returns
    /// how many draws were settled. Never throws for a data problem; it just settles nothing.
    /// </summary>
    Task<int> AutoCoverPendingDrawsAsync(IReadOnlyCollection<Transaction> newTransactions);
    Task<SavingsGoalDto> UndoWithdrawalAsync(int goalId, int transactionId);

    // Deposit management (income earmark: money INTO the goal)
    Task<SavingsGoalDto> DepositFromTransactionAsync(int goalId, decimal amount, int transactionId);
    Task<SavingsGoalDto> UndoDepositAsync(int goalId, int transactionId);
    
    // Progress calculations
    Task<SavingsGoalDto> CalculateGoalProgressAsync(int goalId);
    Task<decimal> CalculateLeftoverBudgetAsync(int budgetId);
    
    // Achievement system
    Task<IEnumerable<SavingsAchievementDto>> CheckForNewAchievementsAsync(int userId);
    Task<IEnumerable<SavingsAchievementDto>> GetUserAchievementsAsync(int userId);
}
