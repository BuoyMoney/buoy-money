using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Who gets paid a referral reward, and when.
///
/// The qualifying event is the referee connecting a real bank account, because signup and
/// email verification are both free to fake at volume and a financial institution is not.
/// These tests pin the rules that stand between a referral programme and a farm.
/// </summary>
public class ReferralRewardServiceTests
{
    private const string CouponId = "coupon_free_month";
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Mock<IBillingService> _billing = null!;
    private static BuoyAIDbContext _context = null!;
    private static Mock<IGamificationService> _gamification = null!;

    private static ReferralRewardService CreateService(
        IEnumerable<Referral> referrals,
        IEnumerable<Account>? accounts = null,
        int vestingDays = 7,
        int maxPerYear = 5,
        IEnumerable<User>? users = null,
        Dictionary<string, string?>? extraConfig = null)
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new BuoyAIDbContext(options);
        _context.Set<Referral>().AddRange(referrals);
        if (accounts != null) _context.Accounts.AddRange(accounts);
        if (users != null) _context.Users.AddRange(users);
        _context.SaveChanges();

        _billing = new Mock<IBillingService>();
        _billing.Setup(b => b.ApplyRewardCouponAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new SubscriptionDiscountDto { Code = "REF", Description = "100% off for 1 month" });

        _gamification = new Mock<IGamificationService>();

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(_billing.Object);
        services.AddSingleton(_gamification.Object);

