using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.UnitTests.Controllers;

public class CloudBillingControllerTests
{
  private readonly Mock<ICloudBillingService> _billing = new();

  private CloudBillingController Controller(int userId = 42, string? idempotencyKey = null)
  {
    var controller = new CloudBillingController(_billing.Object);
    var http = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
    };
    if (idempotencyKey != null) http.Request.Headers["Idempotency-Key"] = idempotencyKey;
    controller.ControllerContext = new ControllerContext { HttpContext = http };
    return controller;
  }

  private static CloudSubscriptionState State() => new("sub_1", "active", "yearly", false, null, false, [], [], []);

  // ── who is asking ──

  [Fact]
  public void TheWholeController_RequiresSignIn_AndIsHostedOnly()
  {
    typeof(CloudBillingController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
    typeof(CloudBillingController).GetCustomAttribute<BuoyAI.API.Filters.HostedOnlyAttribute>().Should().NotBeNull();
    typeof(CloudBillingController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
      .Should().NotContain(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null);
  }

  [Fact]
  public void ItInheritsTheBaseController() =>
    typeof(CloudBillingController).BaseType.Should().Be(typeof(BaseController));

  [Fact]
  public async Task TheUser_IsAlwaysTheSignedInOne()
  {
    _billing.Setup(b => b.GetStatusAsync(7, It.IsAny<CancellationToken>()))
      .ReturnsAsync(new CloudBillingStatus(true, false, null, new CloudProductAccess(false, false, false, 0, new Dictionary<string, DateTime>(), false)));

    var result = await Controller(userId: 7).GetStatus(CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>();
    _billing.Verify(b => b.GetStatusAsync(7, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public void NoRequestBody_HasAPlaceToNameACustomerOrUser()
  {
    var fields = new[] { typeof(CloudCheckoutBody), typeof(CloudChangeBody) }
      .SelectMany(t => t.GetProperties()).Select(p => p.Name.ToLowerInvariant()).ToList();
    fields.Should().NotContain(new[] { "customerid", "userid", "environment", "founding", "stripecustomerid" });
  }

  // ── checkout ──

  [Fact]
  public async Task Checkout_ReturnsTheUrl_DirectlyNotWrapped()
  {
    _billing.Setup(b => b.StartCheckoutAsync(42, "yearly", It.IsAny<IReadOnlyList<CloudProductSelection>>(), "web", "k-1", It.IsAny<CancellationToken>()))
      .ReturnsAsync("https://checkout.stripe.com/x");

    var result = await Controller(idempotencyKey: "k-1").StartCheckout(
      new CloudCheckoutBody { Interval = "yearly", Platform = "web", Products = [new("bank_sync")] }, CancellationToken.None);

    var ok = result.Should().BeOfType<OkObjectResult>().Subject;
    ok.Value!.GetType().GetProperty("url")!.GetValue(ok.Value).Should().Be("https://checkout.stripe.com/x");
  }

  [Theory]
  [InlineData(null)]
  [InlineData("daily")]
  [InlineData("")]
  [InlineData("Yearly")]
  public async Task Checkout_RefusesABadInterval(string? interval)
  {
    var result = await Controller().StartCheckout(
      new CloudCheckoutBody { Interval = interval!, Products = [new("ai")] }, CancellationToken.None);
    result.Should().BeOfType<BadRequestObjectResult>();
    _billing.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task Checkout_RefusesNoProducts_AndTooManyProducts()
  {
    var controller = Controller();
    (await controller.StartCheckout(new CloudCheckoutBody { Interval = "yearly", Products = null }, CancellationToken.None))
      .Should().BeOfType<BadRequestObjectResult>();
    (await controller.StartCheckout(new CloudCheckoutBody { Interval = "yearly", Products = [] }, CancellationToken.None))
      .Should().BeOfType<BadRequestObjectResult>();
    (await controller.StartCheckout(new CloudCheckoutBody
    {
      Interval = "yearly", Products = Enumerable.Range(0, 11).Select(i => new CloudProductSelection("p" + i)).ToList()
    }, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    _billing.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task AnOverlongIdempotencyKey_IsDropped_NotForwardedToStripe()
  {
    _billing.Setup(b => b.StartCheckoutAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<CloudProductSelection>>(),
        It.IsAny<string?>(), null, It.IsAny<CancellationToken>())).ReturnsAsync("https://x");

    await Controller(idempotencyKey: new string('k', 201)).StartCheckout(
      new CloudCheckoutBody { Interval = "yearly", Products = [new("ai")] }, CancellationToken.None);

    _billing.Verify(b => b.StartCheckoutAsync(42, "yearly", It.IsAny<IReadOnlyList<CloudProductSelection>>(), null, null, It.IsAny<CancellationToken>()), Times.Once);
  }

  // ── changes ──

  [Theory]
  [InlineData("add")]
  [InlineData("set_quantity")]
  [InlineData("remove")]
  [InlineData("undo_remove")]
  [InlineData("cancel")]
  [InlineData("undo_cancel")]
  public async Task EveryKnownChange_IsForwarded(string type)
  {
    _billing.Setup(b => b.ApplyAsync(42, type, "ai", 2, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(State());

    var result = await Controller().Apply(new CloudChangeBody { Type = type, Product = "ai", Quantity = 2 }, CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<CloudSubscriptionState>();
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("delete_everything")]
  [InlineData("ADD")]
  public async Task AnUnknownChange_IsRefused_BeforeAnythingIsCalled(string? type)
  {
    var controller = Controller();
    (await controller.Apply(new CloudChangeBody { Type = type }, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    (await controller.Preview(new CloudChangeBody { Type = type }, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    _billing.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task ThePreviewsProrationDate_IsForwarded()
  {
    var when = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
    _billing.Setup(b => b.PreviewAsync(42, "add", "ai", null, when, It.IsAny<CancellationToken>()))
      .ReturnsAsync(new CloudPreview("add", "now", null, 0, 0, "usd", when, [], []));

    var result = await Controller().Preview(new CloudChangeBody { Type = "add", Product = "ai", ProrationDate = when }, CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>();
  }

  // ── refusals ──

  [Theory]
  [InlineData("already_owned", 409)]
  [InlineData("payment_required", 402)]
  [InlineData("invalid_selection", 400)]
  [InlineData("not_available", 404)]
  [InlineData("unreachable", 503)]
  public async Task ARefusal_KeepsItsStatusAndStableCode(string code, int status)
  {
    _billing.Setup(b => b.ApplyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(),
        It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new CloudBillingException(code, "message", status));

    var result = await Controller().Apply(new CloudChangeBody { Type = "add", Product = "ai" }, CancellationToken.None);

    var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
    objectResult.StatusCode.Should().Be(status);
    objectResult.Value!.GetType().GetProperty("code")!.GetValue(objectResult.Value).Should().Be(code);
  }

  [Fact]
  public async Task AnUnexpectedFailure_IsNotSwallowed()
  {
    _billing.Setup(b => b.GetCatalogAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
    var act = () => Controller().GetCatalog(CancellationToken.None);
    await act.Should().ThrowAsync<InvalidOperationException>();
  }
}
