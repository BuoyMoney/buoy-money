using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs;

namespace BuoyAI.API.Controllers;

[ApiController]
[Route("api/consent")]
[Authorize]
public class ConsentController : BaseController
{
    private readonly IConsentService _consentService;
    private readonly ILogger<ConsentController> _logger;

    public ConsentController(
        IConsentService consentService,
        ILogger<ConsentController> logger)
    {
        _consentService = consentService;
        _logger = logger;
    }

    /// <summary>
    /// Records user consent for a specific service or feature
    /// </summary>
    [HttpPost("record")]
    public async Task<IActionResult> RecordConsent([FromBody] RecordConsentRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            
            // Add client IP and user agent from request headers
            request.IpAddress = GetClientIpAddress();
            request.UserAgent = Request.Headers.UserAgent.ToString();

            await _consentService.RecordConsentAsync(userId, request);
            return Ok(new { success = true, message = "Consent recorded successfully" });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument recording consent for user {UserId}", GetCurrentUserId());
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets consent history for the current user
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<IEnumerable<ConsentHistoryResponse>>> GetConsentHistory()
    {
        var userId = GetCurrentUserId();
        var history = await _consentService.GetConsentHistoryAsync(userId);
        return Ok(history);
    }

    /// <summary>
    /// Checks if user has valid consent for a specific type
    /// </summary>
    [HttpGet("check/{consentType}")]
    public async Task<ActionResult<bool>> CheckConsent(string consentType)
    {
        try
        {
            var userId = GetCurrentUserId();
            var hasConsent = await _consentService.HasValidConsentAsync(userId, consentType);
            return Ok(new { hasConsent });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument checking consent for user {UserId}, type {ConsentType}", GetCurrentUserId(), consentType);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Revokes user consent for a specific type
    /// </summary>
    [HttpPost("revoke/{consentType}")]
    public async Task<IActionResult> RevokeConsent(string consentType)
    {
        try
        {
            var userId = GetCurrentUserId();
            await _consentService.RevokeConsentAsync(userId, consentType);
            return Ok(new { success = true, message = "Consent revoked successfully" });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument revoking consent for user {UserId}, type {ConsentType}", GetCurrentUserId(), consentType);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets Plaid-specific consent data for privacy policy display
    /// </summary>
    [HttpGet("plaid")]
    public async Task<ActionResult<PlaidConsentData>> GetPlaidConsentData()
    {
        var userId = GetCurrentUserId();
        var consentData = await _consentService.GetPlaidConsentDataAsync(userId);
        return Ok(consentData);
    }

    private string GetClientIpAddress()
    {
        // Try to get the real IP address from headers (in case of proxy/load balancer)
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',')[0].Trim();
        }

        var realIp = Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp))
        {
            return realIp;
        }

        // Fallback to connection remote IP
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }
}
