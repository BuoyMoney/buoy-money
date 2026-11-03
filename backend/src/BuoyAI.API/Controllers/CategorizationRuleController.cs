using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.CategorizationRule;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing user-defined categorization rules
/// </summary>
[Authorize]
public class CategorizationRuleController : BaseController
{
    private readonly ICategorizationRuleService _ruleService;
    private readonly ILogger<CategorizationRuleController> _logger;

    public CategorizationRuleController(
        ICategorizationRuleService ruleService,
        ILogger<CategorizationRuleController> logger)
    {
        _ruleService = ruleService;
        _logger = logger;
    }

    /// <summary>
    /// Get all categorization rules for the current user
    /// </summary>
    /// <param name="includeInactive">Whether to include inactive rules</param>
    [HttpGet]
    public async Task<IActionResult> GetRules([FromQuery] bool includeInactive = false)
    {
        var userId = GetCurrentUserId();
        var rules = await _ruleService.GetUserRulesAsync(userId, includeInactive);
        return Ok(rules);
    }

    /// <summary>
    /// Get a specific categorization rule by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetRule(int id)
    {
        var userId = GetCurrentUserId();
        var rule = await _ruleService.GetRuleAsync(userId, id);
        return HandleServiceResponse(rule, "Rule not found");
    }

    /// <summary>
    /// Create a new categorization rule
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateRule([FromBody] CreateCategorizationRuleRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = GetCurrentUserId();
            var rule = await _ruleService.CreateRuleAsync(userId, request);
            return CreatedAtAction(nameof(GetRule), new { id = rule.Id }, rule);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Create a categorization rule from an existing transaction
    /// </summary>
    [HttpPost("from-transaction")]
    public async Task<IActionResult> CreateRuleFromTransaction([FromBody] CreateRuleFromTransactionRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = GetCurrentUserId();
            var rule = await _ruleService.CreateRuleFromTransactionAsync(userId, request);
            return CreatedAtAction(nameof(GetRule), new { id = rule.Id }, rule);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Create a categorization rule when user recategorizes a transaction ("Always categorize as" feature)
    /// </summary>
    [HttpPost("from-categorization")]
    public async Task<IActionResult> CreateRuleFromCategorization([FromBody] CreateRuleFromCategorizationRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = GetCurrentUserId();
            var rule = await _ruleService.CreateRuleFromCategorizationAsync(userId, request);
            return CreatedAtAction(nameof(GetRule), new { id = rule.Id }, rule);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing categorization rule
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateRule(int id, [FromBody] UpdateCategorizationRuleRequest request)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = GetCurrentUserId();
            var rule = await _ruleService.UpdateRuleAsync(userId, id, request);
            return Ok(rule);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Delete a categorization rule
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRule(int id)
    {
        var userId = GetCurrentUserId();
        var success = await _ruleService.DeleteRuleAsync(userId, id);

        if (!success)
        {
            return NotFound(new { message = "Rule not found" });
        }

        return NoContent();
    }

    /// <summary>
    /// Toggle a rule's active status
    /// </summary>
    [HttpPost("{id}/toggle")]
    public async Task<IActionResult> ToggleRule(int id)
    {
        try
        {
            var userId = GetCurrentUserId();
            var rule = await _ruleService.ToggleRuleAsync(userId, id);
            return Ok(rule);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Reorder rules by changing their priorities
    /// </summary>
    [HttpPost("reorder")]
    public async Task<IActionResult> ReorderRules([FromBody] List<int> ruleIds)
    {
        try
        {
            var userId = GetCurrentUserId();
            var success = await _ruleService.ReorderRulesAsync(userId, ruleIds);

            if (!success)
            {
                return BadRequest(new { message = "Failed to reorder rules" });
            }

            return Ok(new { message = "Rules reordered successfully" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get rule statistics for the current user
    /// </summary>
    [HttpGet("statistics")]
    public async Task<IActionResult> GetStatistics()
    {
        var userId = GetCurrentUserId();
        var stats = await _ruleService.GetRuleStatisticsAsync(userId);
        return Ok(stats);
    }
}
