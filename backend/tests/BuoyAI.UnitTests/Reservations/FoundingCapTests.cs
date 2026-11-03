using Moq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

public class FoundingCapTests
{
  private static BuoyAIDbContext NewContext() =>
    new(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .Options);

  private static ReservationService NewService(BuoyAIDbContext context, int spotLimit = 150, bool enabled = true)
  {
    var checkout = new Mock<IReservationCheckoutClient>();
    checkout.Setup(c => c.CreateSessionAsync(It.IsAny<Stripe.Checkout.SessionCreateOptions>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync("https://checkout.stripe.com/c/pay/cs_test");
    return new ReservationService(
      context,
      Options.Create(new ReservationOptions { Enabled = enabled }),
      checkout.Object,
      NullLogger<ReservationService>.Instance,
      Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(e => e.EnvironmentName == "Production"),
      Options.Create(new FoundingOptions { SpotLimit = spotLimit }));
  }

  private static async Task Seed(BuoyAIDbContext ctx, int paid, int refunded = 0)
  {
    for (var i = 0; i < paid + refunded; i++)
    {
      ctx.Reservations.Add(new Reservation
      {
        Email = $"p{i}@example.com",
        StripeCheckoutSessionId = $"cs_{i}",
        Status = i < paid ? ReservationStatus.Paid : ReservationStatus.Refunded,
      });
    }
    await ctx.SaveChangesAsync();
  }

  [Theory]
  [InlineData(0, 150)]
  [InlineData(-5, 150)]
  [InlineData(150, 150)]
  [InlineData(200, 200)]
  public void EffectiveLimit_ZeroOrMissingMeansDefault(int configured, int expected) =>
    new FoundingOptions { SpotLimit = configured }.EffectiveLimit.Should().Be(expected);

  [Fact]
  public async Task Status_CountsPaidOnly_RefundedDoNotCount()
  {
    using var ctx = NewContext();
    await Seed(ctx, paid: 10, refunded: 5);

    var status = await NewService(ctx).GetFoundingStatusAsync();

    status.Should().Be(new FoundingStatus(150, 10, 140, true));
  }

  [Fact]
  public async Task Checkout_At149Taken_IsAccepted()
  {
    using var ctx = NewContext();
    await Seed(ctx, paid: 149);

    var url = await NewService(ctx).CreateCheckoutAsync("ai", "test");

    url.Should().StartWith("https://checkout.stripe.com");
  }

  [Fact]
  public async Task Checkout_At150Taken_IsRefused()
  {
    using var ctx = NewContext();
    await Seed(ctx, paid: 150);

    var act = () => NewService(ctx).CreateCheckoutAsync("ai", "test");

    (await act.Should().ThrowAsync<FoundingSpotsTakenException>()).Which.Limit.Should().Be(150);
  }

  [Fact]
  public async Task Checkout_RefundedReservationsFreeTheirSpot()
  {
    using var ctx = NewContext();
    await Seed(ctx, paid: 149, refunded: 20);

    var url = await NewService(ctx).CreateCheckoutAsync("ai", "test");

    url.Should().NotBeNullOrEmpty();
  }

  [Fact]
  public async Task Status_PastTheLimit_RemainingNeverNegative_AndClosed()
  {
    using var ctx = NewContext();
    await Seed(ctx, paid: 152);

    var status = await NewService(ctx).GetFoundingStatusAsync();

    status.Taken.Should().Be(152);
    status.Remaining.Should().Be(0);
    status.Open.Should().BeFalse();
  }

  [Fact]
  public async Task Status_WhenReservationsDisabled_IsNotOpen()
  {
    using var ctx = NewContext();

    var status = await NewService(ctx, enabled: false).GetFoundingStatusAsync();

    status.Open.Should().BeFalse();
    status.Remaining.Should().Be(150);
  }

  [Fact]
  public async Task Status_ZeroConfiguredLimit_UsesDefault150()
  {
    using var ctx = NewContext();

    var status = await NewService(ctx, spotLimit: 0).GetFoundingStatusAsync();

    status.Limit.Should().Be(150);
  }

  [Fact]
  public async Task PaidReservationPastTheLimit_IsStillFounding()
  {
    using var ctx = NewContext();
    await Seed(ctx, paid: 151);

    (await NewService(ctx).HasPaidReservationAsync("p150@example.com")).Should().BeTrue();
  }

  [Fact]
  public async Task ReservationsController_FullCap_ReturnsConflict()
  {
    var service = new Mock<IReservationService>();
    service.Setup(s => s.CreateCheckoutAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new FoundingSpotsTakenException(150));
    var controller = new ReservationsController(service.Object, new MemoryCache(new MemoryCacheOptions()));

    var result = await controller.Checkout(new ReservationCheckoutRequest { Choice = "ai" }, default);

    var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
    conflict.Value!.ToString().Should().Contain("All 150 founding spots are taken.");
  }

  [Fact]
  public async Task FoundingController_Status_ReturnsNumbersAndCaches()
  {
    var service = new Mock<IReservationService>();
    service.Setup(s => s.GetFoundingStatusAsync(It.IsAny<CancellationToken>()))
      .ReturnsAsync(new FoundingStatus(150, 42, 108, true));
    var controller = new FoundingController(service.Object, new MemoryCache(new MemoryCacheOptions()));

    await controller.Status(default);
    var result = await controller.Status(default);

    var body = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<FoundingStatusResponse>().Subject;
    (body.Limit, body.Taken, body.Remaining, body.Open).Should().Be((150, 42, 108, true));
    service.Verify(s => s.GetFoundingStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
  }
}
