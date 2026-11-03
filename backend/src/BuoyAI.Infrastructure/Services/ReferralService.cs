using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using System.Security.Cryptography;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Double-sided referral loop. Each user has a shareable code; when a new user redeems it,
/// a <see cref="Referral"/> is recorded and the referrer earns the "Recruiter" badge. The
/// premium-time reward is intentionally NOT granted here (tracked as "pending" — see the
/// redesign doc §4.3, pending product + legal sign-off).
/// </summary>
public class ReferralService : IReferralService
{
  private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no ambiguous 0/O/1/I
  private const int CodeLength = 8;

  private readonly BuoyAIDbContext _context;
  private readonly IGamificationService _gamificationService;
  private readonly IBillingService _billingService;
  private readonly IConfiguration _configuration;
  private readonly ILogger<ReferralService> _logger;

  public ReferralService(
    BuoyAIDbContext context,
    IGamificationService gamificationService,
    IBillingService billingService,
    IConfiguration configuration,
    ILogger<ReferralService> logger)
  {
    _context = context;
    _gamificationService = gamificationService;
    _billingService = billingService;
    _configuration = configuration;
    _logger = logger;
  }

  /// <summary>
  /// Share links point at the environment that generated them. This was hardcoded to
  /// production, so every link minted on staging sent the recipient to prod — the same bug
  /// class as the hardcoded email links.
  /// </summary>
  private string LinkBase =>
    (_configuration["App:BaseUrl"] ?? "https://buoy-ai.com").TrimEnd('/') + "/app/?ref=";

  public async Task<ReferralSummaryDto> GetSummaryAsync(int userId)
  {
    var code = await GetOrCreateCodeAsync(userId);

    var mine = _context.Set<Referral>().Where(r => r.ReferrerUserId == userId);
    var granted = await mine.CountAsync(r => r.RewardStatus == "granted");
    var pending = await mine.CountAsync(r => r.RewardStatus != "granted");

    return new ReferralSummaryDto
    {
      Code = code,
      Link = LinkBase + code,
      // Only rewarded referrals count as successful. A referee who signed up and never
      // connected anything has not delivered the thing the reward is paid for, and counting
      // them would promise a reward that is never coming.
      SuccessfulReferrals = granted,
      PendingReferrals = pending,
      RewardDaysRemaining = await GetRewardDaysRemainingAsync(userId)
    };
  }

  /// <summary>
  /// Days left on the currently applied discount, or 0.
  ///
  /// Read from Stripe via the billing layer rather than computed from our own rows: Stripe
  /// owns when a discount ends, and it can be consumed or expire without us being told.
  /// Failures degrade to 0 — this drives a badge, not access.
  /// </summary>
  private async Task<int> GetRewardDaysRemainingAsync(int userId)
  {
    try
    {
      var discount = await _billingService.GetActiveDiscountAsync(userId);
      if (discount?.EndsAt == null) return 0;

      var days = (int)Math.Ceiling((discount.EndsAt.Value - DateTime.UtcNow).TotalDays);
      return days > 0 ? days : 0;
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Could not read reward days remaining for user {UserId}", userId);
      return 0;
    }
  }

  public async Task<bool> RedeemAsync(int refereeUserId, string code)
  {
    if (string.IsNullOrWhiteSpace(code)) return false;
    code = code.Trim().ToUpperInvariant();

    // A user can only be referred once, ever.
    if (await _context.Set<Referral>().AnyAsync(r => r.RefereeUserId == refereeUserId))
      return false;

    var referrer = await _context.Set<User>()
      .FirstOrDefaultAsync(u => u.ReferralCode == code);
    if (referrer == null || referrer.Id == refereeUserId) return false;

    _context.Set<Referral>().Add(new Referral
    {
      ReferrerUserId = referrer.Id,
      RefereeUserId = refereeUserId,
      ReferralCode = code,
      RewardStatus = "pending",
      CreatedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();

    // Connector, not Recruiter. Someone used your code — real and worth marking — but they
    // have not completed setup, so no reward has been paid. Recruiter is awarded by
    // ReferralRewardService once the referee connects an account and the coupon lands, so the
    // badge a user sees always matches whether they were actually paid.
    await _gamificationService.AwardAsync(referrer.Id, AchievementDefinitions.Connector);
    _logger.LogInformation("User {Referee} redeemed referral from {Referrer}", refereeUserId, referrer.Id);
    return true;
  }

  private async Task<string> GetOrCreateCodeAsync(int userId)
  {
    var user = await _context.Set<User>().FirstOrDefaultAsync(u => u.Id == userId)
      ?? throw new InvalidOperationException($"User {userId} not found");
    if (!string.IsNullOrWhiteSpace(user.ReferralCode)) return user.ReferralCode!;

    string code;
    var attempts = 0;
    do
    {
      code = GenerateCode();
      attempts++;
    } while (await _context.Set<User>().AnyAsync(u => u.ReferralCode == code) && attempts < 10);

    user.ReferralCode = code;
    await _context.SaveChangesAsync();
    return code;
  }

  /// <summary>
  /// A random 8-character code.
  ///
  /// This was previously derived deterministically from the user id. Since the algorithm ships
  /// in the repo and user ids are small sequential integers, anyone could compute EVERY user's
  /// referral code by iterating. That makes "someone shared this code with me" meaningless,
  /// which is the entire signal a referral reward is paid on, and it lets an attacker attribute
  /// signups to arbitrary accounts.
  ///
  /// The original note cited testability as the reason for avoiding randomness. What actually
  /// needs testing is the collision-retry loop in GetOrCreateCodeAsync and the code's shape,
  /// neither of which requires predicting the exact characters.
  /// </summary>
  private static string GenerateCode()
  {
    var chars = new char[CodeLength];
    for (var i = 0; i < CodeLength; i++)
    {
      chars[i] = CodeChars[RandomNumberGenerator.GetInt32(CodeChars.Length)];
    }
    return new string(chars);
  }
}
