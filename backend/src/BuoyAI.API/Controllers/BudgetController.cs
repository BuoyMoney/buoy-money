using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

[Authorize]
public class BudgetController : BaseController
{
  private readonly IBudgetService _budgetService;
  private readonly ICountUpBudgetService _countUpBudgetService;
  private readonly IFamilyAuthorizationService _authService;
  private readonly ILogger<BudgetController> _logger;

  public BudgetController(
    IBudgetService budgetService,
    ICountUpBudgetService countUpBudgetService,
    IFamilyAuthorizationService authService,
    ILogger<BudgetController> logger)
  {
    _budgetService = budgetService;
    _countUpBudgetService = countUpBudgetService;
    _authService = authService;
    _logger = logger;
  }

  [HttpGet]
  public async Task<IActionResult> GetBudgets()
  {
    var userId = GetCurrentUserId();
    var budgets = await _budgetService.GetBudgetsAsync(userId);

    return Ok(budgets);
  }

  [HttpGet("active")]
  public async Task<IActionResult> GetActiveBudget()
  {
    var userId = GetCurrentUserId();
    var budget = await _budgetService.GetActiveBudgetAsync(userId);

    return HandleServiceResponse(budget, "No active budget found");
  }

  [HttpPost]
  public async Task<IActionResult> CreateBudget([FromBody] CreateBudgetRequest request)
  {
    if (string.IsNullOrEmpty(request.Name))
    {
      return BadRequest(new { message = "Budget name is required" });
    }

    if (request.Categories?.Any() != true)
    {
      return BadRequest(new { message = "At least one budget category is required" });
    }

    var userId = GetCurrentUserId();
    var budget = await _budgetService.CreateBudgetAsync(userId, request);

    return CreatedAtAction(nameof(GetActiveBudget), budget);
  }

