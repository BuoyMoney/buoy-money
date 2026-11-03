using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class BankRequestServiceTests
{
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly BankRequestService _service;

    public BankRequestServiceTests()
    {
        _users.Setup(u => u.GetByIdAsync(7)).ReturnsAsync(new User
        {
            Id = 7, Email = "sam@example.com", FirstName = "Sam", PasswordHash = "x",
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resend:ReplyToEmail"] = "support@buoy-ai.com",
        }).Build();
        _service = new BankRequestService(
            _users.Object, _email.Object, new MemoryCache(new MemoryCacheOptions()), config,
            new Mock<ILogger<BankRequestService>>().Object);
    }

    private IInvocation? LastEmail() => _email.Invocations
        .LastOrDefault(i => i.Method.Name == nameof(IEmailService.SendRawEmailAsync));

    [Fact]
    public async Task SubmitAsync_ValidBank_EmailsSupportWithTheBankAndWhoAsked()
    {
        var ok = await _service.SubmitAsync(7, "Azura Credit Union", "android");

        ok.Should().BeTrue();
        var mail = LastEmail();
        mail.Should().NotBeNull();
        mail!.Arguments[0].Should().Be("support@buoy-ai.com");
        ((string)mail.Arguments[1]).Should().Contain("Azura Credit Union");
        ((string)mail.Arguments[3]).Should().Contain("sam@example.com").And.Contain("android");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SubmitAsync_EmptyBank_IsDroppedWithoutEmail(string? bank)
    {
        var ok = await _service.SubmitAsync(7, bank, "web");

        ok.Should().BeFalse();
        LastEmail().Should().BeNull();
    }

    [Fact]
    public async Task SubmitAsync_StopsEmailingAfterTheDailyLimit()
    {
        for (var i = 0; i < BankRequestService.DailyLimit; i++)
        {
            (await _service.SubmitAsync(7, $"Bank {i}", "web")).Should().BeTrue();
        }

        var extra = await _service.SubmitAsync(7, "One more", "web");

        extra.Should().BeFalse();
        _email.Invocations.Count(i => i.Method.Name == nameof(IEmailService.SendRawEmailAsync))
            .Should().Be(BankRequestService.DailyLimit);
    }

    [Fact]
    public async Task SubmitAsync_TheLimitIsPerUser()
    {
        _users.Setup(u => u.GetByIdAsync(8)).ReturnsAsync(new User { Id = 8, Email = "b@example.com", PasswordHash = "x" });
        for (var i = 0; i < BankRequestService.DailyLimit; i++)
        {
            await _service.SubmitAsync(7, $"Bank {i}", "web");
        }

        (await _service.SubmitAsync(8, "Other user's bank", "web")).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitAsync_FlattensAndCapsTheName_SoTheSubjectStaysOneLine()
    {
        var pasted = "First Line\r\nBcc: someone@example.com\n" + new string('x', 500);

        await _service.SubmitAsync(7, pasted, "web");

        var subject = (string)LastEmail()!.Arguments[1];
        subject.Should().NotContain("\n").And.NotContain("\r");
        subject.Length.Should().BeLessThanOrEqualTo("Bank request: ".Length + BankRequestService.MaxLength);
    }

    [Fact]
    public async Task SubmitAsync_EscapesTheNameInTheHtmlBody()
    {
        await _service.SubmitAsync(7, "<script>x</script>", "web");

        var html = (string)LastEmail()!.Arguments[2];
        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task SubmitAsync_AFailedEmailStillCountsAsSubmitted()
    {
        _email.Setup(e => e.SendRawEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("resend down"));

        var ok = await _service.SubmitAsync(7, "Some Bank", "ios");

        ok.Should().BeTrue();
    }
}
