using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BuoyAI.Infrastructure.Repositories;

public class CategoryGroupRepository : ICategoryGroupRepository
{
  private readonly BuoyAIDbContext _context;

  public CategoryGroupRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<IEnumerable<CategoryGroup>> GetSystemGroupsAsync()
  {
    return await _context.CategoryGroups
      .Where(g => g.UserId == null && g.FamilyId == null)
      .OrderBy(g => g.SortOrder)
      .ToListAsync();
  }

  public async Task<IEnumerable<CategoryGroup>> GetAccessibleGroupsAsync(int userId, int? familyId)
  {
    var query = _context.CategoryGroups
      .Where(g => 
        // System groups
        (g.UserId == null && g.FamilyId == null) ||
        // User's custom groups
        g.UserId == userId ||
        // Family's custom groups (if user is in a family)
        (familyId != null && g.FamilyId == familyId));

    return await query
      .OrderBy(g => g.SortOrder)
      .ThenBy(g => g.Name)
      .ToListAsync();
  }

  public async Task<CategoryGroup?> GetByIdAsync(int id)
  {
    return await _context.CategoryGroups.FindAsync(id);
  }

  public async Task<CategoryGroup> CreateAsync(CategoryGroup group)
  {
    group.CreatedAt = DateTime.UtcNow;
    group.UpdatedAt = DateTime.UtcNow;
    
    // Set sort order to end if not specified
    if (group.SortOrder == 0)
    {
      var maxSortOrder = await _context.CategoryGroups
        .Where(g => g.UserId == group.UserId && g.FamilyId == group.FamilyId)
        .MaxAsync(g => (int?)g.SortOrder) ?? 0;
      group.SortOrder = maxSortOrder + 1;
    }
    
    _context.CategoryGroups.Add(group);
    await _context.SaveChangesAsync();
    return group;
  }

  public async Task<CategoryGroup> UpdateAsync(CategoryGroup group)
  {
    group.UpdatedAt = DateTime.UtcNow;
    _context.CategoryGroups.Update(group);
    await _context.SaveChangesAsync();
    return group;
  }

  public async Task DeleteAsync(int id)
  {
    var group = await _context.CategoryGroups.FindAsync(id);
    if (group != null)
    {
      // Cannot delete system groups
      if (group.UserId == null && group.FamilyId == null)
      {
        throw new InvalidOperationException("Cannot delete system category groups");
      }
      
      // Move all categories in this group to "Other" (group 9)
      var categoriesToMove = await _context.BudgetCategories
        .Where(c => c.GroupId == id)
        .ToListAsync();
      
      foreach (var category in categoriesToMove)
      {
        category.GroupId = 9; // "Other" group
        category.UpdatedAt = DateTime.UtcNow;
      }
      
      _context.CategoryGroups.Remove(group);
      await _context.SaveChangesAsync();
    }
  }

  public Task<int?> GetDefaultGroupIdForCategoryAsync(TransactionCategory category)
  {
    // Map transaction categories to default groups
    int groupId = category switch
    {
      // Income group (1)
      TransactionCategory.Salary => 1,
      TransactionCategory.Bonus => 1,
      TransactionCategory.Investment => 1,
      TransactionCategory.OtherIncome => 1,
      
      // Housing group (2)
      TransactionCategory.Rent => 2,
      TransactionCategory.Utilities => 2,
      
      // Food & Dining group (3)
      TransactionCategory.Groceries => 3,
      TransactionCategory.DiningOut => 3,
      
      // Transportation group (4)
      TransactionCategory.Transportation => 4,
      
      // Health group (5)
      TransactionCategory.Healthcare => 5,
      TransactionCategory.Insurance => 5,
      
      // Entertainment group (6)
      TransactionCategory.Entertainment => 6,
      TransactionCategory.Subscriptions => 6,
      
      // Shopping group (7)
      TransactionCategory.Shopping => 7,
      TransactionCategory.PersonalCare => 7,
      
      // Financial group (8)
      TransactionCategory.LoanPayments => 8,
      TransactionCategory.CreditPayment => 8,
      TransactionCategory.Transfer => 8,
      
      // Other group (9)
      TransactionCategory.Education => 9,
      TransactionCategory.Gifts => 9,
      TransactionCategory.Travel => 9,
      TransactionCategory.Uncategorized => 9,
      
      _ => 9 // Default to "Other"
    };
    
    return Task.FromResult<int?>(groupId);
  }
}