namespace BuoyAI.Core.Entities;

/// <summary>
/// A successful referral: <see cref="RefereeUserId"/> signed up using
/// <see cref="ReferrerUserId"/>'s referral code. One row per referred user (unique on
/// referee). Reward fulfillment (e.g. premium-time) is tracked via <see cref="RewardStatus"/>
/// and deliberately NOT auto-granted here — see docs/gamification/gamification-redesign.md §4.3
/// (premium-time reward pending product + legal sign-off).
/// </summary>
public class Referral
{
  public int Id { get; set; }
  public int ReferrerUserId { get; set; }
  public int RefereeUserId { get; set; }
  public string ReferralCode { get; set; } = string.Empty;

  /// <summary>"pending" until the double-sided reward is fulfilled, then "granted".</summary>
  public string RewardStatus { get; set; } = "pending";
  public DateTime CreatedAt { get; set; }

  public User Referrer { get; set; } = null!;
  public User Referee { get; set; } = null!;
}
