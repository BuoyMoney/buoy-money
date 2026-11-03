using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing budget category groups.
/// Allows organizing budget categories into custom groups for better visibility and organization.
/// </summary>
[Authorize]
[Route("api/budget")]
public class BudgetGroupController : BaseController
{
  private readonly IBudgetCategoryGroupService _groupService;
  private readonly ILogger<BudgetGroupController> _logger;

  public BudgetGroupController(
    IBudgetCategoryGroupService groupService,
    ILogger<BudgetGroupController> logger)
  {
    _groupService = groupService;
    _logger = logger;
  }

  // ============================================
  // Category Groups Endpoints
  // ============================================

  [HttpGet("{budgetId}/grouped")]
  public async Task<IActionResult> GetGroupedBudgetOverview(int budgetId)
  {
    try
    {
      var userId = GetCurrentUserId();
      var overview = await _groupService.GetGroupedBudgetOverviewAsync(budgetId, userId);

      return Ok(overview);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  [HttpGet("{budgetId}/groups")]
  public async Task<IActionResult> GetCategoryGroups(int budgetId)
  {
    try
    {
      var userId = GetCurrentUserId();
      var groups = await _groupService.GetCategoryGroupsAsync(budgetId, userId);

      return Ok(groups);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
  }

  [HttpPost("{budgetId}/groups")]
  public async Task<IActionResult> CreateCategoryGroup(int budgetId, [FromBody] CreateCategoryGroupRequest request)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(request.Name))
        return BadRequest(new { message = "Group name is required" });

      var userId = GetCurrentUserId();
      var group = await _groupService.CreateCategoryGroupAsync(budgetId, userId, request);

      return CreatedAtAction(nameof(GetCategoryGroups), new { budgetId }, group);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  [HttpPut("groups/{groupId}")]
  public async Task<IActionResult> UpdateCategoryGroup(int groupId, [FromBody] UpdateCategoryGroupRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var group = await _groupService.UpdateCategoryGroupAsync(groupId, userId, request);

      return Ok(group);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Delete a custom category group (moves categories to "Other")
  /// </summary>
  [HttpDelete("groups/{groupId}")]
  public async Task<IActionResult> DeleteCategoryGroup(int groupId)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _groupService.DeleteCategoryGroupAsync(groupId, userId);

      return NoContent();
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  [HttpPut("{budgetId}/groups/reorder")]
  public async Task<IActionResult> ReorderCategoryGroups(int budgetId, [FromBody] ReorderCategoryGroupsRequest request)
  {
    try
    {
      if (request.GroupIds == null || !request.GroupIds.Any())
        return BadRequest(new { message = "Group IDs are required" });

      var userId = GetCurrentUserId();
      await _groupService.ReorderCategoryGroupsAsync(budgetId, userId, request);

      return Ok(new { message = "Groups reordered successfully" });
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  // ============================================
  // Inline Category Editing Endpoints
  // ============================================

  /// <summary>
  /// Update category inline (amount, rollover, count-up settings)
  /// </summary>
  [HttpPatch("categories/{categoryId}")]
  public async Task<IActionResult> UpdateCategoryInline(int categoryId, [FromBody] InlineUpdateCategoryRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var category = await _groupService.UpdateCategoryInlineAsync(categoryId, userId, request);

      return Ok(category);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  [HttpPut("categories/{categoryId}/move")]
  public async Task<IActionResult> MoveCategoryToGroup(int categoryId, [FromBody] MoveCategoryToGroupRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      request.CategoryId = categoryId;  // Ensure consistency
      var category = await _groupService.MoveCategoryToGroupAsync(categoryId, userId, request);

      return Ok(category);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }
}
