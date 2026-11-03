using System;
using System.Text;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Chat titles and remembered facts are cut to a maximum length before they are saved. A cut
/// that lands inside an emoji leaves half of it (a lone UTF-16 surrogate). Npgsql writes text
/// with a strict UTF-8 encoder and throws on that, so on Postgres the whole save fails: the
/// user's first chat message is lost and the stream answers "An error occurred". The in-memory
/// provider doesn't encode, so these tests encode the saved text the same strict way.
/// </summary>
public class ChatTextTruncationTests : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private const string Emoji = "\U0001F4B0"; // two UTF-16 chars

    private readonly BuoyAIDbContext _context;

    public ChatTextTruncationTests()
    {
        _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ShouldEncodeStrictly(string? text)
    {
        text.Should().NotBeNull();
        var act = () => StrictUtf8.GetBytes(text!);
        act.Should().NotThrow("Npgsql refuses text containing half an emoji");
    }

    private AiConversationService NewConversationService()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new AiConversationService(
            _context,
            NullLogger<AiConversationService>.Instance,
            new BuoyAiMetrics(new TestMeterFactory()),
            Mock.Of<IChatEmbeddingService>(),
            scopeFactory);
    }

    [Theory]
    [InlineData(45)] // emoji fits wholly before the cut
    [InlineData(46)] // emoji straddles the 47-character cut
    [InlineData(47)] // emoji starts right after the cut
    public async Task FirstMessageTitle_NeverSplitsAnEmoji(int lettersBeforeEmoji)
    {
        _context.Users.Add(new User { Id = 1, Email = "u1@t.co" });
        await _context.SaveChangesAsync();
        var service = NewConversationService();
        var conversation = await service.StartOrGetConversationAsync(1, Guid.NewGuid());
        var message = new string('a', lettersBeforeEmoji) + Emoji + " how much did I spend on food this month?";

        await service.AddMessageAsync(conversation.Id, 1, "user", message);

        var saved = await _context.AiConversations.SingleAsync();
        ShouldEncodeStrictly(saved.Title);
        saved.Title!.Should().EndWith("...");
        saved.Title!.Length.Should().BeLessThanOrEqualTo(50);
    }

    [Fact]
    public async Task RenamedTitle_NeverSplitsAnEmoji()
    {
        _context.Users.Add(new User { Id = 1, Email = "u1@t.co" });
        await _context.SaveChangesAsync();
        var service = NewConversationService();
        var sessionId = Guid.NewGuid();
        await service.StartOrGetConversationAsync(1, sessionId);

        (await service.UpdateConversationTitleAsync(1, sessionId, new string('a', 196) + Emoji + "tail")).Should().BeTrue();

        var saved = await _context.AiConversations.SingleAsync();
        ShouldEncodeStrictly(saved.Title);
        saved.Title!.Length.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task RememberedFact_NeverSplitsAnEmoji()
    {
        _context.Users.Add(new User { Id = 1, Email = "u1@t.co", AiMemoryEnabled = true });
        await _context.SaveChangesAsync();
        var service = new AiMemoryService(_context, new ConfigurationBuilder().Build(),
            NullLogger<AiMemoryService>.Instance, TestGeminiUsage.Create());

        var memory = await service.RememberFactAsync(1, "goal",
            new string('k', 99) + Emoji, new string('v', 999) + Emoji, new string('d', 499) + Emoji);

        memory.Should().NotBeNull();
        ShouldEncodeStrictly(memory!.MemoryKey);
        ShouldEncodeStrictly(memory.MemoryValue);
        ShouldEncodeStrictly(memory.DisplayText);
        memory.MemoryKey.Length.Should().BeLessThanOrEqualTo(100);
        memory.DisplayText!.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Theory]
    [InlineData("", 5, "")]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 3, "abc")]
    public void Truncate_PlainText_CutsAtTheLimit(string input, int max, string expected)
    {
        BuoyAI.Core.Utilities.SafeText.Truncate(input, max).Should().Be(expected);
    }

    [Fact]
    public void Truncate_InsideAnEmoji_DropsTheWholeEmoji()
    {
        var result = BuoyAI.Core.Utilities.SafeText.Truncate("ab" + Emoji + "c", 3);

        result.Should().Be("ab");
    }

    [Fact]
    public void Truncate_NegativeLimit_Throws()
    {
        var act = () => BuoyAI.Core.Utilities.SafeText.Truncate("abc", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("abc", 5, "abc")] // fits: unchanged, no dots
    [InlineData("abcdef", 6, "abcdef")]
    [InlineData("abcdefgh", 6, "abc...")]
    [InlineData("abcdef", 3, "...")]
    [InlineData("abcdef", 2, "ab")] // no room for the dots: just cut
    [InlineData("abcdef", 0, "")]
    public void Ellipsize_CutsToTheLimitIncludingTheDots(string input, int max, string expected)
    {
        BuoyAI.Core.Utilities.SafeText.Ellipsize(input, max).Should().Be(expected);
    }

    [Fact]
    public void Ellipsize_InsideAnEmoji_DropsTheWholeEmoji()
    {
        // Limit 6 keeps 3 chars before the dots; the third is the emoji's first half.
        var result = BuoyAI.Core.Utilities.SafeText.Ellipsize("ab" + Emoji + "cdef", 6);

        result.Should().Be("ab...");
        ShouldEncodeStrictly(result);
    }

    [Fact]
    public void Ellipsize_NegativeLimit_Throws()
    {
        var act = () => BuoyAI.Core.Utilities.SafeText.Ellipsize("abc", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
