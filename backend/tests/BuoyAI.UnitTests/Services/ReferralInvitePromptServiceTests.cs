using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Who gets asked to invite a friend.
///
/// Targeting is the whole design. Asking someone to recommend a product they never used is
/// worse than silence: it advertises that we are not paying attention, and it burns the one
/// invite prompt they were ever going to read.
/// </summary>
public class ReferralInvitePromptServiceTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Mock<IEmailService> _email = null!;
    private static Mock<INotificationService> _notifications = null!;
    private static BuoyAIDbContext _context = null!;

    private static ReferralInvitePromptService CreateService(
        IEnumerable<User> users,
        IEnumerable<Account>? accounts = null,
        int daysSinceSignup = 21)
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new BuoyAIDbContext(options);
        _context.Users.AddRange(users);
        if (accounts != null) _context.Accounts.AddRange(accounts);
        _context.SaveChanges();

        _email = new Mock<IEmailService>();
        _notifications = new Mock<INotificationService>();

        var referral = new Mock<IReferralService>();
        referral.Setup(r => r.GetSummaryAsync(It.IsAny<int>()))
            .ReturnsAsync(new ReferralSummaryDto { Code = "ABCD2345", Link = "https://x/app/?ref=ABCD2345" });

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(_email.Object);
        services.AddSingleton(_notifications.Object);
        services.AddSingleton(referral.Object);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Referral:InvitePrompt:Enabled"] = "true",
            ["Referral:InvitePrompt:DaysSinceSignup"] = daysSinceSignup.ToString(),
        }).Build();

        return new ReferralInvitePromptService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            config,
            new Mock<ILogger<ReferralInvitePromptService>>().Object);
    }

    private static User Member(int id, int joinedDaysAgo) => new()
    {
        Id = id,
        Email = $"user{id}@example.com",
        FirstName = "Test",
        PasswordHash = "hash",
        CreatedAt = Now.AddDays(-joinedDaysAgo),
    };

    private static Account Connected(int id, int userId) =>
        new() { Id = id, UserId = userId, Name = "Checking" };

    private static int EmailCount() => _email.Invocations
        .Count(i => i.Method.Name == nameof(IEmailService.SendReferralInviteAsync));

    [Fact]
    public async Task Sweep_AsksAnEstablishedActivatedUser()
    {
        var service = CreateService(new[] { Member(1, joinedDaysAgo: 40) }, new[] { Connected(1, 1) });

        await service.SweepAsync(CancellationToken.None);

        EmailCount().Should().Be(1);
        _notifications.Verify(
            n => n.CreateNotificationAsync(1, It.IsAny<CreateNotificationRequest>()), Times.Once);
        _context.Users.Single().ReferralInvitePromptedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_NeverAsksSomeoneWhoNeverConnectedAnAccount()
    {
        // The important one. They have no experience to recommend, and asking reads as a
        // system that has not noticed they never got started.
        var service = CreateService(new[] { Member(1, joinedDaysAgo: 40) });

        await service.SweepAsync(CancellationToken.None);

        EmailCount().Should().Be(0);
        _context.Users.Single().ReferralInvitePromptedAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_DoesNotAskDuringOnboarding()
    {
        // Asking for a recommendation of something they have not seen yet.
        var service = CreateService(
            new[] { Member(1, joinedDaysAgo: 3) }, new[] { Connected(1, 1) }, daysSinceSignup: 21);

        await service.SweepAsync(CancellationToken.None);

        EmailCount().Should().Be(0);
    }

    [Fact]
    public async Task Sweep_AsksOnlyOnce()
    {
        var service = CreateService(new[] { Member(1, joinedDaysAgo: 40) }, new[] { Connected(1, 1) });

        await service.SweepAsync(CancellationToken.None);
        await service.SweepAsync(CancellationToken.None);

        EmailCount().Should().Be(1);
    }

    [Fact]
    public async Task Sweep_SkipsDeletedAccounts()
    {
        var deleted = Member(1, joinedDaysAgo: 40);
        deleted.IsDeleted = true;

        var service = CreateService(new[] { deleted }, new[] { Connected(1, 1) });

        await service.SweepAsync(CancellationToken.None);

        EmailCount().Should().Be(0);
    }

    [Fact]
    public async Task Sweep_CarriesTheUsersOwnCode()
    {
        var service = CreateService(new[] { Member(1, joinedDaysAgo: 40) }, new[] { Connected(1, 1) });

        await service.SweepAsync(CancellationToken.None);

        _email.Verify(e => e.SendReferralInviteAsync(
            "user1@example.com", "Test", "ABCD2345", "https://x/app/?ref=ABCD2345"), Times.Once);
    }

    [Fact]
    public async Task Sweep_OneFailureDoesNotStopTheBatch()
    {
        var service = CreateService(
            new[] { Member(1, joinedDaysAgo: 40), Member(2, joinedDaysAgo: 40) },
            new[] { Connected(1, 1), Connected(2, 2) });
        _email.Setup(e => e.SendReferralInviteAsync(
                "user1@example.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("bad address"));

        await service.SweepAsync(CancellationToken.None);

        _context.Users.Single(u => u.Id == 1).ReferralInvitePromptedAt.Should().BeNull();
        _context.Users.Single(u => u.Id == 2).ReferralInvitePromptedAt.Should().NotBeNull();
    }
}
