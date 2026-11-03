using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// MCP tool for recalling durable facts BuoyAI has remembered about a user (goals,
/// preferences, family, financial habits, important dates).
///
/// This is the read half of cross-agent memory (#788 follow-up): the facts live in
/// BuoyAI, not in any single assistant's private memory, so a fact BuoyAI learned in its
/// own in-app chat surfaces here for Claude, ChatGPT, or Gemini alike — and the coming
/// write tool will let those assistants add to the same shared store. Read-only and
/// user-scoped; honors the user's AiMemoryEnabled opt-in exactly like the in-app chat.
/// </summary>
[McpServerToolType]
public sealed class MemoryReadTools
{
    private readonly IAiMemoryService _memoryService;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<MemoryReadTools> _logger;

    public MemoryReadTools(
        IAiMemoryService memoryService,
        IUserRepository userRepository,
        ILogger<MemoryReadTools> logger)
    {
        _memoryService = memoryService;
        _userRepository = userRepository;
        _logger = logger;
    }

    [McpServerTool(Name = "recall_memory", ReadOnly = true)]
    [Description(
        "Recall durable facts Buoy Money has remembered about this user: savings goals, " +
        "preferences, family details, financial habits, and important dates. Call this at " +
        "the start of a money conversation so your advice reflects what the user has already " +
        "told Buoy Money, on this assistant or any other. Returns nothing if the user has AI " +
        "memory turned off.")]
    public async Task<string> RecallMemoryAsync(
        [Description("User ID to recall memories for")] int userId,
        [Description("Optional topic to focus the recall (e.g. 'savings goals', 'family'). Leave empty to return all remembered facts.")]
        string? topic = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: recall_memory called for user {UserId} (topic: {Topic})",
                userId, string.IsNullOrWhiteSpace(topic) ? "(all)" : topic);

            // Respect the opt-in exactly like the in-app assistant: no flag, no memories.
            var user = await _userRepository.GetByIdAsync(userId);
            if (user is null || !user.AiMemoryEnabled)
            {
                return JsonSerializer.Serialize(new
                {
                    Enabled = false,
                    Message = "This user has AI memory turned off (or has no remembered facts). " +
                              "They can enable it in Buoy Money under Settings.",
                    Memories = Array.Empty<object>()
                });
            }

            var memories = string.IsNullOrWhiteSpace(topic)
                ? await _memoryService.GetUserMemoriesAsync(userId)
                : await _memoryService.GetRelevantMemoriesAsync(userId, topic, limit: 20);

            var list = memories
                .Select(m => new
                {
                    Type = m.MemoryType,
                    Fact = string.IsNullOrWhiteSpace(m.DisplayText) ? m.MemoryValue : m.DisplayText
                })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                Enabled = true,
                Count = list.Count,
                Message = list.Count == 0
                    ? "Buoy Money hasn't remembered anything about this user yet."
                    : $"Buoy Money remembers {list.Count} fact(s) about this user.",
                Memories = list
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP Tool: recall_memory failed for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = "Failed to recall memories." });
        }
    }
}
