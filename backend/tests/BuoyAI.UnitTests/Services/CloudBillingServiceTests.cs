using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class CloudBillingServiceTests
{
  private readonly BuoyAIDbContext _context = new(new DbContextOptionsBuilder<BuoyAIDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
  private readonly Mock<ICloudBillingClient> _client = new();
  private readonly Mock<IReservationService> _reservations = new();
  private readonly Mock<IStripeService> _stripe = new();
  private readonly Mock<ISubscriptionSyncService> _sync = new();
  private readonly Mock<IEntitlementService> _entitlements = new();

  private CloudBillingService Service(bool enabled = true, string environment = "Production")
  {
    _entitlements.Setup(e => e.GetProductAccessAsync(It.IsAny<int>())).ReturnsAsync(ProductAccess.None);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["App:BaseUrl"] = "https://buoy-ai.com/",
    }).Build();
    return new CloudBillingService(
      _context, _client.Object, _reservations.Object, _stripe.Object, _sync.Object, _entitlements.Object, config,
      Options.Create(new CloudBillingOptions { Enabled = enabled }),
      Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment),
      NullLogger<CloudBillingService>.Instance);
  }

  private async Task<User> UserAsync(string email = "pat@example.com", bool verified = true, string? customer = "cus_1")
  {
    var user = new User
    {
      Email = email, Username = email, PasswordHash = "x", FirstName = "P", LastName = "T",
      IsEmailVerified = verified, StripeCustomerId = customer
    };
    _context.Users.Add(user);
    await _context.SaveChangesAsync();
    return user;
  }

  private static CloudSubscriptionState State() => new(
    "sub_1", "active", "yearly", true, null, false,
    [new CloudProductState("bank_sync", 1, ["bank_sync"], null, null)], [], []);

  // ── off by default ──

  [Fact]
  public async Task WhenOff_NothingLeavesTheServer()
  {
    var user = await UserAsync();
    var service = Service(enabled: false);

    var status = await service.GetStatusAsync(user.Id);
    status.Enabled.Should().BeFalse();
    status.Subscription.Should().BeNull();

    foreach (var act in new Func<Task>[]
    {
      () => service.GetCatalogAsync(user.Id),
      () => service.StartCheckoutAsync(user.Id, "yearly", [new("ai")], null, null),
      () => service.PreviewAsync(user.Id, "add", "ai", null, null),
      () => service.ApplyAsync(user.Id, "add", "ai", null, null, null)
    })
    {
      (await act.Should().ThrowAsync<CloudBillingException>()).Which.Code.Should().Be("not_available");
    }

    _client.VerifyNoOtherCalls();
    _stripe.VerifyNoOtherCalls();
  }

  // ── status ──

  [Fact]
  public async Task Status_ReportsWhatTheUserHolds_FromTheirOwnDatabase()
  {
    var user = await UserAsync();
    _entitlements.Setup(e => e.GetProductAccessAsync(user.Id)).ReturnsAsync(
      ProductRules.Normalize([ProductEntitlements.BankSync, ProductEntitlements.Ai], 2, null, founding: true));
    _client.Setup(c => c.GetSubscriptionAsync("cus_1", "production", It.IsAny<CancellationToken>())).ReturnsAsync(State());
    var service = Service();
    _entitlements.Setup(e => e.GetProductAccessAsync(user.Id)).ReturnsAsync(
      ProductRules.Normalize([ProductEntitlements.BankSync, ProductEntitlements.Ai], 2, null, founding: true));

    var status = await service.GetStatusAsync(user.Id);

    status.Enabled.Should().BeTrue();
    status.Access.Should().BeEquivalentTo(new { BankSync = true, Ai = true, InstantRefresh = false, ExtraLogins = 2, Founding = true });
    status.Subscription!.SubscriptionId.Should().Be("sub_1");
    status.SubscriptionLookupFailed.Should().BeFalse();
  }

  [Fact]
  public async Task Status_WithNoStripeCustomer_AsksNobody()
  {
    var user = await UserAsync(customer: null);
    var status = await Service().GetStatusAsync(user.Id);
    status.Subscription.Should().BeNull();
    _client.Verify(c => c.GetSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task Status_StillOpens_WhenTheBillingServiceIsDown()
  {
    var user = await UserAsync();
    _client.Setup(c => c.GetSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new CloudBillingException("unreachable", "down", 503));

    var status = await Service().GetStatusAsync(user.Id);

    status.Subscription.Should().BeNull();
    status.SubscriptionLookupFailed.Should().BeTrue();
    status.Access.Should().NotBeNull();
  }

  [Fact]
  public async Task Status_StillOpens_EvenWhenTheBillingServiceRefuses()
  {
    var user = await UserAsync();
    _client.Setup(c => c.GetSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new CloudBillingException("environment_mismatch", "other env", 409));

    var status = await Service().GetStatusAsync(user.Id);

    status.SubscriptionLookupFailed.Should().BeTrue();
    status.Access.Should().NotBeNull();
  }

  // ── founding eligibility ──

  [Theory]
  [InlineData(true, true, true)]
  [InlineData(true, false, false)]
  [InlineData(false, true, false)]   // an unverified email proves nothing
  [InlineData(false, false, false)]
  public async Task Founding_NeedsAVerifiedEmail_AndAPaidReservation(bool verified, bool reserved, bool expected)
  {
    var user = await UserAsync(verified: verified);
    _reservations.Setup(r => r.HasPaidReservationAsync("pat@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(reserved);
    _client.Setup(c => c.GetSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync((CloudSubscriptionState?)null);

    (await Service().GetStatusAsync(user.Id)).FoundingEligible.Should().Be(expected);
  }

  [Fact]
  public async Task AnUnverifiedEmail_DoesNotEvenAskTheReservations_ItCannotCount()
  {
    var user = await UserAsync(verified: false);
    _client.Setup(c => c.GetSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync((CloudSubscriptionState?)null);
    await Service().GetStatusAsync(user.Id);
    _reservations.Verify(r => r.HasPaidReservationAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task TheCatalogShown_IsTheUsersPriceList()
  {
    var user = await UserAsync();
    _reservations.Setup(r => r.HasPaidReservationAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    _client.Setup(c => c.GetCatalogAsync(true, It.IsAny<CancellationToken>()))
      .ReturnsAsync(new CloudCatalog("v", true, "usd", []));

    (await Service().GetCatalogAsync(user.Id)).Founding.Should().BeTrue();
  }

  // ── checkout ──

  [Fact]
  public async Task Checkout_SendsServerSideFacts_NotRequestFields()
  {
    var user = await UserAsync();
    _reservations.Setup(r => r.HasPaidReservationAsync("pat@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(true);
    _reservations.Setup(r => r.GetPaidReservationCreditCentsAsync("pat@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(100);
    CloudCheckoutRequest? sent = null;
    _client.Setup(c => c.CheckoutAsync(It.IsAny<CloudCheckoutRequest>(), It.IsAny<CancellationToken>()))
      .Callback<CloudCheckoutRequest, CancellationToken>((r, _) => sent = r)
      .ReturnsAsync(new CloudCheckoutResponse("https://checkout.stripe.com/x", "cs_1"));

    var url = await Service().StartCheckoutAsync(user.Id, "yearly", [new("bank_sync"), new("ai")], "web", "idem-1");

    url.Should().Be("https://checkout.stripe.com/x");
    sent!.CustomerId.Should().Be("cus_1");
    sent.UserId.Should().Be(user.Id);
    sent.Environment.Should().Be("production");
    sent.Founding.Should().BeTrue();
    sent.Interval.Should().Be("yearly");
    sent.IdempotencyKey.Should().Be("idem-1");
    sent.SuccessUrl.Should().Be("https://buoy-ai.com/#/billing?checkout=success");
    sent.CancelUrl.Should().Be("https://buoy-ai.com/#/billing?checkout=cancel");
    sent.Products.Select(p => p.Product).Should().Equal("bank_sync", "ai");
    sent.FoundingCreditCents.Should().Be(100); // the reservation comes off the first payment
  }

  [Fact]
  public async Task Checkout_FromTheCloudPortal_ReturnsToThePortalBilling_NotTheApp()
  {
    var user = await UserAsync();
    CloudCheckoutRequest? sent = null;
    _client.Setup(c => c.CheckoutAsync(It.IsAny<CloudCheckoutRequest>(), It.IsAny<CancellationToken>()))
      .Callback<CloudCheckoutRequest, CancellationToken>((r, _) => sent = r)
      .ReturnsAsync(new CloudCheckoutResponse("https://checkout.stripe.com/x", "cs_1"));

    await Service().StartCheckoutAsync(user.Id, "yearly", [new("bank_sync")], "portal", null);

    sent!.SuccessUrl.Should().Be("https://buoy-ai.com/cloud/billing?checkout=success");
    sent.CancelUrl.Should().Be("https://buoy-ai.com/cloud/billing?checkout=cancel");
  }

  [Fact]
  public async Task Checkout_NotFounding_WhenThereIsNoReservation()
  {
    var user = await UserAsync();
    CloudCheckoutRequest? sent = null;
    _client.Setup(c => c.CheckoutAsync(It.IsAny<CloudCheckoutRequest>(), It.IsAny<CancellationToken>()))
      .Callback<CloudCheckoutRequest, CancellationToken>((r, _) => sent = r)
      .ReturnsAsync(new CloudCheckoutResponse("https://x", "cs"));
    await Service().StartCheckoutAsync(user.Id, "monthly", [new("ai")], null, null);
    sent!.Founding.Should().BeFalse();
    sent.FoundingCreditCents.Should().BeNull();
    _reservations.Verify(r => r.GetPaidReservationCreditCentsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task Checkout_CreatesTheStripeCustomer_WhenTheUserHasNone()
  {
    var user = await UserAsync(customer: null);
    _stripe.Setup(s => s.CreateCustomerAsync(It.IsAny<User>())).ReturnsAsync("cus_new");
    _client.Setup(c => c.CheckoutAsync(It.IsAny<CloudCheckoutRequest>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new CloudCheckoutResponse("https://x", "cs"));

    await Service().StartCheckoutAsync(user.Id, "yearly", [new("ai")], null, null);

    (await _context.Users.FindAsync(user.Id))!.StripeCustomerId.Should().Be("cus_new");
    _client.Verify(c => c.CheckoutAsync(It.Is<CloudCheckoutRequest>(r => r.CustomerId == "cus_new"), It.IsAny<CancellationToken>()));
  }

  [Theory]
  [InlineData(UserSubscriptionStatus.Active)]
  [InlineData(UserSubscriptionStatus.PastDue)]
  public async Task Checkout_IsRefused_ForSomeoneWhoAlreadyHasAPlan(UserSubscriptionStatus status)
  {
    var user = await UserAsync();
    _context.UserSubscriptions.Add(new UserSubscription
    {
      UserId = user.Id, Tier = SubscriptionTier.Premium, BillingCycle = SubscriptionBillingCycle.Monthly, Status = status,
      StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();

    var act = () => Service().StartCheckoutAsync(user.Id, "yearly", [new("ai")], null, null);

    (await act.Should().ThrowAsync<CloudBillingException>()).Which.Code.Should().Be("already_subscribed");
    _client.Verify(c => c.CheckoutAsync(It.IsAny<CloudCheckoutRequest>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Theory]
  [InlineData(UserSubscriptionStatus.Canceled)]
  [InlineData(UserSubscriptionStatus.Expired)]
  [InlineData(UserSubscriptionStatus.TrialExpired)]
  [InlineData(UserSubscriptionStatus.Trialing)]
  public async Task Checkout_IsAllowed_AfterALapsedOrTrialPlan(UserSubscriptionStatus status)
  {
    var user = await UserAsync();
    _context.UserSubscriptions.Add(new UserSubscription
    {
      UserId = user.Id, Tier = SubscriptionTier.Premium, BillingCycle = SubscriptionBillingCycle.Monthly, Status = status,
      StartDate = DateTime.UtcNow, NextBillingDate = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    });
    await _context.SaveChangesAsync();
    _client.Setup(c => c.CheckoutAsync(It.IsAny<CloudCheckoutRequest>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new CloudCheckoutResponse("https://x", "cs"));

    var act = () => Service().StartCheckoutAsync(user.Id, "yearly", [new("ai")], null, null);
    await act.Should().NotThrowAsync();
  }

  [Fact]
  public async Task Checkout_IsRefused_InsideTheIosApp()
  {
    var user = await UserAsync();
    var act = () => Service().StartCheckoutAsync(user.Id, "yearly", [new("ai")], "iOS", null);
    var ex = (await act.Should().ThrowAsync<CloudBillingException>()).Which;
    ex.Code.Should().Be("web_purchase_required");
    ex.Message.Should().Contain("BuoyAI Cloud").And.NotContain("Buoy Money Cloud");
    ex.StatusCode.Should().Be(403);
    _client.Verify(c => c.CheckoutAsync(It.IsAny<CloudCheckoutRequest>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Theory]
  [InlineData("Production", "production")]
  [InlineData("Staging", "staging")]
  [InlineData("Development", "development")]
  public async Task TheEnvironmentTag_IsTheHostsOwn(string host, string tag)
  {
    var user = await UserAsync();
    CloudCheckoutRequest? sent = null;
    _client.Setup(c => c.CheckoutAsync(It.IsAny<CloudCheckoutRequest>(), It.IsAny<CancellationToken>()))
      .Callback<CloudCheckoutRequest, CancellationToken>((r, _) => sent = r)
      .ReturnsAsync(new CloudCheckoutResponse("https://x", "cs"));
    await Service(environment: host).StartCheckoutAsync(user.Id, "yearly", [new("ai")], null, null);
    sent!.Environment.Should().Be(tag);
  }

  // ── changes ──

  [Fact]
  public async Task Apply_UsesTheUsersOwnCustomer_AndReprojectsAtOnce()
  {
    var user = await UserAsync();
    CloudChangeRequest? sent = null;
    _client.Setup(c => c.ApplyAsync(It.IsAny<CloudChangeRequest>(), It.IsAny<CancellationToken>()))
      .Callback<CloudChangeRequest, CancellationToken>((r, _) => sent = r)
      .ReturnsAsync(State());

    var state = await Service().ApplyAsync(user.Id, "add", "ai", 1, null, "idem-9");

    state.SubscriptionId.Should().Be("sub_1");
    sent!.CustomerId.Should().Be("cus_1");
    sent.Environment.Should().Be("production");
    sent.Type.Should().Be("add");
    sent.Product.Should().Be("ai");
    sent.IdempotencyKey.Should().Be("idem-9");
    _sync.Verify(s => s.RefreshFromStripeAsync(user.Id, "cloud-change", TimeSpan.Zero, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task Apply_DoesNotReproject_WhenTheChangeWasRefused()
  {
    var user = await UserAsync();
    _client.Setup(c => c.ApplyAsync(It.IsAny<CloudChangeRequest>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new CloudBillingException("already_owned", "no", 409));

    var act = () => Service().ApplyAsync(user.Id, "add", "ai", null, null, null);

    await act.Should().ThrowAsync<CloudBillingException>();
    _sync.Verify(s => s.RefreshFromStripeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task Preview_PassesTheProrationDateThrough()
  {
    var user = await UserAsync();
    var when = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
    CloudChangeRequest? sent = null;
    _client.Setup(c => c.PreviewAsync(It.IsAny<CloudChangeRequest>(), It.IsAny<CancellationToken>()))
      .Callback<CloudChangeRequest, CancellationToken>((r, _) => sent = r)
      .ReturnsAsync(new CloudPreview("add", "now", null, 0, 0, "usd", when, [], []));

    await Service().PreviewAsync(user.Id, "add", "ai", null, when);

    sent!.ProrationDate.Should().Be(when);
  }

  [Fact]
  public async Task ChangingAPlan_WithNoStripeCustomer_IsNoSubscription()
  {
    var user = await UserAsync(customer: null);
    foreach (var act in new Func<Task>[]
    {
      () => Service().PreviewAsync(user.Id, "add", "ai", null, null),
      () => Service().ApplyAsync(user.Id, "add", "ai", null, null, null)
    })
    {
      (await act.Should().ThrowAsync<CloudBillingException>()).Which.Code.Should().Be("no_subscription");
    }
  }

  [Fact]
  public async Task AnUnknownUser_IsRefused()
  {
    var act = () => Service().GetStatusAsync(777);
    (await act.Should().ThrowAsync<CloudBillingException>()).Which.Code.Should().Be("user_not_found");
  }
}
