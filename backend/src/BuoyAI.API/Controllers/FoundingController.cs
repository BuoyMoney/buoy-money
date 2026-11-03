using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

public class FoundingStatusResponse
{
  public int Limit { get; set; }
  public int Taken { get; set; }
  public int Remaining { get; set; }
  public bool Open { get; set; }
}

/// <summary>Public founding-spot counter for the marketing page. Numbers only, no personal data.</summary>
[AllowAnonymous]
[EnableRateLimiting("auth")]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class FoundingController : BaseController
{
  private const string CacheKey = "founding:status";

  private readonly IReservationService _reservations;
  private readonly IMemoryCache _cache;

  public FoundingController(IReservationService reservations, IMemoryCache cache)
  {
    _reservations = reservations;
    _cache = cache;
  }

  [HttpGet("status")]
  [ProducesResponseType(typeof(FoundingStatusResponse), StatusCodes.Status200OK)]
  public async Task<IActionResult> Status(CancellationToken ct)
  {
    if (!_cache.TryGetValue(CacheKey, out FoundingStatusResponse? body) || body == null)
    {
      var s = await _reservations.GetFoundingStatusAsync(ct);
      body = new FoundingStatusResponse { Limit = s.Limit, Taken = s.Taken, Remaining = s.Remaining, Open = s.Open };
      _cache.Set(CacheKey, body, TimeSpan.FromSeconds(30));
    }

    return Ok(body);
  }
}
