using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.API.Controllers;

/// <summary>
/// "Money Wrapped" recaps: the monthly short story (deep-linked from the
/// money_wrapped notification) and the full yearly recap.
/// </summary>
[Authorize]
public class WrappedController : BaseController
{
  private readonly IWrappedService _wrapped;

  public WrappedController(IWrappedService wrapped)
  {
    _wrapped = wrapped;
  }

  /// <summary>Monthly recap. Defaults to last month when year/month are omitted.</summary>
  [HttpGet("monthly")]
  public async Task<IActionResult> GetMonthly([FromQuery] int? year, [FromQuery] int? month)
  {
    var userId = GetCurrentUserId();
    var reference = LocalToday().AddMonths(-1);
    var y = year ?? reference.Year;
    var m = month ?? reference.Month;
    if (m is < 1 or > 12 || y < 2000 || y > LocalToday().Year)
    {
      return BadRequest("Invalid year/month");
    }
    var dto = await _wrapped.GetMonthlyAsync(userId, y, m);
    return HandleServiceResponse(dto, "No activity in that month yet");
  }

  /// <summary>Yearly recap (year-to-date for the current year). Defaults to the current year.</summary>
  [HttpGet("yearly")]
  public async Task<IActionResult> GetYearly([FromQuery] int? year)
  {
    var userId = GetCurrentUserId();
    var y = year ?? LocalToday().Year;
    if (y < 2000 || y > LocalToday().Year)
    {
      return BadRequest("Invalid year");
    }
    var dto = await _wrapped.GetYearlyAsync(userId, y);
    return HandleServiceResponse(dto, "No activity in that year yet");
  }

  /// <summary>
  /// The user's local "today" from the X-Timezone-Offset header (minutes from
  /// UTC), same convention as CountUpBudgetService. Matters at month/year
  /// boundaries — a Jan 1st 00:30 local open should default to December.
  /// </summary>
  private DateTime LocalToday()
  {
    var header = Request.Headers["X-Timezone-Offset"].FirstOrDefault();
    if (int.TryParse(header, out var offset) && offset is >= -840 and <= 840)
    {
      return DateTime.UtcNow.AddMinutes(offset).Date;
    }
    return DateTime.UtcNow.Date;
  }
}
