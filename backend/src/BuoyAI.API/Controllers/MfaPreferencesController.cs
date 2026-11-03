using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class MfaPreferencesController : BaseController
{
    private readonly IMfaPreferencesService _mfaPreferencesService;
    private readonly ILogger<MfaPreferencesController> _logger;

    public MfaPreferencesController(
        IMfaPreferencesService mfaPreferencesService,
        ILogger<MfaPreferencesController> logger)
    {
        _mfaPreferencesService = mfaPreferencesService;
        _logger = logger;
    }

    /// <summary>
    /// Get user's MFA preferences
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPreferences()
    {
        var userId = GetCurrentUserId();
        var preferences = await _mfaPreferencesService.GetPreferencesAsync(userId);
        return Ok(preferences);
    }

    /// <summary>
    /// Update user's MFA preferences
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdateMfaPreferencesDto request)
    {
        try
        {
            var userId = GetCurrentUserId();
            await _mfaPreferencesService.UpdatePreferencesAsync(userId, request);
            return Ok(new { message = "MFA preferences updated successfully" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Get available MFA methods for current user
    /// </summary>
    [HttpGet("available-methods")]
    public async Task<IActionResult> GetAvailableMethods()
    {
        var userId = GetCurrentUserId();
        var methods = await _mfaPreferencesService.GetAvailableMethodsAsync(userId);
        return Ok(new { methods });
    }
}
