using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Invites founding members to the self-host closed beta (task 24). Admin only, with phishing-resistant MFA like the
/// broadcast email, and off (404) unless <c>Beta:Enabled</c> is true. A send is a dry run unless the body says
/// <c>"dryRun": false</c>.
/// </summary>
[Route("api/admin/beta-invites")]
[Authorize(Roles = "Admin")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class BetaInvitesController : BaseController
{
    private readonly IBetaInviteService _invites;
    private readonly ITwoFactorService _twoFactor;

    public BetaInvitesController(IBetaInviteService invites, ITwoFactorService twoFactor)
    {
        _invites = invites;
        _twoFactor = twoFactor;
    }

    public sealed record SendBody(int Limit = 10, bool DryRun = true);

    /// <summary>How many founding members would be invited, and the exact email they would get.</summary>
    [HttpGet]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        if (!_invites.Enabled) return NotFound();
        if (!await _twoFactor.CanAccessPlaidFeaturesAsync(GetCurrentUserId()))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Phishing-resistant MFA (TOTP or passkey) is required to view beta invites." });
        }
        return Ok(await _invites.PreviewAsync(ct));
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendBody? body, CancellationToken ct)
    {
        if (!_invites.Enabled) return NotFound();
        if (!await _twoFactor.CanAccessPlaidFeaturesAsync(GetCurrentUserId()))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Phishing-resistant MFA (TOTP or passkey) is required to send beta invites." });
        }
        body ??= new SendBody();
        if (body.Limit < 1)
        {
            return BadRequest(new { message = "limit must be at least 1." });
        }
        return Ok(await _invites.SendAsync(body.Limit, body.DryRun, ct));
    }
}
