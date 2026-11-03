using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing category groups within budgets.
/// Extracted from BudgetService for better separation of concerns.
/// Handles: groups, inline category updates, grouped budget overview.
/// </summary>
public class BudgetCategoryGroupService : IBudgetCategoryGroupService
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<BudgetCategoryGroupService> _logger;
    private readonly IUserRepository _userRepository;
    private readonly IBudgetRepository _budgetRepository;
    private readonly IBudgetSpendingService _spendingService;

    public BudgetCategoryGroupService(
        BuoyAIDbContext context,
        ILogger<BudgetCategoryGroupService> logger,
        IUserRepository userRepository,
        IBudgetRepository budgetRepository,
        IBudgetSpendingService spendingService)
    {
        _context = context;
        _logger = logger;
        _userRepository = userRepository;
        _budgetRepository = budgetRepository;
        _spendingService = spendingService;
    }

    // ============================================
    // Category Group Operations
    // ============================================

    public async Task<IEnumerable<CategoryGroupDto>> GetCategoryGroupsAsync(int budgetId, int userId)
    {
        var hasAccess = await _budgetRepository.HasAccessAsync(budgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        var budget = await _context.Budgets.FindAsync(budgetId);
        if (budget == null)
            throw new KeyNotFoundException($"Budget {budgetId} not found");

        // Get user's family ID if any
        var user = await _userRepository.GetByIdAsync(userId);
        var familyId = user?.FamilyId ?? budget.FamilyId;

        // Get all accessible groups
        var groups = await _context.CategoryGroups
            .Where(g =>
                (g.UserId == null && g.FamilyId == null) ||
                g.UserId == userId ||
                (familyId != null && g.FamilyId == familyId))
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.Name)
            .ToListAsync();

        // Get categories for this budget grouped by GroupId
        var categories = await _context.BudgetCategories
            .Include(c => c.Group)
            .Include(c => c.CategoryNav)
            .Where(c => c.BudgetId == budgetId)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        var categoryLookup = categories.GroupBy(c => c.GroupId ?? 0).ToDictionary(g => g.Key, g => g.ToList());

        return groups.Select(g => new CategoryGroupDto
        {
            Id = g.Id,
            Name = g.Name,
            Icon = g.Icon,
            SortOrder = g.SortOrder,
            IsSystem = g.UserId == null && g.FamilyId == null,
            Categories = categoryLookup.TryGetValue(g.Id, out var cats)
                ? cats.Select(c => c.ToDto()).ToList()
                : new List<BudgetCategoryDto>()
        });
    }

    public async Task<CategoryGroupDto> CreateCategoryGroupAsync(int budgetId, int userId, CreateCategoryGroupRequest request)
    {
        var hasAccess = await _budgetRepository.HasAccessAsync(budgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        var budget = await _context.Budgets.FindAsync(budgetId);
        if (budget == null)
            throw new KeyNotFoundException($"Budget {budgetId} not found");

        // Determine if this should be a family or personal group
        int? groupFamilyId = budget.FamilyId;
        int? groupUserId = groupFamilyId == null ? userId : null;

        var maxSortOrder = await _context.CategoryGroups
            .Where(g => g.UserId == groupUserId && g.FamilyId == groupFamilyId)
            .MaxAsync(g => (int?)g.SortOrder) ?? 8; // Start after system groups

        var group = new CategoryGroup
        {
            Name = request.Name,
            Icon = request.Icon,
            SortOrder = request.SortOrder ?? maxSortOrder + 1,
            UserId = groupUserId,
            FamilyId = groupFamilyId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.CategoryGroups.Add(group);
        await _context.SaveChangesAsync();

        _logger.LogInformation("User {UserId} created category group '{GroupName}' for budget {BudgetId}",
            userId, group.Name, budgetId);

        return new CategoryGroupDto
        {
            Id = group.Id,
            Name = group.Name,
            Icon = group.Icon,
            SortOrder = group.SortOrder,
            IsSystem = false,
            Categories = new List<BudgetCategoryDto>()
        };
    }

    public async Task<CategoryGroupDto> UpdateCategoryGroupAsync(int groupId, int userId, UpdateCategoryGroupRequest request)
    {
        var group = await _context.CategoryGroups.FindAsync(groupId);
        if (group == null)
            throw new KeyNotFoundException($"Category group {groupId} not found");

        // Cannot edit system groups
        if (group.UserId == null && group.FamilyId == null)
            throw new InvalidOperationException("Cannot modify system category groups");

        // Verify user has access
        if (group.UserId != userId)
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (group.FamilyId == null || user?.FamilyId != group.FamilyId)
                throw new UnauthorizedAccessException("User does not have access to this category group");
        }

        if (request.Name != null) group.Name = request.Name;
        if (request.Icon != null) group.Icon = request.Icon;
        if (request.SortOrder.HasValue) group.SortOrder = request.SortOrder.Value;
        group.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("User {UserId} updated category group {GroupId}", userId, groupId);

        var categories = await _context.BudgetCategories
            .Include(c => c.CategoryNav)
            .Where(c => c.GroupId == groupId)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        return new CategoryGroupDto
        {
            Id = group.Id,
            Name = group.Name,
            Icon = group.Icon,
            SortOrder = group.SortOrder,
            IsSystem = false,
            Categories = categories.Select(c => c.ToDto()).ToList()
        };
    }

    public async Task DeleteCategoryGroupAsync(int groupId, int userId)
    {
        var group = await _context.CategoryGroups.FindAsync(groupId);
        if (group == null)
            throw new KeyNotFoundException($"Category group {groupId} not found");

        // Cannot delete system groups
        if (group.UserId == null && group.FamilyId == null)
            throw new InvalidOperationException("Cannot delete system category groups");

        // Verify user has access
        if (group.UserId != userId)
        {
            var user = await _userRepository.GetByIdAsync(userId);
            if (group.FamilyId == null || user?.FamilyId != group.FamilyId)
                throw new UnauthorizedAccessException("User does not have access to this category group");
        }

        // Move all categories to "Other" group
        var categoriesToMove = await _context.BudgetCategories
            .Where(c => c.GroupId == groupId)
            .ToListAsync();

        foreach (var category in categoriesToMove)
        {
            category.GroupId = CategoryGroup.OtherGroupId;
            category.UpdatedAt = DateTime.UtcNow;
        }

        _context.CategoryGroups.Remove(group);
        await _context.SaveChangesAsync();

        _logger.LogInformation("User {UserId} deleted category group {GroupId}, moved {CategoryCount} categories to Other",
            userId, groupId, categoriesToMove.Count);
    }

    public async Task ReorderCategoryGroupsAsync(int budgetId, int userId, ReorderCategoryGroupsRequest request)
    {
        var hasAccess = await _budgetRepository.HasAccessAsync(budgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        var user = await _userRepository.GetByIdAsync(userId);
        var familyId = user?.FamilyId;

        // Only reorder groups the caller actually owns (personal) or that belong to the caller's family.
        // Never touch system groups (UserId==null && FamilyId==null) — their SortOrder is global and
        // shared by every tenant — nor another user's/family's private groups, which the client could
        // smuggle in via GroupIds to corrupt other tenants' ordering (mirrors UpdateCategoryGroupAsync).
        var groups = await _context.CategoryGroups
            .Where(g => request.GroupIds.Contains(g.Id)
                && (g.UserId == userId || (familyId != null && g.FamilyId == familyId)))
            .ToListAsync();

        for (int i = 0; i < request.GroupIds.Count; i++)
        {
            var group = groups.FirstOrDefault(g => g.Id == request.GroupIds[i]);
            if (group != null)
            {
                group.SortOrder = i;
                group.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("User {UserId} reordered category groups for budget {BudgetId}", userId, budgetId);
    }

    // ============================================
    // Inline Category Updates
    // ============================================

    public async Task<BudgetCategoryDto> UpdateCategoryInlineAsync(int categoryId, int userId, InlineUpdateCategoryRequest request)
    {
        var category = await _context.BudgetCategories
            .Include(c => c.Budget)
            .Include(c => c.Group)
            .Include(c => c.CategoryNav)
            .FirstOrDefaultAsync(c => c.Id == categoryId);

        if (category == null)
            throw new KeyNotFoundException($"Budget category {categoryId} not found");

        var hasAccess = await _budgetRepository.HasAccessAsync(category.BudgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        // Apply updates
        if (request.AllocatedAmount.HasValue)
        {
            var oldAmount = category.AllocatedAmount;
            category.AllocatedAmount = request.AllocatedAmount.Value;

            // Update budget totals
            category.Budget.TotalExpenses += (request.AllocatedAmount.Value - oldAmount);
        }

        if (request.AllowRollover.HasValue)
            category.AllowRollover = request.AllowRollover.Value;

        if (request.IsCountUpEnabled.HasValue)
            category.IsCountUpEnabled = request.IsCountUpEnabled.Value;

        if (request.SortOrder.HasValue)
            category.SortOrder = request.SortOrder.Value;

        category.UpdatedAt = DateTime.UtcNow;
        category.Budget.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("User {UserId} inline-updated category {CategoryId}", userId, categoryId);

        return category.ToDto();
    }

    public async Task<BudgetCategoryDto> MoveCategoryToGroupAsync(int categoryId, int userId, MoveCategoryToGroupRequest request)
    {
        var category = await _context.BudgetCategories
            .Include(c => c.Budget)
            .Include(c => c.CategoryNav)
            .FirstOrDefaultAsync(c => c.Id == categoryId);

        if (category == null)
            throw new KeyNotFoundException($"Budget category {categoryId} not found");

        var hasAccess = await _budgetRepository.HasAccessAsync(category.BudgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        // Default to "Other" group if no group specified.
        var targetGroupId = request.GroupId ?? CategoryGroup.OtherGroupId;

        // Validate the destination group is within the caller's scope. Trusting a raw client GroupId let
        // a user move their own category into another user's/family's private group — leaking that
        // group's (possibly sensitive) name back via the response DTO (an enumeration oracle) and
        // hiding the category from the user's own overview (GetGroupedBudgetOverviewAsync only renders
        // groups in the caller's accessible set). System groups (UserId==null && FamilyId==null) remain
        // valid targets.
        var user = await _userRepository.GetByIdAsync(userId);
        var familyId = user?.FamilyId;
        var targetGroup = await _context.CategoryGroups.FindAsync(targetGroupId);
        if (targetGroup == null)
            throw new KeyNotFoundException($"Category group {targetGroupId} not found");

        var isSystemGroup = targetGroup.UserId == null && targetGroup.FamilyId == null;
        var isOwnedByUser = targetGroup.UserId == userId;
        var isFamilyGroup = familyId != null && targetGroup.FamilyId == familyId;
        if (!isSystemGroup && !isOwnedByUser && !isFamilyGroup)
            throw new UnauthorizedAccessException("You do not have access to the target category group");

        category.GroupId = targetGroupId;

        if (request.SortOrder.HasValue)
            category.SortOrder = request.SortOrder.Value;
        else
        {
            // Put at end of new group
            var maxSortOrder = await _context.BudgetCategories
                .Where(c => c.BudgetId == category.BudgetId && c.GroupId == category.GroupId)
                .MaxAsync(c => (int?)c.SortOrder) ?? 0;
            category.SortOrder = maxSortOrder + 1;
        }

        category.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Reload with group
        category = await _context.BudgetCategories
            .Include(c => c.Group)
            .Include(c => c.CategoryNav)
            .FirstAsync(c => c.Id == categoryId);

        _logger.LogInformation("User {UserId} moved category {CategoryId} to group {GroupId}",
            userId, categoryId, category.GroupId);

        return category.ToDto();
    }

    // ============================================
    // Grouped Budget Overview
    // ============================================

    public async Task<GroupedBudgetOverviewDto> GetGroupedBudgetOverviewAsync(int budgetId, int userId)
    {
        var hasAccess = await _budgetRepository.HasAccessAsync(budgetId, userId);
        if (!hasAccess)
            throw new UnauthorizedAccessException("User does not have access to this budget");

        var budget = await _context.Budgets.FindAsync(budgetId);
        if (budget == null)
            throw new ArgumentException($"Budget {budgetId} not found");

        var user = await _userRepository.GetByIdAsync(userId);

        // Get all groups accessible to this user
        var groups = await _context.CategoryGroups
            .Where(g => g.UserId == null || g.UserId == userId || (g.FamilyId != null && g.FamilyId == user!.FamilyId))
            .OrderBy(g => g.SortOrder)
            .ToListAsync();

        // Get all categories for this budget
        var categories = await _context.BudgetCategories
            .Include(c => c.CategoryNav)
            .Where(c => c.BudgetId == budgetId)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        // Get spending data from transactions
        var categorySpending = await _spendingService.GetCategorySpendingAsync(budgetId);

        // Build a lookup of CategoryId → default GroupId for ungrouped categories
        var defaultGroupMapping = GetDefaultCategoryGroupMapping();

        // Auto-assign ungrouped categories (GroupId = null) to their default system group
        foreach (var cat in categories.Where(c => c.GroupId == null))
        {
            if (cat.CategoryId.HasValue && defaultGroupMapping.TryGetValue(cat.CategoryId.Value, out var defaultGroupId))
                cat.GroupId = defaultGroupId;
            else
                cat.GroupId = CategoryGroup.OtherGroupId; // Fallback to "Other"
        }
        
        var categoryLookup = categories.GroupBy(c => c.GroupId ?? CategoryGroup.OtherGroupId).ToDictionary(g => g.Key, g => g.ToList());

        var groupDtos = groups.Select(g => new CategoryGroupDto
        {
            Id = g.Id,
            Name = g.Name,
            Icon = g.Icon,
            SortOrder = g.SortOrder,
            IsSystem = g.UserId == null && g.FamilyId == null,
            Categories = categoryLookup.TryGetValue(g.Id, out var cats)
                ? cats.Select(c => c.ToDto(categorySpending.GetValueOrDefault(c.Id, 0))).ToList()
                : new List<BudgetCategoryDto>()
        }).ToList();

        // Calculate total spending
        var totalSpent = categorySpending.Values.Sum();
        var totalAllocated = categories.Sum(c => c.AllocatedAmount);

        return new GroupedBudgetOverviewDto
        {
            BudgetId = budget.Id,
            BudgetName = budget.Name,
            TotalAllocated = totalAllocated,
            TotalSpent = totalSpent,
            TotalRemaining = totalAllocated - totalSpent,
            Groups = groupDtos
        };
    }

    /// <summary>
    /// Returns a mapping from system CategoryId values to their default CategoryGroup IDs.
    /// Used to auto-assign ungrouped budget categories to the correct system group.
    /// </summary>
    private static Dictionary<int, int> GetDefaultCategoryGroupMapping()
    {
        return new Dictionary<int, int>
        {
            // Income (Group 1)
            { 1, 1 },   // Salary
            { 2, 1 },   // Bonus
            { 3, 1 },   // Investment Income
            { 4, 1 },   // Other Income
            // Housing (Group 2)
            { 10, 2 },  // Rent/Mortgage
            { 11, 2 },  // Utilities
            { 12, 2 },  // Insurance
            { 13, 2 },  // Subscriptions
            { 14, 2 },  // Loan Payments
            // Food & Dining (Group 3)
            { 20, 3 },  // Groceries
            { 21, 3 },  // Dining Out
            // Transportation (Group 4)
            { 22, 4 },  // Transportation
            // Health (Group 5)
            { 25, 5 },  // Healthcare
            // Entertainment (Group 6)
            { 23, 6 },  // Entertainment
            { 28, 6 },  // Education
            // Shopping (Group 7)
            { 24, 7 },  // Shopping
            { 29, 7 },  // Gifts
            // Financial (Group 8)
            { 30, 8 },  // Credit Payment
            { 31, 8 },  // Transfer
            // Other (Group 9)
            { 26, 9 },  // Travel
            { 27, 9 },  // Personal Care
            { 98, 9 },  // Other
            { 99, 9 },  // Uncategorized
        };
    }
}
