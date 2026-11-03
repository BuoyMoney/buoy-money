using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

public class ReservationServiceTests
{
  private static BuoyAIDbContext NewContext() =>
    new(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString())
      .Options);

  // No Stripe key on purpose: the service must build without one.
  private static ReservationService NewService(
    BuoyAIDbContext context, bool enabled = true, string environment = "Production") =>
    new(
      context,
      Options.Create(new ReservationOptions { Enabled = enabled }),
      new StripeReservationCheckoutClient(new ConfigurationBuilder().Build()),
      NullLogger<ReservationService>.Instance,
      Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(e => e.EnvironmentName == environment));

  private static CompletedReservationCheckout Checkout(
    string sessionId, string? paymentIntent = "pi_1", string choice = "ai") =>
    new(
      sessionId,
      "pat@example.com",
      "cus_1",
      paymentIntent,
      100,
      "usd",
      new Dictionary<string, string> { ["kind"] = "reservation", ["choice"] = choice, ["source"] = "founding_page" });

  [Fact]
  public async Task CreateCheckoutAsync_WhenDisabled_ThrowsDisabledException()
  {
    var service = NewService(NewContext(), enabled: false);

    var act = () => service.CreateCheckoutAsync("ai", "test");

    await act.Should().ThrowAsync<ReservationsDisabledException>();
  }

  [Theory]
  [InlineData("")]
  [InlineData("everything")]
  [InlineData(null)]
  public async Task CreateCheckoutAsync_UnknownChoice_ThrowsInvalidChoice(string? choice)
  {
    var service = NewService(NewContext());

    var act = () => service.CreateCheckoutAsync(choice, "test");

    await act.Should().ThrowAsync<InvalidReservationChoiceException>();
  }

  [Theory]
  [InlineData("bank_sync")]
  [InlineData("ai")]
  [InlineData("both")]
  [InlineData("free")]
  [InlineData("unsure")]
  public async Task CreateCheckoutAsync_KnownChoiceWithoutStripeKey_FailsOnlyAtCallTime(string choice)
  {
    var service = NewService(NewContext());

    var act = () => service.CreateCheckoutAsync(choice, "test");

    await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stripe*");
  }

  [Fact]
  public async Task RecordCompletedAsync_NewSession_StoresReservation()
  {
    using var context = NewContext();
    var service = NewService(context);

    await service.RecordCompletedAsync(Checkout("cs_1"));

    var row = await context.Reservations.SingleAsync();
    row.Email.Should().Be("pat@example.com");
    row.PlanChoice.Should().Be("ai");
    row.Source.Should().Be("founding_page");
    row.Status.Should().Be(ReservationStatus.Paid);
    row.AmountCents.Should().Be(100);
  }

  [Fact]
  public async Task RecordCompletedAsync_SameSessionTwice_StoresOneRow()
  {
    using var context = NewContext();
    var service = NewService(context);

    await service.RecordCompletedAsync(Checkout("cs_1"));
    await service.RecordCompletedAsync(Checkout("cs_1"));

    (await context.Reservations.CountAsync()).Should().Be(1);
  }

  [Fact]
  public async Task RecordCompletedAsync_UnknownChoiceInMetadata_FallsBackToUnsure()
  {
    using var context = NewContext();
    var service = NewService(context);

    await service.RecordCompletedAsync(Checkout("cs_1", choice: "nonsense"));

    (await context.Reservations.SingleAsync()).PlanChoice.Should().Be("unsure");
  }

  [Fact]
  public async Task MarkRefundedAsync_KnownPaymentIntent_MarksRefunded()
  {
    using var context = NewContext();
    var service = NewService(context);
    await service.RecordCompletedAsync(Checkout("cs_1", "pi_1"));

    var marked = await service.MarkRefundedAsync("pi_1");

    marked.Should().BeTrue();
    var row = await context.Reservations.SingleAsync();
    row.Status.Should().Be(ReservationStatus.Refunded);
    row.RefundedAt.Should().NotBeNull();
  }

  [Fact]
  public async Task MarkRefundedAsync_UnknownPaymentIntent_ReturnsFalse()
  {
    using var context = NewContext();
    var service = NewService(context);
    await service.RecordCompletedAsync(Checkout("cs_1", "pi_1"));

    var marked = await service.MarkRefundedAsync("pi_other");

    marked.Should().BeFalse();
    (await context.Reservations.SingleAsync()).Status.Should().Be(ReservationStatus.Paid);
  }

  [Fact]
  public async Task GetCountAsync_ExcludesRefunded()
  {
    using var context = NewContext();
    var service = NewService(context);
    await service.RecordCompletedAsync(Checkout("cs_1", "pi_1"));
    await service.RecordCompletedAsync(Checkout("cs_2", "pi_2"));
    await service.RecordCompletedAsync(Checkout("cs_3", "pi_3"));
    await service.MarkRefundedAsync("pi_2");

    (await service.GetCountAsync()).Should().Be(2);
  }

  [Fact]
  public async Task Record_SkipsSessionsCreatedByAnotherEnvironment()
  {
    var context = NewContext();
    var service = NewService(context, environment: "Production");
    var checkout = Checkout("cs_staging") with
    {
      Metadata = new Dictionary<string, string> { ["kind"] = "reservation", ["choice"] = "ai", ["environment"] = "staging" },
    };

    await service.RecordCompletedAsync(checkout);

    Assert.Equal(0, await context.Reservations.CountAsync());
  }

  [Fact]
  public async Task Record_StagingIgnoresUntaggedSessions()
  {
    var context = NewContext();
    var service = NewService(context, environment: "Staging");

    await service.RecordCompletedAsync(Checkout("cs_untagged"));

    Assert.Equal(0, await context.Reservations.CountAsync());
  }
}
