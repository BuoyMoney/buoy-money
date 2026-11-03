using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// The AI usage screen: the allowance, what has been used this billing period, a per-feature breakdown, and (when AI comes
/// from BuoyAI Cloud) the pay-as-you-go switch. Anyone signed in can see it; only the admin can change pay-as-you-go,
/// because it spends the server owner's money.
/// </summary>
[Authorize]
public class AiUsageController : BaseController
{
  private readonly IAiUsageSummaryService _summary;

  public AiUsageController(IAiUsageSummaryService summary) => _summary = summary;

  [HttpGet]
  public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
    Ok(await _summary.GetAsync(GetCurrentUserId(), cancellationToken));

  /// <summary>Capmicros is the most extra spend allowed beyond the allowance, in micro-dollars (1 USD = 1,000,000).</summary>
  [HttpPut("payg")]
  [Authorize(Roles = "Admin")]
  public async Task<IActionResult> SetPayg([FromBody] SetPaygRequest request, CancellationToken cancellationToken) =>
    Ok(await _summary.SetPaygAsync(GetCurrentUserId(), request.Enabled, request.CapMicros, cancellationToken));
}

public sealed record SetPaygRequest(bool Enabled, long CapMicros);