        var configValues = new Dictionary<string, string?>
        {
            ["Referral:RewardCouponId"] = CouponId,
            ["Referral:VestingDays"] = vestingDays.ToString(),
            ["Referral:MaxRewardsPerYear"] = maxPerYear.ToString(),
        };
        if (extraConfig != null)
        {
            foreach (var (key, value) in extraConfig) configValues[key] = value;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        return new ReferralRewardService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            config,
            new Mock<ILogger<ReferralRewardService>>().Object);
    }

    private static Referral Pending(int id, int referrer, int referee, int ageDays, string status = "pending") => new()
    {
        Id = id,
        ReferrerUserId = referrer,
        RefereeUserId = referee,
        ReferralCode = "CODE" + id,
        RewardStatus = status,
        CreatedAt = Now.AddDays(-ageDays),
    };

    private static Account Connected(int id, int userId, int connectedDaysAgo = 30) =>
        new() { Id = id, UserId = userId, Name = "Checking", CreatedAt = Now.AddDays(-connectedDaysAgo) };

    private static int GrantCount() => _billing.Invocations
        .Count(i => i.Method.Name == nameof(IBillingService.ApplyRewardCouponAsync));

    // #941: each side of a referral gets the coupon for THEIR primary currency —
    // Stripe coupons are single-currency, so a CAD referrer and USD referee must
    // resolve to different coupons or one of them is promised money the coupon
    // cannot pay. Unconfigured currencies fall back to the default (USD) coupon.
    [Fact]
    public async Task Sweep_UsesTheCurrencyMatchedCoupon_PerParticipant()
    {
        var service = CreateService(
            new[] { Pending(1, referrer: 10, referee: 20, ageDays: 30) },
            new[] { Connected(1, 20) },
            users: new[]
            {
                new User { Id = 10, Email = "cad@example.com", Currency = "CAD" },
                new User { Id = 20, Email = "usd@example.com", Currency = "USD" },
            },
            extraConfig: new Dictionary<string, string?>
            {
                ["Referral:RewardCouponIds:CAD"] = "coupon_cad_20",
            });

        await service.SweepAsync(CouponId, CancellationToken.None);

        _billing.Verify(b => b.ApplyRewardCouponAsync(10, "coupon_cad_20"), Times.Once);
        _billing.Verify(b => b.ApplyRewardCouponAsync(20, CouponId), Times.Once);
        _context.Set<Referral>().Single().RewardStatus.Should().Be("granted");
    }

    [Fact]
    public async Task Sweep_GrantsWhenTheRefereeHasConnectedAnAccount()
    {
        var service = CreateService(
            new[] { Pending(1, referrer: 10, referee: 20, ageDays: 30) },
            new[] { Connected(1, 20) });

        await service.SweepAsync(CouponId, CancellationToken.None);

        // Both sides: the offer is "you both get a free month", so paying only the referrer
        // would make the invite copy a lie.
        GrantCount().Should().Be(2);
        _context.Set<Referral>().Single().RewardStatus.Should().Be("granted");
        _billing.Verify(b => b.ApplyRewardCouponAsync(10, CouponId), Times.Once);
        _billing.Verify(b => b.ApplyRewardCouponAsync(20, CouponId), Times.Once);
    }

    [Fact]
    public async Task Sweep_LeavesPendingWhenOnlyOneSideCouldBeRewarded()
    {
        // Marked granted only when BOTH land. Re-applying the same coupon is idempotent in
        // effect (Stripe holds one discount per subscription and we replace rather than
        // stack), so retrying cannot double-grant the side that already succeeded.
        var service = CreateService(
            new[] { Pending(1, referrer: 10, referee: 20, ageDays: 30) },
            new[] { Connected(1, 20) });
        _billing.Setup(b => b.ApplyRewardCouponAsync(20, CouponId))
            .ReturnsAsync((SubscriptionDiscountDto?)null);

        await service.SweepAsync(CouponId, CancellationToken.None);

        _context.Set<Referral>().Single().RewardStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Sweep_DoesNotPayForASignupThatNeverConnectedAnything()
    {
        // The entire abuse surface. Paying on registration pays for throwaway inboxes.
        var service = CreateService(new[] { Pending(1, 10, 20, ageDays: 30) });

        await service.SweepAsync(CouponId, CancellationToken.None);

        GrantCount().Should().Be(0);
        _context.Set<Referral>().Single().RewardStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Sweep_HoldsTheRewardUntilTheConnectionItselfVests()
    {
        // Connect, get paid, vanish is otherwise indistinguishable from a real referral.
        var service = CreateService(
            new[] { Pending(1, 10, 20, ageDays: 2) },
            new[] { Connected(1, 20, connectedDaysAgo: 1) },
            vestingDays: 7);

        await service.SweepAsync(CouponId, CancellationToken.None);

        GrantCount().Should().Be(0);
        _context.Set<Referral>().Single().RewardStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Sweep_VestingIsMeasuredFromTheConnection_NotTheReferralDate()
    {
        // An old referral whose referee only just connected must still wait. Measuring from
        // the referral date instead would let someone sign up, idle out the window, then
        // connect and collect on the very next pass.
        var service = CreateService(
            new[] { Pending(1, 10, 20, ageDays: 90) },
            new[] { Connected(1, 20, connectedDaysAgo: 1) },
            vestingDays: 7);

        await service.SweepAsync(CouponId, CancellationToken.None);

        GrantCount().Should().Be(0);
        _context.Set<Referral>().Single().RewardStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Sweep_IsIdempotent_AlreadyGrantedIsNotPaidTwice()
    {
        var service = CreateService(
            new[] { Pending(1, 10, 20, ageDays: 30, status: "granted") },
            new[] { Connected(1, 20) });

        await service.SweepAsync(CouponId, CancellationToken.None);

        GrantCount().Should().Be(0);
    }

    [Fact]
    public async Task Sweep_StopsAtTheAnnualCap()
    {
        // Bounded farming instead of unbounded. The qualifying event does the heavy lifting;
        // this caps the tail.
        var referrals = new List<Referral>
        {
            Pending(1, 10, 21, 30, status: "granted"),
            Pending(2, 10, 22, 30, status: "granted"),
            Pending(3, 10, 23, ageDays: 30),
        };
        var service = CreateService(
            referrals,
            new[] { Connected(1, 21), Connected(2, 22), Connected(3, 23) },
            maxPerYear: 2);

        await service.SweepAsync(CouponId, CancellationToken.None);

        GrantCount().Should().Be(0);
        // Left pending, not voided: raising the cap should pay a legitimate referral later.
        _context.Set<Referral>().Single(r => r.Id == 3).RewardStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Sweep_CapIsPerReferrer_NotGlobal()
    {
        var referrals = new List<Referral>
        {
            Pending(1, 10, 21, 30, status: "granted"),
            Pending(2, 11, 22, ageDays: 30),
        };
        var service = CreateService(
            referrals,
            new[] { Connected(1, 21), Connected(2, 22) },
            maxPerYear: 1);

        await service.SweepAsync(CouponId, CancellationToken.None);

        // Referrer 11 is unaffected by referrer 10 hitting the cap.
        _billing.Verify(b => b.ApplyRewardCouponAsync(11, CouponId), Times.Once);
        _billing.Verify(b => b.ApplyRewardCouponAsync(10, CouponId), Times.Never);
        _context.Set<Referral>().Single(r => r.Id == 2).RewardStatus.Should().Be("granted");
    }

    [Fact]
    public async Task Sweep_LeavesPendingWhenTheReferrerHasNoSubscriptionYet()
    {
        var service = CreateService(
            new[] { Pending(1, 10, 20, ageDays: 30) },
            new[] { Connected(1, 20) });
        _billing.Setup(b => b.ApplyRewardCouponAsync(It.IsAny<int>(), CouponId))
            .ReturnsAsync((SubscriptionDiscountDto?)null);

        await service.SweepAsync(CouponId, CancellationToken.None);

        _context.Set<Referral>().Single().RewardStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Sweep_OneFailureDoesNotStopTheBatch()
    {
        var referrals = new List<Referral>
        {
            Pending(1, 10, 21, ageDays: 30),
            Pending(2, 11, 22, ageDays: 30),
        };
        var service = CreateService(referrals, new[] { Connected(1, 21), Connected(2, 22) });
        _billing.Setup(b => b.ApplyRewardCouponAsync(10, CouponId))
            .ThrowsAsync(new Exception("stripe down"));

        await service.SweepAsync(CouponId, CancellationToken.None);

        _context.Set<Referral>().Single(r => r.Id == 1).RewardStatus.Should().Be("pending");
        _context.Set<Referral>().Single(r => r.Id == 2).RewardStatus.Should().Be("granted");
    }

    [Fact]
    public async Task Sweep_AwardsRecruiterOnlyWhenTheLoopCompletes()
    {
        // Recruiter marks the COMPLETED loop. Connector was already given at signup, so a
        // referrer's badges always match whether they were actually paid.
        var service = CreateService(
            new[] { Pending(1, referrer: 10, referee: 20, ageDays: 30) },
            new[] { Connected(1, 20) });

        await service.SweepAsync(CouponId, CancellationToken.None);

        _gamification.Verify(
            g => g.AwardAsync(10, BuoyAI.Core.DTOs.Gamification.AchievementDefinitions.Recruiter),
            Times.Once);
    }

    [Fact]
    public async Task Sweep_DoesNotAwardRecruiterForAnUnactivatedReferee()
    {
        var service = CreateService(new[] { Pending(1, 10, 20, ageDays: 30) });

        await service.SweepAsync(CouponId, CancellationToken.None);

        _gamification.Verify(
            g => g.AwardAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_ABadgeFailureDoesNotUndoTheGrant()
    {
        // The money is the point; the badge is decoration. Losing the badge must never cost
        // someone a reward that Stripe has already applied.
        var service = CreateService(
            new[] { Pending(1, 10, 20, ageDays: 30) },
            new[] { Connected(1, 20) });
        _gamification.Setup(g => g.AwardAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("gamification down"));

        await service.SweepAsync(CouponId, CancellationToken.None);

        _context.Set<Referral>().Single().RewardStatus.Should().Be("granted");
    }
}
