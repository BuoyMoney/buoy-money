using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class SavingsRepository : ISavingsRepository
{
    private readonly BuoyAIDbContext _context;

    public SavingsRepository(BuoyAIDbContext context)
    {
        _context = context;
    }

    public async Task<SavingsGoal?> GetByIdAsync(int id)
    {
        return await _context.SavingsGoals
            .Include(sg => sg.Allocations)
            .Include(sg => sg.User)
            // Linked account supplies the name + balance the goal reconciles against.
            .Include(sg => sg.Account)
            .FirstOrDefaultAsync(sg => sg.Id == id);
    }

    public async Task<IEnumerable<SavingsGoal>> GetByUserIdAsync(int userId)
    {
        return await _context.SavingsGoals
            .Include(sg => sg.Allocations)
            .Include(sg => sg.Account)
            .Where(sg => sg.UserId == userId)
            .OrderBy(sg => sg.CreatedAt)
            .ToListAsync();
    }

    public async Task<Dictionary<int, decimal>> GetGoalTotalsByAccountAsync(IEnumerable<int> accountIds)
    {
        var ids = accountIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, decimal>();

        return await _context.SavingsGoals
            .Where(sg => sg.AccountId != null && ids.Contains(sg.AccountId.Value))
            .GroupBy(sg => sg.AccountId!.Value)
            .Select(g => new { AccountId = g.Key, Total = g.Sum(sg => sg.CurrentAmount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Total);
    }

    public async Task<int> GetGoalCountByAccountAsync(int accountId)
    {
        return await _context.SavingsGoals.CountAsync(sg => sg.AccountId == accountId);
    }

    public async Task<IEnumerable<SavingsGoal>> GetByAccountIdAsync(int accountId)
    {
        return await _context.SavingsGoals
            .Include(sg => sg.Allocations)
            .Include(sg => sg.Account)
            .Where(sg => sg.AccountId == accountId)
            .OrderBy(sg => sg.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<SavingsGoal>> GetByUserOrFamilyAsync(int userId, int? familyId)
    {
        var query = _context.SavingsGoals
            .Include(sg => sg.Allocations)
            // Load the owner so shared family goals can attribute OwnerName; without it goal.User is
            // null for other members' goals and the overview renders every one as "Unknown".
            .Include(sg => sg.User)
            .Include(sg => sg.Account)
            .AsQueryable();

        if (familyId.HasValue)
        {
            // Return goals owned by user OR shared with family
            query = query.Where(sg => sg.UserId == userId || sg.FamilyId == familyId);
        }
        else
        {
            // No family, just return user's goals
            query = query.Where(sg => sg.UserId == userId);
        }

        return await query
            .OrderBy(sg => sg.CreatedAt)
            .ToListAsync();
    }

    public async Task<SavingsGoal> CreateAsync(SavingsGoal savingsGoal)
    {
        savingsGoal.CreatedAt = DateTime.UtcNow;
        savingsGoal.UpdatedAt = DateTime.UtcNow;
        
        _context.SavingsGoals.Add(savingsGoal);
        await _context.SaveChangesAsync();
        return savingsGoal;
    }

    public async Task<SavingsGoal> UpdateAsync(SavingsGoal savingsGoal)
    {
        savingsGoal.UpdatedAt = DateTime.UtcNow;
        
        _context.SavingsGoals.Update(savingsGoal);
        await _context.SaveChangesAsync();
        return savingsGoal;
    }

    public async Task DeleteAsync(int id)
    {
        var goal = await _context.SavingsGoals.FindAsync(id);
        if (goal != null)
        {
            _context.SavingsGoals.Remove(goal);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<SavingsAllocation> CreateAllocationAsync(SavingsAllocation allocation)
    {
        allocation.CreatedAt = DateTime.UtcNow;
        
        _context.SavingsAllocations.Add(allocation);
        await _context.SaveChangesAsync();
        return allocation;
    }

    public async Task<IEnumerable<SavingsAllocation>> GetAllocationsByGoalIdAsync(int goalId)
    {
        return await _context.SavingsAllocations
            .Where(sa => sa.SavingsGoalId == goalId)
            .OrderBy(sa => sa.AllocationDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<SavingsAllocation>> GetAllocationsByUserIdAsync(int userId)
    {
        return await _context.SavingsAllocations
            .Include(sa => sa.SavingsGoal)
            .Where(sa => sa.SavingsGoal.UserId == userId)
            .OrderBy(sa => sa.AllocationDate)
            .ToListAsync();
    }

    public async Task DeleteAllocationAsync(int allocationId)
    {
        await _context.SavingsAllocations
            .Where(sa => sa.Id == allocationId)
            .ExecuteDeleteAsync();
    }


    /// <summary>
    /// Marks the goal modified WITHOUT dragging its linked account along.
    /// </summary>
    /// <remarks>
    /// <c>DbSet.Update</c> marks every entity reachable from the root as Modified, and goals now
    /// eager-load <c>Account</c> for reconciliation. Left alone that writes the account row back on
    /// every balance change — persisting a possibly-stale CurrentBalance over whatever Plaid last
    /// synced, and turning several goals sharing one account into repeated writes of the same row.
    /// The account is read-only to this repository, so pin it back to Unchanged.
    /// </remarks>
    private void UpdateGoalWithoutAccount(SavingsGoal goal)
    {
        _context.SavingsGoals.Update(goal);
        if (goal.Account != null)
        {
            _context.Entry(goal.Account).State = EntityState.Unchanged;
        }
    }

    public async Task SaveGoalWithAllocationAsync(SavingsGoal goal, SavingsAllocation allocation)
    {
        allocation.CreatedAt = DateTime.UtcNow;
        goal.UpdatedAt = DateTime.UtcNow;

        // Single SaveChanges spans the ledger insert AND the balance update. The goal row is
        // xmin-guarded, so a concurrent writer makes THIS SaveChanges throw
        // DbUpdateConcurrencyException and roll back the allocation insert too — the caller's
        // retry loop then reloads and re-applies. Never two independent commits.
        _context.SavingsAllocations.Add(allocation);
        UpdateGoalWithoutAccount(goal);
        await _context.SaveChangesAsync();
    }

    public async Task SaveGoalsWithAllocationsAsync(
        IEnumerable<(SavingsGoal Goal, SavingsAllocation Allocation)> changes)
    {
        var now = DateTime.UtcNow;

        // ONE SaveChanges across every goal and ledger row. A reconcile that moved money between
        // goals must not be able to land halfway — that would leave the account MORE out of step
        // than before the user tried to fix it. Every goal row is xmin-guarded, so a concurrent
        // writer on any one of them rolls the whole batch back for the caller's retry loop.
        foreach (var (goal, allocation) in changes)
        {
            allocation.CreatedAt = now;
            goal.UpdatedAt = now;
            _context.SavingsAllocations.Add(allocation);
            UpdateGoalWithoutAccount(goal);
        }

        await _context.SaveChangesAsync();
    }

    public async Task SaveGoalRemovingAllocationsAsync(SavingsGoal goal, IEnumerable<int> allocationIds)
    {
        var ids = allocationIds.ToList();
        goal.UpdatedAt = DateTime.UtcNow;

        var toRemove = await _context.SavingsAllocations
            .Where(sa => ids.Contains(sa.Id))
            .ToListAsync();

        _context.SavingsAllocations.RemoveRange(toRemove);
        UpdateGoalWithoutAccount(goal);
        await _context.SaveChangesAsync();
    }

    public void DetachAll()
    {
        // Detach ONLY savings entities so the next retry re-reads the goal (with a fresh xmin) and drops
        // the failed allocation write. Scoped deliberately: ProcessAutomaticAllocationsAsync runs inside
        // BudgetPeriodService.ResetBudgetAsync, which shares this scoped DbContext and tracks the budget/
        // snapshot/category rows — detaching those would silently drop their pending reset changes.
        foreach (var entry in _context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is SavingsGoal or SavingsAllocation)
                entry.State = EntityState.Detached;
        }
    }

    public async Task<decimal> GetTotalSavedAmountAsync(int userId)
    {
        return await _context.SavingsGoals
            .Where(sg => sg.UserId == userId)
            .SumAsync(sg => sg.CurrentAmount);
    }

    public async Task<decimal> GetMonthlyGrowthAsync(int userId)
    {
        var oneMonthAgo = DateTime.UtcNow.AddMonths(-1);
        
        return await _context.SavingsAllocations
            .Include(sa => sa.SavingsGoal)
            .Where(sa => sa.SavingsGoal.UserId == userId
                         && sa.AllocationDate >= oneMonthAgo
                         && sa.Source != "initial_amount")
            .SumAsync(sa => sa.AllocatedAmount);
    }

    public async Task<int> GetActiveGoalsCountAsync(int userId)
    {
        return await _context.SavingsGoals
            .CountAsync(sg => sg.UserId == userId && sg.IsActive && !sg.IsCompleted);
    }

    public async Task<int> GetCompletedGoalsCountAsync(int userId)
    {
        return await _context.SavingsGoals
            .CountAsync(sg => sg.UserId == userId && sg.IsCompleted);
    }
}
