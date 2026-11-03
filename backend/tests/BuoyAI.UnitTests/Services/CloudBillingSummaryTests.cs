using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>The billing screen's data and the Stripe portal on the hosted service.</summary>
public class CloudBillingSummaryTests
{
  private readonly BuoyAIDbContext _context = new(new DbContextOptionsBuilder<BuoyAIDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
  private readonly Mock<ICloudBillingClient> _client = new();
  private readonly Mock<IEntitlementService> _entitlements = new();

  private CloudBillingService Service(bool enabled = true)
  {
    _entitlements.Setup(e => e.GetProductAccessAsync(It.IsAny<int>())).ReturnsAsync(ProductAccess.None);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["App:BaseUrl"] = "https://buoy-ai.com/",
    }).Build();
    return new CloudBillingService(
      _context, _client.Object, Mock.Of<IReservationService>(), Mock.Of<IStripeService>(), Mock.Of<ISubscriptionSyncService>(),
      _entitlements.Object, config, Options.Create(new CloudBillingOptions { Enabled = enabled }),
      Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"), NullLogger<CloudBillingService>.Instance);
  }

  private async Task<User> UserAsync(string? customer = "cus_1")
  {
    var user = new User
    {
      Email = "pat@example.com", Username = "pat", PasswordHash = "x", FirstName = "P", LastName = "T",
      IsEmailVerified = true, StripeCustomerId = customer
    };
    _context.Users.Add(user);
    await _context.SaveChangesAsync();
    return user;
  }

  private static CloudSubscriptionState State() => new(
    "sub_1", "active", "yearly", true, null, false,
    [new CloudProductState("bank_sync", 1, ["bank_sync"], null, null)],
    [new CloudItemState("bank_sync", "bank_sync_yearly_founding", 1, 2399, "usd")], []);

