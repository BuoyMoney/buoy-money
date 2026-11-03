using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.MCP.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.MCP;

/// <summary>
/// recall_memory is the read half of cross-agent memory: any connected assistant can read
/// the durable facts BuoyAI remembers, and it must honor the AiMemoryEnabled opt-in exactly
/// like the in-app chat does.
/// </summary>
public class MemoryReadToolsTests
{
    private readonly Mock<IAiMemoryService> _memory = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly MemoryReadTools _tool;

    public MemoryReadToolsTests()
    {
        _tool = new MemoryReadTools(_memory.Object, _users.Object, NullLogger<MemoryReadTools>.Instance);
    }

    [Fact]
    public void RecallTool_IsRegistered_ForTheConnectSurface()
    {
        McpToolExecutor.KnownToolTypes.Should().Contain(typeof(MemoryReadTools));
    }

    [Fact]
    public async Task RecallMemory_WhenMemoryDisabled_ReturnsDisabledAndNoFacts()
    {
        _users.Setup(u => u.GetByIdAsync(7)).ReturnsAsync(new User { Id = 7, AiMemoryEnabled = false });

        var json = await _tool.RecallMemoryAsync(7);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Enabled").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("Memories").GetArrayLength().Should().Be(0);
        // Must never call the memory store for an opted-out user.
        _memory.Verify(m => m.GetUserMemoriesAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task RecallMemory_WhenEnabled_ReturnsRememberedFacts()
    {
        _users.Setup(u => u.GetByIdAsync(7)).ReturnsAsync(new User { Id = 7, AiMemoryEnabled = true });
        _memory.Setup(m => m.GetUserMemoriesAsync(7)).ReturnsAsync(new List<UserAiMemory>
        {
            new() { UserId = 7, MemoryType = "goal", MemoryValue = "house downpayment", DisplayText = "Saving for a house downpayment" },
            new() { UserId = 7, MemoryType = "family", MemoryValue = "2 kids", DisplayText = "Has two kids" },
        });

        var json = await _tool.RecallMemoryAsync(7);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Enabled").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("Count").GetInt32().Should().Be(2);
        json.Should().Contain("Saving for a house downpayment");
    }

    [Fact]
    public async Task RecallMemory_WithTopic_UsesRelevanceSearch()
    {
        _users.Setup(u => u.GetByIdAsync(7)).ReturnsAsync(new User { Id = 7, AiMemoryEnabled = true });
        _memory.Setup(m => m.GetRelevantMemoriesAsync(7, "savings", It.IsAny<int>()))
            .ReturnsAsync(new List<UserAiMemory>
            {
                new() { UserId = 7, MemoryType = "goal", DisplayText = "Saving for a house downpayment" },
            });

        var json = await _tool.RecallMemoryAsync(7, topic: "savings");

        json.Should().Contain("house downpayment");
        _memory.Verify(m => m.GetRelevantMemoriesAsync(7, "savings", It.IsAny<int>()), Times.Once);
        _memory.Verify(m => m.GetUserMemoriesAsync(It.IsAny<int>()), Times.Never);
    }
}
