using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using BuoyAI.API.Attributes;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for AI-powered financial visualizations.
/// Provides endpoints for generating, viewing, and managing visualizations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class VisualizationsController : BaseController
{
    private readonly IVisualizationService _visualizationService;
    private readonly ILogger<VisualizationsController> _logger;

    public VisualizationsController(
        IVisualizationService visualizationService,
        ILogger<VisualizationsController> logger)
    {
        _visualizationService = visualizationService;
        _logger = logger;
    }

    /// <summary>
    /// Generate a new AI visualization for the user.
    /// The AI will gather relevant financial data and create a personalized chart.
    /// </summary>
    [HttpPost("generate")]
  [RequiresAiConsent]
    public async Task<IActionResult> Generate([FromBody] GenerateVisualizationRequest request)
    {
        var userId = GetCurrentUserId();
        _logger.LogInformation("User {UserId} requested visualization generation", userId);
        
        var result = await _visualizationService.GenerateVisualizationAsync(userId, request);
        
        if (!result.Success)
        {
            return BadRequest(result.ErrorMessage ?? "Failed to generate visualization");
        }
        
        return Ok(result.Visualization);
    }

    /// <summary>
    /// Get user's visualization history with optional filters.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetVisualizations(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] bool? starredOnly = null,
        [FromQuery] string? category = null,
        [FromQuery] bool includeDismissed = false)
    {
        var userId = GetCurrentUserId();
        
        var request = new GetVisualizationsRequest
        {
            Page = page,
            PageSize = Math.Min(pageSize, 50), // Cap at 50
            StarredOnly = starredOnly,
            Category = category,
            IncludeDismissed = includeDismissed
        };
        
        var result = await _visualizationService.GetVisualizationsAsync(userId, request);
        return Ok(result);
    }

    /// <summary>
    /// Get starred visualizations only.
    /// </summary>
    [HttpGet("starred")]
    public async Task<IActionResult> GetStarred(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        var userId = GetCurrentUserId();
        
        var request = new GetVisualizationsRequest
        {
            Page = page,
            PageSize = Math.Min(pageSize, 50),
            StarredOnly = true,
            IncludeDismissed = false
        };
        
        var result = await _visualizationService.GetVisualizationsAsync(userId, request);
        return Ok(result);
    }

    /// <summary>
    /// Get a specific visualization by ID.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var userId = GetCurrentUserId();
        
        var result = await _visualizationService.GetVisualizationByIdAsync(userId, id);
        
        if (result == null)
        {
            return NotFound();
        }
        
        // Record the view
        await _visualizationService.RecordViewAsync(userId, id);
        
        return Ok(result);
    }

    /// <summary>
    /// Toggle star status on a visualization.
    /// </summary>
    [HttpPost("{id}/star")]
    public async Task<IActionResult> ToggleStar(int id)
    {
        var userId = GetCurrentUserId();
        
        var isStarred = await _visualizationService.ToggleStarAsync(userId, id);
        
        return Ok(new { isStarred });
    }

    /// <summary>
    /// Dismiss a visualization (hide from default list).
    /// </summary>
    [HttpPost("{id}/dismiss")]
    public async Task<IActionResult> Dismiss(int id)
    {
        var userId = GetCurrentUserId();
        
        var success = await _visualizationService.DismissVisualizationAsync(userId, id);
        
        if (!success)
        {
            return NotFound();
        }
        
        return NoContent();
    }

    /// <summary>
    /// Get visualization categories for filtering.
    /// </summary>
    [HttpGet("categories")]
    public IActionResult GetCategories()
    {
        var categories = new[]
        {
            new { value = "spending", label = "Spending" },
            new { value = "savings", label = "Savings" },
            new { value = "budget", label = "Budgets" },
            new { value = "income", label = "Income" },
            new { value = "trends", label = "Trends" },
            new { value = "goals", label = "Goals" }
        };
        
        return Ok(categories);
    }
}
