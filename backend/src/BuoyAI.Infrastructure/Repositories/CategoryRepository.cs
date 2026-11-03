using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BuoyAI.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of ICategoryRepository.
/// </summary>
public class CategoryRepository : ICategoryRepository
{
    private readonly BuoyAIDbContext _context;
    
    public CategoryRepository(BuoyAIDbContext context)
    {
        _context = context;
    }
    
    public async Task<Category?> GetByIdAsync(int id)
    {
        return await _context.Categories
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);
    }
    
    public async Task<IReadOnlyList<Category>> GetSystemCategoriesAsync()
    {
        return await _context.Categories
            .AsNoTracking()
            .Where(c => c.IsSystem)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();
    }
    
    public async Task<IReadOnlyList<Category>> GetUserCategoriesAsync(int userId)
    {
        return await _context.Categories
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();
    }
    
    public async Task<IReadOnlyList<Category>> GetFamilyCategoriesAsync(int familyId)
    {
        return await _context.Categories
            .AsNoTracking()
            .Where(c => c.FamilyId == familyId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();
    }
    
    public async Task<IReadOnlyList<Category>> GetAllForUserAsync(int userId, int? familyId)
    {
        var query = _context.Categories.AsNoTracking();
        
        if (familyId.HasValue)
        {
            var fid = familyId.Value;
            // User has a family: show system + personal + family categories + other family
            // members' personal categories that are referenced in family budgets.
            // The nested Any() calls are EF Core correlated subqueries; EF Core translates
            // them to SQL EXISTS clauses — they do NOT execute per row in C#.
            //
            // Defense-in-depth: the owning-user-is-in-this-family check must match
            // ExistsAndAccessibleAsync. Without it, a malicious client could create a
            // family budget referencing an arbitrary personal CategoryId and then read
            // that category back via this listing. The CategoryId comes from the client
            // on budget create/update, so we cannot trust that membership is transitive
            // through BudgetCategory alone.
            query = query.Where(c =>
                c.IsSystem ||
                c.UserId == userId ||
                c.FamilyId == fid ||
                (c.UserId.HasValue &&
                 c.UserId != userId &&
                 _context.Users.Any(u => u.Id == c.UserId && u.FamilyId == fid) &&
                 _context.BudgetCategories.Any(bc => bc.CategoryId == c.Id && bc.Budget.FamilyId == fid)));
        }
        else
        {
            // User has no family: show system + personal categories only
            query = query.Where(c => c.IsSystem || c.UserId == userId);
        }
        
        return await query
            .OrderBy(c => c.IsSystem ? 0 : (c.FamilyId.HasValue ? 1 : 2)) // System first, then family, then personal
            .ThenBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();
    }
    
    public async Task<IReadOnlyList<Category>> GetByIdsAsync(IEnumerable<int> ids)
    {
        var idList = ids.ToList();
        return await _context.Categories
            .AsNoTracking()
            .Where(c => idList.Contains(c.Id))
            .ToListAsync();
    }
    
    public async Task<bool> ExistsAndAccessibleAsync(int categoryId, int userId, int? familyId)
    {
        // Single query that checks all four accessibility rules in one round-trip:
        //   1. System categories are accessible to everyone.
        //   2. The category belongs to the requesting user.
        //   3. The category is a family category the user is a member of.
        //   4. The category is a family member's personal category that is referenced
        //      in a shared family budget (via EF Core correlated EXISTS subqueries).
        if (!familyId.HasValue)
        {
            return await _context.Categories
                .AsNoTracking()
                .AnyAsync(c =>
                    c.Id == categoryId &&
                    (c.IsSystem || c.UserId == userId));
        }

        var fid = familyId.Value;
        return await _context.Categories
            .AsNoTracking()
            .AnyAsync(c =>
                c.Id == categoryId &&
                (c.IsSystem ||
                 c.UserId == userId ||
                 c.FamilyId == fid ||
                 (c.UserId.HasValue && c.UserId != userId &&
                  _context.Users.Any(u => u.Id == c.UserId.Value && u.FamilyId == fid) &&
                  _context.BudgetCategories.Any(bc => bc.CategoryId == c.Id && bc.Budget.FamilyId == fid))));
    }
    
    public async Task<bool> IsNameUniqueAsync(string name, int? userId, int? familyId, int? excludeCategoryId = null)
    {
        var query = _context.Categories.AsNoTracking();
        
        if (userId.HasValue)
        {
            // Check uniqueness within user's personal categories
            query = query.Where(c => c.UserId == userId.Value && c.Name == name);
        }
        else if (familyId.HasValue)
        {
            // Check uniqueness within family categories
            query = query.Where(c => c.FamilyId == familyId.Value && c.Name == name);
        }
        else
        {
            // Neither user nor family - shouldn't happen for user-created categories
            return true;
        }
        
        if (excludeCategoryId.HasValue)
        {
            query = query.Where(c => c.Id != excludeCategoryId.Value);
        }
        
        return !await query.AnyAsync();
    }
    
    public async Task<Category> AddAsync(Category category)
    {
        category.CreatedAt = DateTime.UtcNow;
        category.UpdatedAt = DateTime.UtcNow;
        
        _context.Categories.Add(category);
        await _context.SaveChangesAsync();
        
        return category;
    }
    
    public async Task UpdateAsync(Category category)
    {
        category.UpdatedAt = DateTime.UtcNow;
        
        _context.Categories.Update(category);
        await _context.SaveChangesAsync();
    }
    
    public async Task DeleteAsync(int id)
    {
        var category = await _context.Categories.FindAsync(id);
        if (category != null)
        {
            _context.Categories.Remove(category);
            await _context.SaveChangesAsync();
        }
    }
    
    public async Task<int> GetNextUserCategoryIdAsync()
    {
        // User-created categories start at 1000
        var maxId = await _context.Categories
            .AsNoTracking()
            .Where(c => c.Id >= 1000)
            .MaxAsync(c => (int?)c.Id);
            
        return (maxId ?? 999) + 1;
    }
}
