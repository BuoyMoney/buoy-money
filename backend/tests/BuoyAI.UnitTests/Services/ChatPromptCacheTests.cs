using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class ChatPromptCacheTests
{
    private static (ChatPromptCache cache, Mock<IGeminiCacheClient> client) Build(
        bool enabled = true, int minTokens = 10, int rollForward = 2)
    {
        var client = new Mock<IGeminiCacheClient>();
        var n = 0;
        client.Setup(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<Content>(), It.IsAny<List<Content>>(),
                It.IsAny<List<FunctionDeclaration>>(), It.IsAny<System.TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"caches/abc{++n}");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:PromptCaching:Enabled"] = enabled ? "true" : "false",
            ["AI:PromptCaching:MinPrefixTokens"] = minTokens.ToString(),
            ["AI:PromptCaching:RollForwardMaxUncovered"] = rollForward.ToString(),
        }).Build();

        var cache = new ChatPromptCache(client.Object, new MemoryCache(new MemoryCacheOptions()), config,
            NullLogger<ChatPromptCache>.Instance);
        return (cache, client);
    }

    private static List<Content> History(int msgs)
    {
        var list = new List<Content>();
        for (var i = 0; i < msgs; i++) list.Add(new Content($"message {i} with enough words to matter", Role.User));
        return list;
    }

    private static Task<PromptCacheRef?> Call(ChatPromptCache c, string conv, List<Content> hist, int tokens = 5000)
        => c.GetOrCreateAsync(conv, "gemini-2.5-flash", new Content("scaffold"), hist,
            new List<FunctionDeclaration>(), tokens);

    [Fact]
    public async Task Disabled_ReturnsNull()
    {
        var (cache, client) = Build(enabled: false);
        (await Call(cache, "c1", History(4))).Should().BeNull();
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BelowThreshold_ReturnsNull()
    {
        var (cache, client) = Build(minTokens: 10000);
        (await Call(cache, "c1", History(4), tokens: 500)).Should().BeNull();
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FirstCall_CreatesCache_CoversAllHistory()
    {
        var (cache, client) = Build();
        var r = await Call(cache, "c1", History(6));
        r.Should().NotBeNull();
        r!.CoveredHistoryCount.Should().Be(6);
        client.Verify(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<Content>(), It.IsAny<List<Content>>(),
            It.IsAny<List<FunctionDeclaration>>(), It.IsAny<System.TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SecondCall_WithinRollForward_ReusesSameCache()
    {
        var (cache, client) = Build(rollForward: 4);
        var first = await Call(cache, "c1", History(6));
        var second = await Call(cache, "c1", History(8)); // +2 msgs, within roll-forward
        second!.CacheName.Should().Be(first!.CacheName);
        second.CoveredHistoryCount.Should().Be(6); // still covers the original 6; caller sends the 2 new
        client.Verify(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<Content>(), It.IsAny<List<Content>>(),
            It.IsAny<List<FunctionDeclaration>>(), It.IsAny<System.TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GrewBeyondRollForward_RecreatesCache()
    {
        var (cache, client) = Build(rollForward: 2);
        var first = await Call(cache, "c1", History(6));
        var second = await Call(cache, "c1", History(12)); // +6, exceeds roll-forward → recreate
        second!.CacheName.Should().NotBe(first!.CacheName);
        second.CoveredHistoryCount.Should().Be(12);
        client.Verify(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<Content>(), It.IsAny<List<Content>>(),
            It.IsAny<List<FunctionDeclaration>>(), It.IsAny<System.TimeSpan>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ClientReturnsNull_FailsOpen()
    {
        var (cache, client) = Build();
        client.Setup(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<Content>(), It.IsAny<List<Content>>(),
                It.IsAny<List<FunctionDeclaration>>(), It.IsAny<System.TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        (await Call(cache, "c1", History(6))).Should().BeNull();
    }

    [Fact]
    public async Task ClientThrows_FailsOpen()
    {
        var (cache, client) = Build();
        client.Setup(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<Content>(), It.IsAny<List<Content>>(),
                It.IsAny<List<FunctionDeclaration>>(), It.IsAny<System.TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Exception("boom"));
        (await Call(cache, "c1", History(6))).Should().BeNull();
    }
}
