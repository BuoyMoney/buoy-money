using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Interfaces;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

public class ReservationsControllerTests
{
  private readonly Mock<IReservationService> _service = new();
  private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

  private ReservationsController NewController() => new(_service.Object, _cache);

  [Fact]
  public async Task Checkout_WhenDisabled_ReturnsNotFound()
  {
    _service.Setup(s => s.CreateCheckoutAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new ReservationsDisabledException());

    var result = await NewController().Checkout(new ReservationCheckoutRequest { Choice = "ai" }, default);

    result.Should().BeOfType<NotFoundResult>();
  }

  [Fact]
  public async Task Checkout_BadChoice_ReturnsBadRequest()
  {
    _service.Setup(s => s.CreateCheckoutAsync("nope", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidReservationChoiceException("nope"));

    var result = await NewController().Checkout(new ReservationCheckoutRequest { Choice = "nope" }, default);

    result.Should().BeOfType<BadRequestObjectResult>();
  }

  [Fact]
  public async Task Checkout_Success_ReturnsUrlDirectly()
  {
    _service.Setup(s => s.CreateCheckoutAsync("both", "founding_page", It.IsAny<CancellationToken>()))
      .ReturnsAsync("https://checkout.stripe.com/c/pay/cs_test_1");

    var result = await NewController().Checkout(
      new ReservationCheckoutRequest { Choice = "both", Source = "founding_page" }, default);

    var ok = result.Should().BeOfType<OkObjectResult>().Subject;
    ok.Value.Should().BeOfType<ReservationCheckoutResponse>()
      .Which.Url.Should().Be("https://checkout.stripe.com/c/pay/cs_test_1");
  }

  [Fact]
  public async Task Stats_WhenDisabled_ReportsDisabledWithoutQueryingCount()
  {
    _service.Setup(s => s.IsEnabled).Returns(false);

    var result = await NewController().Stats(default);

    var stats = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ReservationStatsResponse>().Subject;
    stats.Enabled.Should().BeFalse();
    _service.Verify(s => s.GetCountAsync(It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task Stats_WhenEnabled_CachesCount()
  {
    _service.Setup(s => s.IsEnabled).Returns(true);
    _service.Setup(s => s.GetCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(7);
    var controller = NewController();

    await controller.Stats(default);
    var result = await controller.Stats(default);

    var stats = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ReservationStatsResponse>().Subject;
    stats.Count.Should().Be(7);
    stats.Enabled.Should().BeTrue();
    _service.Verify(s => s.GetCountAsync(It.IsAny<CancellationToken>()), Times.Once);
  }
}
