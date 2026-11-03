using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tool for storing a durable fact a user tells their assistant (cross-agent memory,
/// write half). This is BuoyAI Connect's ONLY write tool and it is deliberately non-financial:
/// it appends to the user's memory store (goals, preferences, family, dates, habits) and can
/// never read, move, or alter a single dollar. Everything money-related on Connect stays
/// read-only. The fence (type allowlist, length + count caps, opt-in gate) lives in
/// AiMemoryService.RememberFactAsync. Requires the buoyai:write:memory scope.
/// </summary>
[McpServerToolType]
public sealed class MemoryWriteTools
{
    private readonly IAiMemoryService _memoryService;
    private readonly ILogger<MemoryWriteTools> _logger;

    public MemoryWriteTools(IAiMemoryService memoryService, ILogger<MemoryWriteTools> logger)
    {
        _memoryService = memoryService;
        _logger = logger;
    }

    [McpServerTool(Name = "remember", ReadOnly = false)]
    [Description(
        "Remember a durable fact the user tells you about their money life, so it's available " +
        "next time on this or any other AI assistant. Use for lasting facts (a savings goal, a " +
        "spending habit, a family detail, an important date, a preference), not one-off chatter. " +
        "This stores a note only. It cannot move, spend, or transfer any money. Ignored if the " +
        "user has AI memory turned off.")]
    public async Task<string> RememberAsync(
        [Description("User ID to remember this fact for")] int userId,
        [Description("Category of fact. Must be one of: date, holiday, preference, goal, family, financial_habit, life_event")]
        string memoryType,
        [Description("A short stable key identifying the fact, e.g. 'house_downpayment', 'wife_birthday', 'dining_overspend'")]
        string key,
        [Description("The fact to store, e.g. 'Saving $40,000 for a house downpayment by 2027'")]
        string value,
        [Description("Optional one-line human-readable version shown to the user. Defaults to the value.")]
        string? displayText = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: remember called for user {UserId} ({Type}/{Key})", userId, memoryType, key);

            var stored = await _memoryService.RememberFactAsync(userId, memoryType, key, value, displayText);
            if (stored is null)
            {
                return JsonSerializer.Serialize(new
                {
                    Remembered = false,
                    Message = "Nothing was stored. Either the user has AI memory turned off, the " +
                              "category wasn't one of the allowed types (date, holiday, preference, " +
                              "goal, family, financial_habit, life_event), or the fact was empty."
                });
            }

            return JsonSerializer.Serialize(new
            {
                Remembered = true,
                Type = stored.MemoryType,
                Fact = string.IsNullOrWhiteSpace(stored.DisplayText) ? stored.MemoryValue : stored.DisplayText,
                Message = "Stored. This is a note only and moved no money; Buoy Money will recall it next time, " +
                          "on any assistant."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP Tool: remember failed for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Remembered = false, Error = "Failed to store the fact." });
        }
    }
}