  [HttpPut("{id}")]
  public async Task<IActionResult> UpdateBudget(int id, [FromBody] CreateBudgetRequest request)
  {
    try
    {
      if (string.IsNullOrEmpty(request.Name))
      {
        return BadRequest(new { message = "Budget name is required" });
      }

      var userId = GetCurrentUserId();
      var budget = await _budgetService.UpdateBudgetAsync(id, userId, request);

      return Ok(budget);
    }
    catch (ArgumentException)
    {
      return NotFound(new { message = "Budget not found" });
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> DeleteBudget(int id)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _budgetService.DeleteBudgetAsync(id, userId);

      return NoContent();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
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

  [HttpPut("category/{categoryId}")]
  public async Task<IActionResult> UpdateBudgetCategory(int categoryId, [FromBody] UpdateBudgetCategoryRequest request)
  {
    var userId = GetCurrentUserId();

    // Authorize against the owning budget first. The category id is user-supplied, so without
    // this check any authenticated user could edit any budget's category (IDOR).
    var existing = await _budgetService.GetBudgetCategoryAsync(categoryId);
    if (existing == null)
      return NotFound(new { message = $"Budget category with ID {categoryId} not found." });

    var authResult = await _authService.CanAccessBudgetAsync(userId, existing.BudgetId, ResourcePermission.Edit);
    if (!authResult.IsAuthorized)
      return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to modify this budget" });

    var category = await _budgetService.UpdateBudgetCategoryAsync(categoryId, request);
    return Ok(category);
  }

  [HttpGet("{budgetId}/countup")]
  public async Task<IActionResult> GetCountUpBudgetStatus(int budgetId)
  {
    var userId = GetCurrentUserId();
    var countUpCategories = await _countUpBudgetService.GetCountUpCategoriesForBudgetAsync(budgetId, userId);

    return Ok(countUpCategories);
  }

  [HttpPost("category/{categoryId}/enable-countup")]
  public async Task<IActionResult> EnableCountUpBudgeting(int categoryId, [FromBody] EnableCountUpRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();

      // Get the category to find its budget for authorization
      var category = await _budgetService.GetBudgetCategoryAsync(categoryId);
      if (category == null)
        return BadRequest(new { message = $"Budget category with ID {categoryId} not found." });

      // Validate user has edit access to the budget
      var authResult = await _authService.CanAccessBudgetAsync(userId, category.BudgetId, ResourcePermission.Edit);
      if (!authResult.IsAuthorized)
      {
        _logger.LogWarning("User {UserId} denied access to enable count-up for category {CategoryId}: {Reason}",
          userId, categoryId, authResult.Reason);
        return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to modify this budget" });
      }

      var result = await _countUpBudgetService.EnableCountUpBudgetingAsync(categoryId, request.Enable);
      return Ok(result);
    }
    catch (ArgumentException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpPut("category/{categoryId}/countup-settings")]
  public async Task<IActionResult> UpdateCountUpSettings(int categoryId, [FromBody] CountUpSettingsRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();

      // Get the category to find its budget for authorization
      var category = await _budgetService.GetBudgetCategoryAsync(categoryId);
      if (category == null)
        return BadRequest(new { message = $"Budget category with ID {categoryId} not found." });

      // Validate user has edit access to the budget
      var authResult = await _authService.CanAccessBudgetAsync(userId, category.BudgetId, ResourcePermission.Edit);
      if (!authResult.IsAuthorized)
      {
        _logger.LogWarning("User {UserId} denied access to update count-up settings for category {CategoryId}: {Reason}",
          userId, categoryId, authResult.Reason);
        return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to modify this budget" });
      }

      var result = await _countUpBudgetService.UpdateCountUpSettingsAsync(categoryId, request);
      return Ok(result);
    }
    catch (ArgumentException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpGet("category/{categoryId}/countup")]
  public async Task<IActionResult> GetCountUpCategoryStatus(int categoryId)
  {
    try
    {
      var userId = GetCurrentUserId();

      // Get the category to find its budget for authorization
      var category = await _budgetService.GetBudgetCategoryAsync(categoryId);
      if (category == null)
        return BadRequest(new { message = $"Budget category with ID {categoryId} not found." });

      // Validate user has read access to the budget
      var authResult = await _authService.CanAccessBudgetAsync(userId, category.BudgetId, ResourcePermission.Read);
      if (!authResult.IsAuthorized)
      {
        _logger.LogWarning("User {UserId} denied access to count-up status for category {CategoryId}: {Reason}",
          userId, categoryId, authResult.Reason);
        return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to view this budget" });
      }

      var result = await _countUpBudgetService.CalculateCountUpStatusAsync(categoryId);
      return Ok(result);
    }
    catch (ArgumentException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  // ============================================
  // Hierarchical Budget Endpoints
  // ============================================

  /// <summary>
  /// Get a specific budget by ID (with access check)
  /// </summary>
  [HttpGet("{id}")]
  public async Task<IActionResult> GetBudgetById(int id)
  {
    var userId = GetCurrentUserId();
    var budget = await _budgetService.GetBudgetByIdAsync(id, userId);

    if (budget == null)
      return NotFound(new { message = "Budget not found or access denied" });

    return Ok(budget);
  }

  /// <summary>
  /// Get all budgets accessible to the user (owned, shared, and family)
  /// </summary>
  [HttpGet("accessible")]
  public async Task<IActionResult> GetAccessibleBudgets()
  {
    var userId = GetCurrentUserId();
    var budgets = await _budgetService.GetAccessibleBudgetsAsync(userId);

    return Ok(budgets);
  }

  /// <summary>
  /// Get the user's primary budget
  /// </summary>
  [HttpGet("primary")]
  public async Task<IActionResult> GetPrimaryBudget()
  {
    var userId = GetCurrentUserId();
    var budget = await _budgetService.GetPrimaryBudgetAsync(userId);

    if (budget == null)
      return NotFound(new { message = "No primary budget found" });

    return Ok(budget);
  }

  /// <summary>
  /// Set a budget as the user's primary budget
  /// </summary>
  [HttpPut("{id}/set-primary")]
  public async Task<IActionResult> SetPrimaryBudget(int id)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _budgetService.SetPrimaryBudgetAsync(id, userId);

      return Ok(new { message = "Budget set as primary" });
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  // ============================================
  // Family Budget Endpoints
  // ============================================

  /// <summary>
  /// Get all budgets for the current user's family (auto-looks up family)
  /// </summary>
  [HttpGet("family")]
  public async Task<IActionResult> GetMyFamilyBudgets()
  {
    var userId = GetCurrentUserId();
    var budgets = await _budgetService.GetMyFamilyBudgetsAsync(userId);

    return Ok(budgets);
  }

  /// <summary>
  /// Get all budgets for a specific family
  /// </summary>
  [HttpGet("family/{familyId}")]
  public async Task<IActionResult> GetFamilyBudgets(int familyId)
  {
    try
    {
      var userId = GetCurrentUserId();
      var budgets = await _budgetService.GetFamilyBudgetsAsync(familyId, userId);

      return Ok(budgets);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
  }

  /// <summary>
  /// Get an overview of all family members' budgets
  /// </summary>
  /// <remarks>
  /// Returns budget summaries for all family members showing:
  /// - Each member's shared budgets
  /// - Total allocations and spending per member
  /// - Rollover settings for each budget
  /// </remarks>
  [HttpGet("family/overview")]
  public async Task<IActionResult> GetFamilyBudgetOverview()
  {
    try
    {
      var userId = GetCurrentUserId();
      var overview = await _budgetService.GetFamilyBudgetOverviewAsync(userId);

      return Ok(overview);
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Create a family budget visible to all family members
  /// </summary>
  [HttpPost("family/{familyId}")]
  public async Task<IActionResult> CreateFamilyBudget(int familyId, [FromBody] CreateBudgetRequest request)
  {
    try
    {
      if (string.IsNullOrEmpty(request.Name))
        return BadRequest(new { message = "Budget name is required" });

      if (request.Categories?.Any() != true)
        return BadRequest(new { message = "At least one budget category is required" });

      var userId = GetCurrentUserId();

      var canCreate = await _authService.CanCreateFamilyBudgetAsync(userId);
      if (!canCreate.IsAuthorized)
        return StatusCode(403, new { message = canCreate.Reason ?? "Only family admins can create family budgets" });

      var budget = await _budgetService.CreateFamilyBudgetAsync(userId, familyId, request);

      return CreatedAtAction(nameof(GetBudgetById), new { id = budget.Id }, budget);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
  }

  /// <summary>
  /// Create an allowance budget funded by a parent budget
  /// </summary>
  [HttpPost("{parentBudgetId}/allowance")]
  public async Task<IActionResult> CreateAllowanceBudget(int parentBudgetId, [FromBody] CreateAllowanceBudgetRequest request)
  {
    try
    {
      if (string.IsNullOrEmpty(request.Name))
        return BadRequest(new { message = "Budget name is required" });

      if (request.AssignedUserId <= 0)
        return BadRequest(new { message = "Assigned user is required" });

      var userId = GetCurrentUserId();

      var canCreate = await _authService.CanCreateFamilyBudgetAsync(userId);
      if (!canCreate.IsAuthorized)
        return StatusCode(403, new { message = canCreate.Reason ?? "Only family admins can create allowances" });

      var budget = await _budgetService.CreateAllowanceBudgetAsync(userId, parentBudgetId, request.AssignedUserId, request);

      return CreatedAtAction(nameof(GetBudgetById), new { id = budget.Id }, budget);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (ArgumentException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Spawn a personal budget from an existing family expense category (RFC #793).
  /// Owner/BudgetAdmin only. Re-purposes the category as the funding link while keeping its
  /// real CategoryId so its existing transactions stay attributed to the family budget.
  /// </summary>
  [HttpPost("category/{categoryId}/spawn-personal")]
  public async Task<IActionResult> SpawnPersonalBudget(int categoryId, [FromBody] SpawnPersonalBudgetRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();

      // Allocation action → Owner/BudgetAdmin only.
      var canCreate = await _authService.CanCreateFamilyBudgetAsync(userId);
      if (!canCreate.IsAuthorized)
        return StatusCode(403, new { message = canCreate.Reason ?? "Only Owner and BudgetAdmin can create family budgets" });

      var budget = await _budgetService.SpawnPersonalBudgetFromCategoryAsync(userId, categoryId, request);

      return CreatedAtAction(nameof(GetBudgetById), new { id = budget.Id }, budget);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      // Category already linked or not a family category.
      return BadRequest(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Create a child budget with rollover settings
  /// </summary>
  /// <remarks>
  /// Creates a child budget under a parent budget with configurable rollover behavior:
  /// - RolloverToSelf: Surplus stays in the child budget (default)
  /// - AllocateToSavings: Surplus is transferred to a savings goal
  /// - PromptUser: User is prompted to decide at period end
  /// </remarks>
  [HttpPost("{parentBudgetId}/children")]
  public async Task<IActionResult> CreateChildBudget(int parentBudgetId, [FromBody] CreateChildBudgetRequest request)
  {
    try
    {
      if (string.IsNullOrEmpty(request.Name))
        return BadRequest(new { message = "Budget name is required" });

      if (request.FundingAmount <= 0)
        return BadRequest(new { message = "Funding amount must be positive" });

      var userId = GetCurrentUserId();

      var canCreate = await _authService.CanCreateFamilyBudgetAsync(userId);
      if (!canCreate.IsAuthorized)
        return StatusCode(403, new { message = canCreate.Reason ?? "Only family admins can create child budgets" });

      var budget = await _budgetService.CreateChildBudgetAsync(userId, parentBudgetId, request);

      return CreatedAtAction(nameof(GetBudgetById), new { id = budget.Id }, budget);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (ArgumentException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Update a child budget's settings including rollover mode
  /// </summary>
  /// <remarks>
  /// Updates settings for a child budget:
  /// - Name: Optional new name
  /// - FundingAmount: Optional new funding amount
  /// - IsSharedWithFamily: Whether family members can view this budget
  /// - RolloverMode: How to handle surplus at period end
  /// - DefaultSavingsGoalId: Target savings goal if rollover mode is AllocateToSavings
  /// </remarks>
  [HttpPatch("{budgetId}/settings")]
  public async Task<IActionResult> UpdateChildBudgetSettings(int budgetId, [FromBody] UpdateChildBudgetRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var budget = await _budgetService.UpdateChildBudgetAsync(budgetId, userId, request);

      return Ok(budget);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (ArgumentException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  // ============================================
  // Budget Sharing Endpoints
  // ============================================

  /// <summary>
  /// Get all shares for a budget
  /// </summary>
  [HttpGet("{budgetId}/shares")]
  public async Task<IActionResult> GetBudgetShares(int budgetId)
  {
    try
    {
      var userId = GetCurrentUserId();
      var shares = await _budgetService.GetBudgetSharesAsync(budgetId, userId);

      return Ok(shares);
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

  /// <summary>
  /// Share a budget with another user
  /// </summary>
  [HttpPost("{budgetId}/shares")]
  public async Task<IActionResult> ShareBudget(int budgetId, [FromBody] ShareBudgetRequest request)
  {
    try
    {
      if (request.ShareWithUserId <= 0)
        return BadRequest(new { message = "User to share with is required" });

      var userId = GetCurrentUserId();
      var share = await _budgetService.ShareBudgetAsync(budgetId, userId, request.ShareWithUserId, request.CanEdit, request.CanViewTransactions);

      return CreatedAtAction(nameof(GetBudgetShares), new { budgetId }, share);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return Conflict(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Remove a budget share
  /// </summary>
  [HttpDelete("{budgetId}/shares/{shareWithUserId}")]
  public async Task<IActionResult> RemoveBudgetShare(int budgetId, int shareWithUserId)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _budgetService.RemoveBudgetShareAsync(budgetId, userId, shareWithUserId);

      return NoContent();
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
  // Transaction Claiming Endpoints
  // ============================================

  /// <summary>
  /// Claim a transaction for personal tracking (for family transactions)
  /// </summary>
  [HttpPost("transactions/{transactionId}/claim")]
  public async Task<IActionResult> ClaimTransaction(int transactionId)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _budgetService.ClaimTransactionAsync(transactionId, userId);

      return Ok(new { message = "Transaction claimed successfully" });
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

  /// <summary>
  /// Unclaim a previously claimed transaction
  /// </summary>
  [HttpDelete("transactions/{transactionId}/claim")]
  public async Task<IActionResult> UnclaimTransaction(int transactionId)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _budgetService.UnclaimTransactionAsync(transactionId, userId);

      return Ok(new { message = "Transaction unclaimed successfully" });
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

  // Transfer endpoints moved to BudgetTransferController
  // Group endpoints moved to BudgetGroupController

  // ── Period History (multi-month navigation) ──────────────────

  /// <summary>
  /// List all completed period snapshots for a budget, newest first.
  /// Used to populate the period picker in the Flutter budget UI.
  /// </summary>
  [HttpGet("{budgetId}/periods")]
  public async Task<IActionResult> GetBudgetPeriods(int budgetId)
  {
    try
    {
      var userId = GetCurrentUserId();
      var periods = await _budgetService.GetBudgetPeriodsAsync(budgetId, userId);
      return Ok(periods);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting period history for budget {BudgetId}", budgetId);
      return StatusCode(500, new { message = "An error occurred while retrieving period history" });
    }
  }

  /// <summary>
  /// Get full detail (including per-category data) for a specific completed period snapshot.
  /// </summary>
  [HttpGet("{budgetId}/periods/{snapshotId:int}")]
  public async Task<IActionResult> GetBudgetPeriodDetail(int budgetId, int snapshotId)
  {
    try
    {
      var userId = GetCurrentUserId();
      var detail = await _budgetService.GetBudgetPeriodDetailAsync(budgetId, snapshotId, userId);
      return HandleServiceResponse(detail, "Period snapshot not found");
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error getting period detail {SnapshotId} for budget {BudgetId}", snapshotId, budgetId);
      return StatusCode(500, new { message = "An error occurred while retrieving period detail" });
    }
  }

  /// <summary>
  /// Update the allocated amount for a single category inside a historical period snapshot.
  /// This lets users rectify budget allocations for a previous month they couldn't edit in time.
  /// </summary>
  [HttpPatch("{budgetId}/periods/{snapshotId:int}/categories/{categorySnapshotId:int}")]
  public async Task<IActionResult> UpdatePeriodCategoryAmount(
    int budgetId, int snapshotId, int categorySnapshotId,
    [FromBody] UpdatePeriodCategoryAmountRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var updated = await _budgetService.UpdatePeriodCategoryAmountAsync(
        budgetId, snapshotId, categorySnapshotId, request.AllocatedAmount, userId);
      return Ok(updated);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error updating period category {CategorySnapshotId} in snapshot {SnapshotId}", categorySnapshotId, snapshotId);
      return StatusCode(500, new { message = "An error occurred while updating category amount" });
    }
  }
}
