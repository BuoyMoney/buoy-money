using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.UserCategory;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing user-defined custom transaction categories
/// </summary>
[Authorize]
public class UserCategoryController : BaseController
{
    private readonly IUserCategoryService _userCategoryService;
    private readonly ILogger<UserCategoryController> _logger;

    public UserCategoryController(
        IUserCategoryService userCategoryService,
        ILogger<UserCategoryController> logger)
    {
        _userCategoryService = userCategoryService;
        _logger = logger;
    }

    /// <summary>
    /// Get all custom categories for the current user
    /// </summary>
    /// <param name="includeInactive">Whether to include inactive (deleted) categories</param>
    /// <param name="includePersonal">Whether to include personal/private categories (set to false for family budget editing)</param>
    [HttpGet]
    public async Task<IActionResult> GetCategories(
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool includePersonal = true)
    {
        var userId = GetCurrentUserId();
        var categories = await _userCategoryService.GetUserCategoriesAsync(userId, includeInactive, includePersonal);
        return Ok(categories);
    }

    /// <summary>
    /// Get only personal/private custom categories for the current user (for personal budget wizard)
    /// </summary>
    /// <param name="includeInactive">Whether to include inactive (deleted) categories</param>
    [HttpGet("personal")]
    public async Task<IActionResult> GetPersonalCategories([FromQuery] bool includeInactive = false)
    {
        var userId = GetCurrentUserId();
        var categories = await _userCategoryService.GetPersonalCategoriesAsync(userId, includeInactive);
        return Ok(categories);
    }

    /// <summary>
    /// Check if a user category is an allowance and if the current user is the owner.
    /// Returns personal categories if the user is the owner (for sub-categorization).
    /// </summary>
    /// <param name="id">The user category ID to check</param>
    [HttpGet("{id}/allowance-info")]
    public async Task<IActionResult> GetAllowanceInfo(int id)
    {
        var userId = GetCurrentUserId();
        var allowanceInfo = await _userCategoryService.GetAllowanceInfoAsync(userId, id);
        return Ok(allowanceInfo);
    }

    /// <summary>
    /// Get a specific custom category by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetCategory(int id)
    {
        var userId = GetCurrentUserId();
        var category = await _userCategoryService.GetUserCategoryAsync(userId, id);
        return HandleServiceResponse(category, "Category not found");
    }

    /// <summary>
    /// Create a new custom category
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateCategory([FromBody] CreateUserCategoryRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = GetCurrentUserId();
            var category = await _userCategoryService.CreateUserCategoryAsync(userId, request);
            return CreatedAtAction(nameof(GetCategory), new { id = category.Id }, category);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation creating category for user");
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing custom category
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCategory(int id, [FromBody] UpdateUserCategoryRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = GetCurrentUserId();
            var category = await _userCategoryService.UpdateUserCategoryAsync(userId, id, request);
            return Ok(category);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation updating category {CategoryId}", id);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Soft delete a custom category (mark as inactive)
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        try
        {
            var userId = GetCurrentUserId();
            await _userCategoryService.DeleteUserCategoryAsync(userId, id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation deleting category {CategoryId}", id);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Permanently delete a custom category and remove from all transactions
    /// </summary>
    [HttpDelete("{id}/permanent")]
    public async Task<IActionResult> PermanentlyDeleteCategory(int id)
    {
        try
        {
            var userId = GetCurrentUserId();
            await _userCategoryService.PermanentlyDeleteUserCategoryAsync(userId, id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation permanently deleting category {CategoryId}", id);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Assign a custom category to a transaction
    /// </summary>
    [HttpPost("assign/{transactionId}")]
    public async Task<IActionResult> AssignCategoryToTransaction(int transactionId, [FromBody] AssignUserCategoryRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            await _userCategoryService.AssignCategoryToTransactionAsync(userId, transactionId, request.UserCategoryId);
            return Ok(new { message = "Category assigned successfully" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation assigning category to transaction {TransactionId}", transactionId);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Reorder categories
    /// </summary>
    [HttpPost("reorder")]
    public async Task<IActionResult> ReorderCategories([FromBody] List<int> categoryIds)
    {
        try
        {
            if (categoryIds == null || categoryIds.Count == 0)
            {
                return BadRequest(new { message = "Category IDs are required" });
            }

            var userId = GetCurrentUserId();
            await _userCategoryService.ReorderCategoriesAsync(userId, categoryIds);
            return Ok(new { message = "Categories reordered successfully" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation reordering categories");
            return BadRequest(new { message = ex.Message });
        }
    }
}
