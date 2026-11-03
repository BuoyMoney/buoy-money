using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.UserCategory;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

public class UserCategoryService : IUserCategoryService
{
    private readonly BuoyAIDbContext _context;
    private readonly IUserRepository _userRepository;
    private readonly IBudgetRepository _budgetRepository;
    private readonly INotificationService _notificationService;
    private readonly ILogger<UserCategoryService> _logger;

    public UserCategoryService(
        BuoyAIDbContext context, 
        IUserRepository userRepository,
        IBudgetRepository budgetRepository,
        INotificationService notificationService,
        ILogger<UserCategoryService> logger)
    {
        _context = context;
        _userRepository = userRepository;
        _budgetRepository = budgetRepository;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<List<UserCategoryDto>> GetUserCategoriesAsync(int userId, bool includeInactive = false, bool includePersonal = true)
    {
        try
        {
            IQueryable<UserCategory> query;

            if (!includePersonal)
            {
                // Family budget context: include non-personal categories from all family members.
                // Use a subquery rather than loading IDs into memory so the database handles
                // the join. Family sizes are typically small (2-10 members) so the subquery
                // result set is always tiny.
                var user = await _userRepository.GetByIdAsync(userId);
                if (user?.FamilyId != null)
                {
                    var fid = user.FamilyId.Value;
                    query = _context.UserCategories
                        .Where(c =>
                            !c.IsPersonal &&
                            _context.Users.Any(u => u.Id == c.UserId && u.FamilyId == fid));
                }
                else
                {
                    // Not in a family: just return own non-personal categories
                    query = _context.UserCategories
                        .Where(c => c.UserId == userId && !c.IsPersonal);
                }
            }
            else
            {
                query = _context.UserCategories
                    .Where(c => c.UserId == userId);
            }

            if (!includeInactive)
            {
                query = query.Where(c => c.IsActive);
            }

            var categories = await query
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name)
                .ToListAsync();

            // Get transaction counts for each category
            var categoryIds = categories.Select(c => c.Id).ToList();
            // In unified system, custom category IDs are 1000 + UserCategory.Id
            var unifiedCategoryIds = categoryIds.Select(id => 1000 + id).ToList();
            var transactionCounts = await _context.Transactions
                .Where(t => t.CategoryId.HasValue && unifiedCategoryIds.Contains(t.CategoryId.Value))
                .GroupBy(t => t.CategoryId!.Value)
                .Select(g => new { CategoryId = g.Key - 1000, Count = g.Count() })
                .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

            return categories.Select(c => MapToDto(c, transactionCounts.GetValueOrDefault(c.Id, 0))).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user categories for user {UserId}", userId);
            throw;
        }
    }

