using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe.Checkout;
using BuoyAI.Core.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

public class ReservationCheckoutOptionsTests
{
  private static (ReservationService Service, Mock<IReservationCheckoutClient> Client, ReservationOptions Options)
    Build(string environment = "Production")
  {
    var context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    var options = new ReservationOptions
    {
      Enabled = true,
      AmountCents = 100,
      Currency = "usd",
      ProductName = "Founding member reservation",
      SuccessUrl = "https://buoy-ai.com/founding/thanks",
      CancelUrl = "https://buoy-ai.com/founding",
    };
    var client = new Mock<IReservationCheckoutClient>();
    var service = new ReservationService(
      context,
      Options.Create(options),
      client.Object,
      NullLogger<ReservationService>.Instance,
      Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(e => e.EnvironmentName == environment));
    return (service, client, options);
  }

  private static async Task<(SessionCreateOptions Captured, string Url, ReservationOptions Options)> Create(
    string choice = "ai", string? source = "founding_page", string environment = "Production")
  {
    var (service, client, options) = Build(environment);
    SessionCreateOptions? captured = null;
    client.Setup(c => c.CreateSessionAsync(It.IsAny<SessionCreateOptions>(), It.IsAny<CancellationToken>()))
      .Callback<SessionCreateOptions, CancellationToken>((o, _) => captured = o)
      .ReturnsAsync("https://checkout.stripe.test/cs_1");

    var url = await service.CreateCheckoutAsync(choice, source);

    captured.Should().NotBeNull();
    return (captured!, url, options);
  }

  [Fact]
  public async Task CreateCheckoutAsync_ReturnsTheUrlFromTheClient()
  {
    var (_, url, _) = await Create();

    url.Should().Be("https://checkout.stripe.test/cs_1");
  }

  [Fact]
  public async Task CreateCheckoutAsync_IsACardOnlyPaymentThatAlwaysCreatesACustomer()
  {
    var (o, _, _) = await Create();

    o.Mode.Should().Be("payment");
    o.PaymentMethodTypes.Should().Equal("card");
    o.CustomerCreation.Should().Be("always");
  }

  [Fact]
  public async Task CreateCheckoutAsync_HasOneLineItemWithPriceDataFromOptions()
  {
    var (o, _, options) = await Create();

    var item = o.LineItems.Should().ContainSingle().Subject;
    item.Quantity.Should().Be(1);
    item.PriceData.Currency.Should().Be(options.Currency);
    item.PriceData.UnitAmount.Should().Be(options.AmountCents);
    item.PriceData.ProductData.Name.Should().Be(options.ProductName);
  }

  [Fact]
  public async Task CreateCheckoutAsync_SavesTheCardOffSessionAndTagsThePaymentIntent()
  {
    var (o, _, _) = await Create(environment: "Staging");

    o.PaymentIntentData.SetupFutureUsage.Should().Be("off_session");
    o.PaymentIntentData.Metadata["kind"].Should().Be("reservation");
    o.PaymentIntentData.Metadata[BuoyAI.Core.Utilities.StripeEnvironmentPolicy.MetadataKey]
      .Should().Be(BuoyAI.Core.Utilities.StripeEnvironmentPolicy.Tag("Staging"));
  }

  [Fact]
  public async Task CreateCheckoutAsync_TagsTheSessionWithKindChoiceSourceAndEnvironment()
  {
    var (o, _, _) = await Create(choice: "both", source: "  founding_page  ", environment: "Production");

    o.Metadata["kind"].Should().Be("reservation");
    o.Metadata["choice"].Should().Be("both");
    o.Metadata["source"].Should().Be("founding_page");
    o.Metadata[BuoyAI.Core.Utilities.StripeEnvironmentPolicy.MetadataKey]
      .Should().Be(BuoyAI.Core.Utilities.StripeEnvironmentPolicy.Tag("Production"));
  }

  [Fact]
  public async Task CreateCheckoutAsync_MissingSource_FallsBackToDirect()
  {
    var (o, _, _) = await Create(source: null);

    o.Metadata["source"].Should().Be("direct");
  }

  [Fact]
  public async Task CreateCheckoutAsync_UsesConfiguredReturnUrlsAndSubmitText()
  {
    var (o, _, options) = await Create();

    o.SuccessUrl.Should().Be(options.SuccessUrl);
    o.CancelUrl.Should().Be(options.CancelUrl);
    o.CustomText.Submit.Message.Should().Contain("refundable any time");
  }

  [Fact]
  public async Task IsReservationPaymentAsync_UsesClientMetadataAndEnvironment()
  {
    var (service, client, _) = Build("Production");
    var key = BuoyAI.Core.Utilities.StripeEnvironmentPolicy.MetadataKey;
    var prod = BuoyAI.Core.Utilities.StripeEnvironmentPolicy.Tag("Production");
    var staging = BuoyAI.Core.Utilities.StripeEnvironmentPolicy.Tag("Staging");
    client.Setup(c => c.GetPaymentIntentMetadataAsync("pi_ok", It.IsAny<CancellationToken>()))
      .ReturnsAsync(new Dictionary<string, string> { ["kind"] = "reservation", [key] = prod });
    client.Setup(c => c.GetPaymentIntentMetadataAsync("pi_other_env", It.IsAny<CancellationToken>()))
      .ReturnsAsync(new Dictionary<string, string> { ["kind"] = "reservation", [key] = staging });
    client.Setup(c => c.GetPaymentIntentMetadataAsync("pi_plan", It.IsAny<CancellationToken>()))
      .ReturnsAsync(new Dictionary<string, string> { ["kind"] = "subscription" });

    (await service.IsReservationPaymentAsync("pi_ok")).Should().BeTrue();
    (await service.IsReservationPaymentAsync("pi_other_env")).Should().BeFalse();
    (await service.IsReservationPaymentAsync("pi_plan")).Should().BeFalse();
    (await service.IsReservationPaymentAsync("pi_unknown")).Should().BeFalse();
  }
}
