namespace BuoyAI.Core.DTOs;

/// <summary>The caller's referral code, shareable link, and how many friends have joined.</summary>
public class ReferralSummaryDto
{
  public string Code { get; set; } = string.Empty;
  public string Link { get; set; } = string.Empty;

  /// <summary>Referrals whose reward has been granted.</summary>
  public int SuccessfulReferrals { get; set; }

  /// <summary>
  /// Referrals recorded but not yet rewarded, because the referee has not connected an
  /// account yet. Surfaced separately so a referrer can tell "my friend hasn't finished
  /// signing up" apart from "this didn't work" — a bare success count explains neither.
  /// </summary>
  public int PendingReferrals { get; set; }

  /// <summary>
  /// Free days currently held from referral rewards, or 0. Mirrors how the trial banner
  /// reads, since to the user both are "how long do I have left".
  /// </summary>
  public int RewardDaysRemaining { get; set; }
}
