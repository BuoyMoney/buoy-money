using BuoyAI.Infrastructure.Configuration;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Utilities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing user AI memories - learned facts from conversations.
/// Uses Gemini to extract significant facts from conversation messages.
/// </summary>
public class AiMemoryService : IAiMemoryService
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<AiMemoryService> _logger;
    private readonly IAiProvider _ai;
    private readonly bool _extractionEnabled;
    // 2.0-flash was decommissioned (generateContent 404s) — extraction silently
    // failed on the old hardcode. Cheap + verified-live model is fine here.
    private const string ExtractionModel = "gemini-2.5-flash";

    public AiMemoryService(
        BuoyAIDbContext context,
        IConfiguration configuration,
        ILogger<AiMemoryService> logger,
        GeminiUsageReporter geminiUsage,
        IAiProvider? aiProvider = null)
    {
        _context = context;
        _logger = logger;
        _ai = aiProvider ?? new GeminiNativeProvider(configuration, NullLogger<GeminiNativeProvider>.Instance, geminiUsage);
        // Gemini: deliberately config-only (see GeminiApiKey.ResolveConfigOnly), so hosted keeps
        // extraction off. Any other provider: on when the provider is set up.
        _extractionEnabled = _ai.Id == AiProviderOptions.Gemini
            ? !string.IsNullOrEmpty(GeminiApiKey.ResolveConfigOnly(configuration))
            : _ai.IsConfigured;
    }

    public async Task ExtractAndStoreMemoriesAsync(int userId, Guid sessionId, string message, string role)
    {
        // Only extract from user messages (not AI responses)
        if (role.ToLowerInvariant() != "user" || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Don't extract if message is too short
        if (message.Length < 20)
        {
            return;
        }

        try
        {
            using var usageScope = AiUsageScope.ForUser(userId);
            var extractedMemories = await ExtractMemoriesFromTextAsync(message);
            
            if (extractedMemories == null || !extractedMemories.Any())
            {
                return;
            }

            foreach (var memory in extractedMemories)
            {
                // Check if we already have this memory
                var existing = await _context.UserAiMemories
                    .FirstOrDefaultAsync(m => 
                        m.UserId == userId && 
                        m.MemoryType == memory.MemoryType && 
                        m.MemoryKey == memory.MemoryKey);

                if (existing != null)
                {
                    // Update if the new confidence is higher or it's the same value
                    if (memory.Confidence > existing.Confidence || memory.MemoryValue == existing.MemoryValue)
                    {
                        existing.MemoryValue = memory.MemoryValue;
                        existing.DisplayText = memory.DisplayText;
                        existing.Confidence = memory.Confidence;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.SourceSessionId = sessionId;
                    }
                }
                else
                {
                    // Create new memory
                    var newMemory = new UserAiMemory
                    {
                        UserId = userId,
                        MemoryType = memory.MemoryType,
                        MemoryKey = memory.MemoryKey,
                        MemoryValue = memory.MemoryValue,
                        DisplayText = memory.DisplayText,
                        Confidence = memory.Confidence,
                        SourceSessionId = sessionId,
                        LearnedAt = DateTime.UtcNow,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _context.UserAiMemories.Add(newMemory);
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("Extracted and stored {Count} memories for user {UserId}", 
                extractedMemories.Count, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract memories from message for user {UserId}", userId);
        }
    }

    // The fence around agent-authored memory writes (cross-agent memory, write half).
    private static readonly HashSet<string> AllowedMemoryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        AiMemoryTypes.Date, AiMemoryTypes.Holiday, AiMemoryTypes.Preference, AiMemoryTypes.Goal,
        AiMemoryTypes.Family, AiMemoryTypes.FinancialHabit, AiMemoryTypes.LifeEvent
    };
    private const int MaxKeyLength = 100;       // matches UserAiMemory.MemoryKey [MaxLength]
    private const int MaxValueLength = 1000;    // MemoryValue is unbounded in schema; cap abuse
    private const int MaxDisplayLength = 500;   // matches UserAiMemory.DisplayText [MaxLength]
    private const int MaxMemoriesPerUser = 200; // bound the store; prune oldest-unused beyond this

    public async Task<UserAiMemory?> RememberFactAsync(
        int userId, string memoryType, string memoryKey, string memoryValue, string? displayText)
    {
        // Opt-in gate: same rule the in-app assistant honors. No flag, no write.
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.AiMemoryEnabled)
        {
            _logger.LogInformation("RememberFact rejected for user {UserId}: AI memory disabled", userId);
            return null;
        }

        // Type must be one we recognize — an assistant can't invent a category (fence).
        memoryType = (memoryType ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedMemoryTypes.Contains(memoryType))
        {
            _logger.LogWarning("RememberFact rejected for user {UserId}: unknown memory type '{Type}'", userId, memoryType);
            return null;
        }

        memoryKey = Truncate((memoryKey ?? string.Empty).Trim(), MaxKeyLength);
        memoryValue = Truncate((memoryValue ?? string.Empty).Trim(), MaxValueLength);
        if (memoryKey.Length == 0 || memoryValue.Length == 0)
        {
            return null;
        }
        var display = Truncate(
            string.IsNullOrWhiteSpace(displayText) ? memoryValue : displayText.Trim(), MaxDisplayLength);

        // Upsert on the natural key. An agent restating a fact updates it in place.
        var existing = await _context.UserAiMemories
            .FirstOrDefaultAsync(m => m.UserId == userId && m.MemoryType == memoryType && m.MemoryKey == memoryKey);

        if (existing != null)
        {
            existing.MemoryValue = memoryValue;
            existing.DisplayText = display;
            existing.Confidence = 1.0m; // explicitly stated by the user via their assistant
            existing.IsActive = true;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.SourceSessionId = null; // authored over MCP, not a chat session
            await _context.SaveChangesAsync();
            _logger.LogInformation("RememberFact updated {Type}/{Key} for user {UserId}", memoryType, memoryKey, userId);
            return existing;
        }

        await PruneIfOverCapAsync(userId);

        var created = new UserAiMemory
        {
            UserId = userId,
            MemoryType = memoryType,
            MemoryKey = memoryKey,
            MemoryValue = memoryValue,
            DisplayText = display,
            Confidence = 1.0m,
            SourceSessionId = null,
            LearnedAt = DateTime.UtcNow,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.UserAiMemories.Add(created);
        await _context.SaveChangesAsync();
        _logger.LogInformation("RememberFact created {Type}/{Key} for user {UserId}", memoryType, memoryKey, userId);
        return created;
    }

    /// <summary>Keeps the per-user store bounded: once at the cap, hard-delete the least recently
    /// used active memories so a chatty agent can't grow it without limit.</summary>
    private async Task PruneIfOverCapAsync(int userId)
    {
        var activeCount = await _context.UserAiMemories.CountAsync(m => m.UserId == userId && m.IsActive);
        if (activeCount < MaxMemoriesPerUser)
        {
            return;
        }

        var toRemove = await _context.UserAiMemories
            .Where(m => m.UserId == userId && m.IsActive)
            .OrderBy(m => m.LastUsedAt ?? m.LearnedAt) // never-used + oldest go first
            .Take(activeCount - MaxMemoriesPerUser + 1)
            .ToListAsync();
        _context.UserAiMemories.RemoveRange(toRemove);
        _logger.LogInformation("Pruned {Count} old memories for user {UserId} (at cap)", toRemove.Count, userId);
    }

    private static string Truncate(string s, int max) => SafeText.Truncate(s, max);

    public async Task<IEnumerable<UserAiMemory>> GetUserMemoriesAsync(int userId)
    {
        return await _context.UserAiMemories
            .Where(m => m.UserId == userId && m.IsActive)
            .OrderByDescending(m => m.UsageCount)
            .ThenByDescending(m => m.LearnedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<UserAiMemory>> GetRelevantMemoriesAsync(int userId, string context, int limit = 5)
    {
        // Get all active memories for the user
        var allMemories = await _context.UserAiMemories
            .Where(m => m.UserId == userId && m.IsActive)
            .ToListAsync();

        if (!allMemories.Any())
        {
            return Enumerable.Empty<UserAiMemory>();
        }

        // Simple keyword-based relevance scoring
        var contextLower = context.ToLowerInvariant();
        var scored = allMemories.Select(m => new
        {
            Memory = m,
            Score = CalculateRelevanceScore(m, contextLower)
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(limit)
        .Select(x => x.Memory);

        return scored;
    }

    public async Task<UserAiMemory?> UpdateMemoryAsync(int userId, int memoryId, string newValue, string newDisplayText)
    {
        var memory = await _context.UserAiMemories
            .FirstOrDefaultAsync(m => m.Id == memoryId && m.UserId == userId);

        if (memory == null)
        {
            return null;
        }

        memory.MemoryValue = newValue;
        memory.DisplayText = newDisplayText;
        memory.WasManuallyEdited = true;
        memory.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return memory;
    }

    public async Task<bool> DeactivateMemoryAsync(int userId, int memoryId)
    {
        var memory = await _context.UserAiMemories
            .FirstOrDefaultAsync(m => m.Id == memoryId && m.UserId == userId);

        if (memory == null)
        {
            return false;
        }

        memory.IsActive = false;
        memory.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteMemoryAsync(int userId, int memoryId)
    {
        var memory = await _context.UserAiMemories
            .FirstOrDefaultAsync(m => m.Id == memoryId && m.UserId == userId);

        if (memory == null)
        {
            return false;
        }

        _context.UserAiMemories.Remove(memory);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAllUserMemoriesAsync(int userId)
    {
        var memories = await _context.UserAiMemories
            .Where(m => m.UserId == userId)
            .ToListAsync();

        _context.UserAiMemories.RemoveRange(memories);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Deleted all {Count} memories for user {UserId}", memories.Count, userId);
    }

    public async Task RecordMemoryUsageAsync(int memoryId)
    {
        var memory = await _context.UserAiMemories.FindAsync(memoryId);
        if (memory != null)
        {
            memory.UsageCount++;
            memory.LastUsedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
    }

    public async Task<string> BuildMemoryContextAsync(int userId, string query)
    {
        var relevantMemories = await GetRelevantMemoriesAsync(userId, query, 5);
        
        if (!relevantMemories.Any())
        {
            return string.Empty;
        }

        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("=== REMEMBERED FACTS ABOUT THIS USER ===");
        contextBuilder.AppendLine("Use these memories to personalize your responses:");
        contextBuilder.AppendLine();

        foreach (var memory in relevantMemories)
        {
            contextBuilder.AppendLine($"- {memory.DisplayText}");
            
            // Record usage
            await RecordMemoryUsageAsync(memory.Id);
        }

        contextBuilder.AppendLine();
        contextBuilder.AppendLine("Naturally incorporate relevant memories into your response, but don't force them if not applicable.");
        contextBuilder.AppendLine();

        return contextBuilder.ToString();
    }

    private int CalculateRelevanceScore(UserAiMemory memory, string contextLower)
    {
        int score = 0;
        var displayTextLower = memory.DisplayText.ToLowerInvariant();
        var keyLower = memory.MemoryKey.ToLowerInvariant();
        var valueLower = memory.MemoryValue.ToLowerInvariant();

        // Check for keyword matches
        var keywords = contextLower.Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var keyword in keywords)
        {
            if (keyword.Length < 3) continue;
            
            if (displayTextLower.Contains(keyword)) score += 2;
            if (keyLower.Contains(keyword)) score += 3;
            if (valueLower.Contains(keyword)) score += 2;
        }

        // Boost score based on memory type relevance to common queries
        if (contextLower.Contains("birthday") && memory.MemoryType == AiMemoryTypes.Date) score += 5;
        if (contextLower.Contains("save") && memory.MemoryType == AiMemoryTypes.Goal) score += 5;
        if (contextLower.Contains("goal") && memory.MemoryType == AiMemoryTypes.Goal) score += 5;
        if (contextLower.Contains("family") && memory.MemoryType == AiMemoryTypes.Family) score += 5;
        if (contextLower.Contains("holiday") && memory.MemoryType == AiMemoryTypes.Holiday) score += 5;
        if (contextLower.Contains("budget") && memory.MemoryType == AiMemoryTypes.FinancialHabit) score += 3;
        if (contextLower.Contains("spend") && memory.MemoryType == AiMemoryTypes.FinancialHabit) score += 3;

        // Boost recently used memories slightly
        if (memory.LastUsedAt.HasValue && (DateTime.UtcNow - memory.LastUsedAt.Value).TotalDays < 7)
        {
            score += 1;
        }

        return score;
    }

    private async Task<List<ExtractedMemory>> ExtractMemoriesFromTextAsync(string text)
    {
        try
        {
            if (!_extractionEnabled) return new List<ExtractedMemory>();

            var prompt = $@"Analyze the following user message and extract any significant facts that would be useful to remember for future conversations. Focus on:

1. Important dates (birthdays, anniversaries, deadlines)
2. Holidays they celebrate
3. Financial goals (saving for something specific)
4. Family information (spouse, kids, parents)
5. Preferences or habits mentioned
6. Life events (job changes, moves, major purchases)

User Message: ""{text}""

Return a JSON array of extracted memories. Each memory should have:
- memoryType: one of [""date"", ""holiday"", ""preference"", ""goal"", ""family"", ""financial_habit"", ""life_event""]
- memoryKey: a unique identifier (lowercase, underscores, e.g., ""wife_birthday"")
- memoryValue: the specific value
- displayText: human-readable description (e.g., ""Wife's birthday is March 15"")
- confidence: 0.0-1.0 how confident you are this is accurate

If no significant memories are found, return an empty array: []

Examples:
- ""My wife's birthday is next month on March 15"" -> {{""memoryType"":""date"",""memoryKey"":""wife_birthday"",""memoryValue"":""March 15"",""displayText"":""Wife's birthday is March 15"",""confidence"":0.95}}
- ""I'm saving up for a house downpayment"" -> {{""memoryType"":""goal"",""memoryKey"":""house_downpayment"",""memoryValue"":""saving for house"",""displayText"":""Saving for a house downpayment"",""confidence"":0.9}}

Return ONLY the JSON array, no other text.";

            var response = await _ai.GenerateTextAsync(new AiTextRequest(prompt, ExtractionModel, "memory-extraction"));
            var responseText = response.Text.Trim();
            if (responseText.Length == 0) responseText = "[]";

            // Clean up response if it has markdown code blocks
            if (responseText.StartsWith("```"))
            {
                responseText = responseText.Replace("```json", "").Replace("```", "").Trim();
            }

            var memories = JsonSerializer.Deserialize<List<ExtractedMemory>>(responseText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return memories ?? new List<ExtractedMemory>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract memories from text using AI");
            return new List<ExtractedMemory>();
        }
    }

    private class ExtractedMemory
    {
        public string MemoryType { get; set; } = string.Empty;
        public string MemoryKey { get; set; } = string.Empty;
        public string MemoryValue { get; set; } = string.Empty;
        public string DisplayText { get; set; } = string.Empty;
        public decimal Confidence { get; set; }
    }
}
