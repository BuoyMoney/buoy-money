using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class TrialProvisioningServiceTests
{
  private readonly BuoyAIDbContext _context;
  private readonly Mock<IStripeService> _stripe = new();
  private readonly Mock<ISubscriptionSyncService> _sync = new();
  private readonly Mock<IFamilyService> _family = new();
  private readonly TrialProvisioningService _service;

  private static readonly DateTime StripeTrialEnd = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

  public TrialProvisioningServiceTests()
  {
    _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    _stripe.Setup(x => x.FindCustomerIdByEmailAsync(It.IsAny<string>()))
      .ReturnsAsync((string?)null);
    _stripe.Setup(x => x.CreateCustomerAsync(It.IsAny<User>()))
      .ReturnsAsync("cus_new");
    _stripe.Setup(x => x.CreateCardlessTrialSubscriptionAsync(
        It.IsAny<string>(), It.IsAny<SubscriptionTier>(), It.IsAny<int>()))
      .ReturnsAsync(new CardlessTrialResult("sub_123", StripeTrialEnd, "trialing"));

    // Default: Stripe has no pre-existing live subscription, so the duplicate guard is a
    // no-op and the normal create path runs.
    _stripe.Setup(x => x.GetLiveSubscriptionAsync(It.IsAny<string>()))
      .ReturnsAsync((StripeSubscriptionSnapshot?)null);

    _service = new TrialProvisioningService(
      _context,
      _stripe.Object,
      _sync.Object,
      _family.Object,
      new ConfigurationBuilder().Build(),
      Mock.Of<ILogger<TrialProvisioningService>>());
  }

  private async Task<User> SeedUserAsync(bool trialing = true, int days = 60)
  {
    var user = new User
    {
      Email = "t@example.com",
      Username = "t",
      PasswordHash = "x",
      FirstName = "T",
      LastName = "User",
      IsTrialActive = trialing,
      TrialStartDate = trialing ? DateTime.UtcNow : null,
      TrialEndDate = trialing ? DateTime.UtcNow.AddDays(days) : null
    };
    _context.Users.Add(user);
    await _context.SaveChangesAsync();
    return user;
  }

  [Fact]
  public async Task Provision_creates_a_trialing_subscription_row()
  {
    var user = await SeedUserAsync();

    (await _service.ProvisionStripeTrialAsync(user.Id, 60)).Should().BeTrue();

    var sub = await _context.UserSubscriptions.SingleAsync();
    sub.StripeSubscriptionId.Should().Be("sub_123");
    sub.Status.Should().Be(UserSubscriptionStatus.Trialing);
    sub.Amount.Should().Be(0m, "nothing is owed while trialing");
  }

  [Fact]
  public async Task Provision_adopts_Stripes_trial_end_rather_than_a_local_calculation()
  {
    var user = await SeedUserAsync();

    await _service.ProvisionStripeTrialAsync(user.Id, 60);

    // Stripe drives both the reminder and the pause, so our cached date has to match its
    // clock exactly or the two disagree at the boundary.
    (await _context.Users.FindAsync(user.Id))!.TrialEndDate.Should().Be(StripeTrialEnd);
  }

  [Fact]
  public async Task Provision_passes_the_requested_days_through_to_Stripe()
  {
    var user = await SeedUserAsync();

    await _service.ProvisionStripeTrialAsync(user.Id, 60);

    // The captured lead's longer offer must reach Stripe, not be replaced by the default.
    _stripe.Verify(x => x.CreateCardlessTrialSubscriptionAsync(
      It.IsAny<string>(), It.IsAny<SubscriptionTier>(), 60), Times.Once);
  }

  [Fact]
  public async Task Provision_is_idempotent_and_never_creates_a_second_subscription()
  {
    var user = await SeedUserAsync();

    await _service.ProvisionStripeTrialAsync(user.Id, 60);
    (await _service.ProvisionStripeTrialAsync(user.Id, 60)).Should().BeTrue();

    (await _context.UserSubscriptions.CountAsync()).Should().Be(1);
    _stripe.Verify(x => x.CreateCardlessTrialSubscriptionAsync(
      It.IsAny<string>(), It.IsAny<SubscriptionTier>(), It.IsAny<int>()), Times.Once);
  }

  [Fact]
  public async Task Provision_reuses_an_existing_Stripe_customer_found_by_email()
  {
    var user = await SeedUserAsync();
    _stripe.Setup(x => x.FindCustomerIdByEmailAsync("t@example.com")).ReturnsAsync("cus_existing");

    await _service.ProvisionStripeTrialAsync(user.Id, 60);

    // A retry after a partial failure must not mint a second customer for the same person.
    _stripe.Verify(x => x.CreateCustomerAsync(It.IsAny<User>()), Times.Never);
    (await _context.Users.FindAsync(user.Id))!.StripeCustomerId.Should().Be("cus_existing");
  }

  [Fact]
  public async Task Provision_swallows_a_Stripe_failure_so_registration_still_succeeds()
  {
    var user = await SeedUserAsync();
    _stripe.Setup(x => x.CreateCardlessTrialSubscriptionAsync(
        It.IsAny<string>(), It.IsAny<SubscriptionTier>(), It.IsAny<int>()))
      .ThrowsAsync(new InvalidOperationException("stripe is down"));

    // Must not throw: signing up cannot depend on a third party being reachable.
    (await _service.ProvisionStripeTrialAsync(user.Id, 60)).Should().BeFalse();

    (await _context.UserSubscriptions.AnyAsync()).Should().BeFalse();
    // The locally granted trial is untouched, so the user still has access.
    (await _context.Users.FindAsync(user.Id))!.IsTrialActive.Should().BeTrue();
  }

  [Fact]
  public async Task Provision_ignores_a_zero_or_negative_trial()
  {
    var user = await SeedUserAsync();

    (await _service.ProvisionStripeTrialAsync(user.Id, 0)).Should().BeFalse();
    (await _service.ProvisionStripeTrialAsync(user.Id, -5)).Should().BeFalse();

    _stripe.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task Reconcile_attaches_Stripe_to_a_trial_that_never_reached_it()
  {
    var user = await SeedUserAsync();

    (await _service.ReconcileMissingTrialsAsync()).Should().Be(1);

    (await _context.UserSubscriptions.SingleAsync()).StripeSubscriptionId.Should().Be("sub_123");
  }

  [Fact]
  public async Task Reconcile_provisions_only_the_days_that_actually_remain()
  {
    // A trial granted 50 days ago with 10 left must not be restored to its full length just
    // because it sat un-provisioned.
    var user = await SeedUserAsync();
    user.TrialStartDate = DateTime.UtcNow.AddDays(-50);
    user.TrialEndDate = DateTime.UtcNow.AddDays(10);
    await _context.SaveChangesAsync();

    await _service.ReconcileMissingTrialsAsync();

    _stripe.Verify(x => x.CreateCardlessTrialSubscriptionAsync(
      It.IsAny<string>(), It.IsAny<SubscriptionTier>(), It.IsInRange(9, 11, Moq.Range.Inclusive)),
      Times.Once);
  }

  [Fact]
  public async Task Reconcile_skips_users_who_already_have_a_Stripe_subscription()
  {
    var user = await SeedUserAsync();
    await _service.ProvisionStripeTrialAsync(user.Id, 60);

    (await _service.ReconcileMissingTrialsAsync()).Should().Be(0);
  }

  [Fact]
  public async Task Reconcile_ignores_expired_and_inactive_trials()
  {
    var expired = await SeedUserAsync();
    expired.TrialEndDate = DateTime.UtcNow.AddDays(-1);
    await _context.SaveChangesAsync();

    (await _service.ReconcileMissingTrialsAsync()).Should().Be(0);
    (await _context.UserSubscriptions.AnyAsync()).Should().BeFalse();
  }

  [Fact]
  public async Task Provision_adopts_an_orphaned_Stripe_subscription_instead_of_duplicating_it()
  {
    // The window this closes: the Stripe subscription was created, then our SaveChangesAsync
    // failed, so Stripe has a subscription we have no row for. Checking only our own DB would
    // create a SECOND one and bill the customer twice on conversion.
    var user = await SeedUserAsync();
    user.StripeCustomerId = "cus_orphan";
    await _context.SaveChangesAsync();

    var orphan = new StripeSubscriptionSnapshot("sub_orphan", "trialing", StripeTrialEnd, null, false);
    _stripe.Setup(x => x.GetLiveSubscriptionAsync("cus_orphan")).ReturnsAsync(orphan);

    (await _service.ProvisionStripeTrialAsync(user.Id, 60)).Should().BeTrue();

    _stripe.Verify(x => x.CreateCardlessTrialSubscriptionAsync(
      It.IsAny<string>(), It.IsAny<SubscriptionTier>(), It.IsAny<int>()), Times.Never);
    _sync.Verify(x => x.ProjectAsync(user.Id, orphan, It.IsAny<CancellationToken>()), Times.Once);
  }

  // ── Lapsed local trials (the limbo the other two sweeps can't see) ──────

  private User SeedUser(int id, string email, bool trialActive, DateTime? trialEnd,
                        SubscriptionTier? tier = null, bool requiresPaid = false)
  {
    var u = new User
    {
      Id = id, Email = email, Username = email, FirstName = "T", LastName = "U",
      IsTrialActive = trialActive, TrialEndDate = trialEnd, SubscriptionTier = tier,
      RequiresPaidPlan = requiresPaid, CreatedAt = DateTime.UtcNow.AddMonths(-3),
      UpdatedAt = DateTime.UtcNow.AddMonths(-3),
    };
    _context.Users.Add(u);
    return u;
  }

  [Fact]
  public async Task CloseLapsedLocalTrialsAsync_ShouldCloseAnExpiredTrialThatNeverReachedStripe()
  {
    var user = SeedUser(1, "lapsed@example.com", trialActive: true,
                        trialEnd: DateTime.UtcNow.AddDays(-45));
    await _context.SaveChangesAsync();

    var closed = await _service.CloseLapsedLocalTrialsAsync();

    closed.Should().Be(1);
    user.IsTrialActive.Should().BeFalse();
    // Same shape Stripe's `paused` handler writes, so the user meets the normal
    // choose-a-plan paywall rather than a 403 with no way forward.
    user.RequiresPaidPlan.Should().BeTrue();
    user.SubscriptionTier.Should().BeNull();
  }

  [Fact]
  public async Task CloseLapsedLocalTrialsAsync_ShouldNotPaywallSomeoneWhoAlreadyHasATier()
  {
    // An operator (or anyone holding a paid tier) with a stale trial flag must not be
    // handed a blocking paywall as a side effect of tidying that flag up.
    var admin = SeedUser(2, "operator@example.com", trialActive: true,
                         trialEnd: DateTime.UtcNow.AddDays(-45), tier: SubscriptionTier.Admin);
    await _context.SaveChangesAsync();

    await _service.CloseLapsedLocalTrialsAsync();

    admin.IsTrialActive.Should().BeFalse();
    admin.RequiresPaidPlan.Should().BeFalse();
    admin.SubscriptionTier.Should().Be(SubscriptionTier.Admin);
  }

  [Fact]
  public async Task CloseLapsedLocalTrialsAsync_ShouldLeaveARunningTrialAlone()
  {
    var running = SeedUser(3, "running@example.com", trialActive: true,
                           trialEnd: DateTime.UtcNow.AddDays(10));
    await _context.SaveChangesAsync();

    var closed = await _service.CloseLapsedLocalTrialsAsync();

    closed.Should().Be(0);
    running.IsTrialActive.Should().BeTrue();
    running.RequiresPaidPlan.Should().BeFalse();
  }

  [Fact]
  public async Task CloseLapsedLocalTrialsAsync_ShouldIgnoreTrialsBackedByStripe()
  {
    // Those belong to the drift sweep, which reads the real subscription state; closing
    // them here from a local date would fight it.
    var backed = SeedUser(4, "backed@example.com", trialActive: true,
                          trialEnd: DateTime.UtcNow.AddDays(-5));
    _context.UserSubscriptions.Add(new UserSubscription
    {
      UserId = 4, StripeSubscriptionId = "sub_live", Tier = SubscriptionTier.Premium,
      Status = UserSubscriptionStatus.Trialing, StartDate = DateTime.UtcNow.AddDays(-35),
    });
    await _context.SaveChangesAsync();

    var closed = await _service.CloseLapsedLocalTrialsAsync();

    closed.Should().Be(0);
    backed.IsTrialActive.Should().BeTrue();
  }

  [Fact]
  public async Task CloseLapsedLocalTrials_StripsFamilyMembersInheritedTier()
  {
    var user = new User
    {
      Email = "lapsed@example.com",
      Username = "lapsed",
      PasswordHash = "x",
      Currency = "USD",
      IsTrialActive = true,
      HasUsedTrial = true,
      TrialEndDate = DateTime.UtcNow.AddDays(-3), // expired, never reached Stripe
    };
    _context.Users.Add(user);
    await _context.SaveChangesAsync();

    var closed = await _service.CloseLapsedLocalTrialsAsync();

    closed.Should().Be(1);
    // The invitees' PremiumInherited must go with the owner's trial coverage.
    _family.Verify(f => f.UpdateFamilyMembersTierAsync(user.Id, null), Times.Once);
  }

  // ── Cloud-only sign-up: a customer, never a trial ──

  [Fact]
  public async Task EnsureCustomer_creates_the_customer_and_no_subscription()
  {
    var user = await SeedUserAsync(trialing: false);

    (await _service.EnsureStripeCustomerAsync(user.Id)).Should().BeTrue();

    (await _context.Users.SingleAsync()).StripeCustomerId.Should().Be("cus_new");
    _context.UserSubscriptions.Should().BeEmpty();
    _stripe.Verify(x => x.CreateCardlessTrialSubscriptionAsync(
      It.IsAny<string>(), It.IsAny<SubscriptionTier>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task EnsureCustomer_reuses_the_customer_Stripe_already_has_for_the_email()
  {
    var user = await SeedUserAsync(trialing: false);
    _stripe.Setup(x => x.FindCustomerIdByEmailAsync("t@example.com")).ReturnsAsync("cus_existing");

    await _service.EnsureStripeCustomerAsync(user.Id);

    (await _context.Users.SingleAsync()).StripeCustomerId.Should().Be("cus_existing");
    _stripe.Verify(x => x.CreateCustomerAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task EnsureCustomer_never_throws_when_Stripe_is_down()
  {
    var user = await SeedUserAsync(trialing: false);
    _stripe.Setup(x => x.CreateCustomerAsync(It.IsAny<User>())).ThrowsAsync(new InvalidOperationException("stripe down"));

    (await _service.EnsureStripeCustomerAsync(user.Id)).Should().BeFalse();
    (await _context.Users.SingleAsync()).StripeCustomerId.Should().BeNull();
  }

  [Fact]
  public async Task EnsureCustomer_for_an_unknown_user_is_false()
  {
    (await _service.EnsureStripeCustomerAsync(999)).Should().BeFalse();
  }
}