  private static CloudUpcomingInvoice Upcoming() => new(
    "sub_1", "active", "yearly", true, false, new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc), "usd", 2399,
    [new CloudUpcomingLine("product", "bank_sync", "Bank sync", 1, 2399, 2399, false)]);

  [Fact]
  public async Task Summary_IsThePlanAndTheNextInvoice_ForTheUsersOwnCustomer()
  {
    var user = await UserAsync();
    _client.Setup(c => c.GetSubscriptionAsync("cus_1", "production", It.IsAny<CancellationToken>())).ReturnsAsync(State());
    _client.Setup(c => c.GetUpcomingAsync("cus_1", "production", It.IsAny<CancellationToken>())).ReturnsAsync(Upcoming());

    var summary = await Service().GetSummaryAsync(user.Id);

    summary.Subscription!.SubscriptionId.Should().Be("sub_1");
    summary.Upcoming!.TotalCents.Should().Be(2399);
    summary.LookupFailed.Should().BeFalse();
  }

  [Fact]
  public async Task Summary_WithNoStripeCustomer_IsEmpty_AndAsksNobody()
  {
    var user = await UserAsync(customer: null);

    var summary = await Service().GetSummaryAsync(user.Id);

    summary.Subscription.Should().BeNull();
    summary.Upcoming.Should().BeNull();
    summary.LookupFailed.Should().BeFalse();
    _client.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task Summary_WithNoCloudPlan_DoesNotAskForAnInvoice()
  {
    var user = await UserAsync();
    _client.Setup(c => c.GetSubscriptionAsync("cus_1", "production", It.IsAny<CancellationToken>()))
      .ReturnsAsync((CloudSubscriptionState?)null);

    var summary = await Service().GetSummaryAsync(user.Id);

    summary.Subscription.Should().BeNull();
    _client.Verify(c => c.GetUpcomingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task Summary_WhenOnlyTheInvoiceFails_KeepsThePlan_AndSaysLookupFailed()
  {
    var user = await UserAsync();
    _client.Setup(c => c.GetSubscriptionAsync("cus_1", "production", It.IsAny<CancellationToken>())).ReturnsAsync(State());
    _client.Setup(c => c.GetUpcomingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new CloudBillingException("timeout", "slow", 504));

    var summary = await Service().GetSummaryAsync(user.Id);

    summary.Subscription!.SubscriptionId.Should().Be("sub_1");
    summary.Upcoming.Should().BeNull();
    summary.LookupFailed.Should().BeTrue();
  }

  [Theory]
  [InlineData("unreachable", 503)]
  [InlineData("environment_mismatch", 409)]
  public async Task Summary_WhenBillingFails_SaysLookupFailed_InsteadOfThrowing(string code, int status)
  {
    var user = await UserAsync();
    _client.Setup(c => c.GetSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new CloudBillingException(code, "x", status));

    var summary = await Service().GetSummaryAsync(user.Id);

    summary.LookupFailed.Should().BeTrue();
    summary.Subscription.Should().BeNull();
  }

  [Fact]
  public async Task Portal_IsOpenedForTheUsersCustomer_AndReturnsToTheBillingScreen()
  {
    var user = await UserAsync();
    CloudPortalSessionRequest? sent = null;
    _client.Setup(c => c.CreatePortalSessionAsync(It.IsAny<CloudPortalSessionRequest>(), It.IsAny<CancellationToken>()))
      .Callback<CloudPortalSessionRequest, CancellationToken>((r, _) => sent = r)
      .ReturnsAsync(new CloudPortalSession("https://billing.stripe.com/p/abc"));

    var url = await Service().CreatePortalSessionAsync(user.Id);

    url.Should().Be("https://billing.stripe.com/p/abc");
    sent!.CustomerId.Should().Be("cus_1");
    sent.Environment.Should().Be("production");
    sent.ReturnUrl.Should().StartWith("https://buoy-ai.com/").And.EndWith("/settings/cloud/billing");
  }

  [Fact]
  public async Task Portal_FromTheCloudPortal_ReturnsToThePortalBilling()
  {
    var user = await UserAsync();
    CloudPortalSessionRequest? sent = null;
    _client.Setup(c => c.CreatePortalSessionAsync(It.IsAny<CloudPortalSessionRequest>(), It.IsAny<CancellationToken>()))
      .Callback<CloudPortalSessionRequest, CancellationToken>((r, _) => sent = r)
      .ReturnsAsync(new CloudPortalSession("https://billing.stripe.com/p/abc"));

    await Service().CreatePortalSessionAsync(user.Id, fromPortal: true);

    sent!.ReturnUrl.Should().Be("https://buoy-ai.com/cloud/billing");
  }

  [Fact]
  public async Task Portal_WithNoStripeCustomer_Is404_AndAsksNobody()
  {
    var user = await UserAsync(customer: null);

    var act = () => Service().CreatePortalSessionAsync(user.Id);

    (await act.Should().ThrowAsync<CloudBillingException>()).Which.Code.Should().Be("no_subscription");
    _client.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task WhenOff_NeitherCallLeavesTheServer()
  {
    var user = await UserAsync();
    var service = Service(enabled: false);

    foreach (var act in new Func<Task>[] { () => service.GetSummaryAsync(user.Id), () => service.CreatePortalSessionAsync(user.Id) })
    {
      (await act.Should().ThrowAsync<CloudBillingException>()).Which.Code.Should().Be("not_available");
    }
    _client.VerifyNoOtherCalls();
  }

  [Theory]
  [InlineData(UserSubscriptionStatus.Active, true)]
  [InlineData(UserSubscriptionStatus.PastDue, true)]
  [InlineData(UserSubscriptionStatus.Canceled, false)]
  public async Task Status_SaysWhetherAnyLivePlanWouldRefuseACheckout(UserSubscriptionStatus status, bool expected)
  {
    var user = await UserAsync();
    _context.UserSubscriptions.Add(new UserSubscription { UserId = user.Id, Status = status });
    await _context.SaveChangesAsync();
    _client.Setup(c => c.GetSubscriptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync((CloudSubscriptionState?)null);

    (await Service().GetStatusAsync(user.Id)).HasLivePlan.Should().Be(expected);
  }

  // ── the controller ──

  private static CloudBillingController Controller(Mock<ICloudBillingService> billing, int userId = 42)
  {
    var controller = new CloudBillingController(billing.Object);
    controller.ControllerContext = new ControllerContext
    {
      HttpContext = new DefaultHttpContext
      {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
      }
    };
    return controller;
  }

  [Fact]
  public async Task TheSummaryAndThePortal_AreForTheSignedInUser_AndReturnedDirectly()
  {
    var billing = new Mock<ICloudBillingService>();
    billing.Setup(b => b.GetSummaryAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new CloudBillingSummary(State(), Upcoming()));
    billing.Setup(b => b.CreatePortalSessionAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync("https://billing.stripe.com/p/abc");

    var summary = await Controller(billing, 7).GetSummary(CancellationToken.None);
    var portal = await Controller(billing, 7).OpenPortal(null, CancellationToken.None);

    summary.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<CloudBillingSummary>();
    portal.Should().BeOfType<OkObjectResult>().Which.Value!.GetType().GetProperty("url")!
      .GetValue(((OkObjectResult)portal).Value).Should().Be("https://billing.stripe.com/p/abc");
    billing.Verify(b => b.GetSummaryAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    billing.Verify(b => b.CreatePortalSessionAsync(7, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task ThePortalSession_FromTheCloudPortal_AsksForThePortalReturn()
  {
    var billing = new Mock<ICloudBillingService>();
    billing.Setup(b => b.CreatePortalSessionAsync(7, true, It.IsAny<CancellationToken>())).ReturnsAsync("https://billing.stripe.com/p/x");

    var portal = await Controller(billing, 7).OpenPortal("portal", CancellationToken.None);

    portal.Should().BeOfType<OkObjectResult>();
    billing.Verify(b => b.CreatePortalSessionAsync(7, true, It.IsAny<CancellationToken>()), Times.Once);
    billing.Verify(b => b.CreatePortalSessionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task AServiceRefusal_KeepsItsStatusAndCode()
  {
    var billing = new Mock<ICloudBillingService>();
    billing.Setup(b => b.CreatePortalSessionAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new CloudBillingException("no_subscription", "You have no Cloud subscription.", 404));

    var result = await Controller(billing).OpenPortal(null, CancellationToken.None);

    result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
  }
}

/// <summary>The new billing-service calls over HTTP.</summary>
public class CloudBillingUpcomingHttpClientTests
{
  private sealed class StubHandler : HttpMessageHandler
  {
    public HttpRequestMessage? Request { get; private set; }
    public string Body { get; private set; } = "";
    public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Request = request;
      Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
      return Respond();
    }
  }

  private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
  }

  private static (CloudBillingHttpClient Client, StubHandler Handler) Make()
  {
    var handler = new StubHandler();
    return (new CloudBillingHttpClient(new Factory(handler),
      Options.Create(new CloudBillingOptions { Enabled = true, BaseUrl = "http://cloud-billing:8080", ServiceKey = "secret-key" })), handler);
  }

  private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  [Fact]
  public async Task Upcoming_AsksForTheCustomerAndEnvironment_WithTheServiceKey()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.OK,
      "{\"subscriptionId\":\"sub_1\",\"status\":\"active\",\"interval\":\"monthly\",\"founding\":false,\"cancelAtPeriodEnd\":false," +
      "\"nextInvoiceAt\":null,\"currency\":\"usd\",\"totalCents\":429,\"lines\":[{\"kind\":\"product\",\"product\":\"bank_sync\"," +
      "\"description\":\"Bank sync\",\"quantity\":1,\"unitAmountCents\":429,\"amountCents\":429,\"proration\":false}]}");

    var invoice = await client.GetUpcomingAsync("cus 1", "production");

    handler.Request!.RequestUri!.PathAndQuery.Should().Be("/v1/subscription/upcoming?customerId=cus%201&environment=production");
    handler.Request.Headers.GetValues("X-Service-Key").Should().Equal("secret-key");
    invoice!.Lines.Should().ContainSingle().Which.Product.Should().Be("bank_sync");
  }

  [Fact]
  public async Task Upcoming_ForNoPlan_IsNull()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.NotFound, "{\"code\":\"no_subscription\",\"message\":\"No Cloud subscription.\"}");

    (await client.GetUpcomingAsync("cus_1", "production")).Should().BeNull();
  }

  [Fact]
  public async Task Portal_PostsTheCustomerAndReturnAddress()
  {
    var (client, handler) = Make();
    handler.Respond = () => Json(HttpStatusCode.OK, "{\"url\":\"https://billing.stripe.com/p/abc\"}");

    var session = await client.CreatePortalSessionAsync(new CloudPortalSessionRequest("cus_1", "production", "https://buoy-ai.com/back"));

    session.Url.Should().Be("https://billing.stripe.com/p/abc");
    handler.Request!.Method.Should().Be(HttpMethod.Post);
    handler.Request.RequestUri!.AbsolutePath.Should().Be("/v1/portal-session");
    handler.Body.Should().Contain("\"customerId\":\"cus_1\"").And.Contain("\"returnUrl\":\"https://buoy-ai.com/back\"");
  }
}
