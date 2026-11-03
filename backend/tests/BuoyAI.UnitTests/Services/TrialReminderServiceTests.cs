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
/// The connect-your-bank sweep re-evaluates the same population every pass, so its selection
/// rules are the only thing between "a two-email sequence" and "a mailing loop". These pin
/// them down for both steps.
/// </summary>
public class TrialReminderServiceTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Mock<IEmailService> _email = null!;
    private static Mock<IEmailPreferencesService> _prefs = null!;
    private static Mock<INotificationService> _notifications = null!;
    private static BuoyAIDbContext _context = null!;

    private static TrialReminderService CreateService(
        IEnumerable<User> users,
        bool optedIn = true,
        int daysAfterSignup = 1,
        int secondDaysAfterFirst = 3,
        int minDaysRemaining = 7,
        IEnumerable<Account>? accounts = null)
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new BuoyAIDbContext(options);
        _context.Users.AddRange(users);
        if (accounts != null) _context.Accounts.AddRange(accounts);
        _context.SaveChanges();

        _email = new Mock<IEmailService>();
        _prefs = new Mock<IEmailPreferencesService>();
        _prefs.Setup(p => p.ShouldSendEmailAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(optedIn);
        _notifications = new Mock<INotificationService>();

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(_email.Object);
        services.AddSingleton(_prefs.Object);
        services.AddSingleton(_notifications.Object);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:TrialReminder:Enabled"] = "true",
            ["Email:TrialReminder:DaysAfterSignup"] = daysAfterSignup.ToString(),
            ["Email:TrialReminder:SecondDaysAfterFirst"] = secondDaysAfterFirst.ToString(),
            ["Email:TrialReminder:MinDaysRemaining"] = minDaysRemaining.ToString(),
        }).Build();

        return new TrialReminderService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            config,
            new Mock<ILogger<TrialReminderService>>().Object);
    }

    private static User Trialing(int id, double startedDaysAgo, int endsInDays) => new()
    {
        Id = id,
        Email = $"user{id}@example.com",
        FirstName = "Test",
        PasswordHash = "hash",
        IsTrialActive = true,
        TrialStartDate = Now.AddDays(-startedDaysAgo),
        TrialEndDate = Now.AddDays(endsInDays),
    };

    /// <summary>A trial user who already got step 1 <paramref name="firstSentDaysAgo"/> days ago.</summary>
    private static User FirstSent(int id, double firstSentDaysAgo, int endsInDays = 20)
    {
        var user = Trialing(id, startedDaysAgo: firstSentDaysAgo + 1, endsInDays: endsInDays);
        user.TrialReminderSentAt = Now.AddDays(-firstSentDaysAgo);
        return user;
    }

    private static int SendCount(int? step = null) => _email.Invocations
        .Count(i => i.Method.Name == nameof(IEmailService.SendConnectReminderAsync)
                    && (step == null || (int)i.Arguments[2] == step));

    private static void FailSendsTo(string email) =>
        _email.Setup(e => e.SendConnectReminderAsync(email, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new Exception("bad address"));

    // ── step 1 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_SendsStepOneADayAfterSignup()
    {
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 1.5, endsInDays: 28) });

        await service.SweepAsync(CancellationToken.None);

        SendCount(step: 1).Should().Be(1);
        _context.Users.Single().TrialReminderSentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_IsIdempotent_SecondPassSendsNothing()
    {
        // The whole reason TrialReminderSentAt exists: without it every pass re-mails.
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 2, endsInDays: 28) });

        await service.SweepAsync(CancellationToken.None);
        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(1);
    }

    [Fact]
    public async Task Sweep_SkipsUsersWhoJustSignedUp()
    {
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 0.25, endsInDays: 30) });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
        _context.Users.Single().TrialReminderSentAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_SkipsExpiredTrials()
    {
        // Telling someone to get started after their trial ended is noise, and the
        // trial-ending email already owns that moment.
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 40, endsInDays: -1) });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
    }

    [Fact]
    public async Task Sweep_SkipsUsersNotOnATrial()
    {
        var notTrialing = Trialing(1, startedDaysAgo: 10, endsInDays: 50);
        notTrialing.IsTrialActive = false;

        var service = CreateService(new[] { notTrialing });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
    }

    [Fact]
    public async Task Sweep_RespectsAnOptOut_ButStillMarksTheUser()
    {
        // Marking matters: leaving it null would re-check an opted-out user on every pass
        // forever, and the preference lookup is a database round trip each time.
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 2, endsInDays: 28) }, optedIn: false);

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
        _context.Users.Single().TrialReminderSentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_LeavesAFailedSendUnmarkedSoItRetries()
    {
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 2, endsInDays: 28) });
        FailSendsTo("user1@example.com");

        await service.SweepAsync(CancellationToken.None);

        _context.Users.Single().TrialReminderSentAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_OneBadAddressDoesNotStopTheBatch()
    {
        var service = CreateService(new[]
        {
            Trialing(1, startedDaysAgo: 2, endsInDays: 28),
            Trialing(2, startedDaysAgo: 3, endsInDays: 27),
        });
        FailSendsTo("user1@example.com");

        await service.SweepAsync(CancellationToken.None);

        _context.Users.Single(u => u.Id == 1).TrialReminderSentAt.Should().BeNull();
        _context.Users.Single(u => u.Id == 2).TrialReminderSentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_ReportsTheStepAndTheTrialDaysLeft()
    {
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 2, endsInDays: 28) });

        await service.SweepAsync(CancellationToken.None);

        _email.Verify(e => e.SendConnectReminderAsync(
            "user1@example.com",
            "Test",
            1,
            It.Is<int>(left => left >= 27 && left <= 28)), Times.Once);
    }

    [Fact]
    public async Task Sweep_SkipsATrialAboutToExpire()
    {
        // Found in production: a dormant trial expiring tomorrow would still have received
        // "here's how to get started". The ENDING email owns that moment, not this one.
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 29, endsInDays: 1) });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
        _context.Users.Single().TrialReminderSentAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_SkipsUsersWhoHaveAlreadyConnectedAnAccount()
    {
        // This is an ACTIVATION nudge. Telling someone with a connected account to get
        // started reads as a system that does not know who they are.
        var service = CreateService(
            new[] { Trialing(1, startedDaysAgo: 2, endsInDays: 28) },
            accounts: new[] { new Account { Id = 1, UserId = 1, Name = "Checking" } });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
    }

    [Fact]
    public async Task Sweep_TargetsOnlyTheUnactivatedUser()
    {
        var service = CreateService(
            new[]
            {
                Trialing(1, startedDaysAgo: 2, endsInDays: 28), // never connected
                Trialing(2, startedDaysAgo: 2, endsInDays: 28), // already set up
            },
            accounts: new[] { new Account { Id = 1, UserId = 2, Name = "Checking" } });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(1);
        _email.Verify(e => e.SendConnectReminderAsync(
            "user1@example.com", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Sweep_StepOneAlsoLeavesAnInAppNotificationPointingAtAccounts()
    {
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 2, endsInDays: 28) });

        await service.SweepAsync(CancellationToken.None);

        _notifications.Verify(n => n.CreateNotificationAsync(1, It.Is<CreateNotificationRequest>(r =>
            r.Type == TrialReminderService.ConnectNudgeNotificationType && r.ActionUrl == "/accounts")), Times.Once);
    }

    [Fact]
    public async Task Sweep_AFailedNotificationDoesNotUndoTheEmail()
    {
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 2, endsInDays: 28) });
        _notifications.Setup(n => n.CreateNotificationAsync(It.IsAny<int>(), It.IsAny<CreateNotificationRequest>()))
            .ThrowsAsync(new Exception("push down"));

        await service.SweepAsync(CancellationToken.None);

        SendCount(step: 1).Should().Be(1);
        _context.Users.Single().TrialReminderSentAt.Should().NotBeNull();
    }

    // ── step 2 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_SendsStepTwoThreeDaysAfterStepOne()
    {
        var service = CreateService(new[] { FirstSent(1, firstSentDaysAgo: 3.5) });

        await service.SweepAsync(CancellationToken.None);

        SendCount(step: 2).Should().Be(1);
        SendCount(step: 1).Should().Be(0);
        _context.Users.Single().TrialReminder2SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_WaitsTheFullGapBeforeStepTwo()
    {
        var service = CreateService(new[] { FirstSent(1, firstSentDaysAgo: 2) });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
        _context.Users.Single().TrialReminder2SentAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_NeverSendsBothStepsInOnePass()
    {
        // A user old enough for step 1 gets step 1 now; step 2 is measured from that send,
        // not from signup, so it cannot follow in the same pass.
        var service = CreateService(new[] { Trialing(1, startedDaysAgo: 10, endsInDays: 20) });

        await service.SweepAsync(CancellationToken.None);

        SendCount(step: 1).Should().Be(1);
        SendCount(step: 2).Should().Be(0);
    }

    [Fact]
    public async Task Sweep_StepTwoIsSentOnce()
    {
        var service = CreateService(new[] { FirstSent(1, firstSentDaysAgo: 4) });

        await service.SweepAsync(CancellationToken.None);
        await service.SweepAsync(CancellationToken.None);

        SendCount(step: 2).Should().Be(1);
    }

    [Fact]
    public async Task Sweep_StepTwoStopsOnceABankIsConnected()
    {
        var service = CreateService(
            new[] { FirstSent(1, firstSentDaysAgo: 4) },
            accounts: new[] { new Account { Id = 1, UserId = 1, Name = "Checking" } });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
    }

    [Fact]
    public async Task Sweep_StepTwoSkipsATrialAboutToExpire()
    {
        var service = CreateService(new[] { FirstSent(1, firstSentDaysAgo: 4, endsInDays: 2) });

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
    }

    [Fact]
    public async Task Sweep_StepTwoHasNoInAppCopy()
    {
        // Step 2 is a personal note asking for a reply; a second push would just be nagging.
        var service = CreateService(new[] { FirstSent(1, firstSentDaysAgo: 4) });

        await service.SweepAsync(CancellationToken.None);

        _notifications.Verify(n => n.CreateNotificationAsync(It.IsAny<int>(), It.IsAny<CreateNotificationRequest>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_StepTwoRespectsAnOptOut_ButStillMarksTheUser()
    {
        var service = CreateService(new[] { FirstSent(1, firstSentDaysAgo: 4) }, optedIn: false);

        await service.SweepAsync(CancellationToken.None);

        SendCount().Should().Be(0);
        _context.Users.Single().TrialReminder2SentAt.Should().NotBeNull();
    }
}
