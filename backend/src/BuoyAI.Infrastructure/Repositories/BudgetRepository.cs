using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class BudgetRepository : IBudgetRepository
{
  private readonly BuoyAIDbContext _context;

  public BudgetRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<IEnumerable<Budget>> GetByUserIdAsync(int userId)
  {
    return await _context.Budgets
      .Where(b => b.UserId == userId)
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .Include(b => b.Shares)
        .ThenInclude(s => s.SharedWithUser)
      .Include(b => b.ChildBudgets)
      .OrderByDescending(b => b.IsPrimary)
      .ThenByDescending(b => b.CreatedAt)
      .ToListAsync();
  }

  public async Task<Budget?> GetByIdAsync(int id)
  {
    return await _context.Budgets
      .Include(b => b.User)
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .Include(b => b.Categories)
        .ThenInclude(c => c.AssignedUser)
      .Include(b => b.Categories)
        .ThenInclude(c => c.SavingsGoal)
      .Include(b => b.Categories)
        .ThenInclude(c => c.RolloverOverflowSavingsGoal)
      .Include(b => b.Shares)
        .ThenInclude(s => s.SharedWithUser)
      .Include(b => b.ParentBudget)
      .Include(b => b.ChildBudgets)
      .FirstOrDefaultAsync(b => b.Id == id);
  }

  public async Task<Budget?> GetActiveByUserIdAsync(int userId)
  {
    // First try to get the primary budget
    var primaryBudget = await _context.Budgets
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .FirstOrDefaultAsync(b => b.UserId == userId && b.IsPrimary && b.IsActive);
    
    if (primaryBudget != null)
      return primaryBudget;
    
    // Fall back to any active budget
    return await _context.Budgets
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .FirstOrDefaultAsync(b => b.UserId == userId && b.IsActive);
  }

  public async Task<Budget?> GetActivePersonalBudgetAsync(int userId)
  {
    return await _context.Budgets
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .Where(b => b.UserId == userId && b.IsActive && b.Type == BudgetType.Personal)
      .OrderByDescending(b => b.IsPrimary)
      .ThenByDescending(b => b.CreatedAt)
      .FirstOrDefaultAsync();
  }

  public async Task<IEnumerable<Budget>> GetActiveBudgetsByTypeAsync(int userId, BudgetType type)
  {
    return await _context.Budgets
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .Where(b => b.UserId == userId && b.IsActive && b.Type == type)
      .OrderByDescending(b => b.IsPrimary)
      .ThenByDescending(b => b.CreatedAt)
      .ToListAsync();
  }

  public async Task<Budget> CreateAsync(Budget budget)
  {
    budget.CreatedAt = DateTime.UtcNow;
    budget.UpdatedAt = DateTime.UtcNow;
    
    foreach (var category in budget.Categories)
    {
      category.CreatedAt = DateTime.UtcNow;
      category.UpdatedAt = DateTime.UtcNow;
    }
    
    _context.Budgets.Add(budget);
    await _context.SaveChangesAsync();
    return budget;
  }

  public async Task<Budget> UpdateAsync(Budget budget)
  {
    budget.UpdatedAt = DateTime.UtcNow;
    
    _context.Budgets.Update(budget);
    await _context.SaveChangesAsync();
    return budget;
  }

  public async Task DeleteAsync(int id)
  {
    var budget = await _context.Budgets.FindAsync(id);
    if (budget != null)
    {
      _context.Budgets.Remove(budget);
      await _context.SaveChangesAsync();
    }
  }

  public async Task<BudgetCategory?> GetBudgetCategoryAsync(int categoryId)
  {
    return await _context.BudgetCategories
      .Include(bc => bc.Budget)
      .Include(bc => bc.AssignedUser)
      .Include(bc => bc.LinkedBudget)
      .Include(bc => bc.CategoryNav)
      .FirstOrDefaultAsync(bc => bc.Id == categoryId);
  }

  public async Task<BudgetCategory> UpdateBudgetCategoryAsync(BudgetCategory category)
  {
    category.UpdatedAt = DateTime.UtcNow;
    
    _context.BudgetCategories.Update(category);
    await _context.SaveChangesAsync();
    return category;
  }

  // Multi-budget operations
  public async Task<Budget?> GetPrimaryBudgetAsync(int userId)
  {
    return await _context.Budgets
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .FirstOrDefaultAsync(b => b.UserId == userId && b.IsPrimary);
  }

  public async Task<Budget?> GetPrimaryFamilyBudgetAsync(int familyId)
  {
    return await _context.Budgets
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .Include(b => b.ChildBudgets)
      .FirstOrDefaultAsync(b => b.FamilyId == familyId && b.Type == BudgetType.Family && b.IsPrimary);
  }

  public async Task<IEnumerable<Budget>> GetByFamilyIdAsync(int familyId)
  {
    return await _context.Budgets
      .Where(b => b.FamilyId == familyId && b.Type == BudgetType.Family)
      .Include(b => b.User)
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .Include(b => b.Shares)
        .ThenInclude(s => s.SharedWithUser)
      .Include(b => b.ChildBudgets)
      .OrderByDescending(b => b.Type == BudgetType.Family)
      .ThenByDescending(b => b.CreatedAt)
      .ToListAsync();
  }

  public async Task<IEnumerable<Budget>> GetAccessibleBudgetsAsync(int userId)
  {
    // Get user's own budgets
    var ownBudgets = await _context.Budgets
      .Where(b => b.UserId == userId)
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .Include(b => b.Shares)
        .ThenInclude(s => s.SharedWithUser)
      .ToListAsync();

    // Get budgets shared with this user
    var sharedBudgetIds = await _context.BudgetShares
      .Where(s => s.SharedWithUserId == userId)
      .Select(s => s.BudgetId)
      .ToListAsync();

    var sharedBudgets = await _context.Budgets
      .Where(b => sharedBudgetIds.Contains(b.Id))
      .Include(b => b.User)
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .ToListAsync();

    // Get family budgets if user is in a family
    var user = await _context.Users
      .Include(u => u.FamilyMemberships)
      .FirstOrDefaultAsync(u => u.Id == userId);

    var familyBudgets = new List<Budget>();
    if (user?.FamilyId != null)
    {
      // Family budgets: Type = Family is the source of truth for visibility
      familyBudgets = await _context.Budgets
        .Where(b => b.FamilyId == user.FamilyId &&
                    b.UserId != userId &&
                    b.Type == BudgetType.Family)
        .Include(b => b.User)
        .Include(b => b.Categories)
          .ThenInclude(c => c.CategoryNav)
        .Include(b => b.ChildBudgets)
        .ToListAsync();
    }

    // Combine and deduplicate
    var allBudgets = ownBudgets
      .Union(sharedBudgets)
      .Union(familyBudgets)
      .DistinctBy(b => b.Id)
      .OrderByDescending(b => b.UserId == userId) // Own budgets first
      .ThenByDescending(b => b.IsPrimary)
      .ThenByDescending(b => b.CreatedAt)
      .ToList();

    return allBudgets;
  }

  public async Task<IEnumerable<Budget>> GetChildBudgetsAsync(int parentBudgetId)
  {
    return await _context.Budgets
      .Where(b => b.ParentBudgetId == parentBudgetId)
      .Include(b => b.User)
      .Include(b => b.Categories)
        .ThenInclude(c => c.CategoryNav)
      .ToListAsync();
  }

  // Budget sharing operations
  public async Task<BudgetShare> CreateBudgetShareAsync(BudgetShare share)
  {
    share.CreatedAt = DateTime.UtcNow;
    share.UpdatedAt = DateTime.UtcNow;
    
    _context.BudgetShares.Add(share);
    await _context.SaveChangesAsync();
    
    // Reload with navigation properties
    return await _context.BudgetShares
      .Include(s => s.SharedWithUser)
      .FirstAsync(s => s.Id == share.Id);
  }

  public async Task DeleteBudgetShareAsync(int budgetId, int sharedWithUserId)
  {
    var share = await _context.BudgetShares
      .FirstOrDefaultAsync(s => s.BudgetId == budgetId && s.SharedWithUserId == sharedWithUserId);
    
    if (share != null)
    {
      _context.BudgetShares.Remove(share);
      await _context.SaveChangesAsync();
    }
  }

  public async Task<IEnumerable<BudgetShare>> GetBudgetSharesAsync(int budgetId)
  {
    return await _context.BudgetShares
      .Where(s => s.BudgetId == budgetId)
      .Include(s => s.SharedWithUser)
      .ToListAsync();
  }

  public async Task<BudgetShare?> GetBudgetShareAsync(int budgetId, int sharedWithUserId)
  {
    return await _context.BudgetShares
      .Include(s => s.SharedWithUser)
      .FirstOrDefaultAsync(s => s.BudgetId == budgetId && s.SharedWithUserId == sharedWithUserId);
  }

  public async Task<BudgetCategory> CreateBudgetCategoryAsync(BudgetCategory category)
  {
    category.CreatedAt = DateTime.UtcNow;
    category.UpdatedAt = DateTime.UtcNow;
    
    _context.BudgetCategories.Add(category);
    await _context.SaveChangesAsync();
    
    // Reload with navigation properties
    return await _context.BudgetCategories
      .Include(c => c.Budget)
      .Include(c => c.AssignedUser)
      .Include(c => c.LinkedBudget)
      .FirstAsync(c => c.Id == category.Id);
  }

  public async Task<BudgetCategory?> GetBudgetCategoryByCategoryIdAsync(int categoryId)
  {
    // Find a budget category that uses this category and has an assigned user (allowance)
    return await _context.BudgetCategories
      .Include(c => c.CategoryNav)
      .Include(c => c.AssignedUser)
      .FirstOrDefaultAsync(c => c.CategoryId == categoryId && c.AssignedUserId.HasValue);
  }

  public async Task<BudgetCategory?> GetLinkedCategoryForChildAsync(int childBudgetId)
  {
    // The family category that funds this child: IsAllowance + LinkedBudgetId == child.
    // Used both to detect linked Personal children and to keep the allocation in sync.
    return await _context.BudgetCategories
      .Include(c => c.CategoryNav)
      .Include(c => c.AssignedUser)
      .FirstOrDefaultAsync(c => c.LinkedBudgetId == childBudgetId && c.IsAllowance);
  }

  public async Task<bool> HasAccessAsync(int budgetId, int userId)
  {
    var budget = await _context.Budgets.FindAsync(budgetId);
    if (budget == null) return false;

    // Owner always has access
    if (budget.UserId == userId) return true;

    // Check family access - user must be in the same family as the budget
    if (budget.FamilyId.HasValue)
    {
      var user = await _context.Users.FindAsync(userId);
      if (user?.FamilyId == budget.FamilyId) return true;
    }

    // Check shared access
    var hasShare = await _context.BudgetShares
      .AnyAsync(s => s.BudgetId == budgetId && s.SharedWithUserId == userId);

    return hasShare;
  }
}
