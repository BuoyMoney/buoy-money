using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

public class ReservationCheckoutRequest
{
  public string? Choice { get; set; }
  public string? Source { get; set; }
}

public class ReservationCheckoutResponse
{
  public string Url { get; set; } = string.Empty;
}

public class ReservationStatsResponse
{
  public int Count { get; set; }
  public bool Enabled { get; set; }
}

/// <summary>
/// Public endpoints for the founding-member reservation on the marketing site. Anonymous on
/// purpose (visitors have no account) and on the stricter "auth" rate limit (10/min per IP)
/// because checkout creation calls Stripe.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("auth")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class ReservationsController : BaseController
{
  private const string StatsCacheKey = "reservations:stats";

  private readonly IReservationService _reservations;
  private readonly IMemoryCache _cache;

  public ReservationsController(IReservationService reservations, IMemoryCache cache)
  {
    _reservations = reservations;
    _cache = cache;
  }

  [HttpPost("checkout")]
  [ProducesResponseType(typeof(ReservationCheckoutResponse), StatusCodes.Status200OK)]
  public async Task<IActionResult> Checkout([FromBody] ReservationCheckoutRequest request, CancellationToken ct)
  {
    try
    {
      var url = await _reservations.CreateCheckoutAsync(request.Choice, request.Source, ct);
      return Ok(new ReservationCheckoutResponse { Url = url });
    }
    catch (ReservationsDisabledException)
    {
      return NotFound();
    }
    catch (FoundingSpotsTakenException ex)
    {
      return Conflict(new { message = ex.Message, code = "founding_full", limit = ex.Limit });
    }
    catch (InvalidReservationChoiceException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  [HttpGet("stats")]
  [ProducesResponseType(typeof(ReservationStatsResponse), StatusCodes.Status200OK)]
  public async Task<IActionResult> Stats(CancellationToken ct)
  {
    if (!_reservations.IsEnabled)
    {
      return Ok(new ReservationStatsResponse { Count = 0, Enabled = false });
    }

    if (!_cache.TryGetValue(StatsCacheKey, out ReservationStatsResponse? stats) || stats == null)
    {
      stats = new ReservationStatsResponse
      {
        Count = await _reservations.GetCountAsync(ct),
        Enabled = true,
      };
      _cache.Set(StatsCacheKey, stats, TimeSpan.FromSeconds(60));
    }

    return Ok(stats);
  }
}
