using BuoyAI.Core.DTOs.Category;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Core.Interfaces.Services;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service implementation for category operations.
/// </summary>
public class CategoryService : ICategoryService
{
    private readonly ICategoryRepository _categoryRepository;
    private readonly IUserRepository _userRepository;
    private readonly IFamilyRepository _familyRepository;
    private readonly ICategorizationRuleService _categorizationRuleService;
    private readonly ILogger<CategoryService> _logger;

    public CategoryService(
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        IFamilyRepository familyRepository,
        ICategorizationRuleService categorizationRuleService,
        ILogger<CategoryService> logger)
    {
        _categoryRepository = categoryRepository;
        _userRepository = userRepository;
        _familyRepository = familyRepository;
        _categorizationRuleService = categorizationRuleService;
        _logger = logger;
    }
    
    public async Task<CategoriesResponseDto> GetAllCategoriesAsync(int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
        {
            _logger.LogWarning("User {UserId} not found when fetching categories", userId);
            return new CategoriesResponseDto();
        }
        
        var allCategories = await _categoryRepository.GetAllForUserAsync(userId, user.FamilyId);
        
        var systemCategories = allCategories.Where(c => c.IsSystem).ToList();
        var personalCategories = allCategories.Where(c => c.UserId == userId).ToList();
        var familyCategories = allCategories.Where(c => c.FamilyId.HasValue).ToList();
        
        // Determine user's family role for edit permissions on family categories
        FamilyMemberRole? familyRole = null;
        if (user.FamilyId.HasValue)
        {
            var member = await _familyRepository.GetMemberAsync(user.FamilyId.Value, userId);
            familyRole = member?.Role;
        }
        
        return new CategoriesResponseDto
        {
            SystemCategories = systemCategories.Select(c => MapToDto(c, userId, familyRole)).ToList(),
            PersonalCategories = personalCategories.Select(c => MapToDto(c, userId, familyRole)).ToList(),
            FamilyCategories = familyCategories.Select(c => MapToDto(c, userId, familyRole)).ToList(),
            AllCategories = allCategories.Select(c => MapToDto(c, userId, familyRole)).ToList()
        };
    }
    
    public async Task<CategoryDto?> GetCategoryByIdAsync(int categoryId, int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null) return null;
        
        var canAccess = await _categoryRepository.ExistsAndAccessibleAsync(categoryId, userId, user.FamilyId);
        if (!canAccess) return null;
        
        var category = await _categoryRepository.GetByIdAsync(categoryId);
        if (category == null) return null;
        
        // Get family role for permissions
        FamilyMemberRole? familyRole = null;
        if (user.FamilyId.HasValue)
        {
            var member = await _familyRepository.GetMemberAsync(user.FamilyId.Value, userId);
            familyRole = member?.Role;
        }
        