    public async Task<List<UserCategoryDto>> GetPersonalCategoriesAsync(int userId, bool includeInactive = false)
    {
        try
        {
            var query = _context.UserCategories
                .Where(c => c.UserId == userId && c.IsPersonal);

            if (!includeInactive)
            {
                query = query.Where(c => c.IsActive);
            }

            var categories = await query
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name)
                .ToListAsync();

            // Get transaction counts for each category
            var categoryIds = categories.Select(c => c.Id).ToList();
            // In unified system, custom category IDs are 1000 + UserCategory.Id
            var unifiedCategoryIds = categoryIds.Select(id => 1000 + id).ToList();
            var transactionCounts = await _context.Transactions
                .Where(t => t.CategoryId.HasValue && unifiedCategoryIds.Contains(t.CategoryId.Value))
                .GroupBy(t => t.CategoryId!.Value)
                .Select(g => new { CategoryId = g.Key - 1000, Count = g.Count() })
                .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

            return categories.Select(c => MapToDto(c, transactionCounts.GetValueOrDefault(c.Id, 0))).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting personal categories for user {UserId}", userId);
            throw;
        }
    }

    public async Task<UserCategoryDto?> GetUserCategoryAsync(int userId, int categoryId)
    {
        try
        {
            var category = await _context.UserCategories
                .FirstOrDefaultAsync(c => c.Id == categoryId && c.UserId == userId);

            if (category == null)
            {
                return null;
            }

            // In unified system, custom category IDs are 1000 + UserCategory.Id
            var unifiedCategoryId = 1000 + categoryId;
            var transactionCount = await _context.Transactions
                .CountAsync(t => t.CategoryId == unifiedCategoryId);

            return MapToDto(category, transactionCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user category {CategoryId} for user {UserId}", categoryId, userId);
            throw;
        }
    }

    public async Task<UserCategoryDto> CreateUserCategoryAsync(int userId, CreateUserCategoryRequest request)
    {
        try
        {
            // Check for duplicate name
            var existingCategory = await _context.UserCategories
                .FirstOrDefaultAsync(c => c.UserId == userId && c.Name.ToLower() == request.Name.ToLower());

            if (existingCategory != null)
            {
                throw new InvalidOperationException($"A category with the name '{request.Name}' already exists.");
            }

            // Get the max sort order if not specified
            var maxSortOrder = await _context.UserCategories
                .Where(c => c.UserId == userId)
                .MaxAsync(c => (int?)c.SortOrder) ?? -1;

            var category = new UserCategory
            {
                UserId = userId,
                Name = request.Name.Trim(),
                Color = request.Color,
                Icon = request.Icon?.Trim(),
                IsIncome = request.IsIncome,
                IsPersonal = request.IsPersonal,
                Description = request.Description?.Trim(),
                SortOrder = request.SortOrder > 0 ? request.SortOrder : maxSortOrder + 1,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.UserCategories.Add(category);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Created custom category '{Name}' for user {UserId} (IsPersonal: {IsPersonal})", 
                request.Name, userId, request.IsPersonal);

            return MapToDto(category, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user category for user {UserId}", userId);
            throw;
        }
    }

    public async Task<UserCategoryDto> UpdateUserCategoryAsync(int userId, int categoryId, UpdateUserCategoryRequest request)
    {
        try
        {
            var category = await _context.UserCategories
                .FirstOrDefaultAsync(c => c.Id == categoryId && c.UserId == userId);

            if (category == null)
            {
                throw new InvalidOperationException("Category not found.");
            }

            // Check for duplicate name if name is being changed
            if (!string.IsNullOrEmpty(request.Name) && request.Name.ToLower() != category.Name.ToLower())
            {
                var existingCategory = await _context.UserCategories
                    .FirstOrDefaultAsync(c => c.UserId == userId && c.Name.ToLower() == request.Name.ToLower() && c.Id != categoryId);

                if (existingCategory != null)
                {
                    throw new InvalidOperationException($"A category with the name '{request.Name}' already exists.");
                }
            }

            // Apply updates
            if (!string.IsNullOrEmpty(request.Name))
                category.Name = request.Name.Trim();
            
            if (!string.IsNullOrEmpty(request.Color))
                category.Color = request.Color;
            
            if (request.Icon != null)
                category.Icon = request.Icon.Trim();
            
            if (request.IsIncome.HasValue)
                category.IsIncome = request.IsIncome.Value;
            
            if (request.Description != null)
                category.Description = request.Description.Trim();
            
            if (request.SortOrder.HasValue)
                category.SortOrder = request.SortOrder.Value;
            
            if (request.IsActive.HasValue)
                category.IsActive = request.IsActive.Value;

            category.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // In unified system, custom category IDs are 1000 + UserCategory.Id
            var unifiedCategoryId = 1000 + categoryId;
            var transactionCount = await _context.Transactions
                .CountAsync(t => t.CategoryId == unifiedCategoryId);

            _logger.LogInformation("Updated custom category {CategoryId} for user {UserId}", categoryId, userId);

            return MapToDto(category, transactionCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user category {CategoryId} for user {UserId}", categoryId, userId);
            throw;
        }
    }

    public async Task<bool> DeleteUserCategoryAsync(int userId, int categoryId)
    {
        try
        {
            var category = await _context.UserCategories
                .FirstOrDefaultAsync(c => c.Id == categoryId && c.UserId == userId);

            if (category == null)
            {
                throw new InvalidOperationException("Category not found.");
            }

            // Soft delete - mark as inactive
            category.IsActive = false;
            category.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Soft deleted custom category {CategoryId} for user {UserId}", categoryId, userId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user category {CategoryId} for user {UserId}", categoryId, userId);
            throw;
        }
    }

    public async Task<bool> PermanentlyDeleteUserCategoryAsync(int userId, int categoryId)
    {
        try
        {
            var category = await _context.UserCategories
                .FirstOrDefaultAsync(c => c.Id == categoryId && c.UserId == userId);

            if (category == null)
            {
                throw new InvalidOperationException("Category not found.");
            }

            // Remove category reference from all transactions (unified CategoryId = 1000 + userCategoryId)
            var unifiedCategoryId = 1000 + categoryId;
            var transactionsWithCategory = await _context.Transactions
                .Where(t => t.CategoryId == unifiedCategoryId)
                .ToListAsync();

            foreach (var transaction in transactionsWithCategory)
            {
                transaction.CategoryId = null;
                transaction.UpdatedAt = DateTime.UtcNow;
            }

            // Permanently delete the category
            _context.UserCategories.Remove(category);

            await _context.SaveChangesAsync();

            _logger.LogInformation("Permanently deleted custom category {CategoryId} for user {UserId}, removed from {TransactionCount} transactions", 
                categoryId, userId, transactionsWithCategory.Count);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error permanently deleting user category {CategoryId} for user {UserId}", categoryId, userId);
            throw;
        }
    }

    public async Task<bool> AssignCategoryToTransactionAsync(int userId, int transactionId, int? userCategoryId)
    {
        try
        {
            // Get user info for family context
            var user = await _userRepository.GetByIdAsync(userId);
            
            // Verify the transaction belongs to this user or family member
            var transaction = await _context.Transactions
                .Include(t => t.Account)
                .FirstOrDefaultAsync(t => t.Id == transactionId);

            if (transaction == null)
            {
                throw new InvalidOperationException("Transaction not found.");
            }
            
            // Check ownership - allow if user owns the account OR is in the same family
            if (transaction.Account.UserId != userId)
            {
                if (user?.FamilyId == null)
                    throw new InvalidOperationException("Transaction not found.");
                
                var transactionOwner = await _userRepository.GetByIdAsync(transaction.Account.UserId);
                if (transactionOwner?.FamilyId != user.FamilyId)
                    throw new InvalidOperationException("Transaction not found.");
            }

            // If assigning a category, verify it belongs to this user
            UserCategory? category = null;
            if (userCategoryId.HasValue)
            {
                category = await _context.UserCategories
                    .FirstOrDefaultAsync(c => c.Id == userCategoryId.Value && c.UserId == userId && c.IsActive);

                if (category == null)
                {
                    throw new InvalidOperationException("Category not found or is not active.");
                }
            }

            // In unified system, custom category IDs are 1000 + UserCategory.Id
            transaction.CategoryId = userCategoryId.HasValue ? 1000 + userCategoryId.Value : null;
            transaction.IsUserCategorized = true;
            transaction.NeedsReview = false;
            transaction.UpdatedAt = DateTime.UtcNow;
            
            // Handle allowance notification for family members
            if (userCategoryId.HasValue && user?.FamilyId != null)
            {
                var allowanceInfo = await GetAllowanceInfoForCategoryAsync(userCategoryId.Value, user.FamilyId.Value);
                
                if (allowanceInfo != null && allowanceInfo.IsAllowance && allowanceInfo.AssignedUserId.HasValue)
                {
                    // If the current user is NOT the allowance owner, send notification
                    if (allowanceInfo.AssignedUserId.Value != userId)
                    {
                        _logger.LogInformation("Non-owner {UserId} categorized transaction {TransactionId} to allowance. Notifying owner {OwnerId}",
                            userId, transactionId, allowanceInfo.AssignedUserId.Value);
                        
                        var categorizedByUserName = user != null 
                            ? $"{user.FirstName} {user.LastName}".Trim() 
                            : "Someone";
                        
                        await _notificationService.CreateTransactionSubCategoryNotificationAsync(
                            allowanceInfo.AssignedUserId.Value,
                            transactionId,
                            transaction.MerchantName ?? transaction.Description ?? "Transaction",
                            allowanceInfo.CategoryName ?? "Your Allowance",
                            categorizedByUserName
                        );
                    }
                }
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Assigned custom category {CategoryId} to transaction {TransactionId} for user {UserId}", 
                userCategoryId, transactionId, userId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error assigning category to transaction {TransactionId} for user {UserId}", transactionId, userId);
            throw;
        }
    }
    
    /// <summary>
    /// Gets allowance info for a user category - whether it's an allowance and who it's assigned to
    /// </summary>
    private async Task<AllowanceInfoInternal?> GetAllowanceInfoForCategoryAsync(int userCategoryId, int familyId)
    {
        // Find the family budget with this category as an allowance
        var familyBudgets = await _budgetRepository.GetByFamilyIdAsync(familyId);
        
        foreach (var budget in familyBudgets)
        {
            // In unified system, custom category IDs are 1000 + UserCategory.Id
            var unifiedCategoryId = 1000 + userCategoryId;
            var budgetCategory = budget.Categories.FirstOrDefault(c => 
                c.CategoryId == unifiedCategoryId && c.IsAllowance);
            
            if (budgetCategory != null)
            {
                return new AllowanceInfoInternal
                {
                    IsAllowance = true,
                    AssignedUserId = budgetCategory.AssignedUserId,
                    CategoryName = budgetCategory.CategoryNav?.Name
                };
            }
        }
        
        return null;
    }
    
    private class AllowanceInfoInternal
    {
        public bool IsAllowance { get; set; }
        public int? AssignedUserId { get; set; }
        public string? CategoryName { get; set; }
    }

    public async Task<bool> ReorderCategoriesAsync(int userId, List<int> categoryIds)
    {
        try
        {
            var categories = await _context.UserCategories
                .Where(c => c.UserId == userId && categoryIds.Contains(c.Id))
                .ToListAsync();

            if (categories.Count != categoryIds.Count)
            {
                throw new InvalidOperationException("One or more categories not found.");
            }

            for (int i = 0; i < categoryIds.Count; i++)
            {
                var category = categories.First(c => c.Id == categoryIds[i]);
                category.SortOrder = i;
                category.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Reordered {Count} categories for user {UserId}", categoryIds.Count, userId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reordering categories for user {UserId}", userId);
            throw;
        }
    }

    public async Task<AllowanceInfoResponse> GetAllowanceInfoAsync(int userId, int userCategoryId)
    {
        try
        {
            var user = await _userRepository.GetByIdAsync(userId);
            
            // Check if this category is used as an allowance in any family budget
            // In unified system, custom category IDs are 1000 + UserCategory.Id
            var unifiedCategoryId = 1000 + userCategoryId;
            var budgetCategory = await _budgetRepository.GetBudgetCategoryByCategoryIdAsync(unifiedCategoryId);
            
            if (budgetCategory == null || !budgetCategory.AssignedUserId.HasValue)
            {
                // Not an allowance category
                return new AllowanceInfoResponse
                {
                    IsAllowance = false,
                    IsCurrentUserOwner = false,
                    CategoryName = null,
                    PersonalCategories = []
                };
            }
            
            // It's an allowance - check if current user is the owner
            var isOwner = budgetCategory.AssignedUserId.Value == userId;
            
            var response = new AllowanceInfoResponse
            {
                IsAllowance = true,
                IsCurrentUserOwner = isOwner,
                CategoryName = budgetCategory.CategoryNav?.Name,
                PersonalCategories = []
            };
            
            // If the user is the owner, include their personal categories
            if (isOwner)
            {
                var personalCategories = await GetPersonalCategoriesAsync(userId, includeInactive: false);
                response.PersonalCategories = personalCategories;
            }
            
            _logger.LogInformation("Allowance info for category {UserCategoryId}: IsAllowance={IsAllowance}, IsCurrentUserOwner={IsOwner}",
                userCategoryId, response.IsAllowance, response.IsCurrentUserOwner);
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting allowance info for category {UserCategoryId}, user {UserId}", userCategoryId, userId);
            throw;
        }
    }

    private static UserCategoryDto MapToDto(UserCategory category, int transactionCount)
    {
        return new UserCategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Color = category.Color,
            Icon = category.Icon,
            IsIncome = category.IsIncome,
            IsPersonal = category.IsPersonal,
            Description = category.Description,
            SortOrder = category.SortOrder,
            IsActive = category.IsActive,
            TransactionCount = transactionCount,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt
        };
    }
}
