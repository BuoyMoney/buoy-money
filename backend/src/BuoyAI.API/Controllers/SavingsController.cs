using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SavingsController : BaseController
{
    private readonly ISavingsService _savingsService;
    private readonly IFamilyAuthorizationService _authService;
    private readonly ILogger<SavingsController> _logger;

    public SavingsController(
        ISavingsService savingsService,
        IFamilyAuthorizationService authService,
        ILogger<SavingsController> logger)
    {
        _savingsService = savingsService;
        _authService = authService;
        _logger = logger;
    }

    /// <summary>
    /// Get savings overview dashboard data for the current user
    /// </summary>
    [HttpGet("overview")]
    public async Task<ActionResult<SavingsOverviewDto>> GetSavingsOverview()
    {
        var userId = GetCurrentUserId();
        var overview = await _savingsService.GetSavingsOverviewAsync(userId);
        return Ok(overview);
    }

    /// <summary>
    /// Get all savings goals for the current user
    /// </summary>
    [HttpGet("goals")]
    public async Task<ActionResult<IEnumerable<SavingsGoalDto>>> GetSavingsGoals()
    {
        var userId = GetCurrentUserId();
        var goals = await _savingsService.GetSavingsGoalsAsync(userId);
        return Ok(goals);
    }

    /// <summary>
    /// Get a specific savings goal by ID
    /// </summary>
    [HttpGet("goals/{goalId}")]
    public async Task<ActionResult<SavingsGoalDto>> GetSavingsGoal(int goalId)
    {
        var userId = GetCurrentUserId();

        // Validate user has read access to this goal
        var authResult = await _authService.CanAccessGoalAsync(userId, goalId, ResourcePermission.Read);
        if (!authResult.IsAuthorized)
        {
            // Check if the denial is because the resource wasn't found
            if (authResult.Reason?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                return NotFound($"Savings goal with ID {goalId} not found");
            }
            _logger.LogWarning("User {UserId} denied access to goal {GoalId}: {Reason}",
                userId, goalId, authResult.Reason);
            return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to access this savings goal" });
        }

        var goal = await _savingsService.GetSavingsGoalAsync(goalId, userId);
        if (goal == null)
            return NotFound($"Savings goal with ID {goalId} not found");

        return Ok(goal);
    }

    /// <summary>
    /// Create a new savings goal
    /// </summary>
    [HttpPost("goals")]
    public async Task<ActionResult<SavingsGoalDto>> CreateSavingsGoal([FromBody] CreateSavingsGoalRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Guard against out-of-range enum values: JSON binding will happily coerce any
            // integer into the enum, but only Personal/Family are meaningful. An undefined
            // value would match neither budget scope and silently receive no auto-allocations.
            if (!Enum.IsDefined(typeof(BuoyAI.Core.Enums.SavingsGoalType), request.Type))
                return BadRequest("Invalid savings goal type.");

            var userId = GetCurrentUserId();
            var createdGoal = await _savingsService.CreateSavingsGoalAsync(userId, request);

            return CreatedAtAction(
                nameof(GetSavingsGoal),
                new { goalId = createdGoal.Id },
                createdGoal);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument creating savings goal");
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation creating savings goal");
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Update an existing savings goal
    /// </summary>
    [HttpPut("goals/{goalId}")]
    public async Task<ActionResult<SavingsGoalDto>> UpdateSavingsGoal(int goalId, [FromBody] UpdateSavingsGoalRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (request.Type.HasValue &&
                !Enum.IsDefined(typeof(BuoyAI.Core.Enums.SavingsGoalType), request.Type.Value))
                return BadRequest("Invalid savings goal type.");

            var userId = GetCurrentUserId();

            // Validate user has edit access to this goal
            var authResult = await _authService.CanAccessGoalAsync(userId, goalId, ResourcePermission.Edit);
            if (!authResult.IsAuthorized)
            {
                // Check if the denial is because the resource wasn't found
                if (authResult.Reason?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return NotFound($"Savings goal with ID {goalId} not found");
                }
                _logger.LogWarning("User {UserId} denied edit access to goal {GoalId}: {Reason}",
                    userId, goalId, authResult.Reason);
                return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to edit this savings goal" });
            }

            var updatedGoal = await _savingsService.UpdateSavingsGoalAsync(goalId, request, userId);
            return Ok(updatedGoal);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Savings goal not found: {GoalId}", goalId);
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation updating savings goal {GoalId}", goalId);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Reset a savings goal's current balance back to zero. The goal
    /// itself (name, target) and its allocation history are preserved;
    /// CurrentAmount is zeroed and IsCompleted is cleared. A completed goal
    /// rejoining the auto-allocation pool keeps only as much of its
    /// allocation % as still fits under 100%. Requires Edit permission
    /// (same as updating the goal).
    /// </summary>
    [HttpPost("goals/{goalId}/reset")]
    public async Task<ActionResult<SavingsGoalDto>> ResetSavingsGoal(int goalId)
    {
        try
        {
            var userId = GetCurrentUserId();

            var authResult = await _authService.CanAccessGoalAsync(userId, goalId, ResourcePermission.Edit);
            if (!authResult.IsAuthorized)
            {
                if (authResult.Reason?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                    return NotFound($"Savings goal with ID {goalId} not found");

                _logger.LogWarning("User {UserId} denied reset access to goal {GoalId}: {Reason}",
                    userId, goalId, authResult.Reason);
                return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to reset this savings goal" });
            }

            var updatedGoal = await _savingsService.ResetSavingsGoalAsync(goalId);
            return Ok(updatedGoal);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Savings goal not found: {GoalId}", goalId);
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Delete a savings goal
    /// </summary>
    [HttpDelete("goals/{goalId}")]
    public async Task<ActionResult> DeleteSavingsGoal(int goalId)
    {
        try
        {
            var userId = GetCurrentUserId();

            // Validate user has delete access to this goal
            var authResult = await _authService.CanAccessGoalAsync(userId, goalId, ResourcePermission.Delete);
            if (!authResult.IsAuthorized)
            {
                // Check if the denial is because the resource wasn't found
                if (authResult.Reason?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return NotFound($"Savings goal with ID {goalId} not found");
                }
                _logger.LogWarning("User {UserId} denied delete access to goal {GoalId}: {Reason}",
                    userId, goalId, authResult.Reason);
                return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to delete this savings goal" });
            }

            await _savingsService.DeleteSavingsGoalAsync(goalId);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Savings goal not found: {GoalId}", goalId);
            return NotFound(ex.Message);
        }
        catch (DbUpdateException ex)
        {
            // Defense-in-depth: even with the transfer FKs relaxed to ON DELETE SET NULL (migration
            // 0014), surface any residual referential-integrity block as a 409 instead of a raw 500.
            _logger.LogWarning(ex, "Could not delete savings goal {GoalId} due to a referential constraint", goalId);
            return Conflict(new { message = "This savings goal is referenced by transfer history and could not be deleted." });
        }
    }

    /// <summary>
    /// Manually allocate money to a savings goal
    /// </summary>
    [HttpPost("goals/{goalId}/allocate")]
    public async Task<ActionResult<SavingsGoalDto>> AllocateToGoal(int goalId, [FromBody] ManualAllocationRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (request.Amount <= 0)
                return BadRequest("Allocation amount must be greater than zero");

            var userId = GetCurrentUserId();

            // Validate user has allocate access to this goal (all non-ReadOnly family members can allocate)
            var authResult = await _authService.CanAccessGoalAsync(userId, goalId, ResourcePermission.Allocate);
            if (!authResult.IsAuthorized)
            {
                _logger.LogWarning("User {UserId} denied allocate access to goal {GoalId}: {Reason}",
                    userId, goalId, authResult.Reason);
                return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to allocate to this savings goal" });
            }

            var updatedGoal = await _savingsService.AllocateToGoalAsync(goalId, request, userId);
            return Ok(updatedGoal);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Savings goal not found: {GoalId}", goalId);
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Sets a goal's balance to the balance of the account holding it. Only for a goal that is
    /// alone in its account; with siblings the client must reconcile them together instead.
    /// </summary>
    [HttpPost("goals/{goalId}/sync-to-account")]
    public async Task<ActionResult<SavingsGoalDto>> SyncGoalToAccount(int goalId)
    {
        try
        {
            var userId = GetCurrentUserId();

            var authResult = await _authService.CanAccessGoalAsync(userId, goalId, ResourcePermission.Allocate);
            if (!authResult.IsAuthorized)
            {
                _logger.LogWarning("User {UserId} denied sync access to goal {GoalId}: {Reason}",
                    userId, goalId, authResult.Reason);
                return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to modify this savings goal" });
            }

            return Ok(await _savingsService.SyncGoalToAccountAsync(goalId, userId));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Savings goal not found: {GoalId}", goalId);
            return NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Unlinked, wrong account type, or more than one goal on the account — all of which are
            // the caller asking for something that doesn't make sense, not a server fault.
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Redistributes balances across the goals sharing one account, in one transaction.
    /// </summary>
    [HttpPost("accounts/{accountId}/reconcile")]
    public async Task<ActionResult<ReconcileResultDto>> ReconcileAccount(
        int accountId, [FromBody] ReconcileAccountRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var userId = GetCurrentUserId();
            return Ok(await _savingsService.ReconcileAccountAsync(accountId, request, userId));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Get progress details for a specific goal
    /// </summary>
    [HttpGet("goals/{goalId}/progress")]
    public async Task<ActionResult<SavingsGoalDto>> GetGoalProgress(int goalId)
    {
        try
        {
            var userId = GetCurrentUserId();

            // Validate user has read access to this goal
            var authResult = await _authService.CanAccessGoalAsync(userId, goalId, ResourcePermission.Read);
            if (!authResult.IsAuthorized)
            {
                // Check if the denial is because the resource wasn't found
                if (authResult.Reason?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return NotFound($"Savings goal with ID {goalId} not found");
                }
                _logger.LogWarning("User {UserId} denied access to goal {GoalId} progress: {Reason}",
                    userId, goalId, authResult.Reason);
                return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to view this savings goal" });
            }

            var goalProgress = await _savingsService.CalculateGoalProgressAsync(goalId);
            return Ok(goalProgress);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Savings goal not found: {GoalId}", goalId);
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Get user achievements
    /// </summary>
    [HttpGet("achievements")]
    public async Task<ActionResult<IEnumerable<SavingsAchievementDto>>> GetAchievements()
    {
        var userId = GetCurrentUserId();
        var achievements = await _savingsService.GetUserAchievementsAsync(userId);
        return Ok(achievements);
    }

    /// <summary>
    /// Trigger automatic allocations for a budget (typically called by budget service)
    /// </summary>
    [HttpPost("allocations/process/{budgetId}")]
    public async Task<ActionResult> ProcessAutomaticAllocations(int budgetId)
    {
        var userId = GetCurrentUserId();

        // Processing allocations MOVES money into savings goals, so it requires Allocate permission —
        // not Read. Gating on Read let a ReadOnly family member mutate savings balances (and repeat the
        // call to stack duplicate allocations) on any family budget they could merely view.
        var authResult = await _authService.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Allocate);
        if (!authResult.IsAuthorized)
        {
            _logger.LogWarning("User {UserId} denied access to process allocations for budget {BudgetId}: {Reason}",
                userId, budgetId, authResult.Reason);
            return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to process allocations for this budget" });
        }

        await _savingsService.ProcessAutomaticAllocationsAsync(budgetId);
        return Ok(new { message = "Automatic allocations processed successfully" });
    }

    /// <summary>
    /// Get leftover budget amount for a specific budget
    /// </summary>
    [HttpGet("leftover/{budgetId}")]
    public async Task<ActionResult<decimal>> GetLeftoverBudget(int budgetId)
    {
        var userId = GetCurrentUserId();

        // Validate user has read access to this budget
        var authResult = await _authService.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Read);
        if (!authResult.IsAuthorized)
        {
            _logger.LogWarning("User {UserId} denied access to budget {BudgetId} leftover: {Reason}",
                userId, budgetId, authResult.Reason);
            return StatusCode(403, new { message = authResult.Reason ?? "You don't have permission to view this budget" });
        }

        var leftover = await _savingsService.CalculateLeftoverBudgetAsync(budgetId);
        return Ok(new { leftoverAmount = leftover });
    }

    /// <summary>
    /// Check for new achievements (can be called manually)
    /// </summary>
    [HttpPost("achievements/check")]
    public async Task<ActionResult<IEnumerable<SavingsAchievementDto>>> CheckForNewAchievements()
    {
        var userId = GetCurrentUserId();
        var newAchievements = await _savingsService.CheckForNewAchievementsAsync(userId);
        return Ok(newAchievements);
    }
}
