using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class MarketingLeadServiceTests
{
  private readonly BuoyAIDbContext _context;
  private readonly Mock<IEmailService> _emailService = new();
  private readonly MarketingLeadService _service;

  public MarketingLeadServiceTests()
  {
    _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    _service = new MarketingLeadService(
      _context,
      _emailService.Object,
      BuildConfig(standard: 14, extended: 30),
      Mock.Of<ILogger<MarketingLeadService>>());
  }

  private static IConfiguration BuildConfig(int standard, int extended) =>
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["Subscription:TrialPeriodDays"] = standard.ToString(),
      ["Subscription:ExtendedTrialPeriodDays"] = extended.ToString()
    }).Build();

  private static CaptureLeadRequest Req(string email, string? source = "popup_timed") =>
    new() { Email = email, Source = source };

  [Fact]
  public async Task Capture_stores_the_lead_and_returns_the_extended_offer()
  {
    var result = await _service.CaptureAsync(Req("Visitor@Example.com "));

    result.TrialDays.Should().Be(30);
    result.StandardTrialDays.Should().Be(14);

    var lead = await _context.MarketingLeads.SingleAsync();
    // Normalized on the way in, so the registration lookup can match exactly.
    lead.Email.Should().Be("visitor@example.com");
    lead.OfferedTrialDays.Should().Be(30);
    lead.RedeemedAt.Should().BeNull();
  }

  [Fact]
  public async Task Capture_is_idempotent_and_never_stacks_days()
  {
    await _service.CaptureAsync(Req("a@example.com"));
    var second = await _service.CaptureAsync(Req("A@EXAMPLE.COM"));

    second.TrialDays.Should().Be(30);
    var lead = await _context.MarketingLeads.SingleAsync();
    lead.CaptureCount.Should().Be(2);
    lead.OfferedTrialDays.Should().Be(30);
  }

  [Fact]
  public async Task Capture_does_not_resend_the_confirmation_on_a_repeat_submit()
  {
    await _service.CaptureAsync(Req("a@example.com"));
    await _service.CaptureAsync(Req("a@example.com"));

    _emailService.Verify(
      x => x.SendExtendedTrialOfferAsync(
        It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CalculatorSnapshotDto?>(), It.IsAny<string>()),
      Times.Once);
  }

  [Fact]
  public async Task Capture_survives_a_failing_email_and_leaves_the_row_retryable()
  {
    _emailService
      .Setup(x => x.SendExtendedTrialOfferAsync(
        It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CalculatorSnapshotDto?>(), It.IsAny<string>()))
      .ThrowsAsync(new InvalidOperationException("resend is down"));

    var result = await _service.CaptureAsync(Req("a@example.com"));

    result.TrialDays.Should().Be(30);
    var lead = await _context.MarketingLeads.SingleAsync();
    lead.ConfirmationSentAt.Should().BeNull("a send that never landed must stay retryable");
  }

  [Fact]
  public async Task Capture_rejects_an_unrecognised_source_rather_than_storing_it()
  {
    await _service.CaptureAsync(Req("a@example.com", source: "<script>whatever</script>"));

    (await _context.MarketingLeads.SingleAsync()).Source.Should().BeNull();
  }

  [Fact]
  public async Task Capture_round_trips_the_calculator_snapshot_into_the_email()
  {
    var snapshot = new CalculatorSnapshotDto
    {
      MonthlyBudget = 300m,
      Days = 30,
      SkipsPerWeek = 2,
      DailyAllowance = 10m,
      Banked = 80m,
      Yearly = 960m
    };

    await _service.CaptureAsync(new CaptureLeadRequest
    {
      Email = "a@example.com",
      Source = "calculator_exit",
      Calculator = snapshot
    });

    _emailService.Verify(x => x.SendExtendedTrialOfferAsync(
      "a@example.com", 30, 14,
      It.Is<CalculatorSnapshotDto>(s => s.MonthlyBudget == 300m && s.Yearly == 960m),
      It.IsAny<string>()),
      Times.Once);
  }

  [Fact]
  public async Task Unredeemed_offer_is_found_regardless_of_how_the_email_was_typed()
  {
    await _service.CaptureAsync(Req("a@example.com"));

    (await _service.GetUnredeemedOfferDaysAsync("  A@Example.COM ")).Should().Be(30);
  }

  [Fact]
  public async Task Redeeming_consumes_the_offer_exactly_once()
  {
    await _service.CaptureAsync(Req("a@example.com"));

    await _service.MarkRedeemedAsync("a@example.com", userId: 7);

    (await _service.GetUnredeemedOfferDaysAsync("a@example.com"))
      .Should().BeNull("a redeemed offer must not be honoured a second time");

    var lead = await _context.MarketingLeads.SingleAsync();
    lead.RedeemedByUserId.Should().Be(7);

    // A second redemption is a harmless no-op, not a crash.
    await _service.MarkRedeemedAsync("a@example.com", userId: 9);
    (await _context.MarketingLeads.SingleAsync()).RedeemedByUserId.Should().Be(7);
  }

  [Fact]
  public async Task Unknown_email_has_no_offer()
  {
    (await _service.GetUnredeemedOfferDaysAsync("nobody@example.com")).Should().BeNull();
    (await _service.GetUnredeemedOfferDaysAsync("")).Should().BeNull();
  }

  [Fact]
  public void A_misconfigured_extended_offer_never_promises_less_than_the_standard_one()
  {
    var service = new MarketingLeadService(
      _context,
      _emailService.Object,
      BuildConfig(standard: 14, extended: 7),
      Mock.Of<ILogger<MarketingLeadService>>());

    service.GetOfferDays().Extended.Should().Be(14);
  }

  // ── Existing accounts must never be promised a trial they cannot claim ──

  [Fact]
  public async Task Capture_from_an_existing_account_records_no_redeemable_offer()
  {
    _context.Users.Add(new User
    {
      Email = "member@example.com",
      Username = "member",
      PasswordHash = "x",
      FirstName = "M",
      LastName = "Ember"
    });
    await _context.SaveChangesAsync();

    var result = await _service.CaptureAsync(Req("Member@Example.com"));

    // The HTTP response is identical to a brand-new address on purpose: a public endpoint
    // that answered differently would confirm whether an address has an account.
    result.TrialDays.Should().Be(30);

    // ...but nothing redeemable was stored, so registration can never honour it.
    (await _context.MarketingLeads.SingleAsync()).OfferedTrialDays.Should().Be(0);
    (await _service.GetUnredeemedOfferDaysAsync("member@example.com")).Should().BeNull();
  }

  [Fact]
  public async Task Capture_from_an_existing_account_sends_the_sign_in_email_not_the_offer()
  {
    _context.Users.Add(new User
    {
      Email = "member@example.com",
      Username = "member",
      PasswordHash = "x",
      FirstName = "M",
      LastName = "Ember"
    });
    await _context.SaveChangesAsync();

    await _service.CaptureAsync(Req("member@example.com"));

    _emailService.Verify(x => x.SendExistingAccountOfferAsync(
      "member@example.com", It.IsAny<CalculatorSnapshotDto?>(), It.IsAny<string>()), Times.Once);
    _emailService.Verify(x => x.SendExtendedTrialOfferAsync(
      It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CalculatorSnapshotDto?>(),
      It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task Capture_from_an_email_that_already_used_its_lifetime_trial_offers_nothing()
  {
    // No account (it was deleted), but TrialHistory outlives deletion — which is exactly the
    // delete-and-re-register route the popup must not reopen.
    _context.TrialHistories.Add(new TrialHistory
    {
      EmailHash = TrialEmailHasher.Compute("churned@example.com")!,
      FirstTrialStartedAt = DateTime.UtcNow.AddMonths(-6),
      LastTrialGrantedAt = DateTime.UtcNow.AddMonths(-6),
      TrialGrantCount = 1
    });
    await _context.SaveChangesAsync();

    await _service.CaptureAsync(Req("churned@example.com"));

    (await _service.GetUnredeemedOfferDaysAsync("churned@example.com")).Should().BeNull();
  }

  [Fact]
  public async Task An_offer_is_retired_if_the_address_registers_before_resubmitting()
  {
    await _service.CaptureAsync(Req("later@example.com"));
    (await _service.GetUnredeemedOfferDaysAsync("later@example.com")).Should().Be(30);

    _context.Users.Add(new User
    {
      Email = "later@example.com",
      Username = "later",
      PasswordHash = "x",
      FirstName = "L",
      LastName = "Ater"
    });
    await _context.SaveChangesAsync();

    await _service.CaptureAsync(Req("later@example.com"));

    (await _service.GetUnredeemedOfferDaysAsync("later@example.com")).Should().BeNull();
  }

  // ── Unsubscribe ──

  [Fact]
  public async Task Unsubscribing_is_idempotent_and_suppresses_further_sends()
  {
    await _service.CaptureAsync(Req("a@example.com"));
    var token = (await _context.MarketingLeads.SingleAsync()).UnsubscribeToken;
    token.Should().NotBeNullOrWhiteSpace();

    (await _service.UnsubscribeAsync(token)).Should().BeTrue();
    var firstAt = (await _context.MarketingLeads.SingleAsync()).UnsubscribedAt;
    firstAt.Should().NotBeNull();

    // Clicking the link again is still a success, and does not move the timestamp.
    (await _service.UnsubscribeAsync(token)).Should().BeTrue();
    (await _context.MarketingLeads.SingleAsync()).UnsubscribedAt.Should().Be(firstAt);
  }

  [Fact]
  public async Task An_unsubscribed_lead_is_not_emailed_again_even_if_they_resubmit()
  {
    // Simulate a first capture whose email never landed, so the retry path is live.
    _emailService
      .Setup(x => x.SendExtendedTrialOfferAsync(
        It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CalculatorSnapshotDto?>(),
        It.IsAny<string>()))
      .ThrowsAsync(new InvalidOperationException("resend is down"));
    await _service.CaptureAsync(Req("a@example.com"));

    var lead = await _context.MarketingLeads.SingleAsync();
    await _service.UnsubscribeAsync(lead.UnsubscribeToken);
    _emailService.Invocations.Clear();

    await _service.CaptureAsync(Req("a@example.com"));

    _emailService.Invocations.Should().BeEmpty("an opt-out is not undone by resubmitting the form");
  }

  [Fact]
  public async Task Unsubscribing_with_an_unknown_token_reports_not_found_without_throwing()
  {
    (await _service.UnsubscribeAsync("nope")).Should().BeFalse();
    (await _service.UnsubscribeAsync("")).Should().BeFalse();
  }

  [Fact]
  public async Task The_response_is_byte_identical_across_every_eligibility_state()
  {
    // Regression: the repeat-submit path used to echo the stored row, so a redeemed or
    // retired offer answered 14 where a fresh address answered 30. That difference is an
    // account-enumeration oracle on a public, unauthenticated endpoint.
    async Task<(int Trial, int Standard)> Capture(string email)
    {
      var r = await _service.CaptureAsync(Req(email));
      return (r.TrialDays, r.StandardTrialDays);
    }

    var brandNew = await Capture("new@example.com");

    // Same address again.
    var resubmit = await Capture("new@example.com");

    // An address that already has an account.
    _context.Users.Add(new User
    {
      Email = "known@example.com",
      Username = "known",
      PasswordHash = "x",
      FirstName = "K",
      LastName = "Nown"
    });
    await _context.SaveChangesAsync();
    var registered = await Capture("known@example.com");
    var registeredAgain = await Capture("known@example.com");

    // An address whose offer was already redeemed.
    await _service.MarkRedeemedAsync("new@example.com", userId: 3);
    var redeemed = await Capture("new@example.com");

    resubmit.Should().Be(brandNew);
    registered.Should().Be(brandNew);
    registeredAgain.Should().Be(brandNew);
    redeemed.Should().Be(brandNew);
  }
}
