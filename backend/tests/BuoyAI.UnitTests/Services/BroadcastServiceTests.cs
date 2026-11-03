using System;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Admin;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class BroadcastServiceTests
{
    private static BuoyAIDbContext NewContext() =>
        new(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options);

    private static BroadcastService NewService(BuoyAIDbContext ctx, BroadcastSettings? settings = null)
    {
        var ai = new Mock<IAIService>();
        return new BroadcastService(
            ctx,
            Options.Create(settings ?? new BroadcastSettings { MaxRecipients = 1000 }),
            ai.Object,
            NullLogger<BroadcastService>.Instance);
    }

    private static User MakeUser(int id, bool verified = true, bool deleted = false,
        SubscriptionTier? tier = null, bool trial = false) => new()
    {
        Id = id,
        Username = $"u{id}",
        Email = $"u{id}@x.com",
        FirstName = "U",
        LastName = "Ser",
        PasswordHash = "x",
        IsEmailVerified = verified,
        IsDeleted = deleted,
        SubscriptionTier = tier,
        IsTrialActive = trial,
    };

    private static BroadcastRequest Req(string category, bool dryRun = true, int? confirm = null,
        string audience = "all", string? key = null) => new()
    {
        Subject = "Heads up",
        BodyMarkdown = "We have **news**.",
        Audience = audience,
        Category = category,
        DryRun = dryRun,
        ConfirmRecipientCount = confirm,
        IdempotencyKey = key,
    };

    [Fact]
    public async Task DryRun_RendersPipeTablesAsHtmlTables()
    {
        using var ctx = NewContext();
        ctx.Users.Add(MakeUser(1));
        await ctx.SaveChangesAsync();
        var req = Req("announcement");
        req.BodyMarkdown = "| Plan | Price |\n| --- | --- |\n| Standard | $5 |";

        var result = await NewService(ctx).CreateAsync(req, 99, "admin");

        result.Response!.PreviewHtml.Should().Contain("<table>");
        result.Response.PreviewHtml.Should().Contain("<th>Plan</th>");
        result.Response.PreviewHtml.Should().Contain("<td>Standard</td>");
    }

    [Fact]
    public async Task DryRun_EscapesRawHtmlInBody()
    {
        using var ctx = NewContext();
        ctx.Users.Add(MakeUser(1));
        await ctx.SaveChangesAsync();
        var req = Req("announcement");
        req.BodyMarkdown = "Hi <img src=\"https://x.test/p.gif\"> <script>alert(1)</script> **ok**";

        var result = await NewService(ctx).CreateAsync(req, 99, "admin");

        result.Response!.PreviewHtml.Should().NotContain("<img");
        result.Response.PreviewHtml.Should().NotContain("<script>");
        result.Response.PreviewHtml.Should().Contain("&lt;img");
        result.Response.PreviewHtml.Should().Contain("<strong>ok</strong>");
    }

    [Fact]
    public async Task DryRun_Announcement_RespectsProductAnnouncementsOptOut()
    {
        using var ctx = NewContext();
        ctx.Users.AddRange(MakeUser(1), MakeUser(2), MakeUser(3));
        // user 3 opts out of product announcements
        ctx.UserEmailPreferences.Add(new UserEmailPreferences { UserId = 3, ProductAnnouncements = false });
        await ctx.SaveChangesAsync();

        var result = await NewService(ctx).CreateAsync(Req("announcement"), 99, "admin");

        result.Success.Should().BeTrue();
        result.Response!.DryRun.Should().BeTrue();
        result.Response.RecipientCount.Should().Be(2); // users 1, 2
        result.Response.SkippedCount.Should().Be(1);    // user 3 opted out
        result.Response.PreviewHtml.Should().Contain("<strong>news</strong>");
        ctx.Broadcasts.Should().BeEmpty(); // dry run never persists
    }

    [Fact]
    public async Task Incident_OverridesOptOut_AndReachesEveryone()
    {
        using var ctx = NewContext();
        ctx.Users.AddRange(MakeUser(1), MakeUser(2), MakeUser(3));
        ctx.UserEmailPreferences.Add(new UserEmailPreferences { UserId = 3, ProductAnnouncements = false });
        await ctx.SaveChangesAsync();

        var result = await NewService(ctx).CreateAsync(Req("incident"), 99, "admin");

        result.Response!.RecipientCount.Should().Be(3); // opt-out ignored for incidents
        result.Response.SkippedCount.Should().Be(0);
    }

    [Fact]
    public async Task Audience_All_ExcludesUnverifiedAndDeleted()
    {
        using var ctx = NewContext();
        ctx.Users.AddRange(
            MakeUser(1),
            MakeUser(2, verified: false),
            MakeUser(3, deleted: true));
        await ctx.SaveChangesAsync();

        var result = await NewService(ctx).CreateAsync(Req("incident"), 99, "admin");

        result.Response!.RecipientCount.Should().Be(1); // only the verified, non-deleted user
    }

    [Fact]
    public async Task Audience_Active_RequiresSubscriptionOrTrial()
    {
        using var ctx = NewContext();
        ctx.Users.AddRange(
            MakeUser(1, tier: SubscriptionTier.Premium),
            MakeUser(2, trial: true),
            MakeUser(3)); // no sub, no trial -> excluded from "active"
        await ctx.SaveChangesAsync();

        var result = await NewService(ctx).CreateAsync(Req("incident", audience: "active"), 99, "admin");

        result.Response!.RecipientCount.Should().Be(2);
    }

    [Fact]
    public async Task RealSend_ConfirmMismatch_IsRejected()
    {
        using var ctx = NewContext();
        ctx.Users.AddRange(MakeUser(1), MakeUser(2));
        await ctx.SaveChangesAsync();

        var result = await NewService(ctx).CreateAsync(Req("incident", dryRun: false, confirm: 5), 99, "admin");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("confirmRecipientCount");
        ctx.Broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task RealSend_ExceedsCap_IsRejected()
    {
        using var ctx = NewContext();
        ctx.Users.AddRange(MakeUser(1), MakeUser(2), MakeUser(3));
        await ctx.SaveChangesAsync();

        var svc = NewService(ctx, new BroadcastSettings { MaxRecipients = 2 });
        var result = await svc.CreateAsync(Req("incident", dryRun: false, confirm: 3), 99, "admin");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("exceeds");
    }

    [Fact]
    public async Task RealSend_PersistsBroadcastAndPendingRecipients()
    {
        using var ctx = NewContext();
        ctx.Users.AddRange(MakeUser(1), MakeUser(2));
        await ctx.SaveChangesAsync();

        var result = await NewService(ctx).CreateAsync(
            Req("incident", dryRun: false, confirm: 2, key: "abc"), 42, "admin");

        result.Success.Should().BeTrue();
        result.Response!.BroadcastId.Should().NotBeNull();
        result.Response.Status.Should().Be("pending");

        var broadcast = await ctx.Broadcasts.Include(b => b.Recipients).SingleAsync();
        broadcast.CreatedByUserId.Should().Be(42);
        broadcast.TotalRecipients.Should().Be(2);
        broadcast.IdempotencyKey.Should().Be("abc");
        broadcast.Recipients.Should().HaveCount(2);
        broadcast.Recipients.Should().OnlyContain(r => r.Status == BroadcastRecipientStatus.Pending);
    }

    [Fact]
    public async Task RealSend_SameIdempotencyKey_ReturnsExistingBroadcast()
    {
        using var ctx = NewContext();
        ctx.Users.AddRange(MakeUser(1), MakeUser(2));
        await ctx.SaveChangesAsync();
        var svc = NewService(ctx);

        var first = await svc.CreateAsync(Req("incident", dryRun: false, confirm: 2, key: "dup"), 1, "admin");
        var second = await svc.CreateAsync(Req("incident", dryRun: false, confirm: 2, key: "dup"), 1, "admin");

        first.Response!.BroadcastId.Should().Be(second.Response!.BroadcastId);
        (await ctx.Broadcasts.CountAsync()).Should().Be(1); // no second broadcast created
    }
}
