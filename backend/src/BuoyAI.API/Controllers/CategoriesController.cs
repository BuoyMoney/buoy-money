using BuoyAI.Core.DTOs.Category;
using BuoyAI.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// API controller for category operations.
/// </summary>
[Authorize]
public class CategoriesController : BaseController
{
    private readonly ICategoryService _categoryService;
    private readonly ILogger<CategoriesController> _logger;
    
    public CategoriesController(
        ICategoryService categoryService,
        ILogger<CategoriesController> logger)
    {
        _categoryService = categoryService;
        _logger = logger;
    }
    
    /// <summary>
    /// Get all categories accessible to the current user.
    /// Returns system categories, personal categories, and family categories (if applicable).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetCurrentUserId();
        var result = await _categoryService.GetAllCategoriesAsync(userId);
        return Ok(result);
    }
    
    /// <summary>
    /// Get a single category by ID.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var userId = GetCurrentUserId();
        var result = await _categoryService.GetCategoryByIdAsync(id, userId);
        return HandleServiceResponse(result, "Category not found");
    }
    
    /// <summary>
    /// Create a new category.
    /// By default creates a personal category. Set IsFamily=true to create a family-shared category.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCategoryDto dto)
    {
        var userId = GetCurrentUserId();
        
        try
        {
            var result = await _categoryService.CreateCategoryAsync(dto, userId);
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
    
    /// <summary>
    /// Update an existing category.
    /// Cannot update system categories.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCategoryDto dto)
    {
        var userId = GetCurrentUserId();
        
        try
        {
            var result = await _categoryService.UpdateCategoryAsync(id, dto, userId);
            return HandleServiceResponse(result, "Category not found or cannot be modified");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
    
    /// <summary>
    /// Delete a category.
    /// Cannot delete system categories.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = GetCurrentUserId();
        var result = await _categoryService.DeleteCategoryAsync(id, userId);
        
        if (!result)
        {
            return NotFound(new { message = "Category not found or cannot be deleted" });
        }
        
        return NoContent();
    }
    
    /// <summary>
    /// Get category names for a list of IDs.
    /// Useful for bulk lookups in reports and transaction lists.
    /// </summary>
    [HttpPost("names")]
    public async Task<IActionResult> GetNames([FromBody] int[] categoryIds)
    {
        var result = await _categoryService.GetCategoryNamesAsync(categoryIds);
        return Ok(result);
    }
}
