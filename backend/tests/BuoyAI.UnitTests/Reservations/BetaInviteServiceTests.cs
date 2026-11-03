using BuoyAI.API.Controllers;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

/// <summary>Closed-beta invites for founding members (task 24): once per email, paid only, dry run by default.</summary>
public class BetaInviteServiceTests
{
    private readonly BuoyAIDbContext _db = new(new DbContextOptionsBuilder<BuoyAIDbContext>()
        .UseInMemoryDatabase($"beta-{Guid.NewGuid()}").Options);
    private readonly Mock<IEmailService> _email = new();
    private int _session;

    private BetaInviteService Service(BetaOptions? options = null) => new(
        _db, _email.Object, Options.Create(options ?? new BetaOptions { Enabled = true, FeedbackAddress = "beta@example.test" }),
        NullLogger<BetaInviteService>.Instance);

    private void Add(string? email, string status = ReservationStatus.Paid, int day = 1, DateTime? invited = null)
    {
        _db.Reservations.Add(new Reservation
        {
            Email = email, StripeCheckoutSessionId = $"cs_{++_session}", AmountCents = 100, Status = status,
            CreatedAt = new DateTime(2026, 10, day), BetaInvitedAt = invited,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Preview_CountsPaidUninvitedEmailsOnce()
    {
        Add("pat@example.com", day: 1);
        Add("PAT@example.com", day: 2);                                        // same person, second reservation
        Add("sam@example.com", ReservationStatus.Refunded, day: 3);            // refunded: not a founding member
        Add("lee@example.com", day: 4, invited: new DateTime(2026, 10, 10));   // already invited
        Add(null);

        var preview = await Service().PreviewAsync();

        preview.Eligible.Should().Be(1);
        preview.AlreadyInvited.Should().Be(1);
        preview.TextBody.Should().Contain("https://buoy-ai.com/docs/selfhost/install").And.Contain("$1 reservation comes off your first payment");
        preview.TextBody.Should().NotContain("—").And.NotContain("60-day");
    }

    [Fact]
    public async Task ADryRun_SendsNothing_AndMarksNothing()
    {
        Add("pat@example.com");

        var result = await Service().SendAsync(10, dryRun: true);

        result.Should().Be(new BetaInviteResult(true, 0, 1, 1));
        _email.VerifyNoOtherCalls();
        (await _db.Reservations.AnyAsync(r => r.BetaInvitedAt != null)).Should().BeFalse();
    }

    [Fact]
    public async Task ASend_MailsEachPersonOnce_EarliestFirst_AndMarksEveryRow()
    {
        Add("late@example.com", day: 9);
        Add("pat@example.com", day: 1);
        Add("Pat@Example.com", day: 5);

        var first = await Service().SendAsync(1, dryRun: false);
        var second = await Service().SendAsync(10, dryRun: false);
        var third = await Service().SendAsync(10, dryRun: false);

        first.Should().Be(new BetaInviteResult(false, 1, 1, 1));
        second.Sent.Should().Be(1);
        third.Sent.Should().Be(0);
        _email.Verify(e => e.SendRawEmailAsync("pat@example.com", BetaInviteService.Subject, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _email.Verify(e => e.SendRawEmailAsync("late@example.com", BetaInviteService.Subject, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        (await _db.Reservations.CountAsync(r => r.BetaInvitedAt == null)).Should().Be(0);
    }

    [Fact]
    public async Task OneSend_IsCappedByTheSetting()
    {
        for (var i = 1; i <= 5; i++) Add($"p{i}@example.com", day: i);

        var result = await Service(new BetaOptions { Enabled = true, MaxPerSend = 2 }).SendAsync(100, dryRun: false);

        result.Sent.Should().Be(2);
        result.Remaining.Should().Be(3);
    }

    [Fact]
    public async Task AFailedEmail_IsNotRetried_SoNobodyGetsTwo()
    {
        Add("pat@example.com");
        _email.Setup(e => e.SendRawEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("mail down"));

        var result = await Service().SendAsync(10, dryRun: false);
        var again = await Service().SendAsync(10, dryRun: false);

        result.Sent.Should().Be(0);
        again.WouldSend.Should().Be(0);
    }

    [Fact]
    public async Task WithoutAFeedbackAddress_TheEmailLeavesOutTheReplyLines()
    {
        var body = (await Service(new BetaOptions { Enabled = true }).PreviewAsync()).TextBody;

        body.Should().NotContain("write to").And.Contain("You are getting this because you reserved a founding spot.");
    }

    // ------------------------------------------------------------------ the admin endpoint

    private static BetaInvitesController Controller(IBetaInviteService invites, bool mfa = true)
    {
        var twoFactor = new Mock<ITwoFactorService>();
        twoFactor.Setup(t => t.CanAccessPlaidFeaturesAsync(It.IsAny<int>())).ReturnsAsync(mfa);
        var controller = new BetaInvitesController(invites, twoFactor.Object);
        var http = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1")], "test")),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    [Fact]
    public async Task Endpoint_IsOffUnlessEnabled()
    {
        var controller = Controller(Service(new BetaOptions { Enabled = false }));

        (await controller.Preview(default)).Should().BeOfType<NotFoundResult>();
        (await controller.Send(new BetaInvitesController.SendBody(5, false), default)).Should().BeOfType<NotFoundResult>();
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Endpoint_NeedsStrongMfaToSend()
    {
        Add("pat@example.com");

        var result = await Controller(Service(), mfa: false).Send(new BetaInvitesController.SendBody(5, false), default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Endpoint_DefaultsToADryRun()
    {
        Add("pat@example.com");

        var result = await Controller(Service()).Send(null, default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<BetaInviteResult>().Which.DryRun.Should().BeTrue();
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Endpoint_NeedsStrongMfaToPreview()
    {
        Add("pat@example.com");

        var result = await Controller(Service(), mfa: false).Preview(default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task TwoConcurrentSends_MailEachPersonOnce()
    {
        var name = $"beta-race-{Guid.NewGuid()}";
        BuoyAIDbContext NewDb() => new(new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(name).Options);
        BetaInviteService NewService() => new(NewDb(), _email.Object,
            Options.Create(new BetaOptions { Enabled = true }), NullLogger<BetaInviteService>.Instance);
        await using (var seed = NewDb())
        {
            for (var i = 1; i <= 5; i++)
            {
                seed.Reservations.Add(new Reservation
                {
                    Email = $"p{i}@example.com", StripeCheckoutSessionId = $"cs_r{i}", AmountCents = 100,
                    Status = ReservationStatus.Paid, CreatedAt = new DateTime(2026, 10, i),
                });
            }
            await seed.SaveChangesAsync();
        }
        _email.Setup(e => e.SendRawEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async () => await Task.Delay(20));

        var results = await Task.WhenAll(NewService().SendAsync(10, false), NewService().SendAsync(10, false));

        results.Sum(r => r.Sent).Should().Be(5);
        _email.Verify(e => e.SendRawEmailAsync(It.IsAny<string>(), BetaInviteService.Subject, It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(5));
    }
}
