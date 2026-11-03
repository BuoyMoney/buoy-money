using System;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The validation fence around agent-authored memory writes (cross-agent memory, write half):
/// opt-in gate, type allowlist, length caps, per-user count cap, and upsert on the natural key.
/// </summary>
public class AiMemoryRememberFactTests : IDisposable
{
    private readonly BuoyAIDbContext _context;
    private readonly AiMemoryService _service;

    public AiMemoryRememberFactTests()
    {
        _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var config = new ConfigurationBuilder().Build();
        _service = new AiMemoryService(_context, config, NullLogger<AiMemoryService>.Instance, TestGeminiUsage.Create());
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedUser(int id, bool memoryEnabled)
    {
        _context.Users.Add(new User { Id = id, Email = $"u{id}@t.co", AiMemoryEnabled = memoryEnabled });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task RememberFact_WhenMemoryDisabled_StoresNothing()
    {
        await SeedUser(1, memoryEnabled: false);

        var result = await _service.RememberFactAsync(1, "goal", "house", "Saving for a house", null);

        result.Should().BeNull();
        (await _context.UserAiMemories.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RememberFact_UnknownType_IsRejected()
    {
        await SeedUser(1, memoryEnabled: true);

        var result = await _service.RememberFactAsync(1, "transfer_money", "x", "do a thing", null);

        result.Should().BeNull();
        (await _context.UserAiMemories.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RememberFact_ValidFact_IsStoredAndDefaultsDisplayText()
    {
        await SeedUser(1, memoryEnabled: true);

        var result = await _service.RememberFactAsync(1, "GOAL", "house_downpayment", "Saving $40k by 2027", null);

        result.Should().NotBeNull();
        result!.MemoryType.Should().Be("goal");           // normalized lower-case
        result.Confidence.Should().Be(1.0m);              // explicitly stated
        result.SourceSessionId.Should().BeNull();         // authored over MCP, no chat session
        result.DisplayText.Should().Be("Saving $40k by 2027"); // defaults to value
    }

    [Fact]
    public async Task RememberFact_SameKeyTwice_UpsertsInPlace()
    {
        await SeedUser(1, memoryEnabled: true);

        await _service.RememberFactAsync(1, "goal", "house", "Saving $30k", null);
        await _service.RememberFactAsync(1, "goal", "house", "Saving $40k now", null);

        var rows = await _context.UserAiMemories.Where(m => m.UserId == 1).ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].MemoryValue.Should().Be("Saving $40k now");
    }

    [Fact]
    public async Task RememberFact_EmptyValue_IsRejected()
    {
        await SeedUser(1, memoryEnabled: true);

        var result = await _service.RememberFactAsync(1, "goal", "house", "   ", null);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RememberFact_OverlongValue_IsTruncated()
    {
        await SeedUser(1, memoryEnabled: true);
        var huge = new string('x', 5000);

        var result = await _service.RememberFactAsync(1, "preference", "note", huge, null);

        result.Should().NotBeNull();
        result!.MemoryValue.Length.Should().BeLessThanOrEqualTo(1000);
        result.DisplayText!.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task RememberFact_AtCap_PrunesOldestUnusedBeforeInserting()
    {
        await SeedUser(1, memoryEnabled: true);
        // Fill to the cap (200) with distinct keys, oldest-learned first.
        for (var i = 0; i < 200; i++)
        {
            _context.UserAiMemories.Add(new UserAiMemory
            {
                UserId = 1, MemoryType = "preference", MemoryKey = $"k{i}", MemoryValue = $"v{i}",
                IsActive = true, LearnedAt = DateTime.UtcNow.AddDays(-200 + i), LastUsedAt = null
            });
        }
        await _context.SaveChangesAsync();

        var result = await _service.RememberFactAsync(1, "goal", "new_goal", "A brand new goal", null);

        result.Should().NotBeNull();
        // Still bounded, and the oldest (k0) was evicted to make room.
        (await _context.UserAiMemories.CountAsync(m => m.UserId == 1 && m.IsActive))
            .Should().BeLessThanOrEqualTo(200);
        (await _context.UserAiMemories.AnyAsync(m => m.MemoryKey == "k0")).Should().BeFalse();
        (await _context.UserAiMemories.AnyAsync(m => m.MemoryKey == "new_goal")).Should().BeTrue();
    }
}
