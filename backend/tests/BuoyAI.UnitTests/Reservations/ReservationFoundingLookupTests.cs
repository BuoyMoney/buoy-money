using Moq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

/// <summary>A paid, not refunded reservation under an email is what qualifies a customer for founding prices.</summary>
public class ReservationFoundingLookupTests
{
  private readonly BuoyAIDbContext _context = new(new DbContextOptionsBuilder<BuoyAIDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

  private ReservationService Service() => new(
    _context,
    Options.Create(new ReservationOptions { Enabled = true }),
    new StripeReservationCheckoutClient(new ConfigurationBuilder().Build()),
    NullLogger<ReservationService>.Instance,
    Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(e => e.EnvironmentName == "Production"));

  private async Task AddAsync(string? email, string status = ReservationStatus.Paid, string session = "cs_1")
  {
    _context.Reservations.Add(new Reservation
    {
      Email = email, StripeCheckoutSessionId = session, AmountCents = 100, Status = status
    });
    await _context.SaveChangesAsync();
  }

  [Fact]
  public async Task APaidReservation_Qualifies()
  {
    await AddAsync("pat@example.com");
    (await Service().HasPaidReservationAsync("pat@example.com")).Should().BeTrue();
  }

  [Theory]
  [InlineData("PAT@Example.com")]
  [InlineData("  pat@example.com ")]
  public async Task TheMatch_IgnoresCaseAndPadding(string asked)
  {
    await AddAsync("Pat@example.com");
    (await Service().HasPaidReservationAsync(asked)).Should().BeTrue();
  }

  [Fact]
  public async Task ARefundedReservation_DoesNotQualify()
  {
    await AddAsync("pat@example.com", ReservationStatus.Refunded);
    (await Service().HasPaidReservationAsync("pat@example.com")).Should().BeFalse();
  }

  [Fact]
  public async Task ARefundedOne_DoesNotHideAPaidOne()
  {
    await AddAsync("pat@example.com", ReservationStatus.Refunded, "cs_1");
    await AddAsync("pat@example.com", ReservationStatus.Paid, "cs_2");
    (await Service().HasPaidReservationAsync("pat@example.com")).Should().BeTrue();
  }

  [Fact]
  public async Task AnotherPersonsReservation_DoesNotQualify()
  {
    await AddAsync("sam@example.com");
    (await Service().HasPaidReservationAsync("pat@example.com")).Should().BeFalse();
  }

  [Fact]
  public async Task ASimilarEmail_IsNotAMatch()
  {
    await AddAsync("pat@example.com");
    (await Service().HasPaidReservationAsync("pat@example.co")).Should().BeFalse();
    (await Service().HasPaidReservationAsync("at@example.com")).Should().BeFalse();
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public async Task NoEmail_NeverQualifies(string? email)
  {
    await AddAsync(null);
    (await Service().HasPaidReservationAsync(email)).Should().BeFalse();
  }

  // ---- the credit against the first Cloud payment (task 22)

  [Fact]
  public async Task TheCredit_IsTheEarliestPaidReservation()
  {
    _context.Reservations.Add(new Reservation { Email = "pat@example.com", StripeCheckoutSessionId = "cs_old", AmountCents = 100, Status = ReservationStatus.Paid, CreatedAt = new DateTime(2026, 10, 1) });
    _context.Reservations.Add(new Reservation { Email = "pat@example.com", StripeCheckoutSessionId = "cs_new", AmountCents = 500, Status = ReservationStatus.Paid, CreatedAt = new DateTime(2026, 10, 9) });
    await _context.SaveChangesAsync();

    (await Service().GetPaidReservationCreditCentsAsync(" PAT@example.com ")).Should().Be(100);
  }

  [Fact]
  public async Task ARefundedReservation_GivesNoCredit()
  {
    await AddAsync("pat@example.com", ReservationStatus.Refunded);
    (await Service().GetPaidReservationCreditCentsAsync("pat@example.com")).Should().Be(0);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("sam@example.com")]
  public async Task NoReservation_GivesNoCredit(string? email)
  {
    await AddAsync("pat@example.com");
    (await Service().GetPaidReservationCreditCentsAsync(email)).Should().Be(0);
  }
}