        return MapToDto(category, userId, familyRole);
    }
    
    public async Task<CategoryDto> CreateCategoryAsync(CreateCategoryDto dto, int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId) 
            ?? throw new InvalidOperationException("User not found");
        
        // Determine scope
        int? targetFamilyId = null;
        if (dto.IsFamily)
        {
            if (!user.FamilyId.HasValue)
            {
                throw new InvalidOperationException("Cannot create family category: user is not in a family");
            }
            
            // Check user has permission to create family categories
            var member = await _familyRepository.GetMemberAsync(user.FamilyId.Value, userId);
            if (member == null || member.Role == FamilyMemberRole.ReadOnly)
            {
                throw new InvalidOperationException("Insufficient permissions to create family categories");
            }
            
            targetFamilyId = user.FamilyId;
        }
        
        // Check name uniqueness
        var isUnique = await _categoryRepository.IsNameUniqueAsync(
            dto.Name, 
            dto.IsFamily ? null : userId, 
            targetFamilyId);
            
        if (!isUnique)
        {
            throw new InvalidOperationException($"A category named '{dto.Name}' already exists");
        }
        
        // Get next available ID (1000+)
        var nextId = await _categoryRepository.GetNextUserCategoryIdAsync();
        
        var category = new Category
        {
            Id = nextId,
            Name = dto.Name.Trim(),
            Color = dto.Color,
            Icon = dto.Icon,
            IsIncome = dto.IsIncome,
            IsSystem = false,
            SortOrder = dto.SortOrder,
            Description = dto.Description,
            UserId = dto.IsFamily ? null : userId,
            FamilyId = targetFamilyId,
            CreatedByUserId = userId
        };
        
        var created = await _categoryRepository.AddAsync(category);
        _logger.LogInformation("Created category {CategoryId} '{Name}' for user {UserId}", 
            created.Id, created.Name, userId);
        
        // Get family role for CanEdit flag
        FamilyMemberRole? familyRole = null;
        if (user.FamilyId.HasValue)
        {
            var member = await _familyRepository.GetMemberAsync(user.FamilyId.Value, userId);
            familyRole = member?.Role;
        }
        
        return MapToDto(created, userId, familyRole);
    }
    
    public async Task<CategoryDto?> UpdateCategoryAsync(int categoryId, UpdateCategoryDto dto, int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null) return null;
        
        var category = await _categoryRepository.GetByIdAsync(categoryId);
        if (category == null) return null;
        
        // Cannot edit system categories
        if (category.IsSystem)
        {
            _logger.LogWarning("Attempted to edit system category {CategoryId}", categoryId);
            return null;
        }
        
        // Check permissions
        if (category.UserId.HasValue && category.UserId != userId)
        {
            _logger.LogWarning("User {UserId} attempted to edit another user's category {CategoryId}", 
                userId, categoryId);
            return null;
        }
        
        if (category.FamilyId.HasValue)
        {
            if (user.FamilyId != category.FamilyId)
            {
                _logger.LogWarning("User {UserId} attempted to edit category {CategoryId} from different family", 
                    userId, categoryId);
                return null;
            }
            
            // Check family permissions
            var member = await _familyRepository.GetMemberAsync(category.FamilyId.Value, userId);
            if (member == null || member.Role == FamilyMemberRole.ReadOnly)
            {
                _logger.LogWarning("User {UserId} lacks permissions to edit family category {CategoryId}", 
                    userId, categoryId);
                return null;
            }
        }
        
        // Check name uniqueness if name is being changed
        if (!string.IsNullOrEmpty(dto.Name) && dto.Name != category.Name)
        {
            var isUnique = await _categoryRepository.IsNameUniqueAsync(
                dto.Name, 
                category.UserId, 
                category.FamilyId, 
                categoryId);
                
            if (!isUnique)
            {
                throw new InvalidOperationException($"A category named '{dto.Name}' already exists");
            }
        }
        
        // Apply updates
        if (!string.IsNullOrEmpty(dto.Name)) category.Name = dto.Name.Trim();
        if (!string.IsNullOrEmpty(dto.Color)) category.Color = dto.Color;
        if (dto.Icon != null) category.Icon = dto.Icon;
        if (dto.IsIncome.HasValue) category.IsIncome = dto.IsIncome.Value;
        if (dto.SortOrder.HasValue) category.SortOrder = dto.SortOrder.Value;
        if (dto.Description != null) category.Description = dto.Description;
        
        await _categoryRepository.UpdateAsync(category);
        _logger.LogInformation("Updated category {CategoryId}", categoryId);
        
        // Get family role for CanEdit flag
        FamilyMemberRole? familyRole = null;
        if (user.FamilyId.HasValue)
        {
            var member = await _familyRepository.GetMemberAsync(user.FamilyId.Value, userId);
            familyRole = member?.Role;
        }
        
        return MapToDto(category, userId, familyRole);
    }
    
    public async Task<bool> DeleteCategoryAsync(int categoryId, int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null) return false;
        
        var category = await _categoryRepository.GetByIdAsync(categoryId);
        if (category == null) return false;
        
        // Cannot delete system categories
        if (category.IsSystem)
        {
            _logger.LogWarning("Attempted to delete system category {CategoryId}", categoryId);
            return false;
        }
        
        // Check permissions
        if (category.UserId.HasValue && category.UserId != userId)
        {
            _logger.LogWarning("User {UserId} attempted to delete another user's category {CategoryId}", 
                userId, categoryId);
            return false;
        }
        
        if (category.FamilyId.HasValue)
        {
            if (user.FamilyId != category.FamilyId)
            {
                return false;
            }
            
            // Check family permissions - need at least BudgetAdmin to delete
            var member = await _familyRepository.GetMemberAsync(category.FamilyId.Value, userId);
            if (member == null || member.Role < FamilyMemberRole.BudgetAdmin)
            {
                _logger.LogWarning("User {UserId} lacks permissions to delete family category {CategoryId}", 
                    userId, categoryId);
                return false;
            }
        }
        
        // Deactivate any categorization rules that assign this category BEFORE
        // deleting it. The AssignCategoryId FK is SetNull, so without this the rule
        // would survive with a null category, keep matching merchants, and silently
        // force transactions to Uncategorized (or clear NeedsReview with no category).
        // Transactions/budgets referencing the category are left with a null CategoryId
        // by the FK's SetNull behavior (treated as Uncategorized on read).
        await _categorizationRuleService.DeactivateRulesForCategoryAsync(categoryId);

        await _categoryRepository.DeleteAsync(categoryId);
        _logger.LogInformation("Deleted category {CategoryId}", categoryId);

        return true;
    }
    
    public async Task<string> GetCategoryNameAsync(int categoryId)
    {
        var category = await _categoryRepository.GetByIdAsync(categoryId);
        return category?.Name ?? "Unknown";
    }
    
    public async Task<bool> ValidateCategoryAccessAsync(int categoryId, int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null) return false;
        
        return await _categoryRepository.ExistsAndAccessibleAsync(categoryId, userId, user.FamilyId);
    }
    
    public async Task<Dictionary<int, string>> GetCategoryNamesAsync(IEnumerable<int> categoryIds)
    {
        var categories = await _categoryRepository.GetByIdsAsync(categoryIds);
        return categories.ToDictionary(c => c.Id, c => c.Name);
    }
    
    private static CategoryDto MapToDto(Category category, int userId, FamilyMemberRole? familyRole)
    {
        var scope = category.IsSystem ? "system" 
            : category.FamilyId.HasValue ? "family" 
            : "personal";
        
        // Determine if user can edit this category
        var canEdit = false;
        if (!category.IsSystem)
        {
            if (category.UserId == userId)
            {
                // User owns this personal category
                canEdit = true;
            }
            else if (category.FamilyId.HasValue && familyRole.HasValue)
            {
                // Family category - need at least Member role to edit
                canEdit = familyRole >= FamilyMemberRole.Member;
            }
        }
        
        return new CategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            Color = category.Color,
            Icon = category.Icon,
            IsIncome = category.IsIncome,
            IsSystem = category.IsSystem,
            SortOrder = category.SortOrder,
            Description = category.Description,
            Scope = scope,
            FamilyId = category.FamilyId,
            CanEdit = canEdit
        };
    }
}
