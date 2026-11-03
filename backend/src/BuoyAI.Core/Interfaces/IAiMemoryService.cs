using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for managing user AI memories - learned facts about users from conversations.
/// </summary>
public interface IAiMemoryService
{
    /// <summary>
    /// Extracts and stores memories from a conversation message.
    /// Uses AI to identify significant facts worth remembering.
    /// </summary>
    Task ExtractAndStoreMemoriesAsync(int userId, Guid sessionId, string message, string role);

    /// <summary>
    /// Stores a single fact authored directly by an assistant (cross-agent memory write half).
    /// Unlike <see cref="ExtractAndStoreMemoriesAsync"/> (Gemini extraction from chat), this is a
    /// structured, validated write: <paramref name="memoryType"/> must be a known
    /// <see cref="Entities.AiMemoryTypes"/> value, text is length-capped, and the per-user memory
    /// count is bounded (oldest-unused pruned). Upserts on (UserId, MemoryType, MemoryKey).
    /// Returns null when the user has AI memory disabled or the input is invalid.
    /// </summary>
    Task<UserAiMemory?> RememberFactAsync(
        int userId, string memoryType, string memoryKey, string memoryValue, string? displayText);

    /// <summary>
    /// Retrieves all active memories for a user.
    /// </summary>
    Task<IEnumerable<UserAiMemory>> GetUserMemoriesAsync(int userId);

    /// <summary>
    /// Retrieves memories relevant to a given context/query.
    /// </summary>
    Task<IEnumerable<UserAiMemory>> GetRelevantMemoriesAsync(int userId, string context, int limit = 5);

    /// <summary>
    /// Updates a memory's value and marks it as manually edited.
    /// </summary>
    Task<UserAiMemory?> UpdateMemoryAsync(int userId, int memoryId, string newValue, string newDisplayText);

    /// <summary>
    /// Deactivates a memory (soft delete).
    /// </summary>
    Task<bool> DeactivateMemoryAsync(int userId, int memoryId);

    /// <summary>
    /// Permanently deletes a memory.
    /// </summary>
    Task<bool> DeleteMemoryAsync(int userId, int memoryId);

    /// <summary>
    /// Deletes all memories for a user (for account deletion or privacy requests).
    /// </summary>
    Task DeleteAllUserMemoriesAsync(int userId);

    /// <summary>
    /// Records that a memory was used in a conversation.
    /// </summary>
    Task RecordMemoryUsageAsync(int memoryId);

    /// <summary>
    /// Builds a context string from relevant memories for AI prompts.
    /// </summary>
    Task<string> BuildMemoryContextAsync(int userId, string query);
}
