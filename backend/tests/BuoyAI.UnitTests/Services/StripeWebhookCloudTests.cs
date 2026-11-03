using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.UnitTests.Services;

/// <summary>The webhook's new work for Cloud (multi-product) subscriptions, and proof that hosted plans are untouched.</summary>
public class StripeWebhookCloudTests
{
  private const string Secret = "whsec_test_secret";

  private readonly Mock<IStripeService> _stripe = new();
  private readonly Mock<IBillingService> _billing = new();
  private readonly Mock<INotificationService> _notifications = new();
  private readonly Mock<ISubscriptionSyncService> _sync = new();
  private readonly Mock<IReservationService> _reservations = new();

  private StripeWebhookController NewController(string payload, string? environment = null)
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:WebhookSecret"] = Secret }).Build();
    var controller = new StripeWebhookController(
      _stripe.Object, _billing.Object, _notifications.Object, _sync.Object,
      NullLogger<StripeWebhookController>.Instance, config, new BuoyAiMetrics(new TestBillingMeterFactory()),
      _reservations.Object,
      environment == null ? null : Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(h => h.EnvironmentName == environment));

    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
    var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

    var http = new DefaultHttpContext();
    http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
    http.Request.Headers["Stripe-Signature"] = $"t={timestamp},v1={signature}";
    controller.ControllerContext = new ControllerContext { HttpContext = http };
    return controller;
  }

  private static string EventJson(string type, string objectJson) =>
    "{\"id\":\"evt_1\",\"object\":\"event\",\"api_version\":\"" + Stripe.StripeConfiguration.ApiVersion +
    "\",\"created\":1700000000,\"livemode\":false,\"pending_webhooks\":1,\"request\":{\"id\":null,\"idempotency_key\":null},\"type\":\"" + type +
    "\",\"data\":{\"object\":" + objectJson + "}}";

  private static string PriceJson(string id, string? entitlement, long cents) =>
    "{\"id\":\"" + id + "\",\"object\":\"price\",\"currency\":\"usd\",\"unit_amount\":" + cents +
    ",\"metadata\":{" + (entitlement == null ? "" : "\"buoy_entitlement\":\"" + entitlement + "\"") + "}" +
    ",\"recurring\":{\"interval\":\"year\",\"interval_count\":1},\"type\":\"recurring\"}";

  private static string SubscriptionJson(params (string Price, string? Entitlement, long Cents, long Qty)[] items) =>
    "{\"id\":\"sub_1\",\"object\":\"subscription\",\"status\":\"active\",\"customer\":\"cus_1\",\"cancel_at_period_end\":false," +
    "\"metadata\":{\"userId\":\"42\",\"entitlements\":\"bank_sync,ai\",\"interval\":\"year\"}," +
    "\"items\":{\"object\":\"list\",\"has_more\":false,\"url\":\"/x\",\"data\":[" +
    string.Join(",", items.Select((i, n) =>
      "{\"id\":\"si_" + n + "\",\"object\":\"subscription_item\",\"quantity\":" + i.Qty +
      ",\"current_period_end\":1798000000,\"price\":" + PriceJson(i.Price, i.Entitlement, i.Cents) + "}")) + "]}}";

  private static string InvoiceJson(string? subscriptionId, bool cloud = true, string? environment = null) =>
    "{\"id\":\"in_1\",\"object\":\"invoice\",\"amount_paid\":2399" +
    (subscriptionId == null ? "" :
      ",\"parent\":{\"type\":\"subscription_details\",\"subscription_details\":{\"subscription\":\"" + subscriptionId + "\",\"metadata\":{" +
      (cloud ? "\"entitlements\":\"bank_sync,ai\"" : "\"tier\":\"1\"") +
      (environment == null ? "" : ",\"environment\":\"" + environment + "\"") + "}}}") + "}";

  private static StripeSubscriptionSnapshot Snapshot() =>
    new("sub_1", "active", null, "price_a", false);

  // ── invoice.paid ──

  [Fact]
  public async Task InvoicePaid_ReReadsTheSubscription_AndProjectsIt()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ReturnsAsync(Snapshot());

    var result = await NewController(EventJson("invoice.paid", InvoiceJson("sub_1"))).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _sync.Verify(s => s.ProjectAsync(42, Snapshot(), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task InvoicePaid_ForAHostedPlan_MakesNoStripeCallAndProjectsNothing()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);

    var result = await NewController(EventJson("invoice.paid", InvoiceJson("sub_1", cloud: false))).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _stripe.Verify(s => s.GetSubscriptionAsync(It.IsAny<string>()), Times.Never);
    _sync.Verify(s => s.ProjectAsync(It.IsAny<int>(), It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task InvoicePaid_ForAnotherEnvironmentsPlan_IsIgnored()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);

    var result = await NewController(
      EventJson("invoice.paid", InvoiceJson("sub_1", environment: "production")), "Staging").HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _stripe.Verify(s => s.GetSubscriptionAsync(It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task SubscriptionCreated_ForAnotherEnvironment_ActivatesNothing()
  {
    var json = SubscriptionJson(("price_bank", "bank_sync", 1200, 1))
      .Replace("\"interval\":\"year\"}", "\"interval\":\"year\",\"environment\":\"production\"}");

    var result = await NewController(EventJson("customer.subscription.created", json), "Staging").HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _billing.Verify(b => b.ActivateSubscriptionFromCheckoutAsync(
      It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IDictionary<string, string>?>(),
      It.IsAny<string?>(), It.IsAny<decimal?>()), Times.Never);
    _sync.Verify(s => s.ProjectAsync(It.IsAny<int>(), It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task InvoicePaid_ForAOneOffInvoice_DoesNothing()
  {
    var result = await NewController(EventJson("invoice.paid", InvoiceJson(null))).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _stripe.Verify(s => s.GetSubscriptionAsync(It.IsAny<string>()), Times.Never);
    _sync.Verify(s => s.ProjectAsync(It.IsAny<int>(), It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task InvoicePaid_ForASubscriptionWeHaveNoUserFor_DoesNothing()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync((int?)null);

    var result = await NewController(EventJson("invoice.paid", InvoiceJson("sub_1"))).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _stripe.Verify(s => s.GetSubscriptionAsync(It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task InvoicePaid_WhenStripeNoLongerKnowsTheSubscription_ProjectsNothing()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ReturnsAsync((StripeSubscriptionSnapshot?)null);

    var result = await NewController(EventJson("invoice.paid", InvoiceJson("sub_1"))).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _sync.Verify(s => s.ProjectAsync(It.IsAny<int>(), It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task InvoicePaid_DoesNotRunTheLegacyPaymentPath()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ReturnsAsync(Snapshot());

    await NewController(EventJson("invoice.paid", InvoiceJson("sub_1"))).HandleWebhook();

    // invoice.payment_succeeded still owns payment rows and emails; invoice.paid must not double them.
    _billing.Verify(b => b.HandleSuccessfulPaymentAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>()), Times.Never);
  }

  // ── schedules ──

  [Theory]
  [InlineData("subscription_schedule.created")]
  [InlineData("subscription_schedule.updated")]
  [InlineData("subscription_schedule.completed")]
  [InlineData("subscription_schedule.canceled")]
  [InlineData("subscription_schedule.aborted")]
  public async Task AScheduleChange_ReProjectsItsSubscription(string type)
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ReturnsAsync(Snapshot());

    var result = await NewController(EventJson(type,
      "{\"id\":\"sub_sched_1\",\"object\":\"subscription_schedule\",\"status\":\"active\",\"subscription\":\"sub_1\",\"phases\":[]}")).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _sync.Verify(s => s.ProjectAsync(42, It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task AReleasedSchedule_UsesTheSubscriptionItWasReleasedInto()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ReturnsAsync(Snapshot());

    await NewController(EventJson("subscription_schedule.released",
      "{\"id\":\"sub_sched_1\",\"object\":\"subscription_schedule\",\"status\":\"released\",\"subscription\":null,\"released_subscription\":\"sub_1\",\"phases\":[]}")).HandleWebhook();

    _sync.Verify(s => s.ProjectAsync(42, It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task AScheduleWithNoSubscription_DoesNothing()
  {
    var result = await NewController(EventJson("subscription_schedule.updated",
      "{\"id\":\"sub_sched_1\",\"object\":\"subscription_schedule\",\"status\":\"active\",\"subscription\":null,\"phases\":[]}")).HandleWebhook();
    result.Should().BeOfType<OkResult>();
    _stripe.Verify(s => s.GetSubscriptionAsync(It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task AProjectionFailure_FailsTheEvent_SoStripeRetries()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ReturnsAsync(Snapshot());
    _sync.Setup(s => s.ProjectAsync(It.IsAny<int>(), It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidOperationException("db down"));

    var act = () => NewController(EventJson("invoice.paid", InvoiceJson("sub_1"))).HandleWebhook();

    await act.Should().ThrowAsync<InvalidOperationException>();
    _billing.Verify(b => b.MarkStripeEventProcessedAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
  }

  // ── a new subscription ──

  [Fact]
  public async Task ANewCloudSubscription_RecordsTheSumOfItsItems_AndIsProjected()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);

    var json = SubscriptionJson(("price_a", "bank_sync", 2399, 1), ("price_b", "extra_logins", 600, 3), ("price_c", "ai", 1499, 1));
    var result = await NewController(EventJson("customer.subscription.created", json)).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _billing.Verify(b => b.ActivateSubscriptionFromCheckoutAsync(
      null, "sub_1", "cus_1", It.Is<IDictionary<string, string>>(m => m["userId"] == "42" && m["entitlements"] == "bank_sync,ai"),
      "active", (2399 + 3 * 600 + 1499) / 100m), Times.Once);
    _sync.Verify(s => s.ProjectAsync(42, It.Is<StripeSubscriptionSnapshot>(x => x.IsCatalog && x.Items!.Count == 3), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task ANewHostedSubscription_IsHandledExactlyAsBefore_NoProjection()
  {
    var json = SubscriptionJson(("price_prem_m", null, 1499, 1));
    var result = await NewController(EventJson("customer.subscription.created", json)).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _billing.Verify(b => b.ActivateSubscriptionFromCheckoutAsync(
      null, "sub_1", "cus_1", It.IsAny<IDictionary<string, string>>(), "active", 14.99m), Times.Once);
    _sync.Verify(s => s.ProjectAsync(It.IsAny<int>(), It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task AnUpdatedCloudSubscription_ProjectsEveryItem()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);

    var json = SubscriptionJson(("price_a", "bank_sync", 2399, 1), ("price_c", "ai", 1499, 1));
    await NewController(EventJson("customer.subscription.updated", json)).HandleWebhook();

    _sync.Verify(s => s.ProjectAsync(42,
      It.Is<StripeSubscriptionSnapshot>(x => x.Items!.Select(i => i.Entitlements).OrderBy(e => e).SequenceEqual(new[] { "ai", "bank_sync" })),
      It.IsAny<CancellationToken>()), Times.Once);
  }

  // ── out-of-order and ended subscriptions ──

  [Fact]
  public async Task AnUpdatedCloudSubscription_ProjectsTheLiveState_NotAPossiblyStaleEvent()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    var live = new StripeSubscriptionSnapshot("sub_1", "active", null, "price_live", false,
      [new StripeSnapshotItem("price_live", null, "bank_sync", 1, false, "year", 2399)], null, null);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ReturnsAsync(live);

    var json = SubscriptionJson(("price_a", "bank_sync", 2399, 1), ("price_c", "ai", 1499, 1));   // the older event
    await NewController(EventJson("customer.subscription.updated", json)).HandleWebhook();

    _sync.Verify(s => s.ProjectAsync(42, live, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task WhenStripeCannotBeReRead_TheEventPayloadIsStillProjected()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ThrowsAsync(new InvalidOperationException("stripe down"));

    var json = SubscriptionJson(("price_a", "bank_sync", 2399, 1));
    var result = await NewController(EventJson("customer.subscription.updated", json)).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _sync.Verify(s => s.ProjectAsync(42, It.Is<StripeSubscriptionSnapshot>(x => x.IsCatalog), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task AHostedSubscriptionUpdate_IsNeverReReadFromStripe()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);

    await NewController(EventJson("customer.subscription.updated", SubscriptionJson(("price_prem_m", null, 1499, 1)))).HandleWebhook();

    _stripe.Verify(s => s.GetSubscriptionAsync(It.IsAny<string>()), Times.Never);
    _sync.Verify(s => s.ProjectAsync(42, It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task ADeletedCloudSubscription_RemovesItsProducts()
  {
    _billing.Setup(b => b.GetUserIdByStripeSubscriptionIdAsync("sub_1")).ReturnsAsync(42);
    var ended = new StripeSubscriptionSnapshot("sub_1", "canceled", null, "price_a", false,
      [new StripeSnapshotItem("price_a", null, "bank_sync", 1, false, "year", 2399)], null, null);
    _stripe.Setup(s => s.GetSubscriptionAsync("sub_1")).ReturnsAsync(ended);

    var json = SubscriptionJson(("price_a", "bank_sync", 2399, 1)).Replace("\"status\":\"active\"", "\"status\":\"canceled\"");
    var result = await NewController(EventJson("customer.subscription.deleted", json)).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _billing.Verify(b => b.CancelSubscriptionByStripeIdAsync("sub_1"), Times.Once);
    _sync.Verify(s => s.ProjectAsync(42, ended, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task ADeletedHostedSubscription_IsHandledExactlyAsBefore()
  {
    var json = SubscriptionJson(("price_prem_m", null, 1499, 1)).Replace("\"status\":\"active\"", "\"status\":\"canceled\"");
    await NewController(EventJson("customer.subscription.deleted", json)).HandleWebhook();

    _billing.Verify(b => b.CancelSubscriptionByStripeIdAsync("sub_1"), Times.Once);
    _sync.Verify(s => s.ProjectAsync(It.IsAny<int>(), It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  // ── disputes ──

  [Fact]
  public async Task ADispute_IsAcknowledged_AndChangesNothing()
  {
    var result = await NewController(EventJson("charge.dispute.created",
      "{\"id\":\"dp_1\",\"object\":\"dispute\",\"amount\":2399}")).HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _billing.Verify(b => b.MarkStripeEventProcessedAsync("evt_1", "charge.dispute.created"), Times.Once);
    _sync.VerifyNoOtherCalls();
  }
}
