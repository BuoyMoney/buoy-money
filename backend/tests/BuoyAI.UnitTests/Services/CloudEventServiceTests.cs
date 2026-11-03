using System.Security.Cryptography;
using System.Text;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Cloud;
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

namespace BuoyAI.UnitTests.Services;

public class CloudEventServiceTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-test";

    private readonly BuoyAIDbContext _db;
    private readonly Mock<INotificationService> _notifications = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<IEmailPreferencesService> _prefs = new();
    private readonly CloudEventService _service;

    public CloudEventServiceTests()
    {
        _db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase($"cloud-events-{Guid.NewGuid()}").Options);
        _db.Users.Add(new User { Id = 7, Username = "kat", Email = "kat@home.test", FirstName = "Kat", StripeCustomerId = "cus_kat" });
        _db.Users.Add(new User { Id = 8, Username = "gone", Email = "gone@home.test", FirstName = "Gone", StripeCustomerId = "cus_gone", IsDeleted = true });
        _db.SaveChanges();
        _prefs.Setup(p => p.ShouldSendEmailAsync(It.IsAny<int>(), It.IsAny<string>())).ReturnsAsync(true);
        _service = new CloudEventService(_db, _notifications.Object, _email.Object, _prefs.Object, NullLogger<CloudEventService>.Instance);
    }

    [Fact]
    public async Task AiAlertAt80_NotifiesTheCustomerWhoHoldsTheStripeCustomerId()
    {
        var outcome = await _service.HandleAsync(new CloudEvent
        {
            Type = "ai_allowance", AccountId = "cus_kat", Level = 80, Percent = 80,
            UsedMicros = 4_000_000, AllowanceMicros = 5_000_000, PeriodEnd = new DateTime(2026, 11, 1),
        }, default);

        outcome.Should().Be(CloudEventOutcome.Delivered);
        _notifications.Verify(n => n.CreateNotificationAsync(7, It.Is<CreateNotificationRequest>(r =>
            r.Type == "cloud_ai_allowance" && r.Message.Contains("$4.00") && r.Message.Contains("$5.00") && r.ActionUrl == null)), Times.Once);
        _prefs.Verify(p => p.ShouldSendEmailAsync(7, "budget_warning"), Times.Once);
        _email.Verify(e => e.SendRawEmailAsync("kat@home.test", "You have used 80% of your AI allowance", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData(true, "keeps working")]
    [InlineData(false, "paused")]
    public async Task AiAlertAt100_SaysWhetherAiKeepsWorking(bool payg, string expected)
    {
        await _service.HandleAsync(new CloudEvent
        {
            Type = "ai_allowance", AccountId = "cus_kat", Level = 100, UsedMicros = 5_000_000, AllowanceMicros = 5_000_000, PaygEnabled = payg,
        }, default);

        _notifications.Verify(n => n.CreateNotificationAsync(7, It.Is<CreateNotificationRequest>(r => r.Message.Contains(expected))), Times.Once);
        _prefs.Verify(p => p.ShouldSendEmailAsync(7, "budget_exceeded"), Times.Once);
    }

    [Fact]
    public async Task EmailPreferenceOff_StillSendsTheInAppNotice_ButNoEmail()
    {
        _prefs.Setup(p => p.ShouldSendEmailAsync(7, "budget_warning")).ReturnsAsync(false);

        await _service.HandleAsync(new CloudEvent { Type = "ai_allowance", AccountId = "cus_kat", Level = 80 }, default);

        _notifications.Verify(n => n.CreateNotificationAsync(7, It.IsAny<CreateNotificationRequest>()), Times.Once);
        _email.Verify(e => e.SendRawEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false, "A new server joined")]
    [InlineData(true, "paired again")]
    public async Task ServerPaired_IsASecurityEmail(bool replaced, string title)
    {
        await _service.HandleAsync(new CloudEvent
        {
            Type = "server_paired", AccountId = "cus_kat", ServerId = Guid.NewGuid(), ServerName = "<b>attic</b>", ReplacedKeys = replaced,
        }, default);

        _prefs.Verify(p => p.ShouldSendEmailAsync(7, "security_alert"), Times.Once);
        _notifications.Verify(n => n.CreateNotificationAsync(7, It.Is<CreateNotificationRequest>(r => r.Title.Contains(title))), Times.Once);
        // The server name is chosen by whoever paired it, so it is escaped in the HTML part.
        _email.Verify(e => e.SendRawEmailAsync("kat@home.test", It.IsAny<string>(),
            It.Is<string>(html => html.Contains("&lt;b&gt;attic&lt;/b&gt;") && !html.Contains("<b>attic")), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task BankNeedsAttention_UsesTheConnectionIssuePreferences()
    {
        await _service.HandleAsync(new CloudEvent { Type = "bank_needs_attention", AccountId = "cus_kat", ItemId = Guid.NewGuid(), Reason = "ITEM_LOGIN_REQUIRED" }, default);

        _notifications.Verify(n => n.CreateNotificationAsync(7, It.Is<CreateNotificationRequest>(r =>
            r.Type == "plaid_update_required" && r.Message.Contains("sign in to your bank again"))), Times.Once);
        _prefs.Verify(p => p.ShouldSendEmailAsync(7, "plaid_update_required"), Times.Once);
    }

    [Theory]
    [InlineData("cus_nobody")]
    [InlineData("cus_gone")]
    public async Task UnknownOrDeletedAccount_TellsNobody(string accountId)
    {
        var outcome = await _service.HandleAsync(new CloudEvent { Type = "ai_allowance", AccountId = accountId, Level = 80 }, default);

        outcome.Should().Be(CloudEventOutcome.UnknownAccount);
        _notifications.VerifyNoOtherCalls();
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TwoUsersWithOneCustomerId_TellsNobody()
    {
        _db.Users.Add(new User { Id = 9, Username = "dup", Email = "dup@home.test", StripeCustomerId = "cus_kat" });
        _db.SaveChanges();

        var outcome = await _service.HandleAsync(new CloudEvent { Type = "ai_allowance", AccountId = "cus_kat", Level = 80 }, default);

        outcome.Should().Be(CloudEventOutcome.UnknownAccount);
        _notifications.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownTypeOrBadLevel_IsIgnored()
    {
        (await _service.HandleAsync(new CloudEvent { Type = "mystery", AccountId = "cus_kat" }, default)).Should().Be(CloudEventOutcome.UnknownType);
        (await _service.HandleAsync(new CloudEvent { Type = "ai_allowance", AccountId = "cus_kat", Level = 50 }, default)).Should().Be(CloudEventOutcome.Invalid);
        (await _service.HandleAsync(new CloudEvent { Type = "ai_allowance", AccountId = "" }, default)).Should().Be(CloudEventOutcome.Invalid);
        _notifications.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EmailFailure_DoesNotFailTheEvent()
    {
        _email.Setup(e => e.SendRawEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("mail down"));

        var outcome = await _service.HandleAsync(new CloudEvent { Type = "ai_allowance", AccountId = "cus_kat", Level = 80 }, default);

        outcome.Should().Be(CloudEventOutcome.Delivered);
    }

    // ------------------------------------------------------------------ the receiver

    private static string Sign(string body, string secret = Secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static (CloudEventsController Controller, Mock<ICloudEventService> Events) Receiver(string body, string? signature, string secret = Secret)
    {
        var events = new Mock<ICloudEventService>();
        events.Setup(e => e.HandleAsync(It.IsAny<CloudEvent>(), It.IsAny<CancellationToken>())).ReturnsAsync(CloudEventOutcome.Delivered);
        var controller = new CloudEventsController(events.Object, Options.Create(new CloudEventsOptions { WebhookSecret = secret }),
            NullLogger<CloudEventsController>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (signature != null) http.Request.Headers[CloudEventsController.SignatureHeader] = signature;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, events);
    }

    [Fact]
    public async Task Receiver_AcceptsTheGatewaysSignedAlert()
    {
        // The exact shape the AI gateway's WebhookAlertSink posts (camelCase AllowanceAlert).
        const string body = "{\"type\":\"ai_allowance\",\"level\":80,\"accountId\":\"cus_kat\",\"percent\":80,\"usedMicros\":4000000,\"allowanceMicros\":5000000,\"paygEnabled\":false,\"periodEnd\":\"2026-11-01T00:00:00Z\"}";
        var (controller, events) = Receiver(body, Sign(body));

        var result = await controller.Receive(default);

        result.Should().BeOfType<OkObjectResult>();
        events.Verify(e => e.HandleAsync(It.Is<CloudEvent>(c =>
            c.Type == "ai_allowance" && c.AccountId == "cus_kat" && c.Level == 80 && c.AllowanceMicros == 5_000_000 && c.PaygEnabled == false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=00")]
    [InlineData("md5=abc")]
    public async Task Receiver_RefusesAMissingOrWrongSignature(string? signature)
    {
        const string body = "{\"type\":\"ai_allowance\",\"accountId\":\"cus_kat\",\"level\":80}";
        var (controller, events) = Receiver(body, signature);

        (await controller.Receive(default)).Should().BeOfType<UnauthorizedResult>();
        events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Receiver_RefusesABodyChangedAfterSigning()
    {
        const string body = "{\"type\":\"ai_allowance\",\"accountId\":\"cus_kat\",\"level\":80}";
        var (controller, events) = Receiver(body.Replace("cus_kat", "cus_eve"), Sign(body));

        (await controller.Receive(default)).Should().BeOfType<UnauthorizedResult>();
        events.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short-secret")]
    public async Task Receiver_IsOffWithoutALongEnoughSecret(string secret)
    {
        const string body = "{}";
        var (controller, events) = Receiver(body, Sign(body, secret), secret);

        (await controller.Receive(default)).Should().BeOfType<NotFoundResult>();
        events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Receiver_RefusesBadJson()
    {
        const string body = "not json";
        var (controller, _) = Receiver(body, Sign(body));

        (await controller.Receive(default)).Should().BeOfType<BadRequestObjectResult>();
    }
}
