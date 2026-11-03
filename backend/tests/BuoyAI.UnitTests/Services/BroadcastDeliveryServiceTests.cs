using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Regression tests for BroadcastDeliveryService's transient-failure handling: a rate-limited
/// (429) recipient must stay Pending (retryable) instead of being permanently marked Failed,
/// while a genuine permanent error still marks the recipient Failed.
/// </summary>
public class BroadcastDeliveryServiceTests
{
    private static BuoyAIDbContext NewContext() =>
        new(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options);

    private static BuoyAI.Infrastructure.Services.BroadcastDeliveryService NewService(
        BuoyAIDbContext ctx, IEmailService email, BroadcastSettings settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ctx);          // singleton so the service's scope resolves the seeded instance
        services.AddSingleton(email);
        var provider = services.BuildServiceProvider();

        return new BuoyAI.Infrastructure.Services.BroadcastDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            NullLogger<BuoyAI.Infrastructure.Services.BroadcastDeliveryService>.Instance,
            new BuoyAI.Infrastructure.Telemetry.BuoyAiMetrics(new TestMeterFactory()));
    }

    private static async Task<bool> InvokeProcessNextAsync(BuoyAI.Infrastructure.Services.BroadcastDeliveryService svc)
    {
        var m = typeof(BuoyAI.Infrastructure.Services.BroadcastDeliveryService)
            .GetMethod("ProcessNextAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<bool>)m.Invoke(svc, new object[] { CancellationToken.None })!;
    }

    private static async Task<Guid> SeedBroadcastWithOneRecipientAsync(BuoyAIDbContext ctx)
    {
        var id = Guid.NewGuid();
        ctx.Broadcasts.Add(new Broadcast
        {
            Id = id,
            Subject = "Heads up",
            BodyHtml = "<p>news</p>",
            BodyMarkdown = "news",
            Status = BroadcastStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.BroadcastRecipients.Add(new BroadcastRecipient
        {
            BroadcastId = id,
            UserId = 1,
            Email = "a@x.com",
            Status = BroadcastRecipientStatus.Pending,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private static BroadcastSettings FastSettings() =>
        new() { BatchSize = 10, RateLimitBackoffMs = 0, DelayBetweenBatchesMs = 0 };

    [Fact]
    public async Task ProcessNext_RateLimited_LeavesRecipientPendingAndNotFailed()
    {
        using var ctx = NewContext();
        await SeedBroadcastWithOneRecipientAsync(ctx);

        var email = new Mock<IEmailService>();
        email.Setup(e => e.SendRawEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("Resend responded 429 Too Many Requests"));

        var svc = NewService(ctx, email.Object, FastSettings());

        await InvokeProcessNextAsync(svc);

        var recipient = await ctx.BroadcastRecipients.AsNoTracking().FirstAsync();
        recipient.Status.Should().Be(BroadcastRecipientStatus.Pending,
            "a 429 is transient and only Pending rows are ever retried");

        var broadcast = await ctx.Broadcasts.AsNoTracking().FirstAsync();
        broadcast.FailedCount.Should().Be(0);
        broadcast.SentCount.Should().Be(0);
        // Still Sending (not Completed/Failed) so the outer loop resumes it later.
        broadcast.Status.Should().Be(BroadcastStatus.Sending);
    }

    [Fact]
    public async Task ProcessNext_PermanentError_MarksRecipientFailed()
    {
        using var ctx = NewContext();
        await SeedBroadcastWithOneRecipientAsync(ctx);

        var email = new Mock<IEmailService>();
        email.Setup(e => e.SendRawEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("550 mailbox does not exist"));

        var svc = NewService(ctx, email.Object, FastSettings());

        await InvokeProcessNextAsync(svc);

        var recipient = await ctx.BroadcastRecipients.AsNoTracking().FirstAsync();
        recipient.Status.Should().Be(BroadcastRecipientStatus.Failed,
            "a non-rate-limit error is permanent and should not be retried forever");

        var broadcast = await ctx.Broadcasts.AsNoTracking().FirstAsync();
        broadcast.FailedCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessNext_Success_MarksRecipientSent()
    {
        using var ctx = NewContext();
        await SeedBroadcastWithOneRecipientAsync(ctx);

        var email = new Mock<IEmailService>();
        email.Setup(e => e.SendRawEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var svc = NewService(ctx, email.Object, FastSettings());

        await InvokeProcessNextAsync(svc);

        var recipient = await ctx.BroadcastRecipients.AsNoTracking().FirstAsync();
        recipient.Status.Should().Be(BroadcastRecipientStatus.Sent);

        var broadcast = await ctx.Broadcasts.AsNoTracking().FirstAsync();
        broadcast.SentCount.Should().Be(1);
        broadcast.Status.Should().Be(BroadcastStatus.Completed);
    }
}
