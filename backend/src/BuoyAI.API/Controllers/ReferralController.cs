using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class ReferralController : BaseController
{
  private readonly IReferralService _referralService;
  private readonly IConfiguration _configuration;

  public ReferralController(IReferralService referralService, IConfiguration configuration)
  {
    _referralService = referralService;
    _configuration = configuration;
  }

  /// <summary>
  /// The advertised referral credit for a prospective user's currency (#941).
  /// Anonymous: the register page shows this before signup. A currency is only
  /// advertised when a matching reward coupon is configured (Referral:RewardCouponIds),
  /// so the promise always matches what the sweep can actually grant; everything
  /// else falls back to the USD offer. Amounts live in Referral:RewardDisplayAmounts
  /// and must mirror the Stripe coupons' amount_off values.
  /// </summary>
  [HttpGet("reward-info")]
  [AllowAnonymous]
  public IActionResult GetRewardInfo([FromQuery] string? currency = null)
  {
    var amounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    foreach (var child in _configuration.GetSection("Referral:RewardDisplayAmounts").GetChildren())
    {
      if (decimal.TryParse(child.Value, out var value))
      {
        amounts[child.Key] = value;
      }
    }

    var requested = CurrencyCodes.Normalize(currency);
    var hasCoupon = requested != null
      && (requested == "USD"
          || !string.IsNullOrWhiteSpace(_configuration[$"Referral:RewardCouponIds:{requested}"]));

    if (requested != null && hasCoupon && amounts.TryGetValue(requested, out var amount))
    {
      return Ok(new { amount, currency = requested });
    }

    return Ok(new { amount = amounts.GetValueOrDefault("USD", 15m), currency = "USD" });
  }

  /// <summary>The current user's referral code, shareable link, and successful-referral count.</summary>
  [HttpGet]
  public async Task<IActionResult> GetSummary()
  {
    var summary = await _referralService.GetSummaryAsync(GetCurrentUserId());
    return Ok(summary);
  }

  /// <summary>Redeem a referral code for the current user (called once after sign-up).</summary>
  // Rate-limited so a valid 8-char code can't be brute-forced (QE #615 Part E).
  // Self-referral and already-referred are already blocked in RedeemAsync.
  [EnableRateLimiting("auth")]
  [HttpPost("redeem")]
  public async Task<IActionResult> Redeem([FromBody] RedeemReferralRequest request)
  {
    var ok = await _referralService.RedeemAsync(GetCurrentUserId(), request.Code);
    return Ok(new { redeemed = ok });
  }
}

public class RedeemReferralRequest
{
  public string Code { get; set; } = string.Empty;
}
