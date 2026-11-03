using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

public interface ISavingsRepository
{
    // SavingsGoal CRUD operations
    Task<SavingsGoal?> GetByIdAsync(int id);
    Task<IEnumerable<SavingsGoal>> GetByUserIdAsync(int userId);
    Task<IEnumerable<SavingsGoal>> GetByUserOrFamilyAsync(int userId, int? familyId);
    /// <summary>
    /// Total <c>CurrentAmount</c> promised to goals, per account, for the given accounts.
    /// </summary>
    /// <remarks>
    /// Deliberately sums EVERY goal pointing at an account, including goals owned by other family
    /// members. An account balance can only be honestly compared against everything claiming it —
    /// summing just the caller's own goals would report money as unallocated when a family goal
    /// has already spoken for it. Accounts with no goals are absent from the result, not zero.
    /// </remarks>
    Task<Dictionary<int, decimal>> GetGoalTotalsByAccountAsync(IEnumerable<int> accountIds);

    /// <summary>
    /// How many goals are held in this account, across ALL owners.
    /// </summary>
    /// <remarks>
    /// Counts family members' goals too. The one-tap sync is only honest when a goal is genuinely
    /// alone on its account, and the caller cannot see goals belonging to others — so trusting a
    /// caller-scoped count would let the sync claim a sibling's money.
    /// </remarks>
    Task<int> GetGoalCountByAccountAsync(int accountId);
    /// <summary>Every goal (any owner) held in this account, with allocations loaded.</summary>
    Task<IEnumerable<SavingsGoal>> GetByAccountIdAsync(int accountId);

    Task<SavingsGoal> CreateAsync(SavingsGoal savingsGoal);
    Task<SavingsGoal> UpdateAsync(SavingsGoal savingsGoal);
    Task DeleteAsync(int id);
    
    // SavingsAllocation operations
    Task<SavingsAllocation> CreateAllocationAsync(SavingsAllocation allocation);
    Task<IEnumerable<SavingsAllocation>> GetAllocationsByGoalIdAsync(int goalId);
    Task<IEnumerable<SavingsAllocation>> GetAllocationsByUserIdAsync(int userId);
    Task DeleteAllocationAsync(int allocationId);

    // Atomic ledger+balance mutations. The allocation row and the goal's CurrentAmount must
    // move together (one SaveChanges) so an xmin optimistic-concurrency conflict or a crash
    // rolls BOTH back — otherwise the immutable allocation ledger and the authoritative
    // CurrentAmount permanently diverge (R3 savings hardening).
    Task SaveGoalWithAllocationAsync(SavingsGoal goal, SavingsAllocation allocation);

    /// <summary>
    /// Commits several goal balances and their ledger rows in ONE transaction.
    /// </summary>
    /// <remarks>
    /// Reconciling an account moves money between goals, so a partial write would leave the
    /// account further out of step than before the user tried to fix it. All or nothing.
    /// </remarks>
    Task SaveGoalsWithAllocationsAsync(
        IEnumerable<(SavingsGoal Goal, SavingsAllocation Allocation)> changes);
    Task SaveGoalRemovingAllocationsAsync(SavingsGoal goal, IEnumerable<int> allocationIds);
    // Detach every tracked entity so the next optimistic-concurrency retry re-reads fresh rows.
    void DetachAll();

    // Aggregation operations
    Task<decimal> GetTotalSavedAmountAsync(int userId);
    Task<decimal> GetMonthlyGrowthAsync(int userId);
    Task<int> GetActiveGoalsCountAsync(int userId);
    Task<int> GetCompletedGoalsCountAsync(int userId);
}
