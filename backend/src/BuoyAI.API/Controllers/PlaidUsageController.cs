using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs.Plaid;

namespace BuoyAI.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class PlaidUsageController : BaseController
{
    private readonly IPlaidUsageTracker _plaidUsageTracker;
    private readonly IPlaidConnectionRepository _plaidConnectionRepository;
    private readonly ILogger<PlaidUsageController> _logger;

    public PlaidUsageController(
        IPlaidUsageTracker plaidUsageTracker,
        IPlaidConnectionRepository plaidConnectionRepository,
        ILogger<PlaidUsageController> logger)
    {
        _plaidUsageTracker = plaidUsageTracker;
        _plaidConnectionRepository = plaidConnectionRepository;
        _logger = logger;
    }

    /// <summary>
    /// Verifies that the given Plaid itemId belongs to the supplied userId
    /// before exposing per-item usage data. Returns false for unknown or
    /// foreign items to prevent IDOR-style enumeration of other users'
    /// Plaid connections.
    /// </summary>
    private async Task<bool> UserOwnsItemAsync(string itemId, int userId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        var connection = await _plaidConnectionRepository.GetByItemIdAsync(itemId);
        return connection != null && connection.UserId == userId;
    }

    /// <summary>
    /// Get usage statistics for a Plaid item
    /// </summary>
    [HttpGet("stats/{itemId}")]
    public async Task<ActionResult<PlaidUsageStats>> GetUsageStats(string itemId)
    {
        // Resolve the current userId OUTSIDE the try/catch so missing-auth
        // throws (UnauthorizedAccessException) propagate to the global
        // handler as 401 instead of being flattened to 500 below.
        var userId = GetCurrentUserId();
        if (!await UserOwnsItemAsync(itemId, userId))
        {
            return NotFound();
        }

        var stats = await _plaidUsageTracker.GetUsageStats(itemId);
        return Ok(stats);
    }

    /// <summary>
    /// Check if balance requests are available for an item
    /// </summary>
    [HttpGet("can-balance/{itemId}")]
    public async Task<ActionResult<bool>> CanMakeBalanceRequest(string itemId)
    {
        var userId = GetCurrentUserId();
        if (!await UserOwnsItemAsync(itemId, userId))
        {
            return NotFound();
        }

        var canMake = await _plaidUsageTracker.CanMakeBalanceRequest(itemId);
        return Ok(canMake);
    }

    /// <summary>
    /// Check if refresh requests are available for an item
    /// </summary>
    [HttpGet("can-refresh/{itemId}")]
    public async Task<ActionResult<bool>> CanMakeRefreshRequest(string itemId)
    {
        var userId = GetCurrentUserId();
        if (!await UserOwnsItemAsync(itemId, userId))
        {
            return NotFound();
        }

        var canMake = await _plaidUsageTracker.CanMakeRefreshRequest(itemId);
        return Ok(canMake);
    }
}
