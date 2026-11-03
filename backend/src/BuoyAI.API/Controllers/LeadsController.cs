using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Public lead capture for the marketing site.
///
/// Anonymous by necessity: the whole point is to reach visitors who have not created an
/// account. It carries the "auth" rate-limit policy (10/min per IP) rather than the looser
/// "api" one, because an unauthenticated write endpoint that inserts rows and sends email
/// is exactly the shape a scraper abuses.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("auth")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class LeadsController : BaseController
{
    private readonly IMarketingLeadService _leadService;
    private readonly ILogger<LeadsController> _logger;

    public LeadsController(IMarketingLeadService leadService, ILogger<LeadsController> logger)
    {
        _leadService = leadService;
        _logger = logger;
    }

    /// <summary>
    /// Trades an email address for an extended free trial. Idempotent per address.
    /// </summary>
    /// <remarks>
    /// The response is deliberately identical for a new address, a repeat submit, and an
    /// address that already has an account. A public endpoint that answered differently
    /// would be an account-enumeration oracle.
    /// </remarks>
    [HttpPost("extended-trial")]
    [ProducesResponseType(typeof(CaptureLeadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CaptureExtendedTrial([FromBody] CaptureLeadRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { message = "Enter a valid email address." });
        }

        var result = await _leadService.CaptureAsync(request, ct);
        return Ok(result);
    }

    /// <summary>
    /// Opts a captured lead out of further marketing email.
    /// </summary>
    /// <remarks>
    /// A lead has no account, so there is no settings screen for them to use; this link in the
    /// email footer is their only route out, which CAN-SPAM requires once these messages stop
    /// being purely transactional.
    ///
    /// Always returns 200, even for an unknown token. An unsubscribe page that distinguished
    /// "valid" from "unknown" would confirm whether a guessed token maps to a real contact,
    /// and there is nothing the caller can usefully do with the difference anyway.
    /// </remarks>
    [HttpPost("unsubscribe")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Unsubscribe([FromBody] UnsubscribeLeadRequest request, CancellationToken ct)
    {
        var known = await _leadService.UnsubscribeAsync(request.Token ?? string.Empty, ct);
        if (!known)
        {
            _logger.LogInformation("Unsubscribe attempted with an unrecognised token");
        }
        return Ok(new { message = "You're unsubscribed. You won't get marketing email from us again." });
    }

    /// <summary>
    /// The current offer, so the marketing site can render "a month instead of two weeks" without
    /// hardcoding numbers that drift from backend config.
    /// </summary>
    [HttpGet("offer")]
    [ProducesResponseType(typeof(CaptureLeadResponse), StatusCodes.Status200OK)]
    public IActionResult GetOffer()
    {
        var (standard, extended) = _leadService.GetOfferDays();
        return Ok(new CaptureLeadResponse { TrialDays = extended, StandardTrialDays = standard });
    }
}
