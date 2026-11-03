using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Telemetry;
using BuoyAI.UnitTests.Services;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

public class StripeWebhookReservationTests
{
  private const string Secret = "whsec_test_secret";

  private readonly Mock<IStripeService> _stripe = new();
  private readonly Mock<IBillingService> _billing = new();
  private readonly Mock<INotificationService> _notifications = new();
  private readonly Mock<ISubscriptionSyncService> _sync = new();
  private readonly Mock<IReservationService> _reservations = new();

  private StripeWebhookController NewController(string payload)
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:WebhookSecret"] = Secret })
      .Build();

    var controller = new StripeWebhookController(
      _stripe.Object,
      _billing.Object,
      _notifications.Object,
      _sync.Object,
      NullLogger<StripeWebhookController>.Instance,
      config,
      new BuoyAiMetrics(new TestBillingMeterFactory()),
      _reservations.Object);

    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
    var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}")))
      .ToLowerInvariant();

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

  [Fact]
  public async Task HandleWebhook_ReservationCheckout_RecordsReservationAndSkipsBilling()
  {
    var payload = EventJson(
      "checkout.session.completed",
      "{\"id\":\"cs_1\",\"object\":\"checkout.session\",\"mode\":\"payment\",\"payment_status\":\"paid\"," +
      "\"customer\":\"cus_1\",\"payment_intent\":\"pi_1\",\"amount_total\":100,\"currency\":\"usd\"," +
      "\"metadata\":{\"kind\":\"reservation\",\"choice\":\"both\",\"source\":\"founding_page\"}}");
    var controller = NewController(payload);

    var result = await controller.HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _reservations.Verify(r => r.RecordCompletedAsync(
      It.Is<CompletedReservationCheckout>(c => c.SessionId == "cs_1" && c.PaymentIntentId == "pi_1"),
      It.IsAny<CancellationToken>()), Times.Once);
    _billing.Verify(b => b.ActivateSubscriptionFromCheckoutAsync(
      It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
      It.IsAny<IDictionary<string, string>?>(), It.IsAny<string?>(), It.IsAny<decimal?>()), Times.Never);
  }

  [Fact]
  public async Task HandleWebhook_ChargeRefunded_MarksReservationRefunded()
  {
    var payload = EventJson(
      "charge.refunded",
      "{\"id\":\"ch_1\",\"object\":\"charge\",\"payment_intent\":\"pi_1\",\"refunded\":true}");
    var controller = NewController(payload);

    var result = await controller.HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _reservations.Verify(r => r.MarkRefundedAsync("pi_1", It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task HandleWebhook_PartialRefund_LeavesReservationPaid()
  {
    var payload = EventJson(
      "charge.refunded",
      "{\"id\":\"ch_3\",\"object\":\"charge\",\"payment_intent\":\"pi_1\",\"refunded\":false}");
    var controller = NewController(payload);

    var result = await controller.HandleWebhook();

    result.Should().BeOfType<OkResult>();
    _reservations.Verify(r => r.MarkRefundedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task HandleWebhook_RefundBeforeCheckout_FailsSoStripeRetries()
  {
    _reservations.Setup(r => r.MarkRefundedAsync("pi_early", It.IsAny<CancellationToken>())).ReturnsAsync(false);
    _reservations.Setup(r => r.IsReservationPaymentAsync("pi_early", It.IsAny<CancellationToken>())).ReturnsAsync(true);
    var payload = EventJson(
      "charge.refunded",
      "{\"id\":\"ch_4\",\"object\":\"charge\",\"payment_intent\":\"pi_early\",\"refunded\":true}");
    var controller = NewController(payload);

    var act = () => controller.HandleWebhook();

    await act.Should().ThrowAsync<InvalidOperationException>();
    _billing.Verify(b => b.MarkStripeEventProcessedAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task HandleWebhook_ChargeRefundedForNonReservation_IsNoOp()
  {
    _reservations.Setup(r => r.MarkRefundedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(false);
    var payload = EventJson(
      "charge.refunded",
      "{\"id\":\"ch_2\",\"object\":\"charge\",\"payment_intent\":\"pi_sub\",\"refunded\":true}");
    var controller = NewController(payload);

    var result = await controller.HandleWebhook();

    result.Should().BeOfType<OkResult>();
  }
}
