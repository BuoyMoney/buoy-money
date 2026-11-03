using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Gamification;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Grants referral rewards once the referee has actually activated.
///
/// **Why a sweep and not a hook on the Plaid path.** The qualifying event is the referee
/// connecting a bank account, and that is the single most important flow in the product.
/// Granting a reward must never be able to break it, so this is decoupled and retryable
/// rather than inline. Same shape as TrialReconciliationService and TrialReminderService.
///
/// **Why connecting an account is the qualifying event.** Signup and email verification are
/// both free to fake at volume; a real financial institution is not. Paying on registration
/// would pay for throwaway inboxes, which is the whole abuse surface of a referral programme.
///
/// **Why the reward is a coupon.** It leaves TrialHistory untouched, so the "one trial per
/// email, ever" gate keeps meaning what it says, and it applies whether the referrer is
/// trialing, paying or lapsed. A trial extension works in only one of those three states.
///
/// Off by default: it moves real money and needs a configured coupon.
/// </summary>
public class ReferralRewardService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private const int BatchLimit = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReferralRewardService> _logger;

    public ReferralRewardService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ReferralRewardService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var couponId = _configuration["Referral:RewardCouponId"];
        if (string.IsNullOrWhiteSpace(couponId))
        {
            // Deliberately not defaulted to anything. A wrong coupon id fails per-referral at
            // Stripe and leaves rows pending, which looks like a bug; no coupon id disables
            // cleanly and says so. The coupon must be created in Stripe by hand with duration
            // "once" — see ExpiresBeforeItApplies on SubscriptionDiscountDto for why
            // "repeating" silently pays nothing when applied during a trial.
            _logger.LogInformation(
                "Referral reward sweep disabled (Referral:RewardCouponId not set). Referrals are recorded " +
                "and stay pending; set the id of a duration='once' Stripe coupon to start granting.");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(couponId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Referral reward sweep failed; will retry next interval");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One pass. Internal so the rules that decide who gets paid are directly testable.
    /// </summary>
    internal async Task SweepAsync(string couponId, CancellationToken ct)
    {
        // Days the referee must remain activated before the reward vests. Without a hold,
        // connect-then-vanish pays out just as well as a real referral.
        var vestingDays = _configuration.GetValue("Referral:VestingDays", 7);

        // Rewards one referrer may earn in a rolling year. Turns unbounded farming into
        // bounded farming, which is the realistic goal — the qualifying event does the heavy
        // lifting, this caps the tail.
        var maxPerYear = _configuration.GetValue("Referral:MaxRewardsPerYear", 5);

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
        var gamification = scope.ServiceProvider.GetRequiredService<IGamificationService>();

        // Per-currency reward coupons (#941): Stripe coupons with amount_off are
        // single-currency, so each supported currency gets its own coupon id under
        // Referral:RewardCouponIds. A participant whose primary currency has one
        // configured gets that coupon; everyone else gets the default (USD) coupon.
        // Referrer and referee are resolved independently — they may differ.
        var couponByCurrency = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in _configuration.GetSection("Referral:RewardCouponIds").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                couponByCurrency[child.Key] = child.Value!;
            }
        }

        var now = DateTime.UtcNow;
        var activatedBefore = now.AddDays(-vestingDays);
        var yearAgo = now.AddYears(-1);

        var pending = await context.Set<Referral>()
            .Where(r => r.RewardStatus == "pending")
            // The hold is measured from when the referee CONNECTED, not from when the
            // referral was recorded. Those are different dates and only the first one means
            // anything: someone who signs up, waits out the window doing nothing, then
            // connects and disconnects would clear a referral-age check instantly, which is
            // exactly the connect-collect-vanish case this is meant to stop.
            .Where(r => context.Accounts.Any(a => a.UserId == r.RefereeUserId
                                                  && a.CreatedAt <= activatedBefore))
            .OrderBy(r => r.CreatedAt)
            .Take(BatchLimit)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        var participantIds = pending
            .SelectMany(r => new[] { r.ReferrerUserId, r.RefereeUserId })
            .Distinct()
            .ToList();
        var currencyByUser = await context.Users
            .AsNoTracking()
            .Where(u => participantIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Currency, ct);

        string CouponFor(int userId) =>
            currencyByUser.TryGetValue(userId, out var currency)
            && couponByCurrency.TryGetValue(currency, out var currencyCoupon)
                ? currencyCoupon
                : couponId;

        var granted = 0;
        foreach (var referral in pending)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var alreadyGranted = await context.Set<Referral>()
                    .CountAsync(r => r.ReferrerUserId == referral.ReferrerUserId
                                     && r.RewardStatus == "granted"
                                     && r.CreatedAt >= yearAgo, ct);

                // Cap is referrer-only. A referee can be referred at most once ever (guarded
                // in RedeemAsync), so their side is already bounded at one reward for life.
                if (alreadyGranted >= maxPerYear)
                {
                    // Capped, not rejected. Left pending so raising the cap pays it later
                    // rather than silently voiding a legitimate referral.
                    _logger.LogInformation(
                        "Referrer {ReferrerId} is at the annual reward cap ({Cap}); referral {ReferralId} stays pending",
                        referral.ReferrerUserId, maxPerYear, referral.Id);
                    continue;
                }

                // BOTH sides. The offer is double-sided — "you both get a free month" — so
                // paying only the referrer would make the invite copy a lie.
                //
                // Re-applying the same coupon is idempotent in effect: Stripe allows one
                // discount per subscription and we replace rather than stack, so a retry
                // after a partial failure cannot double-grant the side that already
                // succeeded. That is what makes "only mark granted when both land" safe.
                var referrerDiscount = await billing.ApplyRewardCouponAsync(referral.ReferrerUserId, CouponFor(referral.ReferrerUserId));
                var refereeDiscount = await billing.ApplyRewardCouponAsync(referral.RefereeUserId, CouponFor(referral.RefereeUserId));

                if (referrerDiscount == null || refereeDiscount == null)
                {
                    // Usually means one of them has no subscription to attach it to yet.
                    // Stays pending and retries next pass.
                    _logger.LogInformation(
                        "Referral {ReferralId} not fully rewarded yet (referrer={ReferrerOk}, referee={RefereeOk}); leaving pending",
                        referral.Id, referrerDiscount != null, refereeDiscount != null);
                    continue;
                }

                // Loud, because it is silent otherwise: a repeating coupon applied during a
                // long trial expires before the first invoice and the user is charged in full
                // having been promised a free month. Only duration "once" survives a trial.
                if (referrerDiscount.ExpiresBeforeItApplies || refereeDiscount.ExpiresBeforeItApplies)
                {
                    _logger.LogError(
                        "Referral coupon {CouponId} expires before the next invoice — it will pay nothing. " +
                        "Use a coupon with duration 'once', which waits for an invoice instead of a clock.",
                        couponId);
                }

                referral.RewardStatus = "granted";
                granted++;

                // Recruiter marks the COMPLETED loop, so it arrives with the reward rather
                // than at signup. Connector was already awarded when the code was redeemed.
                // Awarding is idempotent, and it must not be able to undo a grant, so a
                // failure here is logged and swallowed.
                try
                {
                    await gamification.AwardAsync(referral.ReferrerUserId, AchievementDefinitions.Recruiter);
                }
                catch (Exception badgeEx)
                {
                    _logger.LogWarning(badgeEx,
                        "Reward granted for referral {ReferralId} but the Recruiter badge failed", referral.Id);
                }

                _logger.LogInformation(
                    "Granted referral reward to both sides of referral {ReferralId} (referrer {ReferrerId}, referee {RefereeId}): {Description}",
                    referral.Id, referral.ReferrerUserId, referral.RefereeUserId, referrerDiscount.Description);
            }
            catch (Exception ex)
            {
                // Left pending so it retries. One failure must not stop the batch.
                _logger.LogError(ex, "Failed to grant referral reward for referral {ReferralId}", referral.Id);
            }
        }

        await context.SaveChangesAsync(ct);

        if (granted > 0)
        {
            _logger.LogInformation("Referral reward sweep granted {Granted} of {Pending} pending referral(s)", granted, pending.Count);
        }
    }
}
