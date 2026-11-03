using BuoyAI.Core.DTOs.Admin;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Admin broadcast / incident email (#552). High blast radius — locked to admins AND gated on
/// phishing-resistant MFA (TOTP/Passkey), mirroring the in-action guard PlaidController uses.
/// </summary>
[Route("api/admin/broadcast")]
[Authorize(Roles = "Admin")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class BroadcastController : BaseController
{
    private readonly IBroadcastService _broadcast;
    private readonly ITwoFactorService _twoFactor;
    private readonly ILogger<BroadcastController> _logger;

    public BroadcastController(
        IBroadcastService broadcast,
        ITwoFactorService twoFactor,
        ILogger<BroadcastController> logger)
    {
        _broadcast = broadcast;
        _twoFactor = twoFactor;
        _logger = logger;
    }

    /// <summary>Preview (dryRun) or send a broadcast email to all/active users.</summary>
    [HttpPost]
    public async Task<IActionResult> Send([FromBody] BroadcastRequest request, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (!await RequirePhishingResistantMfaAsync(userId))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Phishing-resistant MFA (TOTP or passkey) is required to broadcast." });

        var result = await _broadcast.CreateAsync(request, userId, GetCurrentUsername(), ct);
        return result.Success
            ? Ok(result.Response)
            : BadRequest(new { message = result.Error });
    }

    /// <summary>Draft a subject + body from a one-line prompt (AI; returned for review, never sent).</summary>
    [HttpPost("draft")]
    public async Task<IActionResult> Draft([FromBody] BroadcastDraftRequest request, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (!await RequirePhishingResistantMfaAsync(userId))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Phishing-resistant MFA (TOTP or passkey) is required." });

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { message = "prompt is required." });

        var draft = await _broadcast.DraftAsync(request, ct);
        return Ok(draft);
    }

    // The app gates sensitive operations on phishing-resistant MFA via this service method
    // (same one PlaidController uses); there is no [Authorize] policy for it.
    private Task<bool> RequirePhishingResistantMfaAsync(int userId)
        => _twoFactor.CanAccessPlaidFeaturesAsync(userId);
}
