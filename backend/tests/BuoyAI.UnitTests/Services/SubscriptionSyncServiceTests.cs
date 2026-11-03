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

/// <summary>
/// Covers THE mapping. Every path that learns something from Stripe funnels through this
/// service, so these tests are the contract for what each Stripe status does to local state.
/// </summary>
public class SubscriptionSyncServiceTests
{
  private readonly BuoyAIDbContext _context;
  private readonly Mock<IStripeService> _stripe = new();
  private readonly SubscriptionSyncService _service;

  private static readonly DateTime TrialEnd = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

  public SubscriptionSyncServiceTests()
  {
    _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["Stripe:Products:Premium:Monthly"] = "price_prem_m",
      ["Stripe:Products:Standard:Monthly"] = "price_std_m"
    }).Build();

    _service = new SubscriptionSyncService(
      _context, _stripe.Object, config, Mock.Of<ILogger<SubscriptionSyncService>>());
  }

  private async Task<User> SeedAsync(string? customerId = "cus_1", bool trialing = true)
  {
    var user = new User
    {
      Email = "u@example.com",
      Username = "u",
      PasswordHash = "x",
      FirstName = "U",
      LastName = "Ser",
      StripeCustomerId = customerId,
      IsTrialActive = trialing,
      TrialStartDate = trialing ? DateTime.UtcNow.AddDays(-5) : null,
      TrialEndDate = trialing ? DateTime.UtcNow.AddDays(25) : null,
      HasUsedTrial = true
    };
    _context.Users.Add(user);
    await _context.SaveChangesAsync();
    return user;
  }

  private static StripeSubscriptionSnapshot Snap(string status, DateTime? trialEnd = null, string? price = null) =>
    new("sub_1", status, trialEnd, price, false);

  // ── The safety rule ──

  [Fact]
  public async Task Stripe_having_no_subscription_never_revokes_a_local_trial()
  {
    // Pre-migration users have a local trial and no Stripe subscription at all. Treating an
    // absence as "revoke" would delete access we already granted, on the strength of nothing.
    var user = await SeedAsync();
    _stripe.Setup(x => x.GetLiveSubscriptionAsync("cus_1")).ReturnsAsync((StripeSubscriptionSnapshot?)null);

    await _service.RefreshFromStripeAsync(user.Id, "test", TimeSpan.Zero);

    var after = await _context.Users.FindAsync(user.Id);
    after!.IsTrialActive.Should().BeTrue();
    after.TrialEndDate.Should().NotBeNull();
  }

  [Fact]
  public async Task A_user_with_no_Stripe_customer_is_left_entirely_alone()
  {
    var user = await SeedAsync(customerId: null);

    (await _service.RefreshFromStripeAsync(user.Id, "test", TimeSpan.Zero)).Should().BeFalse();

    _stripe.Verify(x => x.GetLiveSubscriptionAsync(It.IsAny<string>()), Times.Never);
    (await _context.Users.FindAsync(user.Id))!.IsTrialActive.Should().BeTrue();
  }

  // ── The mapping ──

  [Fact]
  public async Task Trialing_adopts_Stripes_trial_end()
  {
    // The field that silently drifted before: a trial extended in the Stripe dashboard never
    // reached us, so access kept expiring on our stale date.
    var user = await SeedAsync();
    await _service.ProjectAsync(user.Id, Snap("trialing", TrialEnd));

    var after = await _context.Users.FindAsync(user.Id);
    after!.IsTrialActive.Should().BeTrue();
    after.TrialEndDate.Should().Be(TrialEnd);
    after.RequiresPaidPlan.Should().BeFalse();
  }

  [Fact]
  public async Task Paused_ends_the_trial_and_raises_the_paywall()
  {
    var user = await SeedAsync();
    await _service.ProjectAsync(user.Id, Snap("paused"));

    var after = await _context.Users.FindAsync(user.Id);
    after!.IsTrialActive.Should().BeFalse();
    after.RequiresPaidPlan.Should().BeTrue();
    (await _context.UserSubscriptions.SingleAsync()).Status.Should().Be(UserSubscriptionStatus.TrialExpired);
  }

  [Fact]
  public async Task Active_converts_the_trial_and_sets_the_paid_tier()
  {
    var user = await SeedAsync();
    await _service.ProjectAsync(user.Id, Snap("active", price: "price_prem_m"));

    var after = await _context.Users.FindAsync(user.Id);
    after!.IsTrialActive.Should().BeFalse("a converted user is no longer trialing");
    after.RequiresPaidPlan.Should().BeFalse();
    after.SubscriptionTier.Should().Be(SubscriptionTier.Premium);
  }

  [Fact]
  public async Task Past_due_keeps_access_while_Stripe_retries_the_card()
  {
    // Cutting someone off mid-dunning turns a recoverable card blip into a cancellation.
    var user = await SeedAsync(trialing: false);
    await _service.ProjectAsync(user.Id, Snap("past_due", price: "price_prem_m"));

    (await _context.Users.FindAsync(user.Id))!.RequiresPaidPlan.Should().BeFalse();
    (await _context.UserSubscriptions.SingleAsync()).Status.Should().Be(UserSubscriptionStatus.PastDue);
  }

  [Fact]
  public async Task Canceled_raises_the_paywall()
  {
    var user = await SeedAsync();
    await _service.ProjectAsync(user.Id, Snap("canceled"));

    var after = await _context.Users.FindAsync(user.Id);
    after!.IsTrialActive.Should().BeFalse();
    after.RequiresPaidPlan.Should().BeTrue();
    after.SubscriptionTier.Should().BeNull();
  }

  [Fact]
  public async Task An_unrecognised_status_changes_nothing()
  {
    // Guessing at a status we do not know would silently grant or revoke access on a Stripe
    // API change. Doing nothing is the only safe default.
    var user = await SeedAsync();
    await _service.ProjectAsync(user.Id, Snap("some_future_status"));

    var after = await _context.Users.FindAsync(user.Id);
    after!.IsTrialActive.Should().BeTrue();
    after.RequiresPaidPlan.Should().BeFalse();
  }

  // ── What Stripe must never own ──

  [Fact]
  public async Task Projection_never_clears_HasUsedTrial()
  {
    // If a Stripe status could flip this, cancelling and re-subscribing would hand out a
    // second free trial.
    var user = await SeedAsync();

    foreach (var status in new[] { "canceled", "paused", "active", "trialing" })
    {
      await _service.ProjectAsync(user.Id, Snap(status));
      (await _context.Users.FindAsync(user.Id))!.HasUsedTrial
        .Should().BeTrue($"'{status}' must not reset the lifetime trial marker");
    }
  }

  // ── Debounce and repair ──

  [Fact]
  public async Task Refresh_is_debounced_so_a_hot_endpoint_cannot_hammer_Stripe()
  {
    var user = await SeedAsync();
    user.LastStripeSyncAt = DateTime.UtcNow.AddSeconds(-5);
    await _context.SaveChangesAsync();

    (await _service.RefreshFromStripeAsync(user.Id, "test")).Should().BeFalse();

    _stripe.Verify(x => x.GetLiveSubscriptionAsync(It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task Refresh_proceeds_once_the_debounce_window_has_passed()
  {
    var user = await SeedAsync();
    user.LastStripeSyncAt = DateTime.UtcNow.AddMinutes(-10);
    await _context.SaveChangesAsync();
    _stripe.Setup(x => x.GetLiveSubscriptionAsync("cus_1")).ReturnsAsync(Snap("trialing", TrialEnd));

    (await _service.RefreshFromStripeAsync(user.Id, "test")).Should().BeTrue();
  }

  [Fact]
  public async Task Refresh_survives_a_Stripe_outage_and_leaves_the_cache_intact()
  {
    var user = await SeedAsync();
    _stripe.Setup(x => x.GetLiveSubscriptionAsync("cus_1"))
      .ThrowsAsync(new InvalidOperationException("stripe is down"));

    (await _service.RefreshFromStripeAsync(user.Id, "test", TimeSpan.Zero)).Should().BeFalse();

    // Availability is the whole point: a Stripe outage must not revoke anyone's access.
    (await _context.Users.FindAsync(user.Id))!.IsTrialActive.Should().BeTrue();
  }

  [Fact]
  public async Task Projection_re_attaches_a_subscription_that_has_no_local_row()
  {
    // Repairs the orphan left by a Stripe-succeeded / DB-failed provisioning attempt.
    var user = await SeedAsync();

    await _service.ProjectAsync(user.Id, Snap("trialing", TrialEnd));

    var row = await _context.UserSubscriptions.SingleAsync();
    row.StripeSubscriptionId.Should().Be("sub_1");
    row.UserId.Should().Be(user.Id);
  }

  [Fact]
  public async Task Projection_updates_the_existing_row_rather_than_adding_another()
  {
    var user = await SeedAsync();
    await _service.ProjectAsync(user.Id, Snap("trialing", TrialEnd));
    await _service.ProjectAsync(user.Id, Snap("paused"));

    (await _context.UserSubscriptions.CountAsync()).Should().Be(1);
    (await _context.UserSubscriptions.SingleAsync()).Status.Should().Be(UserSubscriptionStatus.TrialExpired);
  }
}
