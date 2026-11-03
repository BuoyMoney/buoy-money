using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for budget fund transfers, deficit resolution, and rollover management.
/// Handles moving funds between categories, to savings goals, and managing rollover balances.
/// </summary>
[Authorize]
[Route("api/budget")]
public class BudgetTransferController : BaseController
{
  private readonly IBudgetService _budgetService;
  private readonly IBudgetTransferService _transferService;
  private readonly IFamilyAuthorizationService _authService;
  private readonly IUserRepository _userRepository;
  private readonly ILogger<BudgetTransferController> _logger;

  public BudgetTransferController(
    IBudgetService budgetService,
    IBudgetTransferService transferService,
    IFamilyAuthorizationService authService,
    IUserRepository userRepository,
    ILogger<BudgetTransferController> logger)
  {
    _budgetService = budgetService;
    _transferService = transferService;
    _authService = authService;
    _userRepository = userRepository;
    _logger = logger;
  }

  // Moving money / resetting rollovers requires edit access — ReadOnly family members
  // (e.g. kids) may view budgets but must not transfer funds. Throws UnauthorizedAccessException
  // (mapped to 403 by each action's catch) when the caller lacks edit permission.
  private async Task ValidateBudgetEditAsync(int budgetId, int userId)
  {
    var auth = await _authService.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Edit);
    if (!auth.IsAuthorized)
      throw new UnauthorizedAccessException(auth.Reason ?? "You don't have permission to modify this budget");
  }

  private async Task ValidateCategoryEditAsync(int categoryId, int userId)
  {
    var category = await _budgetService.GetBudgetCategoryAsync(categoryId)
      ?? throw new KeyNotFoundException($"Budget category {categoryId} not found");
    await ValidateBudgetEditAsync(category.BudgetId, userId);
  }

  // ============================================
  // Fund Transfer Endpoints
  // ============================================

  /// <summary>
  /// Transfer funds from another category to this category
  /// </summary>
  [HttpPost("categories/{targetCategoryId}/transfer")]
  public async Task<IActionResult> TransferFunds(int targetCategoryId, [FromBody] TransferFundsRequest request)
  {
    try
    {
      if (request.Amount <= 0)
        return BadRequest(new { message = "Transfer amount must be positive" });

      if (request.SourceCategoryId == targetCategoryId)
        return BadRequest(new { message = "Source and target categories must be different" });

      var userId = GetCurrentUserId();
      await ValidateCategoryEditAsync(targetCategoryId, userId);
      var result = await _transferService.TransferFundsAsync(targetCategoryId, userId, request);

      return Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
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

  /// <summary>
  /// Transfer funds from a category to a savings goal
  /// </summary>
  /// <remarks>
  /// Allows users to move remaining budget funds to savings goals.
  /// This can be triggered manually or automatically at period end
  /// when rollover mode is set to AllocateToSavings.
  /// </remarks>
  [HttpPost("categories/{categoryId}/transfer-to-savings")]
  public async Task<IActionResult> TransferToSavings(int categoryId, [FromBody] TransferToSavingsRequest request)
  {
    try
    {
      if (request.Amount <= 0)
        return BadRequest(new { message = "Transfer amount must be positive" });

      if (request.SavingsGoalId <= 0)
        return BadRequest(new { message = "Savings goal is required" });

      var userId = GetCurrentUserId();
      await ValidateCategoryEditAsync(categoryId, userId);
      var result = await _transferService.TransferToSavingsAsync(categoryId, userId, request);

      return Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
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

  /// <summary>
  /// Get transfer history for a budget
  /// </summary>
  [HttpGet("{budgetId}/transfers")]
  public async Task<IActionResult> GetTransferHistory(int budgetId)
  {
    try
    {
      var userId = GetCurrentUserId();
      var transfers = await _transferService.GetTransferHistoryAsync(budgetId, userId);

      return Ok(transfers);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
  }

  // ============================================
  // Deficit Resolution Endpoints
  // ============================================

  /// <summary>
  /// Resolve a budget deficit by pulling from savings, transferring from another category,
  /// or carrying as negative rollover
  /// </summary>
  /// <remarks>
  /// Allows users to resolve overspending in a category using one or more strategies:
  /// - Pull from default savings goal
  /// - Transfer from another category with surplus
  /// - Carry remaining deficit as negative rollover to next period
  /// </remarks>
  [HttpPost("{budgetId}/resolve-deficit")]
  public async Task<IActionResult> ResolveDeficit(int budgetId, [FromBody] ResolveDeficitRequest request)
  {
    try
    {
      if (request.CategoryId <= 0)
        return BadRequest(new { message = "Category ID is required" });

      if (request.DeficitAmount <= 0)
        return BadRequest(new { message = "Deficit amount must be positive" });

      if (request.Actions == null || request.Actions.Count == 0)
        return BadRequest(new { message = "At least one resolution action is required" });

      // Validate total action amounts match deficit
      var totalActionAmount = request.Actions.Sum(a => a.Amount);
      if (totalActionAmount != request.DeficitAmount)
      {
        // User-facing message: format in the caller's own currency (bare ":C"
        // renders "¤" in the locale-less container and pins the seller's currency).
        var currency = (await _userRepository.GetByIdAsync(GetCurrentUserId()))?.Currency;
        return BadRequest(new { message = $"Resolution amounts ({CurrencyCodes.Format(totalActionAmount, currency)}) must equal deficit amount ({CurrencyCodes.Format(request.DeficitAmount, currency)})" });
      }

      var userId = GetCurrentUserId();
      await ValidateBudgetEditAsync(budgetId, userId);
      var result = await _transferService.ResolveDeficitAsync(budgetId, userId, request);

      return Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
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

  // ============================================
  // Rollover Reset Endpoints
  // ============================================

  /// <summary>
  /// Reset rollover balance for a single category
  /// </summary>
  [HttpPost("categories/{categoryId}/reset-rollover")]
  public async Task<IActionResult> ResetCategoryRollover(int categoryId)
  {
    try
    {
      var userId = GetCurrentUserId();
      await ValidateCategoryEditAsync(categoryId, userId);
      var result = await _transferService.ResetCategoryRolloverAsync(categoryId, userId);

      return Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
  }

  /// <summary>
  /// Reset all rollover balances for a budget (Fresh Start)
  /// </summary>
  [HttpPost("{budgetId}/reset-all-rollovers")]
  public async Task<IActionResult> ResetAllRollovers(int budgetId)
  {
    try
    {
      var userId = GetCurrentUserId();
      await ValidateBudgetEditAsync(budgetId, userId);
      var result = await _transferService.ResetAllRolloversAsync(budgetId, userId);

      return Ok(result);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
  }
}
